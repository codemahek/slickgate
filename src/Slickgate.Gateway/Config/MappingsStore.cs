using Slickgate.Core.Configuration;

namespace Slickgate.Gateway.Config;

/// <summary>
/// Owns the admin-managed overlay file (<c>gateway.mappings.yaml</c>). The engine
/// is the only writer; the independent admin UI drives it through the HTTP API.
/// Saves are atomic (temp file + rename) and applied to the runtime immediately;
/// the file watcher also picks them up, which is harmless and idempotent.
/// </summary>
public sealed class MappingsStore(
    GatewayRuntime runtime,
    GatewayHostOptions options,
    ILogger<MappingsStore> logger)
{
    private readonly object _gate = new();

    public string MappingsPath => Path.GetFullPath(options.MappingsPath);

    /// <summary>Reads the overlay from disk (empty when the file is missing).</summary>
    public IReadOnlyList<RouteDefinition> LoadFromDisk()
    {
        lock (_gate)
        {
            if (!File.Exists(MappingsPath))
            {
                return [];
            }

            return YamlConfigLoader.LoadRouteList(File.ReadAllText(MappingsPath));
        }
    }

    /// <summary>Loads the overlay and publishes it, keeping the previous set on error.</summary>
    public bool TryLoad(out string? error)
    {
        try
        {
            runtime.SetMappingRoutes(LoadFromDisk());
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            logger.LogError(ex, "Failed to load mappings from {Path}; keeping previous mappings.",
                MappingsPath);
            return false;
        }
    }

    /// <summary>Creates or replaces a mapping and persists the whole overlay.</summary>
    public RouteDefinition Upsert(RouteDefinition route)
    {
        lock (_gate)
        {
            var current = runtime.MappingRoutes.ToList();
            var index = current.FindIndex(r => string.Equals(r.Id, route.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                current[index] = route;
            }
            else
            {
                current.Add(route);
            }

            SaveAndApply(current);
            return route;
        }
    }

    /// <summary>Removes a mapping by id; returns false when it does not exist.</summary>
    public bool Delete(string id)
    {
        lock (_gate)
        {
            var current = runtime.MappingRoutes.ToList();
            if (current.RemoveAll(r => string.Equals(r.Id, id, StringComparison.Ordinal)) == 0)
            {
                return false;
            }

            SaveAndApply(current);
            return true;
        }
    }

    private void SaveAndApply(IReadOnlyList<RouteDefinition> routes)
    {
        YamlConfigLoader.ValidateRoutes(routes);
        var yaml = RouteYamlWriter.Write(routes);

        var path = MappingsPath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, yaml);
        File.Move(temp, path, overwrite: true);

        runtime.SetMappingRoutes(routes);

        logger.LogInformation("Mappings saved to {Path} ({Count} route(s)).", path, routes.Count);
    }
}
