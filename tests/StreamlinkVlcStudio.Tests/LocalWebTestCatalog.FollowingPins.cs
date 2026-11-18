using System.Net.Http.Json;
using System.Windows.Threading;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    private static Task FollowingPinsAsync() => TestSta.RunOffscreenAsync(() => WithWorkspaceAsync(async fixture =>
    {
        var path = Path.Combine(fixture.Directory, "settings.json");
        await File.WriteAllTextAsync(path, """{"FollowedChannels":{"PinnedOfflineChannelKeys":["Twitch:creator","Kick:other-account"],"NotifyWhenLive":false}}""");
        var settingsService = new JsonSettingsService(path);
        var settings = await settingsService.LoadAsync();
        settings.Chat.ConnectAutomatically = false;
        var live = new FollowedLiveStream(PlatformKind.Kick, "live-creator", "Live creator", "", "", 5, "", null, false, "", "https://kick.com/live-creator");
        FollowedChannel[] offline =
        [
            new(PlatformKind.Twitch, "creator", "Twitch creator", "https://twitch.tv/creator"),
            new(PlatformKind.Kick, "creator", "Kick creator", "https://kick.com/creator")
        ];
        var following = new LastLiveFollowingService
        {
            Result = new([live], [], OfflineChannels: offline),
            Enrich = (channels, _) => Task.FromResult(channels)
        };
        var dispatcher = Dispatcher.CurrentDispatcher;
        var dependencies = fixture.Dependencies with
        {
            Following = following,
            GetSettingsAsync = token => dispatcher.InvokeAsync(() => settings, DispatcherPriority.Background, token).Task,
            ChangeSettingsAsync = (change, token) => dispatcher.InvokeAsync(async () =>
            {
                change(settings);
                await settingsService.SaveAsync(settings, token);
            }, DispatcherPriority.Background, token).Task.Unwrap()
        };
        using var certificate = Certificate();
        await using (var model = TestViewModels.CreateMain(settings, settingsService, new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(), new FakeChatClientFactory(), new MemoryLogger(),
            action => dispatcher.BeginInvoke(action), followedStreamsService: following))
        await using (var server = await Server(certificate, () => new LocalWebWorkspace(dependencies)))
        {
            await model.RefreshFollowedChannelsCommand.ExecuteAsync();
            var desktopTwitch = model.OfflineFollowedChannels.Single(card => card.Platform == PlatformKind.Twitch);
            var desktopKick = model.OfflineFollowedChannels.Single(card => card.Platform == PlatformKind.Kick);
            Assert.True(desktopTwitch.IsPinned);
            Assert.Equal(false, desktopKick.IsPinned);

            using var http = Client(certificate, server.Address);
            using (var anonymous = await Post(http, "following/pins", new { })) Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            var first = await Login(http);
            await server.Users.AddAsync("other-viewer", "Other viewer password 123!");
            var second = await Login(http, "other-viewer", "Other viewer password 123!");
            var initial = await ReadFollowingPins(http, first, "following");
            var twitchKey = initial.GetProperty("offline")[0].GetProperty("key").GetString()!;
            var kickKey = initial.GetProperty("offline")[1].GetProperty("key").GetString()!;
            var liveKey = initial.GetProperty("items")[0].GetProperty("key").GetString()!;
            Assert.SequenceEqual(new[] { "Twitch:creator" }, PinsIn(initial));
            var secondInitial = await ReadFollowingPins(http, second, "following");
            var secondTwitchKey = secondInitial.GetProperty("offline")[0].GetProperty("key").GetString()!;
            Assert.SequenceEqual(new[] { "Twitch:creator" }, PinsIn(secondInitial));

            using (var denied = await Post(http, "following/pin", new { key = kickKey, pinned = true }, first.Cookie))
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using (var denied = await Post(http, "following/pin", new { key = kickKey, pinned = true }, second.Cookie, second.Csrf))
                Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
            using (var denied = await Post(http, "following/pin", new { key = liveKey, pinned = true }, first.Cookie, first.Csrf))
                Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
            using (var denied = await Post(http, "following/pin", new { key = twitchKey }, first.Cookie, first.Csrf))
                Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);

            desktopTwitch.TogglePinCommand.Execute(null);
            Assert.SequenceEqual(Array.Empty<string>(), PinsIn(await ReadFollowingPins(http, first)));
            Assert.SequenceEqual(Array.Empty<string>(), PinsIn(await ReadFollowingPins(http, second)));
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var pin = await Post(http, "following/pin", new { key = kickKey, pinned = true }, first.Cookie, first.Csrf);
                Assert.Equal(HttpStatusCode.OK, pin.StatusCode);
                Assert.SequenceEqual(new[] { "Kick:creator" }, PinsIn(await pin.Content.ReadFromJsonAsync<JsonElement>()));
            }
            Assert.True(ReferenceEquals(desktopKick, model.OfflineFollowedChannels.First()));
            Assert.True(desktopKick.IsPinned);
            Assert.Equal(false, desktopTwitch.IsPinned);
            Assert.SequenceEqual(new[] { "Kick:creator" }, PinsIn(await ReadFollowingPins(http, second)));
            Assert.SequenceEqual(new[] { "Kick:other-account", "Kick:creator" },
                (await new JsonSettingsService(path).LoadAsync()).FollowedChannels.PinnedOfflineChannelKeys);

            var browserEdits = await Task.WhenAll(
                Post(http, "following/pin", new { key = kickKey, pinned = false }, first.Cookie, first.Csrf),
                Post(http, "following/pin", new { key = secondTwitchKey, pinned = true }, second.Cookie, second.Csrf));
            foreach (var edit in browserEdits)
                using (edit) Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
            Assert.True(desktopTwitch.IsPinned);
            Assert.Equal(false, desktopKick.IsPinned);
            Assert.SequenceEqual(new[] { "Twitch:creator" }, PinsIn(await ReadFollowingPins(http, first)));
            desktopTwitch.TogglePinCommand.Execute(null);
            using (var pin = await Post(http, "following/pin", new { key = kickKey, pinned = true }, first.Cookie, first.Csrf))
                Assert.Equal(HttpStatusCode.OK, pin.StatusCode);

            var nowLive = live with { Channel = offline[1].Channel, DisplayName = offline[1].DisplayName, Url = offline[1].Url };
            following.Result = new([live, nowLive], [], OfflineChannels: [offline[0]]);
            var liveSnapshot = await ReadFollowingPins(http, first, "following");
            Assert.Equal(kickKey, liveSnapshot.GetProperty("items")[1].GetProperty("key").GetString());
            Assert.SequenceEqual(Array.Empty<string>(), PinsIn(liveSnapshot));
            using (var denied = await Post(http, "following/pin", new { key = kickKey, pinned = false }, first.Cookie, first.Csrf))
                Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
            following.Result = new([live], [], OfflineChannels: offline);
            Assert.SequenceEqual(new[] { "Kick:creator" }, PinsIn(await ReadFollowingPins(http, first, "following")));
        }

        settingsService = new JsonSettingsService(path);
        settings = await settingsService.LoadAsync();
        Assert.Equal(false, settings.FollowedChannels.NotifyWhenLive);
        await using var restarted = await Server(certificate, () => new LocalWebWorkspace(dependencies));
        using var restartedHttp = Client(certificate, restarted.Address);
        var signedIn = await Login(restartedHttp);
        var restored = await ReadFollowingPins(restartedHttp, signedIn, "following");
        Assert.SequenceEqual(new[] { "Kick:creator" }, PinsIn(restored));
        using var unpin = await Post(restartedHttp, "following/pin",
            new { key = restored.GetProperty("offline")[1].GetProperty("key").GetString(), pinned = false },
            signedIn.Cookie, signedIn.Csrf);
        Assert.Equal(HttpStatusCode.OK, unpin.StatusCode);
        Assert.SequenceEqual(new[] { "Kick:other-account" },
            (await new JsonSettingsService(path).LoadAsync()).FollowedChannels.PinnedOfflineChannelKeys);
    }));

    private static async Task<JsonElement> ReadFollowingPins(HttpClient http, (string Cookie, string Csrf) auth, string action = "following/pins")
    {
        using var response = await Post(http, action, new { }, auth.Cookie, auth.Csrf);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task FollowingPinStatusChangesAsync() => TestSta.RunOffscreenAsync(() => WithWorkspaceAsync(async fixture =>
    {
        var channels = new[] { "alpha", "bravo", "charlie", "delta" }
            .Select(name => new FollowedChannel(PlatformKind.Twitch, name, name, "https://twitch.tv/" + name)).ToArray();
        var keys = channels.Select(channel => channel.Target.StateKey).ToList();
        fixture.Settings.FollowedChannels.PinnedOfflineChannelKeys = keys;
        fixture.Settings.Chat.ConnectAutomatically = false;
        var startedHistory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHistory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var following = new LastLiveFollowingService
        {
            Result = new(channels[2..].Select(channel => new FollowedLiveStream(channel.Platform, channel.Channel,
                channel.DisplayName, "", "", 100, "", null, false, "", channel.Url)).ToArray(), [], OfflineChannels: channels[..2]),
            Enrich = async (offline, token) =>
            {
                startedHistory.TrySetResult();
                await releaseHistory.Task.WaitAsync(token);
                return offline;
            }
        };
        var dispatcher = Dispatcher.CurrentDispatcher;
        await using var model = TestViewModels.CreateMain(fixture.Settings, new FakeSettingsService(fixture.Settings),
            new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(), new MemoryLogger(),
            action => dispatcher.BeginInvoke(action), followedStreamsService: following);
        await using var workspace = new LocalWebWorkspace(fixture.Dependencies with
        {
            GetDesktopFollowingAsync = (refresh, token) => dispatcher.InvokeAsync(
                () => model.GetWebFollowingAsync(refresh, token), DispatcherPriority.Background, token).Task.Unwrap(),
            Following = following
        });
        JsonElement initial;
        try
        {
            initial = await Call(workspace, "following", new { }).WaitAsync(TimeSpan.FromSeconds(3));
            await startedHistory.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.SequenceEqual(keys.Take(2), PinsIn(initial));
            Assert.Equal(2, model.OfflineFollowedChannels.Count(card => card.IsPinned));
        }
        finally
        {
            releaseHistory.TrySetResult();
            await model.RefreshFollowedChannelsCommand.ExecuteAsync();
        }

        var revision = initial.GetProperty("revision").GetInt64();
        following.Result = new([], [], OfflineChannels: channels);
        await model.RefreshFollowedChannelsCommand.ExecuteAsync();
        Assert.Equal(4, model.OfflineFollowedChannels.Count(card => card.IsPinned));
        var synced = await Call(workspace, "following/pins", new { revision });
        Assert.SequenceEqual(keys, PinsIn(synced));
        Assert.Equal(0, synced.GetProperty("items").GetArrayLength());
        Assert.Equal(4, synced.GetProperty("offline").GetArrayLength());
        Assert.Equal(initial.GetProperty("items")[0].GetProperty("key").GetString(),
            synced.GetProperty("offline")[2].GetProperty("key").GetString());

        var afterLostReply = await Call(workspace, "following/pins", new { revision });
        Assert.SequenceEqual(keys, PinsIn(afterLostReply));
        Assert.Equal(4, afterLostReply.GetProperty("offline").GetArrayLength());
        var acknowledged = await Call(workspace, "following/pins", new { revision = synced.GetProperty("revision").GetInt64() });
        Assert.SequenceEqual(keys, PinsIn(acknowledged));
        Assert.Equal(false, acknowledged.TryGetProperty("offline", out _));

        var charlieKey = synced.GetProperty("offline")[2].GetProperty("key").GetString();
        var unpinned = await Call(workspace, "following/pin", new { key = charlieKey, pinned = false });
        Assert.SequenceEqual(keys.Where(key => key != "Twitch:charlie"), PinsIn(unpinned));
        Assert.Equal(3, model.OfflineFollowedChannels.Count(card => card.IsPinned));
    }));

    private static IEnumerable<string> PinsIn(JsonElement payload) =>
        payload.GetProperty("pinnedKeys").EnumerateArray().Select(key => key.GetString()!);
}
