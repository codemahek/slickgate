using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Slickgate.Admin.Infrastructure;
using Slickgate.Admin.Models;
using Slickgate.Admin.Services;

namespace Slickgate.Admin.ViewModels;

/// <summary>Dashboard: gateway health, effective config and config reload.</summary>
public partial class DashboardViewModel : ReactiveViewModelBase
{
    private readonly GatewayAdminClient _api;

    [Reactive]
    public partial HealthResponse? Health { get; set; }

    [Reactive]
    public partial ConfigResponse? Config { get; set; }

    [Reactive]
    public partial bool IsLoading { get; set; }

    [Reactive]
    public partial bool IsReloading { get; set; }

    [Reactive]
    public partial string? ErrorMessage { get; set; }

    public DashboardViewModel(GatewayAdminClient api)
    {
        _api = api;
        Initialization = RefreshAsync();
    }

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
            Health = await _api.GetHealthAsync();
            Config = await _api.GetConfigAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex is GatewayApiException ? ex.Message : $"Could not load status: {ex.Message}";
            Health = null;
            Config = null;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Reloads the gateway configuration. Null on success, otherwise an error message.</summary>
    public async Task<string?> ReloadConfigAsync()
    {
        ErrorMessage = null;
        IsReloading = true;
        try
        {
            var result = await _api.ReloadAsync();
            if (!result.Success)
            {
                return result.Error ?? "Reload failed.";
            }

            await RefreshAsync();
            return null;
        }
        catch (GatewayApiException ex)
        {
            return ex.Message;
        }
        finally
        {
            IsReloading = false;
        }
    }
}
