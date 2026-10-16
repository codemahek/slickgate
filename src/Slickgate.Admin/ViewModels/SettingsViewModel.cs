using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Slickgate.Admin.Infrastructure;
using Slickgate.Admin.Services;
using System.Reactive.Linq;

namespace Slickgate.Admin.ViewModels;

/// <summary>
/// Gateway connection settings (base URL + admin token), persisted to the
/// browser's localStorage and flagged reactively when edited.
/// </summary>
public partial class SettingsViewModel : ReactiveViewModelBase
{
    private readonly GatewaySettings _settings;
    private readonly GatewayAdminClient _api;

    [Reactive]
    public partial string BaseUrl { get; set; } = "";

    [Reactive]
    public partial string Token { get; set; } = "";

    [Reactive]
    public partial bool HasUnsavedChanges { get; set; }

    [Reactive]
    public partial bool IsTesting { get; set; }

    [Reactive]
    public partial bool StatusOk { get; set; }

    [Reactive]
    public partial string? StatusMessage { get; set; }

    public SettingsViewModel(GatewaySettings settings, GatewayAdminClient api)
    {
        _settings = settings;
        _api = api;

        BaseUrl = settings.BaseUrl;
        Token = settings.Token ?? "";

        // Reactive pipeline: flips as soon as either field no longer matches
        // what is stored, without any manual dirty-tracking in the view.
        this.WhenAnyValue(x => x.BaseUrl, x => x.Token, (baseUrl, token) => IsDirty(baseUrl, token))
            .Subscribe(dirty => HasUnsavedChanges = dirty)
            .DisposeWith(Subscriptions);

        Initialization = LoadAsync();
    }

    /// <summary>Syncs the fields with the persisted (localStorage) settings.</summary>
    private async Task LoadAsync()
    {
        await _settings.EnsureLoadedAsync();
        BaseUrl = _settings.BaseUrl;
        Token = _settings.Token ?? "";
        HasUnsavedChanges = false;
    }

    /// <summary>Persists the settings. Null on success, otherwise an error message.</summary>
    public async Task<string?> SaveAsync()
    {
        try
        {
            await _settings.SaveAsync(BaseUrl, Token);
            BaseUrl = _settings.BaseUrl;
            Token = _settings.Token ?? "";
            HasUnsavedChanges = false;
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not save settings: {ex.Message}";
        }
    }

    /// <summary>
    /// Applies (without persisting) then probes <c>/-/health</c>. Null on success,
    /// otherwise an error message.
    /// </summary>
    public async Task<string?> TestAsync()
    {
        IsTesting = true;
        StatusMessage = null;
        StatusOk = false;
        try
        {
            _settings.Apply(BaseUrl, Token);
            var health = await _api.GetHealthAsync();
            StatusOk = true;
            StatusMessage =
                $"Connected. Status '{health.Status}', {health.Routes} routes, revision {health.Revision}.";
            return null;
        }
        catch (Exception ex)
        {
            StatusMessage = ex is GatewayApiException
                ? ex.Message
                : $"Connection test failed: {ex.Message}";
            return StatusMessage;
        }
        finally
        {
            IsTesting = false;
        }
    }

    private bool IsDirty(string baseUrl, string token)
    {
        var url = string.IsNullOrWhiteSpace(baseUrl) ? baseUrl : baseUrl.Trim().TrimEnd('/');
        var current = string.IsNullOrWhiteSpace(token) ? token : token.Trim();

        return !string.Equals(url, _settings.BaseUrl, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(current, _settings.Token ?? "", StringComparison.Ordinal);
    }
}
