using Microsoft.AspNetCore.Http;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    private const string ArchiveFixture = "#EXTM3U\n#EXT-X-TARGETDURATION:10\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXTINF:10,\nfirst.ts?secret=private\n#EXTINF:10,\nsecond.ts\n#EXTINF:10,\nthird.ts\n#EXT-X-ENDLIST\n";

    private static Task LiveArchiveWorkspaceAsync() => WithWorkspaceAsync(async fixture =>
    {
        foreach (var platform in new[] { PlatformKind.Twitch, PlatformKind.Kick })
        {
            var replay = new FakeReplayResolver(new ReplaySessionInfo(platform, "streamer", "https://cdn.example/archive.m3u8?secret=private", "12345",
                DateTimeOffset.UtcNow.AddHours(-2), TimeSpan.FromHours(12), true, ""));
            var dependencies = fixture.Dependencies with
            {
                Replay = replay,
                CreateArchiveMedia = (id, uri) => ArchiveMedia(id, uri, _ => ArchiveFixture)
            };
            await using var workspace = new LocalWebWorkspace(dependencies);
            var live = await Call(workspace, "open", new { input = platform == PlatformKind.Twitch ? "https://twitch.tv/streamer" : "https://kick.com/streamer" });
            var id = live.GetProperty("id").GetString()!;
            var archive = await AwaitArchive(workspace, id);
            Assert.True(archive.GetProperty("available").GetBoolean());
            Assert.Equal(30d, archive.GetProperty("duration").GetDouble());
            Assert.Equal(false, archive.ToString().Contains("private", StringComparison.Ordinal));
            Assert.Equal(false, archive.ToString().Contains("cdn.example", StringComparison.Ordinal));
            for (var i = 0; i < 5; i++) await Call(workspace, "live/archive", new { player = id });
            Assert.Equal(1, replay.CallCount);
            var key = archive.GetProperty("key").GetString()!;
            var opened = await Call(workspace, "open", new { key, liveArchive = true, replacePlayer = id });
            Assert.True(opened.GetProperty("isLiveArchive").GetBoolean());
            Assert.True(opened.GetProperty("isVod").GetBoolean());
            var url = opened.GetProperty("url").GetString()!.Split('/');
            var context = new DefaultHttpContext();
            context.Request.Method = "GET"; context.Response.Body = new MemoryStream();
            await workspace.ServeMediaAsync(context, url[2], url[3]);
            context.Response.Body.Position = 0;
            var playlist = await new StreamReader(context.Response.Body).ReadToEndAsync();
            Assert.True(playlist.Contains("#EXT-X-PLAYLIST-TYPE:EVENT", StringComparison.Ordinal));
            Assert.Equal(false, playlist.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal));
            Assert.Equal(false, playlist.Contains("private", StringComparison.Ordinal));
            // Opening the same VOD normally must retain finite VOD semantics.
            var normal = await Call(workspace, "open", new { key });
            Assert.Equal(false, normal.GetProperty("isLiveArchive").GetBoolean());
        }
    });

    private static Task LiveArchiveCancellationAsync() => WithWorkspaceAsync(async fixture =>
    {
        var resolver = new BlockingReplayResolver(ReplaySessionInfo.Unavailable(PlatformKind.Twitch, "streamer", "No archive"), new TaskCompletionSource().Task);
        var dependencies = fixture.Dependencies with { Replay = resolver };
        await using var workspace = new LocalWebWorkspace(dependencies);
        var live = await Call(workspace, "open", new { input = "https://twitch.tv/streamer" });
        var id = live.GetProperty("id").GetString()!;
        var pending = await Call(workspace, "live/archive", new { player = id });
        Assert.True(pending.GetProperty("pending").GetBoolean());
        await resolver.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Call(workspace, "chat", new { player = id, after = 0 });
        await Call(workspace, "live/archive", new { player = id });
        Assert.Equal(1, resolver.CallCount);
        await Call(workspace, "close", new { player = id }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(32, dependencies.PlaybackSlots.CurrentCount);
    });

    private static async Task LiveArchivePlaylistsAsync()
    {
        var maximumArchive = "#EXTM3U\n#EXT-X-TARGETDURATION:3600\n" +
            string.Concat(Enumerable.Repeat("#EXTINF:3600,\nhour.ts\n", 14 * 24));
        using (var media = ArchiveMedia("test", new("https://cdn.example/archive.m3u8"), _ => maximumArchive))
            Assert.Equal(TimeSpan.FromDays(14).TotalSeconds, await media.ReadArchiveDurationAsync("best", default));
        using (var media = ArchiveMedia("test", new("https://cdn.example/master.m3u8"), uri => uri.AbsolutePath == "/master.m3u8"
            ? "#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"a\",NAME=\"English\",URI=\"audio.m3u8\"\n#EXT-X-STREAM-INF:BANDWIDTH=2000,RESOLUTION=1280x720,AUDIO=\"a\"\n720.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=4000,RESOLUTION=1920x1080,AUDIO=\"a\"\n1080.m3u8\n"
            : uri.AbsolutePath == "/720.m3u8" ? ArchiveFixture : ArchiveFixture.Replace("#EXTINF:10,", "#EXTINF:20,", StringComparison.Ordinal)))
        {
            Assert.Equal(30d, await media.ReadArchiveDurationAsync("720p", default));
            Assert.Equal(60d, await media.ReadArchiveDurationAsync("best", default));
            Assert.Equal(30d, await media.ReadArchiveDurationAsync("720p", default, selectVariant: true));
            var context = new DefaultHttpContext(); context.Request.Method = "GET"; context.Response.Body = new MemoryStream();
            await media.ServeAsync(context, media.Root);
            context.Response.Body.Position = 0;
            var selected = await new StreamReader(context.Response.Body).ReadToEndAsync();
            Assert.Equal(1, selected.Split('\n').Count(line => line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)));
            Assert.True(selected.Contains("RESOLUTION=1280x720", StringComparison.Ordinal));
            Assert.True(selected.Contains("#EXT-X-MEDIA:TYPE=AUDIO", StringComparison.Ordinal));
            Assert.Equal(false, selected.Contains("RESOLUTION=1920x1080", StringComparison.Ordinal));
            var unchanged = media.RewritePlaylist(ArchiveFixture, new("https://cdn.example/archive.m3u8"));
            Assert.True(unchanged.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal));
            media.GrowingArchive = true;
            var growing = media.RewritePlaylist(ArchiveFixture, new("https://cdn.example/archive.m3u8"));
            Assert.Equal(false, growing.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal));
            Assert.True(growing.Contains("#EXT-X-PLAYLIST-TYPE:EVENT", StringComparison.Ordinal));
        }
        foreach (var invalid in new[]
        {
            ArchiveFixture.Replace("#EXTM3U", "#EXTM3U-invalid", StringComparison.Ordinal),
            maximumArchive + "#EXTINF:1,\nextra.ts\n",
            ArchiveFixture.Replace("SEQUENCE:0", "SEQUENCE:100", StringComparison.Ordinal),
            ArchiveFixture.Replace("#EXT-X-ENDLIST", "#EXT-X-SKIP:SKIPPED-SEGMENTS=10", StringComparison.Ordinal),
            ArchiveFixture.Replace("#EXTINF:10,", "#EXTINF:NaN,", StringComparison.Ordinal),
            ArchiveFixture.Replace("third.ts", "", StringComparison.Ordinal),
            ArchiveFixture.Replace("third.ts", "https://127.0.0.1/private.ts", StringComparison.Ordinal)
        })
        {
            using var media = ArchiveMedia("test", new("https://cdn.example/archive.m3u8"), _ => invalid);
            var rejected = false;
            try { await media.ReadArchiveDurationAsync("best", default); }
            catch (InvalidDataException) { rejected = true; }
            Assert.True(rejected);
        }
    }

    private static Task LiveArchiveFallbackAsync() => WithWorkspaceAsync(async fixture =>
    {
        var remote = new Uri("https://d1example.cloudfront.net/broadcast/chunked/index-dvr.m3u8");
        var streamlink = new FakeStreamlinkService
        {
            ResolveStreamUrlOverride = (request, _) => request.Target.IsExplicitVod
                ? Task.FromException<StreamlinkResolvedUrl>(new InvalidOperationException("Use subscriber fallback"))
                : Task.FromResult(new StreamlinkResolvedUrl(new Uri("https://cdn.example/live.m3u8"), ""))
        };
        var subOnly = new FakeTwitchSubOnlyVodResolver
        {
            Override = (_, _) => Task.FromResult(new TwitchSubOnlyVodResolution(new Uri(@"C:\unused\snapshot.m3u8"), "chunked", "", RemotePlaylistUri: remote))
        };
        var requests = new ConcurrentBag<string>();
        var count = 3;
        var dependencies = fixture.Dependencies with
        {
            Streamlink = streamlink,
            SubOnlyVods = subOnly,
            Replay = new FakeReplayResolver(new ReplaySessionInfo(PlatformKind.Twitch, "streamer", "https://twitch.tv/videos/12345", "12345", DateTimeOffset.UtcNow.AddHours(-1), TimeSpan.FromHours(1), true, "")),
            CreateArchiveMedia = (id, uri) => new(id, uri, new HttpClient(new AsyncHttpMessageHandler((request, _) =>
            {
                requests.Add(request.RequestUri!.AbsolutePath);
                var segments = string.Concat(Enumerable.Range(0, Volatile.Read(ref count)).Select(i => $"#EXTINF:10,\n{i}-unmuted.ts\n"));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(request.RequestUri.AbsolutePath.EndsWith(".m3u8", StringComparison.Ordinal)
                        ? $"#EXTM3U\n#EXT-X-TARGETDURATION:10\n#EXT-X-MEDIA-SEQUENCE:0\n{segments}#EXT-X-ENDLIST\n" : "segment")
                });
            })))
        };
        await using var workspace = new LocalWebWorkspace(dependencies);
        var live = await Call(workspace, "open", new { input = "https://twitch.tv/streamer" });
        var archive = await AwaitArchive(workspace, live.GetProperty("id").GetString()!);
        Assert.True(archive.GetProperty("available").GetBoolean());
        Assert.Equal(30d, archive.GetProperty("duration").GetDouble());
        var opened = await Call(workspace, "open", new { key = archive.GetProperty("key").GetString(), liveArchive = true });
        var url = opened.GetProperty("url").GetString()!.Split('/');
        async Task<string> Fetch(string resource)
        {
            var context = new DefaultHttpContext(); context.Request.Method = "GET"; context.Response.Body = new MemoryStream();
            await workspace.ServeMediaAsync(context, url[2], resource);
            context.Response.Body.Position = 0;
            return await new StreamReader(context.Response.Body).ReadToEndAsync();
        }
        var first = await Fetch(url[3]);
        Assert.Equal(3, first.Split('\n').Count(line => line.StartsWith("#EXTINF:", StringComparison.Ordinal)));
        Volatile.Write(ref count, 4);
        var next = await Fetch(url[3]);
        Assert.Equal(4, next.Split('\n').Count(line => line.StartsWith("#EXTINF:", StringComparison.Ordinal)));
        var segment = next.Split('\n').First(line => line.StartsWith("/media/", StringComparison.Ordinal)).Split('/')[^1];
        await Fetch(segment);
        Assert.True(requests.Any(path => path.EndsWith("0-muted.ts", StringComparison.Ordinal)));
        Assert.Equal(false, requests.Any(path => path.EndsWith("0-unmuted.ts", StringComparison.Ordinal)));
    });

    private static LocalWebMedia ArchiveMedia(string id, Uri uri, Func<Uri, string> playlist) => new(id, uri,
        new HttpClient(new AsyncHttpMessageHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(playlist(request.RequestUri!))
        }))));

    private static async Task<JsonElement> AwaitArchive(LocalWebWorkspace workspace, string id)
    {
        for (var i = 0; i < 200; i++)
        {
            var result = await Call(workspace, "live/archive", new { player = id });
            if (!result.GetProperty("pending").GetBoolean()) return result;
            await Task.Delay(10);
        }
        throw new InvalidOperationException("Archive lookup did not finish.");
    }
}
