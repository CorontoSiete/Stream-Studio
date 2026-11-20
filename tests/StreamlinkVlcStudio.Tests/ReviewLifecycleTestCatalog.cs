using System.Windows.Threading;

internal static class ReviewLifecycleTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("background shutdown: a provider timeout does not skip work queued during draining", BackgroundProviderTimeoutAsync),
        ("review lifecycle: stopped overlay renderer completes selection requests", () => RendererShutdownAsync(false)),
        ("review lifecycle: overlay shutdown completes queued selection requests", () => RendererShutdownAsync(true)),
        ("review lifecycle: playback releases its native parking window on the owning thread", ParkingWindowDisposalAsync)
    ];

    private static async Task BackgroundProviderTimeoutAsync()
    {
        var logger = new MemoryLogger();
        var operations = new BackgroundOperationController(logger);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource<LogEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        logger.EntryWritten += (_, entry) => reported.TrySetResult(entry);
        operations.Track(first.Task);
        var draining = operations.DrainAsync(TimeSpan.FromSeconds(3));
        operations.Track(next.Task);
        try
        {
            var providerError = new TimeoutException("Provider timed out.");
            first.SetException(providerError);
            var entry = await reported.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(providerError, entry.Exception);
            Assert.True(!draining.IsCompleted);
        }
        finally
        {
            next.SetResult();
            await draining.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    private static Task ParkingWindowDisposalAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        using var surface = new ParkingVideoSurface();
        var handle = surface.Handle;
        Assert.True(IsWindow(handle));
        await new PlaybackResourceCoordinator(new MemoryLogger(), () => "parking window test").StopAsync(
            new FakePlaybackEngine(), TimeSpan.FromSeconds(2), surface, CancellationToken.None);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        Assert.Equal(IntPtr.Zero, surface.Handle);
        Assert.Equal(false, IsWindow(handle));
    });

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    private static async Task RendererShutdownAsync(bool queueBeforeShutdown)
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = await NativeReplayOverlayFrameScheduler.CreateAsync(
            new MemoryLogger(), _ => { }, dispatcherStopped: () => stopped.TrySetResult());
        var dispatcher = (Dispatcher)typeof(NativeReplayOverlayFrameScheduler)
            .GetField("dispatcher", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scheduler)!;
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? selection = null;
        Task<Uri?>? click = null;
        Task<string?>? text = null;
        try
        {
            if (queueBeforeShutdown)
            {
                _ = dispatcher.BeginInvoke(new Action(() =>
                {
                    started.TrySetResult();
                    release.Wait(TimeSpan.FromSeconds(5));
                }), DispatcherPriority.Send);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                RequestSelection();
            }

            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            release.Set();
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (!queueBeforeShutdown) RequestSelection();
            await Task.WhenAll(selection!, click!, text!).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(false, await selection!);
            Assert.True(await click! is null);
            Assert.True(await text! is null);
        }
        finally { release.Set(); }

        void RequestSelection()
        {
            selection = scheduler.HandleTextSelectionEventAsync(0, 0);
            click = scheduler.HandleTextClickEventAsync(0);
            text = scheduler.GetSelectedMessageBodyTextAsync();
        }
    }
}
