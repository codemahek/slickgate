using Slickgate.Core.Configuration;

namespace Slickgate.Gateway.Config;

/// <summary>
/// Loads the YAML config file from disk and publishes it to the runtime, keeping
/// the previous configuration on error. Invoked by the admin <c>POST /-/reload</c>
/// endpoint ("Apply") — nothing watches the file for changes.
/// </summary>
public sealed partial class ConfigReloader(
    GatewayRuntime runtime,
    GatewayHostOptions options,
    ILogger<ConfigReloader> logger)
{
    public string ConfigPath => Path.GetFullPath(options.ConfigPath);

    public bool TryReload(out string? error)
    {
        GatewayConfig config;
        try
        {
            config = YamlConfigLoader.LoadFile(ConfigPath);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            LogLoadFailed(logger, ConfigPath, ex);
            return false;
        }

        runtime.SetFileConfig(config);
        LogConfigLoaded(logger, ConfigPath, config.Routes.Count);
        error = null;
        return true;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to load {Path}; keeping the previous configuration.")]
    private static partial void LogLoadFailed(ILogger logger, string path, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Configuration loaded from {Path}: {RouteCount} route(s).")]
    private static partial void LogConfigLoaded(ILogger logger, string path, int routeCount);
}
