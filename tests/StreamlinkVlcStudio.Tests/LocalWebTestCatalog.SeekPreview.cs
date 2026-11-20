using System.Collections;
using Microsoft.AspNetCore.Http;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    private static Task LiveArchivePreviewAsync() => WithWorkspaceAsync(async fixture =>
    {
        foreach (var platform in new[] { PlatformKind.Twitch, PlatformKind.Kick })
        {
            var replay = new FakeReplayResolver(new ReplaySessionInfo(platform, "streamer", "https://cdn.example/archive.m3u8?secret=private", "12345",
                DateTimeOffset.UtcNow.AddHours(-2), TimeSpan.FromHours(12), true, ""));
            var handlers = new List<PreviewArchiveHandler>();
            var segmentCount = 3;
            var dependencies = fixture.Dependencies with
            {
                Replay = replay,
                CreateArchiveMedia = (id, uri) =>
                {
                    var handler = new PreviewArchiveHandler(() =>
                        "#EXTM3U\n#EXT-X-TARGETDURATION:10\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n" +
                        string.Concat(Enumerable.Range(0, segmentCount).Select(i => $"#EXTINF:10,\n{i}.ts?secret=private\n")) + "#EXT-X-ENDLIST\n");
                    handlers.Add(handler);
                    return new(id, uri, new HttpClient(handler));
                }
            };
            await using var workspace = new LocalWebWorkspace(dependencies);
            await using var otherWorkspace = new LocalWebWorkspace(dependencies);
            var input = platform == PlatformKind.Twitch ? "https://twitch.tv/streamer" : "https://kick.com/streamer";
            var live = await Call(workspace, "open", new { input });
            var id = live.GetProperty("id").GetString()!;
            var archive = await AwaitArchive(workspace, id);
            var url = archive.GetProperty("url").GetString()!;
            Assert.True(url.StartsWith($"/media/{id}/", StringComparison.Ordinal));
            Assert.Equal(false, url == live.GetProperty("url").GetString());
            Assert.Equal(false, handlers.Single().Disposed);
            Assert.Equal(31, dependencies.PlaybackSlots.CurrentCount); // No second server player.

            static async Task<(int Status, string Body)> Fetch(LocalWebWorkspace owner, string path)
            {
                var parts = path.Split('/');
                var context = new DefaultHttpContext();
                context.Request.Method = "GET"; context.Response.Body = new MemoryStream();
                await owner.ServeMediaAsync(context, parts[2], parts[3]);
                context.Response.Body.Position = 0;
                return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
            }
            var playlist = await Fetch(workspace, url);
            Assert.Equal(200, playlist.Status);
            Assert.True(playlist.Body.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal)); // Finite snapshot starts at broadcast zero.
            Assert.Equal(false, playlist.Body.Contains("private", StringComparison.Ordinal));
            Assert.Equal(false, playlist.Body.Contains("cdn.example", StringComparison.Ordinal));
            var segment = playlist.Body.Split('\n').First(line => line.StartsWith("/media/", StringComparison.Ordinal));
            Assert.Equal("preview segment", (await Fetch(workspace, segment)).Body);
            Assert.Equal(404, (await Fetch(otherWorkspace, url)).Status);
            var peer = await Call(workspace, "open", new { input });
            Assert.Equal(404, (await Fetch(workspace, url.Replace(id, peer.GetProperty("id").GetString()!, StringComparison.Ordinal))).Status);

            // Advance only the lookup deadline; all provider/proxy calls still run normally.
            var players = (IDictionary)typeof(LocalWebWorkspace).GetField("players", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(workspace)!;
            var player = players[id]!;
            void Refresh() => player.GetType().GetProperty("ArchiveRetryAt", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(player, DateTimeOffset.MinValue);
            segmentCount = 4; Refresh();
            var grown = await AwaitArchive(workspace, id);
            Assert.Equal(40d, grown.GetProperty("duration").GetDouble());
            Assert.Equal(url, grown.GetProperty("url").GetString());
            Assert.Equal(1, replay.CallCount); Assert.Equal(1, handlers.Count);
            Assert.Equal(4, (await Fetch(workspace, url)).Body.Split('\n').Count(line => line.StartsWith("#EXTINF:", StringComparison.Ordinal)));

            handlers[0].Expired = true; Refresh();
            var renewed = await AwaitArchive(workspace, id);
            var renewedUrl = renewed.GetProperty("url").GetString()!;
            Assert.Equal(2, replay.CallCount); Assert.Equal(2, handlers.Count);
            Assert.True(handlers[0].Disposed); Assert.Equal(false, handlers[1].Disposed);
            Assert.Equal(false, renewedUrl == url);
            Assert.Equal(404, (await Fetch(workspace, url)).Status);
            Assert.Equal(200, (await Fetch(workspace, renewedUrl)).Status);
            await Call(workspace, "close", new { player = id });
            Assert.True(handlers[1].Disposed);
            Assert.Equal(404, (await Fetch(workspace, renewedUrl)).Status);
            await Call(workspace, "close", new { player = peer.GetProperty("id").GetString() });
            Assert.Equal(32, dependencies.PlaybackSlots.CurrentCount);
        }
    });

    private sealed class PreviewArchiveHandler(Func<string> playlist) : HttpMessageHandler
    {
        internal bool Disposed { get; private set; }
        internal bool Expired { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(Expired ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith(".m3u8", StringComparison.Ordinal) ? playlist() : "preview segment")
            });
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
