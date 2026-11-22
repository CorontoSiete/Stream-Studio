using StreamlinkVlcStudio.Infrastructure.Threading;

namespace StreamlinkVlcStudio.Workspace.ViewModels;

/// <summary>
/// Coordinates a replaceable debounced callback and the cancellation tokens for
/// the work started by that callback.  A token source is retained until the
/// corresponding operation calls <see cref="Complete"/>, so cancellation never
/// disposes a source while its consumer is still using it.
/// </summary>
internal sealed class CancellationDebounceCoordinator : IDisposable
{
    private readonly object gate = new();
    private readonly HashSet<CancellationTokenSource> operations = [];
    private CancellationTokenSource? currentOperation;
    private Timer? timer;
    private long scheduleVersion;
    private TaskCompletionSource? drained;
    private bool disposed;

    public bool Schedule(
        TimeSpan delay,
        Action callback,
        Action<Exception>? callbackErrorHandler = null,
        Action<Action>? dispatch = null)
    {
        ArgumentNullException.ThrowIfNull(callback);

        Timer? previousTimer;
        long scheduledVersion;
        var runImmediately = dispatch is not null && delay == TimeSpan.Zero;
        lock (gate)
        {
            if (disposed)
            {
                return false;
            }

            previousTimer = timer;
            scheduledVersion = scheduleVersion + 1;
            var nextTimer = runImmediately ? null : new Timer(
                _ => RunScheduledCallback(scheduledVersion, callback, callbackErrorHandler, dispatch),
                null,
                delay,
                Timeout.InfiniteTimeSpan);
            // An invalid delay must not invalidate the callback already scheduled.
            timer = nextTimer;
            scheduleVersion = scheduledVersion;
        }

        previousTimer?.Dispose();
        if (runImmediately) RunScheduledCallback(scheduledVersion, callback, callbackErrorHandler, dispatch);
        return true;
    }

    public void CancelScheduled()
    {
        Timer? timerToDispose;
        lock (gate)
        {
            timerToDispose = timer;
            timer = null;
            scheduleVersion++;
        }

        timerToDispose?.Dispose();
    }

    public CancellationTokenSource BeginOperation(CancellationToken lifetimeToken)
    {
        CancellationTokenSource? previousOperation;
        CancellationTokenSource nextOperation;

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            previousOperation = currentOperation;
            nextOperation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            currentOperation = nextOperation;
            operations.Add(nextOperation);
        }

        CancellationSourceCleanup.Cancel(previousOperation);
        return nextOperation;
    }

    public void CancelActive()
    {
        CancellationTokenSource? operation;
        lock (gate)
        {
            operation = currentOperation;
            currentOperation = null;
        }

        CancellationSourceCleanup.Cancel(operation);
    }

    public void Complete(CancellationTokenSource operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        TaskCompletionSource? completion = null;
        lock (gate)
        {
            if (!operations.Remove(operation))
            {
                return;
            }

            if (ReferenceEquals(currentOperation, operation))
            {
                currentOperation = null;
            }

            if (operations.Count == 0)
            {
                completion = drained;
                drained = null;
            }
        }

        operation.Dispose();
        completion?.TrySetResult();
    }

    public async Task DrainAsync(TimeSpan timeout)
    {
        Task? completionTask = null;
        lock (gate)
        {
            if (operations.Count > 0)
            {
                drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                completionTask = drained.Task;
            }
        }

        if (completionTask is not null)
        {
            await completionTask.WaitAsync(timeout);
        }
    }

    public void Dispose()
    {
        Timer? timerToDispose;
        CancellationTokenSource[] operationsToCancel;

        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            timerToDispose = timer;
            timer = null;
            currentOperation = null;
            operationsToCancel = operations.ToArray();
            scheduleVersion++;
        }

        timerToDispose?.Dispose();
        foreach (var operation in operationsToCancel)
        {
            CancellationSourceCleanup.Cancel(operation);
        }
    }

    private void RunScheduledCallback(
        long scheduledVersion,
        Action callback,
        Action<Exception>? callbackErrorHandler,
        Action<Action>? dispatch)
    {
        Timer? completedTimer;
        lock (gate)
        {
            if (disposed || scheduledVersion != scheduleVersion)
            {
                return;
            }

            completedTimer = timer;
            timer = null;
        }

        completedTimer?.Dispose();
        if (dispatch is null)
        {
            InvokeScheduledCallback(callback, callbackErrorHandler);
            return;
        }

        InvokeScheduledCallback(() => dispatch(RunIfCurrent), callbackErrorHandler);

        void RunIfCurrent()
        {
            // The timer may already have posted to the dispatcher when work is replaced or canceled.
            lock (gate)
            {
                if (disposed || scheduledVersion != scheduleVersion) return;
            }
            InvokeScheduledCallback(callback, callbackErrorHandler);
        }
    }

    private static void InvokeScheduledCallback(Action callback, Action<Exception>? callbackErrorHandler)
    {
        try
        {
            callback();
        }
        catch (Exception exception)
        {
            // Report failures from timer and dispatched callbacks through the caller's error path.
            if (callbackErrorHandler is null)
            {
                System.Diagnostics.Debug.WriteLine($"Debounced callback failed: {exception}");
                return;
            }

            try
            {
                callbackErrorHandler(exception);
            }
            catch (Exception handlerException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Debounced callback error handler failed: {handlerException}");
            }
        }
    }

}
