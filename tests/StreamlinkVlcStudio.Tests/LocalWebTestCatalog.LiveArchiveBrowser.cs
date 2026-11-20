using Microsoft.Web.WebView2.Core;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    private static Task BrowserFullArchiveAsync() => BrowserAsync(false, liveSeek: true, fullArchive: true);

    private static async Task CheckBrowserFullArchive(CoreWebView2 core, CoreWebView2Controller controller, BrowserWorkspace workspace)
    {
        await core.ExecuteScriptAsync("""
            window.seekErrors=[];
            window.hlsByVideo=new WeakMap();
            window.Hls=new Proxy(window.Hls,{construct(target,args){
                const h=Reflect.construct(target,args);
                h.on(target.Events.MEDIA_ATTACHING,(_,data)=>window.hlsByVideo.set(data.media,h));
                h.on(target.Events.ERROR,(_,data)=>{if(data.fatal)window.seekErrors.push(data.details)});
                return h;
            }});
            """);
        await core.ExecuteScriptAsync($"document.querySelector('#password').value={JsonSerializer.Serialize(TestPassword)};document.querySelector('#login-form').requestSubmit()");
        await WaitBrowser(core, "!document.querySelector('#studio').hidden && document.querySelectorAll('.card').length===1");
        foreach (var platform in new[] { "Twitch", "Kick" })
        {
            workspace.PlayerPlatform = platform;
            Volatile.Write(ref workspace.LiveWindowStart, 4); // Live only contains seconds 40–60.
            Volatile.Write(ref workspace.ArchiveSegments, 5); // History 0–50 predates opening.
            await OpenVolumePlayer(core, platform.ToLowerInvariant() + "-full-archive");
            await WaitBrowser(core, "document.querySelector('video').videoWidth>0 && Number(document.querySelector('.video-seek').max)===50");
            await core.ExecuteScriptAsync("window.originalLive=document.querySelector('video');originalLive.pause();window.originalLivePosition=originalLive.currentTime");
            foreach (var seconds in new[] { 5, 35 })
            {
                var point = await core.ExecuteScriptAsync($"(()=>{{const r=document.querySelector('.video-seek').getBoundingClientRect();return {{type:'mouseMoved',x:r.left+7+{seconds}/50*(r.width-14),y:r.top+r.height/2}}}})()");
                await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", point);
                await WaitBrowser(core, $"!document.querySelector('.seek-preview-frame').hidden && document.querySelector('.seek-preview-frame').dataset.time==='{seconds}'");
                var component = seconds == 5 ? 0 : 1;
                Assert.Equal("true", await core.ExecuteScriptAsync($"(()=>{{const c=document.querySelector('.seek-preview-frame'),rgb=c.getContext('2d').getImageData(0,0,1,1).data;return rgb[{component}]>100 && rgb[{1 - component}]<30 && originalLive===document.querySelector('video') && originalLive.currentTime===originalLivePosition && originalLive.paused}})()"));
            }
            await CaptureBrowser(core, "full-archive-preview-" + platform.ToLowerInvariant());
            await core.ExecuteScriptAsync("document.querySelector('.video-seek').focus()");
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyDown\",\"key\":\"Home\",\"code\":\"Home\",\"windowsVirtualKeyCode\":36}");
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyUp\",\"key\":\"Home\",\"code\":\"Home\",\"windowsVirtualKeyCode\":36}");
            await WaitBrowser(core, "document.querySelector('video')!==originalLive && document.querySelector('video').duration===Infinity && document.querySelector('video').videoWidth>0 && document.querySelector('video').currentTime<1");
            Assert.Equal("true", await core.ExecuteScriptAsync("(()=>{const video=document.querySelector('video');return video.paused && hlsByVideo.get(video)?.config.startPosition===0})()"));
            Assert.True(workspace.ArchiveRequestedSegments.Contains("index0.ts"));
            await ClickBrowser(core, "[data-action=play]");
            await WaitBrowser(core, "document.querySelector('video').currentTime>1 && document.querySelector('video').getVideoPlaybackQuality().totalVideoFrames>10");
            await ClickBrowser(core, "[data-action=play]");
            await ClickBrowser(core, ".video-seek");
            await WaitBrowser(core, "!document.querySelector('video').seeking && Math.abs(document.querySelector('video').currentTime-25)<1");
            Volatile.Write(ref workspace.ArchiveSegments, 6);
            await WaitBrowser(core, "Number(document.querySelector('.video-seek').max)>=60");
            Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('video').paused && Math.abs(document.querySelector('video').currentTime-25)<1 && Number(document.querySelector('.video-seek').min)===0"));
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 390, 844);
            await WaitBrowser(core, "innerWidth===390");
            await CaptureBrowser(core, "full-archive-" + platform.ToLowerInvariant());
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 1440, 1000);
            await WaitBrowser(core, "innerWidth===1440");
            await ClickBrowser(core, "[data-action=live]");
            await WaitBrowser(core, "(()=>{const video=document.querySelector('video');return video.videoWidth>0 && hlsByVideo.get(video)?.config.startPosition===-1})()");
            await ClickBrowser(core, "[data-action=play]");
            await WaitBrowser(core, "!document.querySelector('video').paused && document.querySelector('video').getVideoPlaybackQuality().totalVideoFrames>10");
            Assert.Equal("[]", await core.ExecuteScriptAsync("window.seekErrors"));
            Assert.Equal("[]", await core.ExecuteScriptAsync("window.testErrors"));
            await CloseVolumePlayers(core);
        }
    }

    private sealed partial class BrowserWorkspace
    {
        public bool FullArchive { get; init; }
        public int ArchiveSegments = 5;
        public ConcurrentBag<string> ArchiveRequestedSegments { get; } = [];
        private readonly Dictionary<string, LocalWebMedia> fullArchiveMedia = new(StringComparer.Ordinal);

        private object FullArchiveState(JsonElement input)
        {
            var id = input.GetProperty("player").GetString()!;
            if (!fullArchiveMedia.TryGetValue(id, out var media)) fullArchiveMedia.Add(id, media = CreateFullArchiveMedia(id));
            return new { available = true, key = "full-history", duration = Volatile.Read(ref ArchiveSegments) * 10d, url = media.PathFor(media.Root) };
        }

        private LocalWebMedia CreateFullArchiveMedia(string id)
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-event");
            var media = new LocalWebMedia(id, new Uri("https://archive.example/stream.m3u8"), new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
            {
                var file = Path.GetFileName(request.RequestUri!.AbsolutePath);
                HttpContent content;
                if (file.EndsWith(".m3u8", StringComparison.Ordinal))
                {
                    var segments = string.Join("", Enumerable.Range(0, Volatile.Read(ref ArchiveSegments)).Select(i => $"#EXTINF:10,\nindex{i}.ts\n"));
                    content = new StringContent($"#EXTM3U\n#EXT-X-TARGETDURATION:10\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n{segments}#EXT-X-ENDLIST\n");
                }
                else
                {
                    ArchiveRequestedSegments.Add(file);
                    content = new ByteArrayContent(await File.ReadAllBytesAsync(Path.Combine(directory, file), token));
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content };
            })));
            return media;
        }

        private object OpenFullArchive(string id, JsonElement input)
        {
            var media = CreateFullArchiveMedia(id);
            media.GrowingArchive = true;
            fullArchiveMedia.Add(id, media);
            vodPlayers.Add(id);
            return new
            {
                id,
                key = "full-history",
                title = "Growing broadcast archive",
                channel = PlayerChannel,
                platform = PlayerPlatform,
                url = media.PathFor(media.Root),
                isVod = true,
                isLiveArchive = true,
                isMp4 = false,
                resume = 29,
                duration = Volatile.Read(ref ArchiveSegments) * 10d,
                quality = input.GetProperty("quality").GetString()
            };
        }
    }
}
