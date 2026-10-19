using ReactiveUI.SourceGenerators;
using Slickgate.Admin.Infrastructure;
using Slickgate.Admin.Services;

namespace Slickgate.Admin.ViewModels;

/// <summary>
/// The gateway's YAML config file: load it, edit it, validate/save it against
/// the gateway, and apply it (explicit reload — nothing watches the file).
/// </summary>
public partial class ConfigViewModel : ReactiveViewModelBase
{
    private readonly GatewayAdminClient _api;

    [Reactive]
    public partial string Yaml { get; set; } = "";

    [Reactive]
    public partial string? ConfigPath { get; set; }

    [Reactive]
    public partial bool IsLoading { get; set; }

    [Reactive]
    public partial bool IsBusy { get; set; }

    [Reactive]
    public partial string? ErrorMessage { get; set; }

    [Reactive]
    public partial string? StatusMessage { get; set; }

    public ConfigViewModel(GatewayAdminClient api)
    {
        _api = api;
        Initialization = LoadAsync();
    }

    /// <summary>Re-reads the config file from disk, discarding unsaved edits.</summary>
    public async Task LoadAsync()
    {
        ErrorMessage = null;
        IsLoading = true;
        try
        {
            var response = await _api.GetRawConfigAsync();
            Yaml = response.Yaml;
            ConfigPath = response.Path;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex is GatewayApiException ? ex.Message : $"Could not load the config file: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Asks the gateway to parse/validate the current text. Returns an error message or null.</summary>
    public async Task<string?> ValidateAsync() =>
        await RunActionAsync(_api.ValidateConfigAsync(Yaml), "Configuration is valid.");

    /// <summary>Writes the current text to the config file (takes effect after Apply). Returns an error message or null.</summary>
    public async Task<string?> SaveAsync() =>
        await RunActionAsync(_api.SaveConfigAsync(Yaml), "Configuration saved. Use Apply to activate it.");

    /// <summary>Saves and then applies the configuration without restarting.</summary>
    public async Task<string?> SaveAndApplyAsync()
    {
        var error = await RunActionAsync(_api.SaveConfigAsync(Yaml), null);
        if (error is not null)
        {
            return error;
        }

        try
        {
            var reload = await _api.ReloadAsync();
            if (!reload.Success)
            {
                return reload.Error ?? "Reload failed.";
            }
        }
        catch (GatewayApiException ex)
        {
            return ex.Message;
        }
        catch (Exception ex)
        {
            return $"Could not apply the configuration: {ex.Message}";
        }

        StatusMessage = "Configuration saved and applied.";
        return null;
    }

    private async Task<string?> RunActionAsync(Task<Models.ConfigActionResponse> action, string? successMessage)
    {
        ErrorMessage = null;
        StatusMessage = null;
        IsBusy = true;
        try
        {
            var response = await action;
            if (!response.Success)
            {
                ErrorMessage = response.Error ?? "The request failed.";
                return ErrorMessage;
            }

            StatusMessage = successMessage;
            return null;
        }
        catch (GatewayApiException ex)
        {
            ErrorMessage = ex.Message;
            return ErrorMessage;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not reach the gateway: {ex.Message}";
            return ErrorMessage;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
