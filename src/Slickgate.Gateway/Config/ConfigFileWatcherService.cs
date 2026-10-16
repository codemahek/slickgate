namespace Slickgate.Gateway.Config;

/// <summary>
/// Watches the YAML config file and hot-applies changes (debounced), like
/// <c>caddy reload</c> without needing a signal or restart.
/// </summary>
public sealed class ConfigFileWatcherService(
    ConfigReloader reloader,
    ILogger<ConfigFileWatcherService> logger) : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var fullPath = reloader.ConfigPath;
        var directory = Path.GetDirectoryName(fullPath);
        var fileName = Path.GetFileName(fullPath);

        if (directory is null || !Directory.Exists(directory))
        {
            logger.LogWarning("Config directory {Dir} does not exist; watching disabled.", directory);
            return;
        }

        reloader.TryReload(out _);

        using var changed = new SemaphoreSlim(0);
        using var watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName |
                           NotifyFilters.Size | NotifyFilters.CreationTime,
        };

        void Signal()
        {
            if (changed.CurrentCount == 0)
            {
                changed.Release();
            }
        }

        watcher.Changed += (_, _) => Signal();
        watcher.Created += (_, _) => Signal();
        watcher.Renamed += (_, _) => Signal();
        watcher.Deleted += (_, _) => Signal();
        watcher.EnableRaisingEvents = true;

        logger.LogInformation("Watching {Path} for changes.", fullPath);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await changed.WaitAsync(stoppingToken);
                await Task.Delay(Debounce, stoppingToken);
                while (changed.CurrentCount > 0)
                {
                    changed.Wait(0);
                }

                reloader.TryReload(out _);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
