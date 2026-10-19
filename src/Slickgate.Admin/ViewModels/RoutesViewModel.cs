using ReactiveUI.SourceGenerators;
using Slickgate.Admin.Infrastructure;
using Slickgate.Admin.Models;
using Slickgate.Admin.Services;

namespace Slickgate.Admin.ViewModels;

/// <summary>Effective routes as the gateway sees them (loaded from the config file).</summary>
public partial class RoutesViewModel : ReactiveViewModelBase
{
    private readonly GatewayAdminClient _api;

    [Reactive]
    public partial IReadOnlyList<RouteSummary> Routes { get; set; } = [];

    [Reactive]
    public partial string Search { get; set; } = "";

    [Reactive]
    public partial bool IsLoading { get; set; }

    [Reactive]
    public partial string? ErrorMessage { get; set; }

    public RoutesViewModel(GatewayAdminClient api)
    {
        _api = api;
        Initialization = RefreshAsync();
    }

    /// <summary>Routes matching the current search; empty when loading failed.</summary>
    public IReadOnlyList<RouteSummary> Filtered =>
        string.IsNullOrWhiteSpace(Search)
            ? Routes
            : Routes.Where(r =>
                r.Id.Contains(Search, StringComparison.OrdinalIgnoreCase)
                || r.Hosts.Any(h => h.Contains(Search, StringComparison.OrdinalIgnoreCase))
                || r.Upstreams.Any(u => u.Contains(Search, StringComparison.OrdinalIgnoreCase))).ToList();

    public async Task RefreshAsync()
    {
        if (IsLoading)
        {
            return;
        }

        ErrorMessage = null;
        IsLoading = true;
        try
        {
            Routes = await _api.GetRoutesAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex is GatewayApiException ? ex.Message : $"Could not load routes: {ex.Message}";
            Routes = [];
        }
        finally
        {
            IsLoading = false;
        }
    }
}
