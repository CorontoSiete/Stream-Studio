using Microsoft.Web.WebView2.Core;

internal static partial class LocalWebTestCatalog
{
    private static Task BrowserLiveSeekAsync() => BrowserAsync(false, liveSeek: true);

    private static async Task CheckBrowserLiveSeek(CoreWebView2 core, CoreWebView2Controller controller, BrowserWorkspace workspace)
    {
        await core.ExecuteScriptAsync("window.seekErrors=[];window.Hls=new Proxy(window.Hls,{construct(target,args){const h=Reflect.construct(target,args);window.seekHls=h;h.on(target.Events.ERROR,(_,data)=>{if(data.fatal)window.seekErrors.push(data.details)});return h}})");
        await core.ExecuteScriptAsync($"document.querySelector('#password').value={JsonSerializer.Serialize(TestPassword)};document.querySelector('#login-form').requestSubmit()");
        await WaitBrowser(core, "!document.querySelector('#studio').hidden && document.querySelectorAll('.card').length===1");
        foreach (var platform in new[] { "Twitch", "Kick" })
        {
            workspace.PlayerPlatform = platform;
            Volatile.Write(ref workspace.LiveWindowStart, 0);
            await OpenVolumePlayer(core, platform.ToLowerInvariant() + "-live-seek");
            await WaitBrowser(core, "document.querySelector('video').duration===Infinity && document.querySelector('video').currentTime>10 && !document.querySelector('.video-seek').disabled");
            await core.ExecuteScriptAsync("window.seekVideo=document.querySelector('video');window.seekSlider=document.querySelector('.video-seek');seekVideo.pause()");
            Assert.Equal("true", await core.ExecuteScriptAsync("!seekSlider.hidden && Number(seekSlider.min)===0 && Number(seekSlider.max)===50"));

            // Trusted clicks on the actual range control must move decoded media.
            await ClickBrowser(core, ".video-seek");
            await WaitBrowser(core, "!seekVideo.seeking && Math.abs(seekVideo.currentTime-25)<1");
            await core.ExecuteScriptAsync("seekSlider.focus()");
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyDown\",\"key\":\"Home\",\"code\":\"Home\",\"windowsVirtualKeyCode\":36}");
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyUp\",\"key\":\"Home\",\"code\":\"Home\",\"windowsVirtualKeyCode\":36}");
            await WaitBrowser(core, "!seekVideo.seeking && seekVideo.currentTime<0.5");
            await ClickBrowser(core, "[data-action=play]");
            await WaitBrowser(core, "!seekVideo.paused && seekVideo.currentTime>1 && seekVideo.videoWidth>0");
            await ClickBrowser(core, "[data-action=live]");
            await WaitBrowser(core, "seekVideo.currentTime>=19 && !seekVideo.seeking");

            Volatile.Write(ref workspace.LiveWindowStart, 2);
            await WaitBrowser(core, "seekHls.latestLevelDetails?.fragmentStart>=20 && Number(seekSlider.max)>=60");
            await core.ExecuteScriptAsync("seekVideo.pause();seekHls.trigger(Hls.Events.BUFFER_FLUSHING,{startOffset:0,endOffset:20,type:null})");
            await WaitBrowser(core, "Number(seekSlider.min)>=19.9");
            await ClickBrowser(core, ".playback-settings summary");
            await ClickBrowser(core, "[data-action=back]");
            await WaitBrowser(core, "!seekVideo.seeking && seekVideo.currentTime>=19.9 && seekVideo.currentTime<21");
            await ClickBrowser(core, "[data-action=forward]");
            await WaitBrowser(core, "!seekVideo.seeking && seekVideo.currentTime>=49.9 && seekVideo.currentTime<51");
            await ClickBrowser(core, ".playback-settings summary");
            await ClickBrowser(core, "[data-action=live]");
            await WaitBrowser(core, "!seekVideo.seeking && Math.abs(seekVideo.currentTime-seekHls.liveSyncPosition)<1");
            await ClickBrowser(core, "[data-action=play]");
            await WaitBrowser(core, "!seekVideo.paused && seekVideo.getVideoPlaybackQuality().totalVideoFrames>10");

            controller.Bounds = new System.Drawing.Rectangle(0, 0, 390, 844);
            await WaitBrowser(core, "innerWidth===390");
            Assert.Equal("true", await core.ExecuteScriptAsync("(()=>{const r=seekSlider.getBoundingClientRect();return r.width>100 && r.left>=0 && r.right<=innerWidth})()"));
            await CaptureBrowser(core, "live-seek-" + platform.ToLowerInvariant());
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 1440, 1000);
            Assert.Equal("[]", await core.ExecuteScriptAsync("window.seekErrors"));
            Assert.Equal("[]", await core.ExecuteScriptAsync("window.testErrors"));
            await CloseVolumePlayers(core);
        }
    }

    private sealed partial class BrowserWorkspace
    {
        public bool LiveSeek { get; init; }
        public int LiveWindowStart;
    }
}
