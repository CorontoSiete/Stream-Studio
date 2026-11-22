namespace StreamlinkVlcStudio.Infrastructure.Threading;

/// <summary>
/// Bounds concurrent operations and closes admission on disposal. Active leases can finish;
/// queued callers are rejected, and the semaphore survives until every caller has released it.
/// </summary>
internal sealed class AsyncOperationGate(int maximumConcurrency = 1) : IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim slots = new(maximumConcurrency, maximumConcurrency);
    private readonly CancellationTokenSource closed = new();
    private int users;
    private bool disposed;

    internal async ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            users++;
        }

        var acquired = false;
        try
        {
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closed.Token);
            await slots.WaitAsync(waiting.Token).ConfigureAwait(false);
            acquired = true;
            lock (gate)
            {
                // Semaphore release can win its race with a cancellation callback. Do not
                // admit canceled work, or turn its cancellation into a disposal error.
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed, this);
            }

            return new Lease(this);
        }
        catch (Exception error)
        {
            var closedWhileWaiting = error is OperationCanceledException && closed.IsCancellationRequested;
            Release(acquired);
            if (error is OperationCanceledException) cancellationToken.ThrowIfCancellationRequested();
            if (closedWhileWaiting) throw new ObjectDisposedException(nameof(AsyncOperationGate));
            throw;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            // Keep the resources alive while cancellation wakes the queued waiters.
            users++;
        }

        try { closed.Cancel(); }
        finally { Release(acquired: false); }
    }

    private void Release(bool acquired)
    {
        lock (gate)
        {
            if (acquired) slots.Release();
            users--;
            if (disposed && users == 0)
            {
                slots.Dispose();
                closed.Dispose();
            }
        }
    }

    private sealed class Lease(AsyncOperationGate owner) : IDisposable
    {
        private AsyncOperationGate? owner = owner;

        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(acquired: true);
    }
}
