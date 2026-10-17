using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Slickgate.Core.Configuration;

namespace Slickgate.Gateway.Certificates;

/// <summary>
/// Resolves the certificate to present for a given SNI host. ACME certificates are
/// loaded from <c>{storage}/{host}/</c>; anything else falls back to an on-the-fly
/// self-signed certificate so TLS never fails during bootstrap.
/// </summary>
public sealed class CertificateManager
{
    private readonly ConcurrentDictionary<string, X509Certificate2> _selfSigned =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, X509Certificate2> _acme =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly X509Certificate2 _fallback;
    private volatile Dictionary<string, TlsMode> _modes = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, X509Certificate2> _file =
        new(StringComparer.OrdinalIgnoreCase);

    private volatile Dictionary<string, (string CertFile, string KeyFile)> _fileSources =
        new(StringComparer.OrdinalIgnoreCase);

    public CertificateManager(string storageDir)
    {
        StorageDir = Path.GetFullPath(storageDir);
        _fallback = CreateSelfSigned("slickgate.default");
    }

    public string StorageDir { get; }

    public void UpdateRoutes(GatewayConfig config)
    {
        var modes = new Dictionary<string, TlsMode>(StringComparer.OrdinalIgnoreCase);
        var sources = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

        foreach (var route in config.Routes)
        {
            var certFile = route.CertFile ?? config.Tls.CertFile;
            var keyFile = route.KeyFile ?? config.Tls.KeyFile;

            foreach (var host in route.Hosts)
            {
                if (route.Tls != TlsMode.Off)
                {
                    modes.TryAdd(host, route.Tls);
                }

                if (route.Tls == TlsMode.File && certFile is not null && keyFile is not null)
                {
                    sources.TryAdd(host, (certFile, keyFile));
                }
            }
        }

        _modes = modes;
        _fileSources = sources;

        // Drop cached certificates whose host is no longer file-backed.
        foreach (var host in _file.Keys)
        {
            if (!sources.ContainsKey(host))
            {
                _file.TryRemove(host, out _);
            }
        }
    }

    /// <summary>Kestrel SNI callback.</summary>
    public X509Certificate2 Select(string? sniHost)
    {
        if (string.IsNullOrWhiteSpace(sniHost))
        {
            return _fallback;
        }

        var mode = _modes.GetValueOrDefault(sniHost, TlsMode.Off);
        return mode switch
        {
            TlsMode.Auto => GetAcme(sniHost) ?? GetSelfSigned(sniHost),
            TlsMode.Internal => GetSelfSigned(sniHost),
            TlsMode.File => GetFile(sniHost) ?? GetSelfSigned(sniHost),
            _ => _fallback,
        };
    }

    /// <summary>
    /// Loads (and caches) the bring-your-own certificate configured for a host via
    /// <c>tls: file</c>. Returns null when the host has no file cert or it cannot be read.
    /// </summary>
    public X509Certificate2? GetFile(string host)
    {
        if (_file.TryGetValue(host, out var cached))
        {
            return cached;
        }

        if (!_fileSources.TryGetValue(host, out var source) ||
            !File.Exists(source.CertFile) ||
            !File.Exists(source.KeyFile))
        {
            return null;
        }

        try
        {
            using var loaded = X509Certificate2.CreateFromPemFile(source.CertFile, source.KeyFile);
            return _file.GetOrAdd(host, NormalizeForTls(loaded));
        }
        catch
        {
            return null;
        }
    }

    public X509Certificate2 GetSelfSigned(string host) =>
        _selfSigned.GetOrAdd(host, CreateSelfSigned);

    public X509Certificate2? GetAcme(string host) =>
        _acme.TryGetValue(host, out var cert) ? cert : null;

    /// <summary>(Re)loads the ACME certificate for a host from disk. Returns null when absent/invalid.</summary>
    public X509Certificate2? ReloadAcme(string host)
    {
        var (certPath, keyPath) = GetAcmePaths(host);
        if (!File.Exists(certPath) || !File.Exists(keyPath))
        {
            return null;
        }

        try
        {
            using var loaded = X509Certificate2.CreateFromPemFile(certPath, keyPath);
            var usable = NormalizeForTls(loaded);
            _acme[host] = usable;
            return usable;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when a certificate exists and is valid for at least <paramref name="validFor"/>.</summary>
    public bool HasValidAcme(string host, TimeSpan validFor)
    {
        var cert = GetAcme(host) ?? ReloadAcme(host);
        if (cert is null)
        {
            return false;
        }

        return cert.NotAfter > DateTime.Now.Add(validFor);
    }

    public (string CertPath, string KeyPath) GetAcmePaths(string host)
    {
        var dir = Path.Combine(StorageDir, "certs", SanitizeHost(host));
        return (Path.Combine(dir, "cert.pem"), Path.Combine(dir, "key.pem"));
    }

    public string AccountKeyPath => Path.Combine(StorageDir, "account.key");

    public static string SanitizeHost(string host) =>
        host.Replace('*', '_').Replace('/', '_').Replace('\\', '_').Replace(':', '_');

    private static X509Certificate2 CreateSelfSigned(string host)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={host}", ecdsa, HashAlgorithmName.SHA256);

        var san = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(host, out var ip))
        {
            san.AddIpAddress(ip);
        }
        else
        {
            san.AddDnsName(host);
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                false));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") },
                false));

        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));

        return NormalizeForTls(ephemeral);
    }

    /// <summary>
    /// Round-trips a certificate through PFX so the private key is usable by
    /// SslStream on all platforms (notably Windows with ephemeral PEM keys).
    /// </summary>
    private static X509Certificate2 NormalizeForTls(X509Certificate2 cert)
    {
        var pfx = cert.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadPkcs12(pfx, password: null, X509KeyStorageFlags.Exportable);
    }
}
