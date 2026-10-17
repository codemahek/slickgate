namespace Slickgate.Gateway.Config;

/// <summary>
/// Watches the YAML config file and the mappings overlay, hot-applying changes
/// (debounced), like <c>caddy reload</c> without needing a signal or restart.
/// </summary>
public sealed class ConfigFileWatcherService(
    ConfigReloader reloader,
    ILogger<ConfigFileWatcherService> logger) : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Reload once at startup so both config and mappings are applied.
        reloader.TryReload(out _);

        using var changed = new SemaphoreSlim(0);
        var watchers = new List<FileSystemWatcher>();

        void Signal()
        {
            if (changed.CurrentCount == 0)
            {
                changed.Release();
            }
        }

        foreach (var file in new[] { reloader.ConfigPath, reloader.MappingsPath })
        {
            var watcher = TryWatch(file, Signal, logger);
            if (watcher is not null)
            {
                watchers.Add(watcher);
                logger.LogInformation("Watching {Path} for changes.", file);
            }
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await changed.WaitAsync(stoppingToken);
                    await Task.Delay(Debounce, stoppingToken);
                    while (changed.CurrentCount > 0)
                    {
                        await changed.WaitAsync(0, stoppingToken);
                    }

                    reloader.TryReload(out _);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            foreach (var watcher in watchers)
            {
                watcher.Dispose();
            }
        }
    }

    private static FileSystemWatcher? TryWatch(
        string path,
        Action onChanged,
        ILogger logger)
    {
        var directory = Path.GetDirectoryName(path);
        var fileName = Path.GetFileName(path);

        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        if (!Directory.Exists(directory))
        {
            logger.LogWarning("Directory {Dir} does not exist; not watching {File}.", directory, fileName);
            return null;
        }

        var watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName |
                           NotifyFilters.Size | NotifyFilters.CreationTime,
        };

        watcher.Changed += (_, _) => onChanged();
        watcher.Created += (_, _) => onChanged();
        watcher.Renamed += (_, _) => onChanged();
        watcher.Deleted += (_, _) => onChanged();
        watcher.EnableRaisingEvents = true;
        return watcher;
    }
}
