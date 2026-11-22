using Microsoft.AspNetCore.Http;
using Microsoft.Web.WebView2.Core;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;
using StreamlinkVlcStudio.Infrastructure.Previews;

internal static partial class LocalWebTestCatalog
{
    private static async Task CardPreviewPlaylistHandoffAsync()
    {
        var root = new Uri("https://cdn.ttvnw.net/redirect.m3u8");
        var effective = new Uri("https://cdn.ttvnw.net/live/index.m3u8");
        var requested = new List<Uri>();
        var refreshedPlaylist = "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:101\n#EXTINF:2,\nnext.ts";
        using var http = new HttpClient(new AsyncHttpMessageHandler((request, _) =>
        {
            requested.Add(request.RequestUri!);
            return Task.FromResult(request.RequestUri == root
                ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = effective } }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(request.RequestUri == effective ? refreshedPlaylist : "segment")
                });
        }));
        using var media = new LocalWebMedia("preview", root, http);
        media.SetDirectPreviewPlaylist("#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:100\n#EXTINF:2,\nfirst.ts?sig=secret",
            effective, PlatformKind.Twitch);

        Assert.Equal("", await ReadAsync(media.Root, "HEAD"));
        var initial = await ReadAsync();
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:100", initial);
        Assert.Equal(0, requested.Count);
        Assert.Equal(false, initial.Contains("secret", StringComparison.Ordinal));
        var segment = initial.Split('\n').Single(line => line.StartsWith("/media/preview/", StringComparison.Ordinal));
        Assert.Equal("segment", await ReadAsync(segment.Split('/')[^1]));
        Assert.Equal(new Uri(effective, "first.ts?sig=secret"), requested.Single());

        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:101", await ReadAsync());
        Assert.SequenceEqual(new[] { root, effective }, requested.Skip(1));
        refreshedPlaylist = "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:2,\nstitched-ad-1.ts";
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync());

        async Task<string> ReadAsync(string? id = null, string method = "GET")
        {
            var context = new DefaultHttpContext();
            context.Request.Method = method;
            using var output = new MemoryStream();
            context.Response.Body = output;
            await media.ServeAsync(context, id ?? media.Root);
            Assert.Equal(200, context.Response.StatusCode);
            output.Position = 0;
            using var reader = new StreamReader(output);
            return await reader.ReadToEndAsync();
        }
    }

    private static Task BrowserCardPreviewAsync() => WithWorkspaceAsync(async fixture =>
    {
        var streamlink = (FakeStreamlinkService)fixture.Dependencies.Streamlink;
        var following = (FakeFollowedStreamsService)fixture.Dependencies.Following;
        var streams = await following.GetLiveFollowedStreamsAsync(fixture.Settings);
        following.EnqueueResult(_ => Task.FromResult(streams with
        {
            OfflineChannels = [new(PlatformKind.Twitch, "offline", "Offline", "https://www.twitch.tv/offline")]
        }));
        var browse = (FakeBrowseService)fixture.Dependencies.Browse;
        browse.CategoryResponder = request => new(BrowseResultStatus.Available, [new(request.Platform, "1", "Games", "", [], 5)], "", "");
        browse.StreamResponder = request => new(BrowseResultStatus.Available,
            [new(request.Platform, "discovery", "Discovery", "Live from Discover", "1", "Games", 5, "", null, false, "en", "https://www.twitch.tv/discovery")], "", "");

        var previewUrls = new ConcurrentQueue<string>();
        var dependencies = fixture.Dependencies with
        {
            CreateCardPreviewMedia = (id, uri) =>
            {
                var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "hover-preview-fmp4");
                var http = new HttpClient(new AsyncHttpMessageHandler(async (request, token) => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new ByteArrayContent(await File.ReadAllBytesAsync(
                        Path.Combine(directory, request.RequestUri == uri ? "index.m3u8" : Path.GetFileName(request.RequestUri!.AbsolutePath)), token))
                }));
                var media = new LocalWebMedia(id, uri, http);
                previewUrls.Enqueue(media.PathFor(media.Root));
                return media;
            }
        };

        await BrowserAsync(false, workspaceFactory: () => new LocalWebWorkspace(dependencies), browserCheck: async core =>
        {
            await core.ExecuteScriptAsync("Object.defineProperty(crypto, 'randomUUID', { value: undefined });");
            await core.ExecuteScriptAsync($"document.querySelector('#password').value={JsonSerializer.Serialize(TestPassword)};document.querySelector('#login-form').requestSubmit();");
            await WaitBrowser(core, "document.querySelectorAll('#cards .card').length===3");
            const string twitch = "#cards .card.is-live[data-provider=twitch] .card-media";
            const string kick = "#cards .card.is-live[data-provider=kick] .card-media";
            await HoverBrowserCardAsync(core, "#cards .card.is-offline .card-main");
            await Task.Delay(250);
            Assert.Equal(0, streamlink.ResolveStreamUrlRequests.Count);

            await HoverBrowserCardAsync(core, twitch);
            await WaitForCardPreviewAsync(core, twitch);
            Assert.True(previewUrls.TryDequeue(out var twitchUrl));
            Assert.Equal("0", await core.ExecuteScriptAsync("document.querySelectorAll('#players video').length"));
            Assert.Equal(0, fixture.Chats.Count);
            Assert.Equal(0, fixture.Settings.RecentStreams.Count);
            Assert.Equal(32, dependencies.PlaybackSlots.CurrentCount);

            await HoverBrowserCardAsync(core, kick);
            await WaitForCardPreviewAsync(core, kick);
            await WaitForCardPreviewCloseAsync(core, twitchUrl);
            Assert.Equal("1", await core.ExecuteScriptAsync("document.querySelectorAll('.card-preview').length"));
            Assert.True(previewUrls.TryDequeue(out var kickUrl));

            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", """{"type":"mouseMoved","x":0,"y":0}""");
            await WaitBrowser(core, "!document.querySelector('.card-preview')");
            await WaitForCardPreviewCloseAsync(core, kickUrl);

            var resolving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            streamlink.ResolveStreamUrlOverride = async (_, token) =>
            {
                resolving.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { if (token.IsCancellationRequested) canceled.TrySetResult(); }
                throw new InvalidOperationException("The test preview should have been canceled.");
            };
            await HoverBrowserCardAsync(core, twitch);
            await resolving.Task.WaitAsync(TimeSpan.FromSeconds(4));
            await core.ExecuteScriptAsync("document.querySelector('[data-page=discover]').click()");
            await WaitBrowser(core, "!!document.querySelector('#cards.categories .card')");
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.Equal("0", await core.ExecuteScriptAsync("document.querySelectorAll('.card-preview').length"));
            Assert.Equal(3, streamlink.ResolveStreamUrlRequests.Count);
            streamlink.ResolveStreamUrlOverride = null;

            // Keep the pointer off the grid while category cards are replaced by live cards.
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", """{"type":"mouseMoved","x":0,"y":0}""");
            await core.ExecuteScriptAsync("document.querySelector('#cards.categories .card-main').click()");
            await WaitBrowser(core, "!!document.querySelector('#cards .card.is-live')");
            await HoverBrowserCardAsync(core, twitch);
            await WaitForCardPreviewAsync(core, twitch);
            var requests = streamlink.ResolveStreamUrlRequests;
            Assert.SequenceEqual(new[] { "streamer", "kickstreamer", "streamer", "discovery" }, requests.Select(request => request.Target.Channel));
            Assert.True(requests.All(request => request.Quality == LivePreviewPolicy.QualityPreference));
            await ClickBrowser(core, twitch);
            await WaitBrowser(core, "document.querySelectorAll('#players video').length===1 && !document.querySelector('.card-preview')");
            Assert.Equal("[]", await core.ExecuteScriptAsync("window.testErrors"));
        });
    });

    private static async Task HoverBrowserCardAsync(CoreWebView2 core, string selector)
    {
        var point = await core.ExecuteScriptAsync($$"""
            (()=>{const e=document.querySelector({{JsonSerializer.Serialize(selector)}}); e.scrollIntoView({block:'nearest'}); const r=e.getBoundingClientRect();return {type:'mouseMoved',x:r.x+r.width/2,y:r.y+r.height/2};})()
            """);
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", point);
    }

    private static async Task WaitForCardPreviewAsync(CoreWebView2 core, string selector)
    {
        var video = "document.querySelector(" + JsonSerializer.Serialize(selector + ".is-previewing > video") + ")";
        await WaitBrowser(core, $$"""
            (()=>{const v={{video}};
                return !!v && v.muted && !v.paused && v.videoWidth>0 && getComputedStyle(v).opacity==='1' && v.getVideoPlaybackQuality().totalVideoFrames>2;})()
            """);
        Assert.Equal("true", await core.ExecuteScriptAsync($$"""
            (()=>{const v={{video}}, frame=v.getBoundingClientRect(), card=v.parentElement.getBoundingClientRect();
                return Math.abs(frame.width-card.width)<1 && Math.abs(frame.height-card.height)<1;})()
            """));
    }

    private static async Task WaitForCardPreviewCloseAsync(CoreWebView2 core, string? url)
    {
        var result = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new
        {
            expression = $$"""
                (async()=>{
                    const url={{JsonSerializer.Serialize(url)}};
                    let status;
                    for (let attempt=0;attempt<20;attempt++) {
                        status=(await fetch(url)).status;
                        if (status===404) break;
                        await new Promise(resolve=>setTimeout(resolve,100));
                    }
                    return status;
                })()
                """,
            awaitPromise = true,
            returnByValue = true
        }));
        using var evaluation = JsonDocument.Parse(result);
        Assert.Equal(404, evaluation.RootElement.GetProperty("result").GetProperty("value").GetInt32());
    }
}
