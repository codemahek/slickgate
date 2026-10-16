using ReactiveUI.SourceGenerators;
using Slickgate.Admin.Infrastructure;
using Slickgate.Admin.Models;
using Slickgate.Admin.Services;

namespace Slickgate.Admin.ViewModels;

/// <summary>
/// Running Docker containers plus one-click import of a container as an
/// admin-managed mapping.
/// </summary>
public partial class ContainersViewModel : ReactiveViewModelBase
{
    private readonly GatewayAdminClient _api;

    [Reactive]
    public partial ContainersResponse? Response { get; set; }

    [Reactive]
    public partial bool IsLoading { get; set; }

    [Reactive]
    public partial string? ErrorMessage { get; set; }

    public bool DockerEnabled => Response?.DockerEnabled ?? false;

    public IReadOnlyList<ContainerView> Containers => Response?.Containers ?? [];

    public ContainersViewModel(GatewayAdminClient api)
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
            Response = await _api.GetContainersAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex is GatewayApiException ? ex.Message : $"Could not list containers: {ex.Message}";
            Response = new ContainersResponse(false, ErrorMessage, 0, []);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Persists a suggested mapping. Null on success, otherwise an error message.</summary>
    public async Task<string?> ImportAsync(RouteMapping mapping, string fallbackId)
    {
        ErrorMessage = null;
        try
        {
            var id = string.IsNullOrWhiteSpace(mapping.Id) ? fallbackId : mapping.Id;
            var response = await _api.UpsertMappingAsync(id, mapping);
            if (!response.Success)
            {
                return response.Error ?? "Import failed.";
            }

            await RefreshAsync();
            return null;
        }
        catch (GatewayApiException ex)
        {
            return ex.Message;
        }
    }
}
