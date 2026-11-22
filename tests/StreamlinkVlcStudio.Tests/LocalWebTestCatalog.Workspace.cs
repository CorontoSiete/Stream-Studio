using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    private sealed record WorkspaceFixture(LocalWebDependencies Dependencies, AppSettings Settings,
        string Directory, List<FakeChatClientFactory> Chats);

    private static Task WorkspaceAsync() => WithWorkspaceAsync();
    private static Task ProfileImagesAsync() => WithWorkspaceAsync(f => VerifyProfileImagesAsync(f.Dependencies, f.Settings, f.Directory));
    private static Task FollowStatusAsync() => WithWorkspaceAsync(f => VerifyFollowStatusAsync(f.Dependencies));
    private static Task OverlayLayoutAsync() => WithWorkspaceAsync(f => VerifyOverlayLayoutAsync(f.Dependencies, f.Directory));
    private static Task CategoryViewersAsync() => WithWorkspaceAsync(f => VerifyCategoryViewersAsync(f.Dependencies));
    private static Task CategoryArtworkAsync() => WithWorkspaceAsync(f => VerifyCategoryArtworkAsync(f.Dependencies));
    private static Task CategoryArtworkLiveAsync() => WithWorkspaceAsync(f => VerifyCategoryArtworkLiveAsync(f.Dependencies));
    private static Task TwitchBadgeRecoveryAsync() => WithWorkspaceAsync(f => VerifyTwitchBadgeRecoveryAsync(f.Dependencies, f.Chats));
    private static Task FollowingLastLiveAsync() => WithWorkspaceAsync(f => VerifyFollowingLastLiveAsync(f.Dependencies));

    private static Task PageAvailabilityAsync() => WithWorkspaceAsync(async fixture =>
    {
        const string failure = "Provider temporarily unavailable.";
        var browse = (FakeBrowseService)fixture.Dependencies.Browse;
        browse.EnqueueCategories(BrowseResult<BrowseCategory>.Unavailable(failure));
        browse.EnqueueStreams(BrowseResult<BrowseLiveStream>.Unavailable(failure));
        await using var workspace = new LocalWebWorkspace(fixture.Dependencies with
        {
            TwitchVods = new FakeTwitchVodService(new TwitchVodSearchResult(TwitchVodSearchStatus.Unavailable, null, [], "", failure)),
            KickVods = new FakeKickVodService(new KickVodSearchResult(KickVodSearchStatus.Unavailable, [], "", failure))
        });

        var results = new[]
        {
            await Call(workspace, "categories", new { }),
            await Call(workspace, "streams", new { categoryId = "1" }),
            await Call(workspace, "vods", new { platform = "twitch", channel = "streamer" }),
            await Call(workspace, "vods", new { platform = "kick", channel = "streamer" })
        };
        foreach (var result in results)
        {
            Assert.Equal(false, result.GetProperty("available").GetBoolean());
            Assert.Equal(failure, result.GetProperty("message").GetString());
        }

        var empty = await Call(workspace, "categories", new { });
        Assert.True(empty.GetProperty("available").GetBoolean());
        Assert.Equal(0, empty.GetProperty("items").GetArrayLength());
    });

    private static Task PlayerReplacementAsync() => WithWorkspaceAsync(async fixture =>
    {
        var dependencies = fixture.Dependencies;
        await using var workspace = new LocalWebWorkspace(dependencies);
        // Use up the global budget so a replacement must reuse the original player's slot.
        for (var index = 1; index < 32; index++) await dependencies.PlaybackSlots.WaitAsync();
        var opened = await Call(workspace, "open", new { input = "https://twitch.tv/streamer" });
        var oldId = opened.GetProperty("id").GetString();
        Assert.Equal(0, dependencies.PlaybackSlots.CurrentCount);

        var streamlink = (FakeStreamlinkService)dependencies.Streamlink;
        streamlink.ResolveStreamUrlOverride = (_, _) => Task.FromException<StreamlinkResolvedUrl>(new IOException("Stream unavailable."));
        var request = new
        {
            key = opened.GetProperty("key").GetString(),
            quality = "720p",
            replacePlayer = oldId
        };
        await Assert.ThrowsAsync<IOException>(() => Call(workspace, "open", request));
        await Call(workspace, "chat", new { player = oldId, after = 0 });
        Assert.Equal(0, fixture.Chats.Single().Client.DisposeCount);
        Assert.Equal(0, dependencies.PlaybackSlots.CurrentCount);

        streamlink.ResolveStreamUrlOverride = null;
        var replaced = await Call(workspace, "open", request);
        Assert.Equal("720p", replaced.GetProperty("quality").GetString());
        Assert.Equal(1, fixture.Chats[0].Client.DisposeCount);
        await Assert.ThrowsAsync<ArgumentException>(() => Call(workspace, "chat", new { player = oldId, after = 0 }));
        Assert.Equal(0, dependencies.PlaybackSlots.CurrentCount);

        await Call(workspace, "close", new { player = replaced.GetProperty("id").GetString() });
        Assert.Equal(1, dependencies.PlaybackSlots.CurrentCount);
    });

    private static Task MediaDuringPlayerClosureAsync() => WithWorkspaceAsync(async fixture =>
    {
        await using var workspace = new LocalWebWorkspace(fixture.Dependencies);
        var opened = await Call(workspace, "open", new { input = "https://twitch.tv/streamer" });
        var id = opened.GetProperty("id").GetString()!;
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpRequestLifetimeFeature>(new ClosingRequestLifetime(() =>
            Task.Run(() => Call(workspace, "close", new { player = id })).GetAwaiter().GetResult()));

        await workspace.ServeMediaAsync(context, id, "profile");
        Assert.Equal(404, context.Response.StatusCode);
        Assert.Equal(32, fixture.Dependencies.PlaybackSlots.CurrentCount);
    });

    private sealed class ClosingRequestLifetime(Action closePlayer) : IHttpRequestLifetimeFeature
    {
        private Action? onRead = closePlayer;
        private CancellationToken requestAborted;

        public CancellationToken RequestAborted
        {
            get
            {
                Interlocked.Exchange(ref onRead, null)?.Invoke();
                return requestAborted;
            }
            set => requestAborted = value;
        }

        public void Abort() { }
    }

    private static async Task WithWorkspaceAsync(Func<WorkspaceFixture, Task>? verification = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "StreamStudioTests", "localweb-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var logger = new MemoryLogger();
        var settings = new AppSettings { StreamlinkPath = "streamlink.exe" };
        settings.Chat.TwitchOAuthToken = "secret-twitch-credential";
        settings.Chat.KickOAuthToken = "secret-kick-credential";
        var history = new JsonVodPlaybackHistory(Path.Combine(directory, "history.json"), logger);
        var chats = new List<FakeChatClientFactory>();
        var streamlink = new FakeStreamlinkService();
        var presentation = new WorkspaceChatPresentation();
        var archiveMessage = new ChatMessage(PlatformKind.Twitch, "streamer", "archive", "Wave", DateTimeOffset.UnixEpoch);
        var vodChat = new FakeVodChatProvider(VodChatFetchResult.Completed([new VodChatMessage(TimeSpan.FromSeconds(3), archiveMessage)], TimeSpan.FromSeconds(60)));
        await using var downloads = new VodDownloadService(Path.Combine(directory, "downloads"), streamlink, logger);
        var dependencies = new LocalWebDependencies
        {
            GetSettingsAsync = _ => Task.FromResult(settings),
            ChangeSettingsAsync = (change, _) => { change(settings); return Task.CompletedTask; },
            Streamlink = streamlink,
            Following = new FakeFollowedStreamsService(
                new(PlatformKind.Twitch, "streamer", "Streamer", "Twitch title", "Just Chatting", 100, "", null, false, "en", "https://twitch.tv/streamer"),
                new(PlatformKind.Kick, "kickstreamer", "Kickstreamer", "Kick title", "Music", 50, "", null, false, "en", "https://kick.com/kickstreamer")),
            Search = new FakeStreamSearchService(new StreamSearchResult(StreamSearchResultStatus.Available, [], "")),
            Browse = new FakeBrowseService(),
            TwitchVods = new FakeTwitchVodService(new TwitchVodSearchResult(TwitchVodSearchStatus.Available, null, [], "", "")),
            KickVods = new FakeKickVodService(new KickVodSearchResult(KickVodSearchStatus.Available, [], "", "")),
            Downloads = downloads,
            History = history,
            VodChat = vodChat,
            ChatPresentation = presentation,
            Replay = new FakeReplayResolver(ReplaySessionInfo.Unavailable(PlatformKind.Twitch, "streamer", "No archive")),
            SubOnlyVods = new FakeTwitchSubOnlyVodResolver(),
            ChatFactory = _ => { var factory = new FakeChatClientFactory(); chats.Add(factory); return factory; },
            CreateClipAsync = (_, _) => Task.FromResult<Uri?>(new("https://clips.twitch.tv/confirmed")),
            GetFollowStatusAsync = (_, _) => Task.FromResult<ChannelFollowResult?>(null),
            FollowAsync = (target, desired, _) => Task.FromResult<ChannelFollowResult?>(new(target.Platform, target.Channel, IsFollowing: desired))
        };
        try
        {
            if (verification is not null)
            {
                await verification(new WorkspaceFixture(dependencies, settings, directory, chats));
                return;
            }
            await VerifyVodRefreshAsync(dependencies);
            await VerifyVodLibraryAsync(dependencies);
            var users = await LocalWebUserStore.OpenAsync(Path.Combine(directory, "web-users.json"));
            var user = await users.AddAsync("watcher", "Watcher password 123!");
            await using var first = new LocalWebUserWorkspace(new LocalWebWorkspace(dependencies), users, user.Id);
            await using var second = new LocalWebUserWorkspace(new LocalWebWorkspace(dependencies), users, user.Id);
            var exposed = await Call(first, "settings", new { });
            Assert.Equal(false, exposed.ToString().Contains("secret-", StringComparison.Ordinal));
            Assert.Equal(false, exposed.ToString().Contains("streamlink.exe", StringComparison.Ordinal));
            var following = await Call(first, "following", new { });
            Assert.Equal("Just Chatting", following.GetProperty("items")[0].GetProperty("categoryName").GetString());
            Assert.Equal("Music", following.GetProperty("items")[1].GetProperty("categoryName").GetString());
            await Call(first, "settings/save", new { quality = "720p", lowLatency = false });
            Assert.Equal("720p", users.PreferencesFor(user.Id).Quality); Assert.Equal(false, users.PreferencesFor(user.Id).LowLatency);
            Assert.Equal("best", settings.DefaultQuality); Assert.Equal(true, settings.LowLatency);
            var opened = await Call(first, "open", new { input = "https://www.twitch.tv/videos/12345" });
            var id = opened.GetProperty("id").GetString()!;
            var archive = await Call(first, "chat", new { player = id, from = 0 });
            Assert.Equal("#123456", archive.GetProperty("messages")[0].GetProperty("message").GetProperty("color").GetString());
            presentation.Version = 1;
            var refreshedArchive = await Call(first, "chat/presentation", new { player = id, version = 0 });
            Assert.Equal(1, refreshedArchive.GetProperty("messages").GetArrayLength());
            Assert.Equal("https://example.com/animated.gif", refreshedArchive.GetProperty("messages")[0].GetProperty("emotes")[0].GetProperty("imageUrl").GetString());
            var unchanged = await Call(first, "chat/presentation", new { player = id, version = 1 });
            Assert.Equal(0, unchanged.GetProperty("messages").GetArrayLength());
            vodChat.Enqueue(VodChatFetchResult.Failed("Provider temporarily unavailable."));
            var retry = await Call(first, "chat", new { player = id, from = 60 });
            Assert.Equal("Failed", retry.GetProperty("outcome").GetString());
            Assert.Contains("Provider temporarily unavailable.", retry.GetProperty("status").GetString()!);
            Assert.Contains("Retrying", retry.GetProperty("status").GetString()!);
            Assert.Equal(0d, retry.GetProperty("through").GetDouble());
            vodChat.Enqueue(VodChatFetchResult.Unsupported("This video has no archived chat."));
            var unavailable = await Call(first, "chat", new { player = id, from = 60 });
            Assert.Equal("This video has no archived chat.", unavailable.GetProperty("status").GetString());
            vodChat.Enqueue(VodChatFetchResult.Completed(Enumerable.Range(0, 2101).Select(index => new VodChatMessage(TimeSpan.FromSeconds(index),
                archiveMessage with { MessageId = "archive-" + index })).ToArray(), TimeSpan.FromHours(1)));
            await Call(first, "chat", new { player = id, from = 60 });
            var boundedRefresh = await Call(first, "chat/presentation", new { player = id, version = 0 });
            Assert.Equal(2000, boundedRefresh.GetProperty("messages").GetArrayLength());
            Assert.True(boundedRefresh.GetProperty("messages").EnumerateArray().Any(m => m.GetProperty("messageId").GetString() == "archive-2100"));
            Assert.True(opened.GetProperty("url").GetString()!.StartsWith("/media/", StringComparison.Ordinal));
            Assert.Equal(false, opened.ToString().Contains("example.com", StringComparison.Ordinal));
            await Call(first, "progress", new { player = id, position = 120, duration = 600, ended = false });
            await Call(first, "close", new { player = id });
            var resumed = await Call(second, "open", new { input = "https://www.twitch.tv/videos/12345" });
            Assert.Equal(120d, resumed.GetProperty("resume").GetDouble());
            var resumedId = resumed.GetProperty("id").GetString();
            await Call(second, "progress", new { player = resumedId, position = 602, duration = 600, ended = true });
            var completed = await history.GetAsync(StreamInputParser.Parse("https://www.twitch.tv/videos/12345", PlatformKind.Twitch));
            Assert.True(completed is { Completed: true });
            Assert.Equal(TimeSpan.FromSeconds(600), completed!.Position);
            await Call(second, "close", new { player = resumedId });
            resumed = await Call(second, "open", new { input = "https://www.twitch.tv/videos/12345" });
            Assert.Equal(0d, resumed.GetProperty("resume").GetDouble());
            foreach (var invalid in new object[] { new { }, new { position = "120", duration = 600 }, new { position = 120, duration = "600" } })
            {
                var request = JsonSerializer.SerializeToNode(invalid)!.AsObject();
                request["player"] = resumed.GetProperty("id").GetString();
                await Assert.ThrowsAsync<ArgumentException>(() => Call(second, "progress", request));
            }
            await Assert.ThrowsAsync<ArgumentException>(() => Call(second, "progress", new { player = resumed.GetProperty("id").GetString(), position = 120, duration = 600, ended = "yes" }));
            await Assert.ThrowsAsync<ArgumentException>(() => Call(first, "downloads/remove", new { id = "invalid" }));
            await Assert.ThrowsAsync<ArgumentException>(() => Call(first, "open", new { downloadId = 1 }));
            await Assert.ThrowsAsync<ArgumentException>(() => Call(first, "open", new { input = 123 }));
            await Assert.ThrowsAsync<ArgumentException>(() => Call(first, "open", new { input = "https://twitch.tv/streamer", replacePlayer = 123 }));
            await Assert.ThrowsAsync<ArgumentException>(() => Call(second, "chat/presentation", new { player = resumed.GetProperty("id").GetString(), version = 1.5 }));
            await Assert.ThrowsAsync<ArgumentException>(() => Call(first, "progress", new { player = resumed.GetProperty("id").GetString(), position = 0, duration = 600 }));
            var context = new DefaultHttpContext();
            await first.ServeMediaAsync(context, resumed.GetProperty("id").GetString()!, "guess");
            Assert.Equal(404, context.Response.StatusCode);
            await Assert.ThrowsAsync<ArgumentException>(() => Call(first, "open", new { input = "http://127.0.0.1/private" }));
            await Assert.ThrowsAsync<ArgumentException>(() => Call(first, "open", new { input = "https://twitch.tv/streamer", quality = "--exec=command" }));
            Assert.Equal(31, dependencies.PlaybackSlots.CurrentCount); // one open player owns one of 32 slots
            var live = await Call(first, "open", new { input = "https://twitch.tv/streamer?ref=/videos/12345" });
            var liveId = live.GetProperty("id").GetString();
            await Assert.ThrowsAsync<ArgumentException>(() => Call(first, "chat", new { player = liveId, after = "1" }));
            chats.Single().Client.Receive(new ChatMessage(PlatformKind.Twitch, "streamer", "live", "Wave", DateTimeOffset.UnixEpoch, MessageId: "live-message"));
            var liveChat = await Call(first, "chat", new { player = liveId, after = 0 });
            Assert.Equal("https://example.com/animated.gif", liveChat.GetProperty("messages")[0].GetProperty("message").GetProperty("emotes")[0].GetProperty("imageUrl").GetString());
            var noDuplicate = await Call(first, "chat", new { player = liveId, after = 1 });
            Assert.Equal(0, noDuplicate.GetProperty("messages").GetArrayLength());
            await Assert.ThrowsAsync<ArgumentException>(() => Call(second, "chat/presentation", new { player = liveId, version = 0 }));
            Assert.Equal(0, settings.RecentStreams.Count);
            settings.RecentStreams =
            [
                new()
                {
                    Platform = PlatformKind.Twitch,
                    Channel = "Streamer",
                    DisplayName = "Streamer Display Name",
                    CategoryName = "Music",
                    ThumbnailUrl = "https://static-cdn.jtvnw.net/thumbnail.jpg",
                    ProfileImageUrl = "https://static-cdn.jtvnw.net/avatar.png",
                    LastWatchedAtUtc = DateTimeOffset.UnixEpoch
                }
            ];
            await Call(first, "started", new { player = liveId });
            var recent = settings.RecentStreams.Single();
            Assert.Equal("streamer", recent.Channel);
            Assert.Equal("Streamer Display Name", recent.DisplayName);
            Assert.Equal("Music", recent.CategoryName);
            Assert.Equal("https://static-cdn.jtvnw.net/thumbnail.jpg", recent.ThumbnailUrl);
            Assert.Equal("https://static-cdn.jtvnw.net/avatar.png", recent.ProfileImageUrl);
            Assert.Equal("720p", recent.LastQuality);
            Assert.True(recent.LastWatchedAtUtc > DateTimeOffset.UnixEpoch);
            await Call(first, "chat/send", new { player = liveId, message = "hello" });
            Assert.SequenceEqual(new[] { "hello" }, chats.Single().Client.SentMessages);
            var followed = await Call(first, "follow", new { player = liveId }); Assert.True(followed.GetProperty("following").GetBoolean());
            var clip = await Call(first, "clip", new { player = liveId }); Assert.Equal("https://clips.twitch.tv/confirmed", clip.GetProperty("url").GetString());
            settings.RecentStreams = Enumerable.Range(0, 5000).Select(index => new RecentStreamSettings
            {
                Platform = PlatformKind.Twitch,
                Channel = "channel" + index
            }).ToList();
            await Assert.ThrowsAsync<InvalidDataException>(() => Call(first, "recent", new { }));
            var slotsBeforeRejectedOpen = dependencies.PlaybackSlots.CurrentCount;
            await Assert.ThrowsAsync<InvalidDataException>(() => Call(first, "open", new { input = "https://www.twitch.tv/videos/99999" }));
            Assert.Equal(slotsBeforeRejectedOpen, dependencies.PlaybackSlots.CurrentCount);
            await first.DisposeAsync(); Assert.Equal(1, chats.Single().Client.DisposeCount);
            await second.DisposeAsync(); Assert.Equal(32, dependencies.PlaybackSlots.CurrentCount);
        }
        finally { await downloads.DisposeAsync(); Directory.Delete(directory, true); }
    }

    private static async Task<JsonElement> Call(ILocalWebWorkspace workspace, string action, object input) =>
        JsonSerializer.SerializeToElement(await workspace.InvokeAsync(action, JsonSerializer.SerializeToElement(input), CancellationToken.None),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    private sealed class WorkspaceChatPresentation : ILocalWebChatPresentation
    {
        public long Version { get; set; }
        public ChatMessage Present(ChatMessage message, ChatSettings settings) => message with
        {
            Color = "#123456",
            Emotes = Version == 0 ? [] : [new(0, 4, "Wave", "https://example.com/animated.gif")]
        };
        public Task ServeAssetAsync(HttpContext context, string assetId) { context.Response.StatusCode = 404; return Task.CompletedTask; }
    }
}
