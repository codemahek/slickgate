using Slickgate.Core.Configuration;

namespace Slickgate.Gateway.Config;

/// <summary>
/// Loads the YAML file (and the admin-managed mappings overlay) from disk and
/// publishes them to the runtime, keeping the previous configuration on error.
/// Shared by the file watcher and the admin <c>POST /-/reload</c> endpoint.
/// </summary>
public sealed partial class ConfigReloader(
    GatewayRuntime runtime,
    GatewayHostOptions options,
    MappingsStore mappings,
    ILogger<ConfigReloader> logger)
{
    public string ConfigPath => Path.GetFullPath(options.ConfigPath);

    public string MappingsPath => mappings.MappingsPath;

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

        // Mappings are secondary: a bad overlay is reported but does not fail the reload.
        if (!mappings.TryLoad(out var mappingsError))
        {
            error = mappingsError;
            return false;
        }

        error = null;
        return true;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to load {Path}; keeping the previous configuration.")]
    private static partial void LogLoadFailed(ILogger logger, string path, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Configuration loaded from {Path}: {RouteCount} file route(s).")]
    private static partial void LogConfigLoaded(ILogger logger, string path, int routeCount);
}
