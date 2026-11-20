using System.Collections.ObjectModel;
using System.Windows.Threading;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> AutoVodDownloadTests { get; } =
    [
        .. AutoVodDownloadStatsTests,
        ("VOD auto-download: Twitch waits for completion, skips backlog, and remembers queued IDs", () => AutoDownloadCandidatesAsync(PlatformKind.Twitch)),
        ("VOD auto-download: Kick waits for completion, skips backlog, and remembers queued IDs", () => AutoDownloadCandidatesAsync(PlatformKind.Kick)),
        ("VOD auto-download: per-channel toggles synchronize cards and separate platforms", AutoDownloadTogglesAsync),
        ("VOD auto-download: paging and transient failures do not lose new broadcasts", AutoDownloadPagingAsync),
        ("VOD auto-download: paging advances through empty and unrelated results", AutoDownloadEmptyPagesAsync),
        ("VOD auto-download: disabling during a completion check prevents queuing", AutoDownloadDisableAsync),
        ("VOD auto-download: enabling a new channel during a check schedules another pass", AutoDownloadEnableDuringCheckAsync),
        ("VOD auto-download: closing cancels an in-flight completion check", AutoDownloadShutdownAsync),
        ("VOD auto-download: startup and timer work without browsing and stop on disposal", AutoDownloadTimerAsync),
        ("VOD auto-download: settings normalize and survive a disk round trip", AutoDownloadSettingsAsync),
        ("VOD auto-download: Twitch completion probe reads playlists without downloading media", () => AutoDownloadProbeAsync(PlatformKind.Twitch)),
        ("VOD auto-download: Kick completion probe reads playlists without downloading media", () => AutoDownloadProbeAsync(PlatformKind.Kick)),
        ("VOD auto-download: refreshed Kick sources are validated before resolution", AutoDownloadKickSourceValidationAsync),
        ("VOD auto-download: unavailable Kick metadata is read once before plugin fallback", AutoDownloadKickFallbackAsync),
        ("VOD auto-download: right-click menu toggles channel preferences in dark theme", () => AutoDownloadMenuAsync(AppTheme.Dark)),
        ("VOD auto-download: right-click menu toggles channel preferences in light theme", () => AutoDownloadMenuAsync(AppTheme.Light))
    ];

    private static MainViewModelDependencies AutoDownloadDependencies(AppSettings settings, CardDownloadTestService service,
        ITwitchVodService? twitch = null, IKickVodService? kick = null, TimeSpan? interval = null) => new()
        {
            Settings = settings,
            SettingsService = new FakeSettingsService(settings),
            StreamlinkService = new FakeStreamlinkService(),
            PlaybackFactory = new FakePlaybackEngineFactory(),
            ChatFactory = new FakeChatClientFactory(),
            Logger = new MemoryLogger(),
            Dispatch = action => Dispatcher.CurrentDispatcher.Invoke(action),
            VodDownloadService = service,
            TwitchVodService = twitch,
            KickVodService = kick,
            AutoVodDownloadRefreshInterval = interval ?? TimeSpan.Zero
        };

    private static VodDownloadsViewModel AutoDownloads(MainViewModelDependencies dependencies) =>
        new(dependencies, _ => { }, () => dependencies.Settings.Downloads.Quality,
            (_, _) => Task.CompletedTask, _ => Task.CompletedTask);

    private static VodViewModel AutoVod(PlatformKind platform, string id, DateTimeOffset started, string channel = "streamer") =>
        platform == PlatformKind.Twitch
            ? new(new TwitchVodItem(id, "stream", "owner", channel, channel, "Broadcast " + id, "",
                $"https://www.twitch.tv/videos/{id}", "", started, started, TimeSpan.FromHours(2), 1,
                TwitchVodTypeFilter.Archive), (_, _) => Task.CompletedTask)
            : new(new KickVodItem(id, "stream", id, channel, channel, "Broadcast " + id,
                $"https://kick.com/{channel}/videos/{id}", $"https://kick.com/{channel}/videos/{id}", "", "",
                started, started, TimeSpan.FromHours(2), 1), (_, _) => Task.CompletedTask);

    private static TwitchVodSearchResult AutoTwitchResult(params VodViewModel[] videos) => new(
        TwitchVodSearchStatus.Available, null, videos.Select(video => new TwitchVodItem(video.Id, "stream", "owner",
            video.Target.Channel, video.ChannelDisplayName, video.Title, "", video.Target.Url, "", video.Target.MediaPublishedAtUtc,
            video.Target.MediaPublishedAtUtc, video.Target.MediaDuration, 1, TwitchVodTypeFilter.Archive)).ToArray(), "", "Fixture");

    private static KickVodSearchResult AutoKickResult(params VodViewModel[] videos) => new(KickVodSearchStatus.Available,
        videos.Select(video => new KickVodItem(video.Id, "stream", video.Id, video.Target.Channel, video.ChannelDisplayName,
            video.Title, video.Target.Url, video.Target.Url, "", "", video.Target.MediaPublishedAtUtc,
            video.Target.MediaStartedAtUtc, video.Target.MediaDuration, 1)).ToArray(), "", "Fixture");

    private static Task AutoDownloadCandidatesAsync(PlatformKind platform) => TestSta.RunOffscreenAsync(async () =>
    {
        var enabled = DateTimeOffset.UtcNow.AddMinutes(-10);
        var old = AutoVod(platform, "100", enabled.AddDays(-2));
        var recording = AutoVod(platform, "101", enabled.AddMinutes(-30));
        var fresh = AutoVod(platform, "102", enabled.AddMinutes(1));
        var other = AutoVod(platform, "103", enabled.AddMinutes(1), "someoneelse");
        var finished = false;
        var probes = new List<string>();
        var service = new CardDownloadTestService
        {
            CompletionProbe = (request, _) =>
            {
                probes.Add(request.Target.MediaId);
                return Task.FromResult(request.Target.MediaId != recording.Id || finished);
            }
        };
        var settings = new AppSettings { StreamlinkPath = "test-streamlink.exe", CustomStreamlinkArguments = "--retry-streams 3" };
        settings.Downloads.Quality = "720p";
        settings.Downloads.AutoDownloadChannels = [new(platform, "streamer", enabled)];
        var dependencies = AutoDownloadDependencies(settings, service,
            new FakeTwitchVodService(AutoTwitchResult(old, recording, fresh, other)),
            new FakeKickVodService(AutoKickResult(old, recording, fresh, other)));
        await using (var downloads = AutoDownloads(dependencies))
        {
            await downloads.CheckAutoDownloadsAsync();
            Assert.Equal(fresh.Id, service.Requests.Single().Target.MediaId);
            Assert.Equal(false, probes.Contains(old.Id));
            Assert.Equal(false, probes.Contains(other.Id));
            Assert.Equal("720p", service.Requests.Single().Quality);
            Assert.Equal("test-streamlink.exe", service.Requests.Single().Options.StreamlinkPath);
            Assert.Equal("--retry-streams", service.Requests.Single().Options.CustomArguments[0]);
            finished = true;
            await downloads.CheckAutoDownloadsAsync();
            Assert.Equal(2, service.Requests.Count);
            await downloads.CheckAutoDownloadsAsync();
            Assert.Equal(2, service.Requests.Count);
        }
        var persisted = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        var restartedService = new CardDownloadTestService();
        await using var restored = AutoDownloads(dependencies with { Settings = persisted, VodDownloadService = restartedService });
        await restored.CheckAutoDownloadsAsync();
        Assert.Equal(0, restartedService.Requests.Count);
    });

    private static Task AutoDownloadTogglesAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new CardDownloadTestService();
        var settings = new AppSettings();
        await using var downloads = AutoDownloads(AutoDownloadDependencies(settings, service,
            new FakeTwitchVodService(AutoTwitchResult()), new FakeKickVodService(AutoKickResult())));
        var first = AutoVod(PlatformKind.Twitch, "101", DateTimeOffset.UtcNow);
        var same = AutoVod(PlatformKind.Twitch, "102", DateTimeOffset.UtcNow);
        var other = AutoVod(PlatformKind.Twitch, "103", DateTimeOffset.UtcNow, "someoneelse");
        var kick = AutoVod(PlatformKind.Kick, "104", DateTimeOffset.UtcNow);
        var cards = new ObservableCollection<VodViewModel> { first, same, other, kick };
        downloads.BindVodCards(cards);
        first.IsAutoDownloadEnabled = true;
        Assert.True(first.IsAutoDownloadEnabled && same.IsAutoDownloadEnabled);
        Assert.Equal(false, other.IsAutoDownloadEnabled || kick.IsAutoDownloadEnabled);
        kick.IsAutoDownloadEnabled = true;
        same.IsAutoDownloadEnabled = false;
        Assert.Equal(false, first.IsAutoDownloadEnabled);
        Assert.True(kick.IsAutoDownloadEnabled);
        Assert.Equal(PlatformKind.Kick, settings.Downloads.AutoDownloadChannels.Single().Platform);
        var later = AutoVod(PlatformKind.Kick, "105", DateTimeOffset.UtcNow);
        cards.Add(later);
        Assert.True(later.IsAutoDownloadEnabled);
    });

    private static Task AutoDownloadPagingAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var enabled = DateTimeOffset.UtcNow.AddDays(-1);
        var first = AutoVod(PlatformKind.Twitch, "101", enabled.AddHours(1));
        var second = AutoVod(PlatformKind.Twitch, "102", enabled.AddHours(3));
        var page = AutoTwitchResult(first) with { NextCursor = "next" };
        var twitch = new FakeTwitchVodService(page, AutoTwitchResult(second), page, AutoTwitchResult(second), page, AutoTwitchResult(second));
        var settings = new AppSettings();
        settings.Downloads.AutoDownloadChannels = [new(PlatformKind.Twitch, "streamer", enabled)];
        var service = new CardDownloadTestService { EnqueueFailure = new IOException("Temporary write failure") };
        await using var downloads = AutoDownloads(AutoDownloadDependencies(settings, service, twitch));
        await downloads.CheckAutoDownloadsAsync();
        Assert.Equal(0, settings.Downloads.AutoDownloadChannels.Single().QueuedVideoIds.Count);
        service.EnqueueFailure = null;
        var failedProbe = false;
        service.CompletionProbe = (request, _) =>
        {
            if (request.Target.MediaId != second.Id || failedProbe) return Task.FromResult(true);
            failedProbe = true;
            return Task.FromException<bool>(new IOException("Temporary playlist failure"));
        };
        await downloads.CheckAutoDownloadsAsync();
        Assert.Equal(1, settings.Downloads.AutoDownloadChannels.Single().QueuedVideoIds.Count);
        await downloads.CheckAutoDownloadsAsync();
        Assert.Equal(2, settings.Downloads.AutoDownloadChannels.Single().QueuedVideoIds.Count);
        Assert.Equal("next", twitch.Requests[1].Cursor);
        Assert.True(twitch.Requests.All(request => request.Type == TwitchVodTypeFilter.Archive));
    });

    private static Task AutoDownloadEmptyPagesAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var enabled = DateTimeOffset.UtcNow.AddDays(-1);
        var unrelated = AutoVod(PlatformKind.Twitch, "101", enabled.AddHours(1), "anotherchannel");
        var newBroadcast = AutoVod(PlatformKind.Twitch, "102", enabled.AddHours(1));
        var twitch = new FakeTwitchVodService(
            AutoTwitchResult() with { NextCursor = "second" },
            AutoTwitchResult(unrelated) with { NextCursor = "third" },
            AutoTwitchResult(newBroadcast));
        var settings = new AppSettings();
        settings.Downloads.AutoDownloadChannels = [new(PlatformKind.Twitch, "streamer", enabled)];
        var service = new CardDownloadTestService();
        await using var downloads = AutoDownloads(AutoDownloadDependencies(settings, service, twitch));

        await downloads.CheckAutoDownloadsAsync();

        Assert.Equal(3, twitch.CallCount);
        Assert.Equal(newBroadcast.Id, service.Requests.Single().Target.MediaId);
    });

    private static Task AutoDownloadDisableAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = new AppSettings();
        var enabled = DateTimeOffset.UtcNow.AddHours(-1);
        settings.Downloads.AutoDownloadChannels = [new(PlatformKind.Twitch, "streamer", enabled)];
        var video = AutoVod(PlatformKind.Twitch, "101", enabled.AddMinutes(1));
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new CardDownloadTestService { CompletionProbe = (_, token) => release.Task.WaitAsync(token) };
        await using var downloads = AutoDownloads(AutoDownloadDependencies(settings, service, new FakeTwitchVodService(AutoTwitchResult(video))));
        downloads.BindVodCards([video]);
        var pending = downloads.CheckAutoDownloadsAsync();
        Assert.True(ReferenceEquals(pending, downloads.CheckAutoDownloadsAsync()));
        video.IsAutoDownloadEnabled = false;
        release.SetResult(true);
        await pending;
        Assert.Equal(0, service.Requests.Count);
    });

    private static Task AutoDownloadEnableDuringCheckAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var now = DateTimeOffset.UtcNow;
        var first = AutoVod(PlatformKind.Twitch, "101", now.AddMinutes(-10));
        var other = AutoVod(PlatformKind.Twitch, "102", now.AddMinutes(-10), "anotherchannel");
        var twitch = new FakeTwitchVodService(AutoTwitchResult(first), AutoTwitchResult(first), AutoTwitchResult(other));
        var settings = new AppSettings();
        settings.Downloads.AutoDownloadChannels = [new(PlatformKind.Twitch, "streamer", now.AddHours(-1))];
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new CardDownloadTestService
        {
            CompletionProbe = (request, token) =>
            {
                if (request.Target.MediaId != first.Id) return Task.FromResult(true);
                entered.TrySetResult();
                return release.Task.WaitAsync(token);
            }
        };
        await using var downloads = AutoDownloads(AutoDownloadDependencies(settings, service, twitch, interval: TimeSpan.Zero));
        downloads.BindVodCards([first, other]);
        downloads.Initialize();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var pending = downloads.CheckAutoDownloadsAsync();
        other.IsAutoDownloadEnabled = true;
        Assert.Equal(1, twitch.CallCount);

        release.SetResult(true);
        await pending;
        Assert.SequenceEqual(new[] { first.Id, other.Id }, service.Requests.Select(request => request.Target.MediaId));
    });

    private static Task AutoDownloadTimerAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var enabled = DateTimeOffset.UtcNow.AddHours(-1);
        var video = AutoVod(PlatformKind.Twitch, "101", enabled.AddMinutes(1));
        var settings = new AppSettings();
        settings.Downloads.AutoDownloadChannels = [new(PlatformKind.Twitch, "streamer", enabled)];
        var service = new CardDownloadTestService { CompletionProbe = (_, _) => Task.FromResult(false) };
        var twitch = new FakeTwitchVodService(AutoTwitchResult(video));
        var dispatcher = Dispatcher.CurrentDispatcher;
        var dependencies = AutoDownloadDependencies(settings, service, twitch, interval: TimeSpan.FromMilliseconds(30)) with
        { Dispatch = action => dispatcher.BeginInvoke(action) };
        await using var downloads = AutoDownloads(dependencies);
        downloads.Initialize();
        downloads.Initialize();
        await TestWait.UntilAsync(() => twitch.CallCount >= 2, TimeSpan.FromSeconds(3));
        service.CompletionProbe = (_, _) => Task.FromResult(true);
        await TestWait.UntilAsync(() => service.Requests.Count == 1, TimeSpan.FromSeconds(3));
        await downloads.DisposeAsync();
        var calls = twitch.CallCount;
        await Task.Delay(100);
        Assert.Equal(calls, twitch.CallCount);
        Assert.Equal(1, service.Requests.Count);
    });

    private static Task AutoDownloadShutdownAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var enabled = DateTimeOffset.UtcNow.AddHours(-1);
        var settings = new AppSettings();
        settings.Downloads.AutoDownloadChannels = [new(PlatformKind.Twitch, "streamer", enabled)];
        var video = AutoVod(PlatformKind.Twitch, "101", enabled.AddMinutes(1));
        var canceled = false;
        var service = new CardDownloadTestService
        {
            CompletionProbe = async (_, token) =>
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { canceled = true; throw; }
                return true;
            }
        };
        await using var downloads = AutoDownloads(AutoDownloadDependencies(settings, service, new FakeTwitchVodService(AutoTwitchResult(video))));
        var pending = downloads.CheckAutoDownloadsAsync();
        await downloads.DisposeAsync();
        await pending;
        Assert.True(canceled);
        Assert.Equal(0, service.Requests.Count);
    });

    private static async Task AutoDownloadSettingsAsync()
    {
        await using var fixture = new VodDownloadTestCatalog.DownloadFixture(PlatformKind.Twitch);
        var enabled = DateTimeOffset.UtcNow;
        var settings = new AppSettings();
        settings.Downloads.AutoDownloadChannels =
        [
            new(PlatformKind.Twitch, "Streamer", enabled),
            new(PlatformKind.Twitch, "streamer", enabled) { QueuedVideoIds = [" 101 ", "101", ""] },
            new(PlatformKind.Kick, "streamer", enabled),
            new((PlatformKind)99, "invalid", enabled),
            new(PlatformKind.Twitch, "bad/name", enabled)
        ];
        var service = new JsonSettingsService(Path.Combine(fixture.Root, "auto-download-settings.json"));
        await service.SaveAsync(settings);
        var restored = await service.LoadAsync();
        Assert.Equal(2, restored.Downloads.AutoDownloadChannels.Count);
        Assert.Equal("101", restored.Downloads.AutoDownloadChannels[0].QueuedVideoIds.Single());
        Assert.Equal(enabled, restored.Downloads.AutoDownloadChannels[0].EnabledAtUtc);
        Assert.Equal(0, new DownloadSettings().AutoDownloadChannels.Count);
    }

    private static async Task AutoDownloadProbeAsync(PlatformKind platform)
    {
        await using var fixture = new VodDownloadTestCatalog.DownloadFixture(platform);
        var probe = (IVodDownloadCompletionProbe)fixture.Service;
        var request = new VodDownloadRequest(fixture.Target, "best", fixture.Options);
        fixture.Handler.Put("/redirected/index.m3u8", Encoding.UTF8.GetBytes(
            VodDownloadTestCatalog.DownloadFixture.SimplePlaylist.Replace("#EXT-X-ENDLIST\n", "")));
        Assert.Equal(false, await probe.IsCompletedAsync(request));
        Assert.Equal(0, (await fixture.Service.GetDownloadsAsync()).Count);
        fixture.Handler.Put("/redirected/index.m3u8", Encoding.UTF8.GetBytes(VodDownloadTestCatalog.DownloadFixture.SimplePlaylist));
        Assert.True(await probe.IsCompletedAsync(request));
        Assert.True(fixture.Handler.Addresses.All(address => address.EndsWith(".m3u8", StringComparison.Ordinal)));
        fixture.Handler.Put("/redirected/index.m3u8", Encoding.UTF8.GetBytes(
            VodDownloadTestCatalog.DownloadFixture.SimplePlaylist.Replace("segment-a.ts", "https://evil.example/segment-a.ts")));
        await Assert.ThrowsAsync<InvalidDataException>(() => probe.IsCompletedAsync(request));
    }

    private static async Task AutoDownloadKickSourceValidationAsync()
    {
        await using var fixture = new VodDownloadTestCatalog.DownloadFixture(PlatformKind.Kick);
        fixture.KickResolver = (target, _) => Task.FromResult(target with { Url = "https://evil.example/video.m3u8" });
        await fixture.RestartAsync();
        Assert.True(await fixture.Service.IsCompletedAsync(new(fixture.Target, "best", fixture.Options)));
        Assert.Equal(fixture.Target.Url, fixture.Resolver.ResolveStreamUrlRequests.Single().Target.Url);
        Assert.True(fixture.Handler.Addresses.All(address => address.StartsWith("https://stream.kick.com/", StringComparison.Ordinal)));
    }

    private static async Task AutoDownloadKickFallbackAsync()
    {
        await using var fixture = new VodDownloadTestCatalog.DownloadFixture(PlatformKind.Kick);
        var metadataReads = 0;
        fixture.KickResolver = (_, _) =>
        {
            metadataReads++;
            throw new InvalidDataException("Metadata unavailable.");
        };
        await fixture.RestartAsync();
        Assert.True(await fixture.Service.IsCompletedAsync(new(fixture.Target, "best", fixture.Options)));
        Assert.Equal(1, metadataReads);
        Assert.Equal(fixture.Target.Url, fixture.Resolver.ResolveStreamUrlRequests.Single().Target.Url);
    }

    private static Task AutoDownloadMenuAsync(AppTheme theme) => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new CardDownloadTestService();
        var dispatcher = Dispatcher.CurrentDispatcher;
        await using var model = CreateDownloadCardsMain(service, [DownloadCardTarget(1), DownloadCardTarget(2)], action => dispatcher.BeginInvoke(action));
        await model.SearchTwitchVodsCommand.ExecuteAsync();
        StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(theme);
        var owner = new MainWindow { DataContext = model };
        RemoveMainWindowAutomaticStartup(owner);
        try
        {
            var cards = (ItemsControl)owner.FindName("VodCardsItemsControl");
            ((Panel)cards.Parent).Children.Remove(cards);
            var host = new Border { DataContext = model, Resources = owner.Resources, Child = cards, Width = 800 };
            using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Auto-download menu")
            { Width = 800, Height = 600, WindowStyle = 0 })
            { RootVisual = host };
            host.Measure(new Size(800, 600));
            host.Arrange(new Rect(0, 0, 800, 600));
            host.UpdateLayout();
            var button = FindVisualDescendants<Button>(host).First(item => ReferenceEquals(item.Command, model.TwitchVods[0].OpenCommand));
            var menu = button.ContextMenu!;
            menu.PlacementTarget = button;
            menu.IsOpen = true;
            dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            try
            {
                var item = menu.Items.OfType<MenuItem>().Single();
                Assert.True(item.IsCheckable && item.IsEnabled);
                Assert.Contains(model.TwitchVods[0].ChannelDisplayName, item.Header.ToString()!);
                item.SetCurrentValue(MenuItem.IsCheckedProperty, true);
                dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.True(model.TwitchVods.All(card => card.IsAutoDownloadEnabled));
                Assert.Equal(1, model.Settings.Downloads.AutoDownloadChannels.Count);
                SaveDownloadCardImage(menu, $"vod-auto-download-menu-{theme}");
                item.SetCurrentValue(MenuItem.IsCheckedProperty, false);
                dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.True(model.TwitchVods.All(card => !card.IsAutoDownloadEnabled));
                Assert.Equal(0, model.Settings.Downloads.AutoDownloadChannels.Count);
            }
            finally { menu.IsOpen = false; }
        }
        finally
        {
            owner.Close();
            StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(AppTheme.Dark);
        }
    });
}
