using System.Diagnostics;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> LoadingPerformanceTests { get; } =
    [
        ("loading performance: shell construction and navigation measurements", MeasureShellLoadingAsync),
        ("loading performance: downloaded library initialization measurements", MeasureDownloadedLibraryAsync)
    ];

    internal static IReadOnlyList<(string Name, Func<Task> Run)> LoadingResponsivenessTests { get; } =
    [
        ("loading responsiveness: startup only lays out the loading shell before settings arrive", LoadingShellAsync),
        ("loading responsiveness: library migration publishes statistics once for the complete snapshot", BatchedLibraryStatsAsync),
        ("loading responsiveness: late library snapshots preserve newer revisions and deleted cards", LateLibrarySnapshotAsync)
    ];

    private static Task LoadingShellAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var window = new MainWindow();
        RemoveMainWindowAutomaticStartup(window);
        try
        {
            var root = (FrameworkElement)window.Content;
            void Layout()
            {
                root.Measure(new Size(1320, 820));
                root.Arrange(new Rect(0, 0, 1320, 820));
                root.UpdateLayout();
                root.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                root.UpdateLayout();
            }
            Layout();
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("SettingsPanel")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("PlaybackHost")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("TopControlsBar")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("UpdateBannerViewport")).Visibility);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("StartupLoadingMessage")).Visibility);
            SaveResponsiveWindowImage(window, "startup-loading");
            var settings = new AppSettings();
            await using var model = TestViewModels.CreateMain(settings, new FakeSettingsService(settings),
                new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(),
                new MemoryLogger(), action => action());
            window.DataContext = model;
            Layout();
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("PlaybackHost")).Visibility);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("TopControlsBar")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("StartupLoadingMessage")).Visibility);
            SaveResponsiveWindowImage(window, "startup-ready");
            model.ToggleSettingsCommand.Execute(null);
            Layout();
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("SettingsPanel")).Visibility);
            window.DataContext = null;
            Layout();
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("SettingsPanel")).Visibility);
        }
        finally { window.Close(); }
    });

    private static Task BatchedLibraryStatsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var items = Enumerable.Range(0, 100).Select(index => AutoStatsItem(PlatformKind.Twitch,
            (20000 + index).ToString(CultureInfo.InvariantCulture), 100)).ToArray();
        var settings = new AppSettings();
        var notifications = 0;
        settings.Downloads.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DownloadSettings.AutomaticDownloads)) notifications++;
        };
        await using var downloads = AutoDownloads(AutoDownloadDependencies(settings, new CardDownloadTestService(items)));
        downloads.Initialize();
        Assert.Equal(100, downloads.VodDownloads.Count);
        Assert.Equal(100, downloads.AutoDownloadCompletedCount);
        Assert.Equal(10000L, downloads.AutoDownloadStorageBytes);
        Assert.Equal(1, notifications);
        await downloads.ChangeDownloadDirectoryAsync(Path.GetTempPath());
        Assert.Equal(100, downloads.VodDownloads.Count);
        Assert.Equal(100, downloads.AutoDownloadCompletedCount);
        Assert.Equal(1, notifications);
    });

    private static Task LateLibrarySnapshotAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var older = AutoStatsItem(PlatformKind.Twitch, "30001", 100) with { Revision = 1 };
        var deleted = AutoStatsItem(PlatformKind.Kick, "30002", 200) with { CreatedAtUtc = older.CreatedAtUtc.AddDays(1) };
        var service = new DelayedLoadingLibraryService(older, deleted);
        var settings = new AppSettings();
        var dependencies = AutoDownloadDependencies(settings, new CardDownloadTestService()) with { VodDownloadService = service };
        await using var downloads = AutoDownloads(dependencies);
        downloads.Initialize();
        var newer = older with { BytesDownloaded = 400, Revision = 3, Format = VodDownloadFormat.Mp4 };
        try
        {
            service.Inner.Publish(newer);
            service.Inner.Publish(deleted);
            Assert.Equal(deleted.Id, downloads.VodDownloads[0].Id);
            await downloads.VodDownloads[0].DeleteCommand.ExecuteAsync();
            var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            downloads.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(VodDownloadsViewModel.HasVodDownloads)) applied.TrySetResult();
            };
            service.Release.SetResult([older, deleted]);
            await applied.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { service.Release.TrySetResult([older, deleted]); }
        Assert.Equal(1, downloads.VodDownloads.Count);
        Assert.Equal(newer, downloads.VodDownloads.Single().Item);
        Assert.Equal(2, downloads.AutoDownloadCompletedCount);
        Assert.Equal(400L, downloads.AutoDownloadStorageBytes);
    });

    private sealed class DelayedLoadingLibraryService(params VodDownloadItem[] items) : IVodDownloadService
    {
        internal CardDownloadTestService Inner { get; } = new(items);
        internal TaskCompletionSource<IReadOnlyList<VodDownloadItem>> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string DownloadDirectory => Inner.DownloadDirectory;
        public event Action<VodDownloadItem>? DownloadChanged { add => Inner.DownloadChanged += value; remove => Inner.DownloadChanged -= value; }
        public void SetBandwidthLimit(long value) => Inner.SetBandwidthLimit(value);
        public Task ChangeDownloadDirectoryAsync(string directory, CancellationToken token = default) => Inner.ChangeDownloadDirectoryAsync(directory, token);
        public Task ChangeDownloadFormatAsync(VodDownloadFormat format, VodDownloadOptions options, CancellationToken token = default) => Inner.ChangeDownloadFormatAsync(format, options, token);
        public Task<IReadOnlyList<VodDownloadItem>> GetDownloadsAsync(CancellationToken token = default) => Release.Task.WaitAsync(token);
        public Task<VodDownloadItem> EnqueueAsync(VodDownloadRequest request, CancellationToken token = default) => Inner.EnqueueAsync(request, token);
        public Task CancelAsync(Guid id, CancellationToken token = default) => Inner.CancelAsync(id, token);
        public Task RetryAsync(Guid id, VodDownloadOptions options, CancellationToken token = default) => Inner.RetryAsync(id, options, token);
        public Task<StreamTarget> GetOfflineTargetAsync(Guid id, CancellationToken token = default) => Inner.GetOfflineTargetAsync(id, token);
        public Task<IVodDownloadPlayback> AcquireOfflinePlaybackAsync(Guid id, CancellationToken token = default) => Inner.AcquireOfflinePlaybackAsync(id, token);
        public Task RemoveAsync(Guid id, CancellationToken token = default) => Inner.RemoveAsync(id, token);
        public ValueTask DisposeAsync() => Inner.DisposeAsync();
    }

    private static Task MeasureDownloadedLibraryAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        for (var trial = 0; trial < 4; trial++)
        {
            const int count = 1000;
            var items = Enumerable.Range(0, count).Select(index => AutoStatsItem(PlatformKind.Twitch,
                (10000 + index).ToString(CultureInfo.InvariantCulture), index + 1) with
            {
                Target = AutoVod(PlatformKind.Twitch, (10000 + index).ToString(CultureInfo.InvariantCulture),
                    DateTimeOffset.UtcNow.AddHours(-3), "channel" + index % 25).Target
            }).ToArray();
            var settings = new AppSettings();
            settings.Downloads.AutoDownloadChannels = Enumerable.Range(0, 25)
                .Select(index => new AutoDownloadChannelSettings(PlatformKind.Twitch, "channel" + index, DateTimeOffset.UtcNow)).ToList();
            var service = new CardDownloadTestService(items);
            await using var downloads = AutoDownloads(AutoDownloadDependencies(settings, service));
            var notifications = 0;
            settings.Downloads.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(DownloadSettings.AutomaticDownloads)) notifications++;
            };
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew();
            downloads.Initialize();
            Assert.Equal(count, downloads.VodDownloads.Count);
            Assert.Equal(count, downloads.AutoDownloadCompletedCount);
            Assert.Equal((long)count * (count + 1) / 2, downloads.AutoDownloadStorageBytes);
            Console.WriteLine(FormattableString.Invariant($"LIBRARY trial={trial} count={count} elapsed_ms={timer.Elapsed.TotalMilliseconds:F2} allocated_bytes={GC.GetAllocatedBytesForCurrentThread() - allocated} settings_notifications={notifications}"));
        }
    });

    private static Task MeasureShellLoadingAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        for (var trial = 0; trial < 5; trial++)
        {
            var settings = new AppSettings();
            settings.Chat.ConnectAutomatically = false;
            var timer = Stopwatch.StartNew();
            await using var model = TestViewModels.CreateMain(settings, new FakeSettingsService(settings),
                new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(),
                new MemoryLogger(), action => action());
            var modelMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            var window = new MainWindow();
            RemoveMainWindowAutomaticStartup(window);
            var constructionMs = timer.Elapsed.TotalMilliseconds;
            try
            {
                var root = (FrameworkElement)window.Content;
                void Layout()
                {
                    root.Measure(new Size(1320, 820));
                    root.Arrange(new Rect(0, 0, 1320, 820));
                    root.UpdateLayout();
                    root.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    root.UpdateLayout();
                }
                timer.Restart();
                Layout();
                var initialMs = timer.Elapsed.TotalMilliseconds;
                var initialSettingsVisibility = ((FrameworkElement)window.FindName("SettingsPanel")).Visibility;
                timer.Restart();
                window.DataContext = model;
                var bindMs = timer.Elapsed.TotalMilliseconds;
                timer.Restart();
                AddStudioCardFixtures(model);
                var cardsMs = timer.Elapsed.TotalMilliseconds;
                timer.Restart();
                Layout();
                var homeMs = timer.Elapsed.TotalMilliseconds;
                timer.Restart();
                model.ToggleSettingsCommand.Execute(null);
                Layout();
                var settingsMs = timer.Elapsed.TotalMilliseconds;
                timer.Restart();
                model.ShowAccountsSettingsCommand.Execute(null);
                Layout();
                var accountsMs = timer.Elapsed.TotalMilliseconds;
                timer.Restart();
                model.ShowHotkeysSettingsCommand.Execute(null);
                Layout();
                var hotkeysMs = timer.Elapsed.TotalMilliseconds;
                Console.WriteLine(FormattableString.Invariant($"SHELL trial={trial} model_ms={modelMs:F2} construct_ms={constructionMs:F2} initial_ms={initialMs:F2} initial_settings={initialSettingsVisibility} bind_ms={bindMs:F2} cards_ms={cardsMs:F2} home_ms={homeMs:F2} settings_ms={settingsMs:F2} accounts_ms={accountsMs:F2} hotkeys_ms={hotkeysMs:F2}"));
                Assert.True(model.IsHotkeysSettingsSelected);
            }
            finally { window.Close(); }
        }
    });
}
