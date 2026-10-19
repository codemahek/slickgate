using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
using Slickgate.Admin.Models;

namespace Slickgate.Admin.Services;

/// <summary>
/// Thin typed client over the gateway's admin HTTP API. Depends only on HTTP —
/// no reference to the engine — so the UI remains fully independent.
/// </summary>
public sealed class GatewayAdminClient(HttpClient http, GatewaySettings settings)
{
    public GatewaySettings Settings => settings;

    public Task<HealthResponse> GetHealthAsync(CancellationToken ct = default) =>
        GetAsync("/-/health", AdminJson.Default.HealthResponse, ct);

    public async Task<List<RouteSummary>> GetRoutesAsync(CancellationToken ct = default)
    {
        var response = await GetAsync("/-/routes", AdminJson.Default.RoutesResponse, ct).ConfigureAwait(false);
        return response.Routes;
    }

    public Task<ConfigResponse> GetConfigAsync(CancellationToken ct = default) =>
        GetAsync("/-/config", AdminJson.Default.ConfigResponse, ct);

    public Task<RawConfigResponse> GetRawConfigAsync(CancellationToken ct = default) =>
        GetAsync("/-/config/raw", AdminJson.Default.RawConfigResponse, ct);

    public Task<ConfigActionResponse> ValidateConfigAsync(string yaml, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, BuildUri("/-/config/validate"))
        {
            Content = JsonContent.Create(new RawConfigRequest(yaml), AdminJson.Default.RawConfigRequest),
        };
        return SendActionAsync(request, ct);
    }

    public Task<ConfigActionResponse> SaveConfigAsync(string yaml, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, BuildUri("/-/config/raw"))
        {
            Content = JsonContent.Create(new RawConfigRequest(yaml), AdminJson.Default.RawConfigRequest),
        };
        return SendActionAsync(request, ct);
    }

    public Task<ReloadResponse> ReloadAsync(CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, BuildUri("/-/reload"));
        return SendAsync(request, AdminJson.Default.ReloadResponse, ct);
    }

    private Task<T> GetAsync<T>(string path, JsonTypeInfo<T> typeInfo, CancellationToken ct) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, BuildUri(path)), typeInfo, ct);

    /// <summary>
    /// Validate/save answer HTTP 400 (with a <see cref="ConfigActionResponse"/> body) when the
    /// YAML is rejected; surface that as a normal <c>Success == false</c> result.
    /// </summary>
    private async Task<ConfigActionResponse> SendActionAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await SendAsync(request, AdminJson.Default.ConfigActionResponse, ct).ConfigureAwait(false);
        }
        catch (GatewayApiException ex) when (ex.StatusCode == (int)HttpStatusCode.BadRequest)
        {
            return new ConfigActionResponse(false, ex.Message);
        }
    }

    private Uri BuildUri(string path) => new(settings.BaseUrl + path);

    private async Task<T> SendAsync<T>(HttpRequestMessage request, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        await settings.EnsureLoadedAsync().ConfigureAwait(false);

        if (settings.HasToken && !request.Headers.Contains("X-Slickgate-Token"))
        {
            request.Headers.TryAddWithoutValidation("X-Slickgate-Token", settings.Token);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new GatewayApiException(0, $"Cannot reach the gateway at {settings.BaseUrl}. {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GatewayApiException(0, $"The gateway at {settings.BaseUrl} did not respond in time.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new GatewayApiException((int)response.StatusCode, await ReadErrorAsync(response, ct).ConfigureAwait(false));
            }

            var value = await response.Content.ReadFromJsonAsync(typeInfo, ct).ConfigureAwait(false);
            return value ?? throw new GatewayApiException((int)response.StatusCode, "The gateway returned an empty response.");
        }
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync(AdminJson.Default.ErrorResponse, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(error?.Error))
            {
                return error!.Error;
            }
        }
        catch
        {
            // fall through to status-based message
        }

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Unauthorized — check the admin token in Settings.",
            HttpStatusCode.NotFound => "Not found.",
            _ => $"Request failed with status {(int)response.StatusCode} {response.ReasonPhrase}.",
        };
    }
}
