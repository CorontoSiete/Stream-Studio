using System.Windows.Automation;
using System.Windows.Threading;

internal static partial class ApplicationTestCatalog
{
    private static IReadOnlyList<(string Name, Func<Task> Run)> AutoVodDownloadStatsTests =>
    [
        ("VOD auto-download: completed totals and storage survive disabling, deletion, conversion, and restart", AutoDownloadStatsLifecycleAsync),
        ("VOD auto-download: earlier downloads migrate into statistics without counting manual VODs", AutoDownloadStatsMigrationAsync),
        ("VOD auto-download: automatic origin persists in library metadata without relabeling manual downloads", AutoDownloadOriginAsync),
        ("VOD auto-download: settings show channel indicators and live totals in dark wide and compact layouts", () => AutoDownloadStatsUiAsync(AppTheme.Dark)),
        ("VOD auto-download: settings show channel indicators and live totals in light wide and compact layouts", () => AutoDownloadStatsUiAsync(AppTheme.Light))
    ];

    private const long AutoStatsGiB = 1024L * 1024 * 1024;

    private static VodDownloadItem AutoStatsItem(PlatformKind platform, string id, long bytes, bool automatic = true,
        VodDownloadState state = VodDownloadState.Completed) => new(Guid.NewGuid(),
            AutoVod(platform, id, DateTimeOffset.UtcNow.AddHours(-3)).Target, "best", DateTimeOffset.UtcNow, state,
            BytesDownloaded: bytes, LocalMediaPath: state == VodDownloadState.Completed ? "offline.m3u8" : "",
            Revision: 1, IsAutomatic: automatic);

    private static Task AutoDownloadStatsLifecycleAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var twitch = AutoStatsItem(PlatformKind.Twitch, "101", 2 * AutoStatsGiB);
        var kick = AutoStatsItem(PlatformKind.Kick, "102", AutoStatsGiB);
        var pending = AutoStatsItem(PlatformKind.Twitch, "103", AutoStatsGiB / 4, state: VodDownloadState.Downloading);
        var manual = AutoStatsItem(PlatformKind.Twitch, "104", 10 * AutoStatsGiB, automatic: false);
        var failed = AutoStatsItem(PlatformKind.Twitch, "105", AutoStatsGiB, state: VodDownloadState.Failed);
        var service = new CardDownloadTestService(twitch, kick, pending, manual, failed);
        var settings = new AppSettings();
        settings.Downloads.AutoDownloadChannels = [new(PlatformKind.Twitch, "streamer", DateTimeOffset.UtcNow), new(PlatformKind.Kick, "streamer", DateTimeOffset.UtcNow)];
        var dependencies = AutoDownloadDependencies(settings, service);
        await using (var downloads = AutoDownloads(dependencies))
        {
            downloads.Initialize();
            Assert.Equal(2, downloads.AutoDownloadCompletedCount);
            Assert.Equal(3 * AutoStatsGiB, downloads.AutoDownloadStorageBytes);
            Assert.Equal(2, downloads.AutoDownloadChannels.Count);
            Assert.Equal(1, downloads.AutoDownloadChannels.Single(channel => channel.Platform == PlatformKind.Twitch).CompletedCount);
            pending = pending with { State = VodDownloadState.Completed, LocalMediaPath = "offline.m3u8", Revision = 2 };
            service.Publish(pending);
            service.Publish(pending);
            Assert.Equal(3, downloads.AutoDownloadCompletedCount);
            Assert.Equal(3 * AutoStatsGiB + AutoStatsGiB / 4, downloads.AutoDownloadStorageBytes);
            service.Publish(pending with { State = VodDownloadState.Downloading, Revision = 1 });
            Assert.Equal(3 * AutoStatsGiB + AutoStatsGiB / 4, downloads.AutoDownloadStorageBytes);

            service.Publish(twitch with { State = VodDownloadState.Converting, Revision = 2 });
            twitch = twitch with { Format = VodDownloadFormat.Mp4, BytesDownloaded = AutoStatsGiB, Revision = 3 };
            service.Publish(twitch);
            Assert.Equal(3, downloads.AutoDownloadCompletedCount);
            Assert.Equal(2 * AutoStatsGiB + AutoStatsGiB / 4, downloads.AutoDownloadStorageBytes);
            settings.Downloads.AutoDownloadChannels = [];
            Assert.Equal(false, downloads.HasAutoDownloadChannels);
            Assert.Equal(3, downloads.AutoDownloadCompletedCount);
            await downloads.VodDownloads.Single(card => card.Id == kick.Id).DeleteCommand.ExecuteAsync();
            Assert.Equal(3, downloads.AutoDownloadCompletedCount);
            Assert.Equal(AutoStatsGiB + AutoStatsGiB / 4, downloads.AutoDownloadStorageBytes);
        }

