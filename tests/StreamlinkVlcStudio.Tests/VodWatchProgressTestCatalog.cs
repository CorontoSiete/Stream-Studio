internal static class VodWatchProgressTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("VOD watch progress: Twitch cards restore saved completion and paged progress", () => SavedCardsAsync(PlatformKind.Twitch)),
        ("VOD watch progress: Kick cards restore saved completion and paged progress", () => SavedCardsAsync(PlatformKind.Kick)),
        ("VOD watch progress: playback and backward seeks update existing cards until confirmed completion", PlaybackAsync),
        ("VOD watch progress: watched status survives rewatching and restarting without losing resume", RewatchAsync),
        ("VOD watch progress: delayed completion preserves the newest resume position and watched status", DelayedCompletionAsync),
        ("VOD watch progress: unreadable history does not prevent browsing", ReadFailureAsync),
        ("VOD watch progress: Twitch pagination reads only newly appended bookmarks", () => PageHistoryWorkAsync(PlatformKind.Twitch)),
        ("VOD watch progress: Kick pagination reads only newly appended bookmarks", () => PageHistoryWorkAsync(PlatformKind.Kick)),
        ("VOD watch progress: Twitch pagination retries earlier unavailable bookmarks", () => RetryPageHistoryAsync(PlatformKind.Twitch)),
        ("VOD watch progress: Kick pagination retries earlier unavailable bookmarks", () => RetryPageHistoryAsync(PlatformKind.Kick)),
        ("VOD watch progress: loaded resume positions remain available during disk work", LoadedHistoryAsync),
        ("VOD watch progress: canceled cached and initial history reads stay canceled", CancelHistoryAsync)
    ];

    internal static TwitchVodItem Video(string id, string title = "Recorded stream") => new(
        id, "stream", "broadcaster", "streamer", "Streamer", title, "",
        $"https://www.twitch.tv/videos/{id}", "", null, null, TimeSpan.FromHours(2),
        100, TwitchVodTypeFilter.Archive, TwitchVodAccessKind.Public);

    internal static VodPlaybackBookmark Bookmark(double seconds, bool completed = false) =>
        new(TimeSpan.FromSeconds(seconds), TimeSpan.FromHours(2), DateTimeOffset.UtcNow, completed);

    internal static MainViewModel CreateMain(IVodPlaybackHistory history, PlatformKind platform = PlatformKind.Twitch,
        Action<Action>? dispatch = null, string[][]? pages = null)
    {
        pages ??= [["1", "2", "3"], ["4"]];
        var settings = VodResumeTestCatalog.Settings();
        var main = new MainViewModel(new MainViewModelDependencies
        {
            Settings = settings,
            SettingsService = new FakeSettingsService(settings),
            StreamlinkService = new FakeStreamlinkService(),
            PlaybackFactory = new FakePlaybackEngineFactory(),
            ChatFactory = new FakeChatClientFactory(),
            Logger = new MemoryLogger(),
            Dispatch = dispatch ?? (action => action()),
            VodPlaybackHistory = history,
            TwitchVodSearchDebounceInterval = TimeSpan.FromHours(1),
            TwitchVodService = new FakeTwitchVodService(pages.Select((ids, index) => new TwitchVodSearchResult(
                TwitchVodSearchStatus.Available, null, ids.Select(id => Video(id)).ToArray(),
                index == 0 ? "next" : "", "page")).ToArray()),
            KickVodService = new FakeKickVodService(pages.Select((ids, index) => new KickVodSearchResult(
                KickVodSearchStatus.Available, ids.Select(id => new KickVodItem(
                    id, "stream", id, "streamer", "Streamer", "Recorded stream",
                    $"https://kick.com/streamer/videos/{id}", $"https://vod.kick.com/{id}.m3u8",
                    "", "", null, null, TimeSpan.FromHours(2), 100)).ToArray(),
                index == 0 ? "next" : "", "page")).ToArray())
        });
        if (platform == PlatformKind.Kick) main.SelectKickVodPlatformCommand.Execute(null);
        main.TwitchVodSearchText = "streamer";
        return main;
    }

    private static async Task SavedCardsAsync(PlatformKind platform)
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var history = files.Create();
        history.Remember(VodResumeTestCatalog.Target(platform, "1"), Bookmark(7198, completed: true));
        history.Remember(VodResumeTestCatalog.Target(platform, "2"), Bookmark(1800));
        history.Remember(VodResumeTestCatalog.Target(platform, "4"), Bookmark(5400));
        // A matching ID on the other platform must not label an unseen VOD.
        history.Remember(VodResumeTestCatalog.Target(platform == PlatformKind.Twitch ? PlatformKind.Kick : PlatformKind.Twitch, "3"),
            Bookmark(7200, completed: true));
        await history.SaveAsync();
        await using var main = CreateMain(files.Create(), platform);
        await main.SearchTwitchVodsCommand.ExecuteAsync();
        Assert.Equal(3, main.TwitchVods.Count);
        Assert.True(main.TwitchVods[0].IsWatched);
        Assert.Equal(false, main.TwitchVods[0].HasWatchProgress);
        Assert.True(main.TwitchVods[1].HasWatchProgress);
        Assert.Equal(25d, main.TwitchVods[1].WatchProgressPercent);
        Assert.Equal(false, main.TwitchVods[2].IsWatched);
        Assert.Equal(false, main.TwitchVods[2].HasWatchProgress);
        await main.LoadMoreTwitchVodsCommand.ExecuteAsync();
        Assert.Equal(75d, main.TwitchVods[3].WatchProgressPercent);
    }

    private static async Task PlaybackAsync()
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var history = files.Create();
        await using var main = CreateMain(history);
        await main.SearchTwitchVodsCommand.ExecuteAsync();
        var card = main.TwitchVods[0];
        var factory = new FakePlaybackEngineFactory();
        await using var tab = VodResumeTestCatalog.Tab(card.Target, history, factory);
        tab.SetVideoHandle(new IntPtr(42));
        await tab.StartAsync(VodResumeTestCatalog.Settings());
        await tab.SeekReplayAsync(TimeSpan.FromHours(1));
        Assert.Equal(50d, card.WatchProgressPercent);
        await tab.SeekReplayAsync(TimeSpan.FromMinutes(30));
        Assert.Equal(25d, card.WatchProgressPercent);
        await tab.SeekReplayAsync(TimeSpan.FromSeconds(7198));
        Assert.Equal(false, card.IsWatched);
        Assert.True(card.HasWatchProgress);
        factory.Engine!.PlaybackHealthOverride = () => new(1, PlaybackEngineState.Ended, 7200000, 0, 0, 0);
        tab.CaptureVodResumePosition(closing: true);
        Assert.True(card.IsWatched);
        Assert.Equal(false, card.HasWatchProgress);
        Assert.True(ReferenceEquals(card, main.TwitchVods[0]));
        // An old queued notification cannot regress completion.
        card.UpdateWatchProgress(Bookmark(60) with { UpdatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1) });
        Assert.True(card.IsWatched);
        var unseen = main.TwitchVods[2];
        await main.DisposeAsync();
        history.Remember(unseen.Target, Bookmark(60));
        Assert.Equal(false, unseen.HasWatchProgress);
    }

    private static async Task RewatchAsync()
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var target = VodResumeTestCatalog.Target(id: "1");
        // Load a legacy completed bookmark without the new HasBeenWatched field.
        await File.WriteAllTextAsync(files.Path,
            """[{"Platform":0,"MediaId":"1","Bookmark":{"Position":"01:59:58","Duration":"02:00:00","UpdatedAtUtc":"2026-01-01T00:00:00Z","Completed":true}}]""");
        var history = files.Create();
        await history.GetAsync(target);
        history.Remember(target, Bookmark(1800));
        await history.SaveAsync();
        var reloaded = files.Create();
        var bookmark = (await reloaded.GetAsync(target))!;
        Assert.True(bookmark.HasBeenWatched);
        Assert.Equal(false, bookmark.Completed);
        var card = new VodViewModel(Video("1"), (_, _) => Task.CompletedTask);
        card.UpdateWatchProgress(bookmark);
        Assert.True(card.IsWatched);
        var factory = new FakePlaybackEngineFactory();
        await using var tab = VodResumeTestCatalog.Tab(target, reloaded, factory);
        tab.SetVideoHandle(new IntPtr(42));
        await tab.StartAsync(VodResumeTestCatalog.Settings());
        Assert.Equal(TimeSpan.FromMinutes(30), factory.Engine!.LastStartPosition!.Value);
    }

    private static async Task DelayedCompletionAsync()
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var target = VodResumeTestCatalog.Target();
        var history = files.Create();
        var resumed = Bookmark(1800);
        history.Remember(target, resumed);
        var notifications = new List<VodPlaybackBookmark>();
        history.BookmarkChanged += (_, bookmark) => notifications.Add(bookmark);

        history.Remember(target, Bookmark(7200, completed: true) with
        {
            UpdatedAtUtc = resumed.UpdatedAtUtc.AddSeconds(-1)
        });
        var expected = resumed with { HasBeenWatched = true };
        Assert.Equal(expected, (await history.GetAsync(target))!);
        Assert.SequenceEqual(new[] { expected }, notifications);

        // A stale position without new completion information must remain a no-op.
        history.Remember(target, Bookmark(60) with { UpdatedAtUtc = resumed.UpdatedAtUtc.AddSeconds(-2) });
        Assert.Equal(1, notifications.Count);
        await history.SaveAsync();
        Assert.Equal(expected, (await files.Create().GetAsync(target))!);
        var card = new VodViewModel(Video("1"), (_, _) => Task.CompletedTask);
        card.UpdateWatchProgress(resumed);
        card.UpdateWatchProgress(notifications[0]);
        Assert.True(card.IsWatched);
    }

    private static async Task ReadFailureAsync()
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        await files.SeedAsync();
        await using var main = CreateMain(files.Create());
        using (var held = new FileStream(files.Path, FileMode.Open, FileAccess.Read, FileShare.None))
            await main.SearchTwitchVodsCommand.ExecuteAsync();
        Assert.Equal(3, main.TwitchVods.Count);
        Assert.Equal("page", main.TwitchVodStatus);
        Assert.Equal(false, main.IsTwitchVodSearchRunning);
    }

    private static async Task PageHistoryWorkAsync(PlatformKind platform)
    {
        var history = new CountingHistory();
        await using var main = CreateMain(history, platform, pages: [["1", "2", "3"], ["2", "4", "4"]]);
        await main.SearchTwitchVodsCommand.ExecuteAsync();
        Assert.SequenceEqual(new[] { "1", "2", "3" }, history.Reads);
        var original = main.TwitchVods.ToArray();
        history.Reads.Clear();
        await main.LoadMoreTwitchVodsCommand.ExecuteAsync();
        Assert.SequenceEqual(new[] { "4" }, history.Reads);
        Assert.SequenceEqual(original, main.TwitchVods.Take(3));
        Assert.Equal(25d, main.TwitchVods[3].WatchProgressPercent);
        history.Remember(original[0].Target, Bookmark(3600));
        Assert.Equal(50d, original[0].WatchProgressPercent);

        // An explicit refresh still reloads progress on the retained card.
        history.Reads.Clear();
        await main.SearchTwitchVodsCommand.ExecuteAsync();
        Assert.SequenceEqual(new[] { "2", "4" }, history.Reads);
    }

    private static SemaphoreSlim HistoryFileGate(JsonVodPlaybackHistory history) =>
        (SemaphoreSlim)typeof(JsonVodPlaybackHistory).GetField("fileGate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(history)!;

    private static async Task RetryPageHistoryAsync(PlatformKind platform)
    {
        var history = new CountingHistory { FailRead = true };
        await using var main = CreateMain(history, platform);
        await main.SearchTwitchVodsCommand.ExecuteAsync();
        Assert.Equal(3, main.TwitchVods.Count);
        Assert.True(main.TwitchVods.All(card => !card.HasWatchProgress));
        history.FailRead = false;
        history.Reads.Clear();
        await main.LoadMoreTwitchVodsCommand.ExecuteAsync();
        Assert.SequenceEqual(new[] { "1", "2", "3", "4" }, history.Reads);
        Assert.True(main.TwitchVods.All(card => card.WatchProgressPercent == 25));
    }

    private static async Task LoadedHistoryAsync()
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var history = await files.SeedAsync();
        var target = VodResumeTestCatalog.Target();
        var fileGate = HistoryFileGate(history);
        await fileGate.WaitAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            // Model a disk operation owning the file gate while playback advances.
            history.Remember(target, Bookmark(1800));
            Assert.Equal(TimeSpan.FromMinutes(30), (await history.GetAsync(target, timeout.Token))!.Position);
            Assert.Equal<VodPlaybackBookmark?>(null, await history.GetAsync(VodResumeTestCatalog.Target(id: "missing"), timeout.Token));
        }
        finally { fileGate.Release(); }
        await history.SaveAsync();
        Assert.Equal(TimeSpan.FromMinutes(30), (await files.Create().GetAsync(target))!.Position);
    }

    private static async Task CancelHistoryAsync()
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var history = files.Create();
        var target = VodResumeTestCatalog.Target();
        var fileGate = HistoryFileGate(history);
        await fileGate.WaitAsync();
        using var canceled = new CancellationTokenSource();
        var pending = history.GetAsync(target, canceled.Token);
        try
        {
            Assert.Equal(false, pending.IsCompleted);
            await canceled.CancelAsync();
            try { await pending; throw new InvalidOperationException("Initial history read ignored cancellation."); }
            catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
        }
        finally { fileGate.Release(); }
        await history.GetAsync(target);
        try { await history.GetAsync(target, canceled.Token); throw new InvalidOperationException("Cached history read ignored cancellation."); }
        catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
    }

    private sealed class CountingHistory : IVodPlaybackHistory
    {
        internal List<string> Reads { get; } = [];
        internal bool FailRead { get; set; }
        public event Action<StreamTarget, VodPlaybackBookmark>? BookmarkChanged;
        public Task<VodPlaybackBookmark?> GetAsync(StreamTarget target, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads.Add(target.MediaId);
            if (FailRead) throw new IOException("History is temporarily unavailable.");
            return Task.FromResult<VodPlaybackBookmark?>(Bookmark(1800));
        }
        public void Remember(StreamTarget target, VodPlaybackBookmark bookmark) => BookmarkChanged?.Invoke(target, bookmark);
        public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
