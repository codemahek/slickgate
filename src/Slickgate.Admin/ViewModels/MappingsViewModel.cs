using ReactiveUI.SourceGenerators;
using Slickgate.Admin.Infrastructure;
using Slickgate.Admin.Models;
using Slickgate.Admin.Services;

namespace Slickgate.Admin.ViewModels;

/// <summary>
/// Admin-managed overlay routes: the CRUD surface that persists to
/// gateway.mappings.yaml through the gateway's admin API.
/// </summary>
public partial class MappingsViewModel : ReactiveViewModelBase
{
    private readonly GatewayAdminClient _api;

    [Reactive]
    public partial IReadOnlyList<RouteMapping> Mappings { get; set; } = [];

    [Reactive]
    public partial bool IsLoading { get; set; }

    [Reactive]
    public partial string? ErrorMessage { get; set; }

    public MappingsViewModel(GatewayAdminClient api)
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
            Mappings = await _api.GetMappingsAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex is GatewayApiException ? ex.Message : $"Could not load mappings: {ex.Message}";
            Mappings = [];
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Upserts a mapping. Null on success, otherwise an error message.</summary>
    public async Task<string?> SaveAsync(RouteMapping mapping)
    {
        ErrorMessage = null;
        try
        {
            var response = await _api.UpsertMappingAsync(mapping.Id ?? "", mapping);
            if (!response.Success)
            {
                return response.Error ?? "Save failed.";
            }

            await RefreshAsync();
            return null;
        }
        catch (GatewayApiException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Deletes a mapping. Null on success, otherwise an error message.</summary>
    public async Task<string?> DeleteAsync(string id)
    {
        ErrorMessage = null;
        try
        {
            var response = await _api.DeleteMappingAsync(id);
            if (!response.Success)
            {
                return response.Error ?? "Delete failed.";
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
