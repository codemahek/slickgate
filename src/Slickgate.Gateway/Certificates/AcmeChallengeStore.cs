using System.Collections.Concurrent;

namespace Slickgate.Gateway.Certificates;

/// <summary>
/// Holds the key authorizations served at
/// <c>/.well-known/acme-challenge/{token}</c> while an HTTP-01 challenge is in flight.
/// </summary>
public sealed class AcmeChallengeStore
{
    private readonly ConcurrentDictionary<string, string> _responses = new(StringComparer.Ordinal);

    public void Set(string token, string keyAuthorization) => _responses[token] = keyAuthorization;

    public void Remove(string token) => _responses.TryRemove(token, out _);

    public bool TryGet(string token, out string keyAuthorization) =>
        _responses.TryGetValue(token, out keyAuthorization!);
}
