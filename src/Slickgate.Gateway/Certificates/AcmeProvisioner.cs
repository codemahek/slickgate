using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using Slickgate.Core.Configuration;
using Directory = System.IO.Directory;

namespace Slickgate.Gateway.Certificates;

/// <summary>
/// Issues and renews Let's Encrypt certificates using the HTTP-01 challenge.
/// Certificates are obtained lazily: any route with <c>tls: auto</c> that lacks a
/// valid certificate is provisioned in the background, then hot-loaded by
/// <see cref="CertificateManager"/>.
/// </summary>
public sealed partial class AcmeProvisioner(
    GatewayRuntime runtime,
    CertificateManager certificates,
    AcmeChallengeStore challenges,
    ILogger<AcmeProvisioner> logger) : BackgroundService
{
    private static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private const int MaxConcurrentOrders = 3;

    private int _provisioning;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        runtime.Changed += OnConfigChanged;

        await SafeProvisionAllAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(CheckInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await SafeProvisionAllAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private void OnConfigChanged(GatewayConfig config)
    {
        if (config.Acme.Enabled)
        {
            // Fire-and-forget; SafeProvisionAllAsync serializes itself.
            _ = SafeProvisionAllAsync(CancellationToken.None);
        }
    }

    private async Task SafeProvisionAllAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _provisioning, 1) == 1)
        {
            return;
        }

        try
        {
            await ProvisionAllAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogProvisioningPassFailed(logger, ex);
        }
        finally
        {
            Interlocked.Exchange(ref _provisioning, 0);
        }
    }

    private async Task ProvisionAllAsync(CancellationToken ct)
    {
        var config = runtime.Effective;
        var acme = config.Acme;
        if (!acme.Enabled)
        {
            return;
        }

        if (config.Server.HttpPort != 80)
        {
            LogHttpPortWarning(logger, config.Server.HttpPort);
        }

        var hosts = config.Routes
            .Where(static r => r.Tls == TlsMode.Auto)
            .SelectMany(static r => r.Hosts)
            .Where(static h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(host => !certificates.HasValidAcme(host, RenewBefore))
            .ToList();

        if (hosts.Count == 0)
        {
            return;
        }

        // One account key per pass (passes never overlap), then provision hosts
        // with bounded parallelism: each order is several round trips of waiting
        // (issue -> challenge poll -> finalize), so serializing them multiplies
        // cold-start time by the number of hosts.
        var accountKey = LoadOrCreateAccountKey(acme);
        using var gate = new SemaphoreSlim(MaxConcurrentOrders);
        var tasks = new List<Task>(hosts.Count);
        foreach (var host in hosts)
        {
            tasks.Add(ProvisionHostAsync(host, acme, accountKey, gate, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task ProvisionHostAsync(
        string host,
        AcmeConfig acme,
        IKey accountKey,
        SemaphoreSlim gate,
        CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            LogRequestingCertificate(logger, host, acme.DirectoryUrl);

            var context = new AcmeContext(new Uri(acme.DirectoryUrl), accountKey);
            await context.NewAccount(acme.Email, termsOfServiceAgreed: true).ConfigureAwait(false);

            var order = await context.NewOrder([host]).ConfigureAwait(false);
            foreach (var authorization in await order.Authorizations().ConfigureAwait(false))
            {
                await SolveHttpChallengeAsync(authorization, ct).ConfigureAwait(false);
            }

            var certKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
            var chain = await order.Generate(new CsrInfo { CommonName = host }, certKey)
                .ConfigureAwait(false);

            var (certPath, keyPath) = certificates.GetAcmePaths(host);
            Directory.CreateDirectory(Path.GetDirectoryName(certPath)!);
            await File.WriteAllTextAsync(certPath, chain.ToPem(), ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(keyPath, certKey.ToPem(), ct).ConfigureAwait(false);

            certificates.ReloadAcme(host);
            LogCertificateInstalled(logger, host, certPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogProvisioningFailed(logger, host, ex);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task SolveHttpChallengeAsync(IAuthorizationContext authorization, CancellationToken ct)
    {
        var challenge = await authorization.Http().ConfigureAwait(false)
            ?? throw new InvalidOperationException("ACME server did not offer an HTTP-01 challenge.");

        var token = challenge.Token;
        challenges.Set(token, challenge.KeyAuthz);

        try
        {
            await challenge.Validate().ConfigureAwait(false);

            for (var attempt = 0; attempt < 30; attempt++)
            {
                var resource = await authorization.Resource().ConfigureAwait(false);
                if (resource.Status == AuthorizationStatus.Valid)
                {
                    return;
                }

                if (resource.Status == AuthorizationStatus.Invalid)
                {
                    throw new InvalidOperationException(
                        $"ACME HTTP-01 validation failed for '{resource.Identifier?.Value}'.");
                }

                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }

            throw new TimeoutException("Timed out waiting for ACME authorization to become valid.");
        }
        finally
        {
            challenges.Remove(token);
        }
    }

    private IKey LoadOrCreateAccountKey(AcmeConfig acme)
    {
        var path = certificates.AccountKeyPath;
        if (File.Exists(path))
        {
            return KeyFactory.FromPem(File.ReadAllText(path));
        }

        var key = KeyFactory.NewKey(KeyAlgorithm.ES256);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, key.ToPem());
        return key;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "ACME provisioning pass failed.")]
    private static partial void LogProvisioningPassFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "acme.enabled is true but server.http_port is {Port}; HTTP-01 challenges require port 80 to be reachable from the internet.")]
    private static partial void LogHttpPortWarning(ILogger logger, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "Requesting ACME certificate for {Host} from {Directory}.")]
    private static partial void LogRequestingCertificate(ILogger logger, string host, string directory);

    [LoggerMessage(Level = LogLevel.Information, Message = "ACME certificate for {Host} installed at {Path}.")]
    private static partial void LogCertificateInstalled(ILogger logger, string host, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "ACME provisioning failed for {Host}; keeping the self-signed fallback.")]
    private static partial void LogProvisioningFailed(ILogger logger, string host, Exception exception);
}
