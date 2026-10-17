using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Configuration;

namespace Slickgate.Core.Proxy;

/// <summary>
/// A hot-swappable <see cref="IProxyConfigProvider"/>: publishes a new immutable
/// snapshot of routes/clusters on every <see cref="Update"/> and fires the previous
/// snapshot's change token so YARP reloads atomically (same pattern as YARP's
/// <c>InMemoryConfigProvider</c>).
/// </summary>
public sealed class DynamicProxyConfigProvider(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters)
    : IProxyConfigProvider
{
    private volatile Snapshot _config = new(routes, clusters, NewRevision());

    public DynamicProxyConfigProvider()
        : this([], [])
    {
    }

    public IProxyConfig GetConfig() => _config;

    /// <summary>
    /// Swaps in a new snapshot, then signals that the old one is stale.
    /// </summary>
    public void Update(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters)
    {
        var newConfig = new Snapshot(routes, clusters, NewRevision());
        var oldConfig = Interlocked.Exchange(ref _config, newConfig);
        oldConfig.SignalChange();
    }

    private static string NewRevision() => Guid.NewGuid().ToString("N");

    private sealed class Snapshot : IProxyConfig
    {
        private readonly CancellationTokenSource _cts = new();

        public Snapshot(
            IReadOnlyList<RouteConfig> routes,
            IReadOnlyList<ClusterConfig> clusters,
            string revisionId)
        {
            RevisionId = revisionId;
            Routes = routes;
            Clusters = clusters;
            ChangeToken = new CancellationChangeToken(_cts.Token);
        }

        public string RevisionId { get; }

        public IReadOnlyList<RouteConfig> Routes { get; }

        public IReadOnlyList<ClusterConfig> Clusters { get; }

        public IChangeToken ChangeToken { get; }

        public void SignalChange() => _cts.Cancel();
    }
}