        var restoredSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        var restoredService = new CardDownloadTestService((await service.GetDownloadsAsync()).ToArray());
        await using var restored = AutoDownloads(dependencies with { Settings = restoredSettings, VodDownloadService = restoredService });
        restored.Initialize();
        Assert.Equal(3, restored.AutoDownloadCompletedCount);
        Assert.Equal(AutoStatsGiB + AutoStatsGiB / 4, restored.AutoDownloadStorageBytes);
        Assert.Equal(false, restored.HasAutoDownloadChannels);
    });

    private static Task AutoDownloadStatsMigrationAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var automatic = AutoStatsItem(PlatformKind.Twitch, "101", AutoStatsGiB, automatic: false);
        var manual = AutoStatsItem(PlatformKind.Twitch, "102", AutoStatsGiB, automatic: false);
        var settings = new AppSettings();
        settings.Downloads.AutoDownloadChannels = [new(PlatformKind.Twitch, "streamer", DateTimeOffset.UtcNow)
        { QueuedVideoIds = [automatic.Target.MediaId] }];
        var service = new CardDownloadTestService(automatic, manual);
        var dependencies = AutoDownloadDependencies(settings, service);
        await using (var downloads = AutoDownloads(dependencies))
        {
            downloads.Initialize();
            Assert.Equal(1, downloads.AutoDownloadCompletedCount);
            Assert.Equal(AutoStatsGiB, downloads.AutoDownloadStorageBytes);
            Assert.Equal(automatic.Id, settings.Downloads.AutomaticDownloads.Single().DownloadId);
            settings.Downloads.AutoDownloadChannels = [];
        }
        await using var restored = AutoDownloads(dependencies);
        restored.Initialize();
        Assert.Equal(1, restored.AutoDownloadCompletedCount);
        Assert.Equal(AutoStatsGiB, restored.AutoDownloadStorageBytes);
    });

    private static async Task AutoDownloadOriginAsync()
    {
        await using var fixture = new VodDownloadTestCatalog.DownloadFixture(PlatformKind.Twitch);
        var manual = await fixture.DownloadAsync();
        var duplicate = await fixture.Service.EnqueueAsync(new(fixture.Target, "best", fixture.Options, IsAutomatic: true));
        Assert.Equal(manual.Id, duplicate.Id);
        Assert.Equal(false, duplicate.IsAutomatic);
        var target = fixture.Target with { Channel = "streamer", MediaId = "67890", Url = "https://www.twitch.tv/videos/67890" };
        var automatic = await fixture.Service.EnqueueAsync(new(target, "best", fixture.Options, IsAutomatic: true));
        automatic = await fixture.WaitAsync(automatic.Id, VodDownloadState.Completed);
        Assert.True(automatic.IsAutomatic);
        await fixture.RestartAsync();
        var restored = await fixture.Service.GetDownloadsAsync();
        Assert.True(restored.Single(item => item.Id == automatic.Id).IsAutomatic);
        Assert.Equal(false, restored.Single(item => item.Id == manual.Id).IsAutomatic);
        var settings = new AppSettings();
        settings.Downloads.AutomaticDownloads = [new(automatic.Id, PlatformKind.Twitch, "Streamer", true),
            new(automatic.Id, PlatformKind.Twitch, "streamer"), new(Guid.Empty, PlatformKind.Twitch, "invalid")];
        var settingsService = new JsonSettingsService(Path.Combine(fixture.Root, "stats-settings.json"));
        await settingsService.SaveAsync(settings);
        var saved = await settingsService.LoadAsync();
        Assert.True(saved.Downloads.AutomaticDownloads.Single().Completed);
        Assert.Equal("streamer", saved.Downloads.AutomaticDownloads.Single().Channel);
    }

    private static Task AutoDownloadStatsUiAsync(AppTheme theme) => TestSta.RunOffscreenAsync(async () =>
    {
        var first = AutoStatsItem(PlatformKind.Twitch, "101", AutoStatsGiB);
        var second = AutoStatsItem(PlatformKind.Kick, "102", 2 * AutoStatsGiB);
        var service = new CardDownloadTestService(first, second);
        var dispatcher = Dispatcher.CurrentDispatcher;
        await using var model = CreateDownloadCardsMain(service, [first.Target], action => dispatcher.BeginInvoke(action));
        model.Settings.Downloads.AutoDownloadChannels = [new(PlatformKind.Twitch, "streamer", DateTimeOffset.UtcNow), new(PlatformKind.Kick, "streamer", DateTimeOffset.UtcNow)];
        model.Initialize();
        await TestWait.UntilAsync(() => model.AutoDownloadCompletedCount == 2, TimeSpan.FromSeconds(3));
        StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(theme);
        var window = new MainWindow { DataContext = model };
        RemoveMainWindowAutomaticStartup(window);
        SetMainWindowViewModel(window, model);
        try
        {
            model.SelectedSettingsCategory = SettingsCategory.Downloads;
            model.IsSettingsOpen = true;
            foreach (var width in new[] { 1320d, 440d })
            {
                LayoutStudioPolishWindow(window, new Size(width, 820));
                var page = (StackPanel)window.FindName("DownloadsSettingsPage");
                var channels = (ItemsControl)window.FindName("AutoVodDownloadChannelsList");
                var count = (TextBlock)window.FindName("AutoVodDownloadCompletedText");
                var storage = (TextBlock)window.FindName("AutoVodDownloadStorageText");
                Assert.Equal("2 VODs", count.Text);
                Assert.Equal(model.AutoDownloadStorageText, storage.Text);
                Assert.Equal(2, channels.Items.Count);
                Assert.Equal(2, FindVisualDescendants<Border>(channels).Count(border => AutomationProperties.GetName(border) == "Automatic VOD downloads enabled"));
                Assert.Equal(Visibility.Collapsed, ((TextBlock)window.FindName("AutoVodDownloadEmptyText")).Visibility);
                var card = (Border)window.FindName("AutoVodDownloadSettingsCard");
                foreach (var text in FindVisualDescendants<TextBlock>(card).Where(text => text.IsVisible && text.ActualWidth > 0))
                {
                    var bounds = text.TransformToAncestor(card).TransformBounds(new Rect(text.RenderSize));
                    Assert.True(bounds.Left >= -1 && bounds.Right <= card.ActualWidth + 1);
                }
                SaveResponsiveWindowImage(window, $"auto-vod-download-settings-{theme}-{width}");
            }

            model.Settings.Downloads.AutoDownloadChannels = [];
            service.Publish(first with { BytesDownloaded = AutoStatsGiB / 2, Format = VodDownloadFormat.Mp4, Revision = 2 });
            dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            LayoutStudioPolishWindow(window, new Size(440, 820));
            Assert.Equal(0, model.AutoDownloadChannels.Count);
            Assert.Equal(Visibility.Visible, ((TextBlock)window.FindName("AutoVodDownloadEmptyText")).Visibility);
            Assert.Equal(2, model.AutoDownloadCompletedCount);
            Assert.Equal(2 * AutoStatsGiB + AutoStatsGiB / 2, model.AutoDownloadStorageBytes);
            SaveResponsiveWindowImage(window, $"auto-vod-download-settings-{theme}-disabled");
        }
        finally
        {
            window.Close();
            StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(AppTheme.Dark);
        }
    });
}
