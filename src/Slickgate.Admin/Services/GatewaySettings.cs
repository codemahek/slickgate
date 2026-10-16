using Microsoft.JSInterop;

namespace Slickgate.Admin.Services;

/// <summary>
/// Connection settings for the gateway admin API. The UI is deployed
/// independently, so the gateway base URL and admin token are user-configurable
/// and persisted in the browser's localStorage.
/// </summary>
public sealed class GatewaySettings(IJSRuntime js, string? defaultBaseUrl)
{
    private const string BaseUrlKey = "slickgate.baseUrl";
    private const string TokenKey = "slickgate.token";

    private bool _loaded;

    public string BaseUrl { get; private set; } =
        string.IsNullOrWhiteSpace(defaultBaseUrl) ? "http://localhost:8080" : defaultBaseUrl.TrimEnd('/');

    public string? Token { get; private set; }

    public bool HasToken => !string.IsNullOrWhiteSpace(Token);

    /// <summary>Reads overrides from localStorage once, on first use.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;

        try
        {
            var url = await js.InvokeAsync<string?>("localStorage.getItem", BaseUrlKey);
            if (!string.IsNullOrWhiteSpace(url))
            {
                BaseUrl = url.TrimEnd('/');
            }

            var token = await js.InvokeAsync<string?>("localStorage.getItem", TokenKey);
            if (!string.IsNullOrWhiteSpace(token))
            {
                Token = token;
            }
        }
        catch (JSException)
        {
            // localStorage unavailable (e.g. private mode) — fall back to defaults.
        }
    }

    /// <summary>Applies values in memory only (no localStorage write).</summary>
    public void Apply(string baseUrl, string? token)
    {
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            BaseUrl = baseUrl.Trim().TrimEnd('/');
        }

        Token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }

    public async Task SaveAsync(string baseUrl, string? token)
    {
        Apply(baseUrl, token);

        try
        {
            await js.InvokeVoidAsync("localStorage.setItem", BaseUrlKey, BaseUrl);
            if (Token is null)
            {
                await js.InvokeVoidAsync("localStorage.removeItem", TokenKey);
            }
            else
            {
                await js.InvokeVoidAsync("localStorage.setItem", TokenKey, Token);
            }
        }
        catch (JSException)
        {
            // Persisting is best-effort; the in-memory values still apply.
        }
    }
}
