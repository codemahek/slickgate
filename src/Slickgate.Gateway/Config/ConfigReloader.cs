using Slickgate.Core.Configuration;

namespace Slickgate.Gateway.Config;

/// <summary>
/// Loads the YAML file from disk and publishes it to the runtime, keeping the
/// previous configuration on error. Shared by the file watcher and the admin
/// <c>POST /-/reload</c> endpoint.
/// </summary>
public sealed class ConfigReloader(GatewayRuntime runtime, GatewayHostOptions options, ILogger<ConfigReloader> logger)
{
    public string ConfigPath => Path.GetFullPath(options.ConfigPath);

    public bool TryReload(out string? error)
    {
        try
        {
            var config = YamlConfigLoader.LoadFile(ConfigPath);
            runtime.SetFileConfig(config);
            logger.LogInformation(
                "Configuration loaded from {Path}: {RouteCount} file route(s).",
                ConfigPath, config.Routes.Count);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            logger.LogError(ex, "Failed to load {Path}; keeping the previous configuration.", ConfigPath);
            return false;
        }
    }
}
