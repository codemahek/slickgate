namespace Slickgate.Admin.Services;

/// <summary>Raised when the gateway returns an error or cannot be reached.</summary>
public sealed class GatewayApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
