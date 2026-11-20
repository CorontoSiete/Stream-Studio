using System.Diagnostics;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.Workspace.ViewModels;

internal sealed class BackgroundOperationController(IAppLogger logger)
{
    private readonly object gate = new();
    private readonly HashSet<Task> operations = [];

    public void Track(Task operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        lock (gate)
        {
            if (!operations.Add(operation)) return;
        }

        _ = operation.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                lock (gate)
                {
                    operations.Remove(operation);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal Task WaitForCurrentAsync() => ObserveCompletionAsync(Task.WhenAll(Snapshot()));

    internal async Task WaitForIdleAsync()
    {
        while (Snapshot() is { Length: > 0 } pending)
        {
            await ObserveCompletionAsync(Task.WhenAll(pending)).ConfigureAwait(false);
            ForgetCompleted(pending);
        }
    }

    public async Task DrainAsync(TimeSpan timeout)
    {
        var startedAt = Stopwatch.GetTimestamp();
        while (true)
        {
            var pending = Snapshot();
            if (pending.Length == 0)
            {
                return;
            }

            var remaining = timeout - Stopwatch.GetElapsedTime(startedAt);
            if (remaining <= TimeSpan.Zero) break;

            // A provider can fail with its own TimeoutException while the drain still
            // has time left. Wait on completion separately from the batch's result.
            var batch = Task.WhenAll(pending);
            try
            {
                await ObserveCompletionAsync(batch).WaitAsync(remaining).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                break;
            }

            if (batch.Exception?.InnerException is { } failure)
            {
                logger.WriteSafely(AppLogLevel.Warning, "UI", "A background UI operation failed during shutdown.", failure);
            }
            ForgetCompleted(pending);
        }

        logger.WriteSafely(AppLogLevel.Warning, "UI", "Timed out waiting for background UI operations during shutdown.");
    }

    private Task[] Snapshot()
    {
        lock (gate) return operations.ToArray();
    }

    private void ForgetCompleted(Task[] pending)
    {
        lock (gate)
        {
            foreach (var task in pending) operations.Remove(task);
        }
    }

    private static async Task ObserveCompletionAsync(Task batch)
    {
        try { await batch.ConfigureAwait(false); }
        catch { /* Canceled or failed operations are still finished for shutdown purposes. */ }
    }
}
