using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.Workspace.ViewModels;

/// <summary>
/// Owns the bounded teardown of a playback engine and its parking surface. The tab remains the
/// state owner, while this coordinator makes the shutdown contract reusable for detached tabs and
/// future playback hosts.
/// </summary>
internal sealed class PlaybackResourceCoordinator
{
    private static readonly TimeSpan CancellationDrainTimeout = TimeSpan.FromSeconds(1);
    private readonly IAppLogger logger;
    private readonly Func<string> displayName;

    public PlaybackResourceCoordinator(IAppLogger logger, Func<string> displayName)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.displayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
    }

    public async Task StopAsync(
        IPlaybackEngine engine,
        TimeSpan? timeout,
        IDisposable? parkingSurface,
        CancellationToken engineCancellationToken,
        Action<Task>? observeBackgroundCleanup = null)
    {
        ArgumentNullException.ThrowIfNull(engine);

        var shutdownCancellation = CancellationTokenSource.CreateLinkedTokenSource(engineCancellationToken);
        var shutdownTask = Task.Run(async () =>
        {
            using (parkingSurface)
            using (engine)
            {
                await engine.StopAsync(shutdownCancellation.Token).ConfigureAwait(false);
            }
        });

        try
        {
            if (timeout is { } stopTimeout && await Task.WhenAny(
                shutdownTask, Task.Delay(stopTimeout)).ConfigureAwait(false) != shutdownTask)
            {
                // Cancellation callbacks can stall just as the engine can. Keep them
                // within the same drain deadline and retain the source until both finish.
                shutdownTask = Task.WhenAll(shutdownTask, shutdownCancellation.CancelAsync());
                await DrainTimedOutShutdownAsync(shutdownTask, observeBackgroundCleanup).ConfigureAwait(false);
                return;
            }

            await shutdownTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (engineCancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.WriteSafely(
                AppLogLevel.Warning,
                "Playback",
                $"Failed to stop playback for {displayName()}.",
                ex);
        }
        finally
        {
            // Disposing the link can wait for a callback already running on the parent token.
            _ = shutdownTask.ContinueWith(completed =>
            {
                _ = completed.Exception;
                shutdownCancellation.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private async Task DrainTimedOutShutdownAsync(
        Task shutdownTask,
        Action<Task>? observeBackgroundCleanup)
    {
        logger.WriteSafely(
            AppLogLevel.Warning,
            "Playback",
            $"Timed out stopping playback for {displayName()}. The engine token was canceled.");

        if (await Task.WhenAny(shutdownTask, Task.Delay(CancellationDrainTimeout)).ConfigureAwait(false) == shutdownTask)
        {
            return;
        }

        logger.WriteSafely(
            AppLogLevel.Warning,
            "Playback",
            $"Playback cleanup for {displayName()} ignored cancellation and remains tracked in the background.");
        observeBackgroundCleanup?.Invoke(shutdownTask);
    }
}
