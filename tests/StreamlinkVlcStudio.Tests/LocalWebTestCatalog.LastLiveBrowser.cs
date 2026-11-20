using Microsoft.Web.WebView2.Core;

internal static partial class LocalWebTestCatalog
{
    private static async Task CheckBrowserFollowingLastLive(CoreWebView2 core, CoreWebView2Controller controller, BrowserWorkspace workspace, string password)
    {
        await core.ExecuteScriptAsync($"window.lastLiveNow={workspace.LastLiveNow.ToUnixTimeMilliseconds()};Date.now=()=>window.lastLiveNow;");
        await core.ExecuteScriptAsync($"document.querySelector('#password').value={JsonSerializer.Serialize(password)};document.querySelector('#login-form').requestSubmit();");
        const string settled = "document.querySelector('#cards').getAttribute('aria-busy')==='false'";
        await WaitBrowser(core, settled + " && document.querySelectorAll('#cards .card').length===10");
        await workspace.HistoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, workspace.HistoryCalls);
        // The list is usable while the provider is still held, and cached history is immediate.
        Assert.Equal("Last live 3 days ago", JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.querySelector('#cards [data-key=twitch] .last-live').textContent")));
        Assert.Equal("Last live unavailable", JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.querySelector('#cards [data-key=kick] .last-live').textContent")));
        await core.ExecuteScriptAsync("window.pinButton=document.querySelector('#cards [data-key=kick] .card-pin');pinButton.focus();pinButton.click();");
        await WaitBrowser(core, "pinButton.getAttribute('aria-pressed')==='true' && pinButton.getAttribute('aria-disabled')==='false'");
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('#cards > .card.is-offline').dataset.key==='kick' && document.activeElement===pinButton"));
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('#cards [data-key=twitch] .card-pin').getAttribute('aria-pressed')==='false'"));
        Assert.Equal("0", await core.ExecuteScriptAsync("document.querySelectorAll('#cards [data-key=live] .card-pin').length"));
        Assert.SequenceEqual(new[] { "Kick:creator" }, workspace.PinSettings.PinnedOfflineChannelKeys);
        await core.ExecuteScriptAsync("window.historyCard=document.querySelector('#cards [data-key=twitch]');window.historyButton=historyCard.querySelector('.card-main');historyButton.focus();");
        workspace.HoldHistory.TrySetResult();
        await WaitBrowser(core, "document.querySelector('#cards [data-key=twitch] .last-live').textContent==='Last live 1 day ago'");
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('#cards [data-key=twitch]')===historyCard && document.activeElement===historyButton"));
        Assert.Equal("true", await core.ExecuteScriptAsync("[...document.querySelectorAll('#followed-channels .last-live')].some(e=>e.dataset.channelKey==='twitch:creator' && e.textContent==='Last live 1 day ago')"));
        foreach (var (key, expected) in new[]
        {
            ("kick", "Last live 2 hours ago"), ("unknown", "Last live unavailable"),
            ("invalid", "Last live unavailable"), ("future", "Last live unavailable"),
            ("sentinel", "Last live unavailable"), ("recent", "Last live just now"),
            ("month", "Last live 1 month ago"), ("year", "Last live 1 year ago")
        })
            Assert.Equal(expected, JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync($"document.querySelector('#cards [data-key={key}] .last-live').textContent")));
        Assert.Equal("0", await core.ExecuteScriptAsync("document.querySelectorAll('#cards [data-key=live] .last-live').length"));
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('#cards [data-key=twitch] .last-live').title==='Latest available broadcast started '+new Date(window.lastLiveNow-86400000).toLocaleString(undefined,{dateStyle:'full',timeStyle:'short'})"));
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('#cards [data-key=kick] .last-live').title.includes(new Date(window.lastLiveNow-7200000).toLocaleDateString(undefined,{weekday:'long',year:'numeric',month:'long',day:'numeric'}))"));
        Assert.Equal("0", await core.ExecuteScriptAsync("document.querySelectorAll('.last-live img,.last-live script').length"));

        // Time labels advance without rebuilding focused cards or requesting history again.
        // Simulate a pin edit on the host while the browser remains on Following.
        workspace.PinSettings.PinnedOfflineChannelKeys = ["Twitch:unknown"];
        await core.ExecuteScriptAsync("window.lastLiveNow+=120000;document.dispatchEvent(new Event('visibilitychange'));");
        Assert.Equal("Last live 2 minutes ago", JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.querySelector('#cards [data-key=recent] .last-live').textContent")));
        Assert.Equal("true", await core.ExecuteScriptAsync("document.activeElement===historyButton"));
        Assert.Equal(1, workspace.HistoryCalls);
        await WaitBrowser(core, "document.querySelector('#cards > .card.is-offline').dataset.key==='unknown'");
        Assert.Equal("true", await core.ExecuteScriptAsync("document.activeElement===historyButton && pinButton.getAttribute('aria-pressed')==='false'"));
        Assert.Equal(1, workspace.HistoryCalls);
        await core.ExecuteScriptAsync("document.querySelector('#cards [data-key=unknown] .card-pin').click();");
        await WaitBrowser(core, "document.querySelector('#cards [data-key=unknown] .card-pin').getAttribute('aria-pressed')==='false'");
        Assert.Equal(0, workspace.PinSettings.PinnedOfflineChannelKeys.Count);
        await core.ExecuteScriptAsync("document.querySelector('#live-filter').click();");
        Assert.Equal("0", await core.ExecuteScriptAsync("document.querySelectorAll('#cards .card-pin').length"));
        await core.ExecuteScriptAsync("document.querySelector('#live-filter').click();");
        await core.ExecuteScriptAsync("document.querySelector('[data-filter=kick]').click()");
        Assert.Equal("Last live 2 hours ago", JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.querySelector('#cards .last-live').textContent")));
        await core.ExecuteScriptAsync("document.querySelector('[data-filter=all]').click()");
        Assert.Equal("Last live 1 day ago", JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.querySelector('#cards [data-key=twitch] .last-live').textContent")));
        await CaptureBrowser(core, "offline-last-live-desktop");
        controller.Bounds = new System.Drawing.Rectangle(0, 0, 360, 900);
        await WaitBrowser(core, "innerWidth===360");
        Assert.Equal("true", await core.ExecuteScriptAsync("document.documentElement.scrollWidth<=document.documentElement.clientWidth"));
        Assert.Equal("true", await core.ExecuteScriptAsync("(()=>{const card=document.querySelector('#cards [data-key=kick]');const pin=card.querySelector('.card-pin').getBoundingClientRect();return pin.width>=36 && pin.height>=36 && card.querySelector('.card-main').getBoundingClientRect().right<=pin.left;})()"));
        await CaptureBrowser(core, "offline-last-live-mobile");
        controller.Bounds = new System.Drawing.Rectangle(0, 0, 1440, 1000);
        await WaitBrowser(core, "innerWidth===1440");

        // An optional provider failure keeps the status snapshot and cached time usable.
        workspace.FailHistory = true;
        await core.ExecuteScriptAsync("document.querySelector('#refresh').click()");
        await WaitBrowser(core, settled + " && document.querySelectorAll('#cards .card').length===10");
        await WaitBrowser(core, "document.querySelector('#cards [data-key=twitch] .last-live').textContent==='Last live 3 days ago'");
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('#notice').hidden"));

        // A normal navigation cancels the outstanding provider request.
        workspace.FailHistory = false;
        workspace.HoldHistory = new(TaskCreationOptions.RunContinuationsAsynchronously);
        workspace.HistoryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await core.ExecuteScriptAsync("document.querySelector('#refresh').click()");
        await workspace.HistoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await core.ExecuteScriptAsync("document.querySelector('[data-page=settings]').click()");
        await WaitBrowser(core, "!document.querySelector('#preferences').hidden");
        await workspace.HistoryCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // Model an already-completed network response that arrives after navigation. Even if
        // fetch cannot abort it, its old generation must not overwrite sidebar/library data.
        await core.ExecuteScriptAsync("window.fetch=new Proxy(window.fetch,{apply(target,self,args){const history=args[0]==='/api/following/history';if(history)args[1]={...args[1],signal:undefined};const result=Reflect.apply(target,self,args);return history?result.then(async response=>{await response.clone().text();setTimeout(()=>window.historyResponseCompleted=true,0);return response}):result}})");
        workspace.HoldHistory = new(TaskCreationOptions.RunContinuationsAsynchronously);
        workspace.HistoryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await core.ExecuteScriptAsync("document.querySelector('[data-page=following]').click()");
        await workspace.HistoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await core.ExecuteScriptAsync("document.querySelector('[data-page=settings]').click()");
        await WaitBrowser(core, "!document.querySelector('#preferences').hidden");
        workspace.HoldHistory.TrySetResult();
        await WaitBrowser(core, "window.historyResponseCompleted===true");
        Assert.Equal("true", await core.ExecuteScriptAsync("[...document.querySelectorAll('#followed-channels .last-live')].some(e=>e.dataset.channelKey==='twitch:creator' && e.textContent==='Last live 3 days ago') && document.querySelectorAll('#cards .last-live').length===0"));

        // Four saved pins, two offline when this browser loaded Following.
        workspace.PinSettings.PinnedOfflineChannelKeys = ["Twitch:creator", "Kick:creator", "Twitch:unknown", "Twitch:month"];
        workspace.FollowingRevision = 1;
        await core.ExecuteScriptAsync("document.querySelector('[data-page=following]').click()");
        await WaitBrowser(core, settled + " && document.querySelectorAll('#cards .card-pin[aria-pressed=true]').length===2");
        Assert.Equal("true", await core.ExecuteScriptAsync("Array.from(document.querySelectorAll('#cards [data-key=unknown], #cards [data-key=month]')).every(card=>!card.querySelector('.card-pin'))"));
        var followingLoads = workspace.FollowingLoads;
        var historyLoads = workspace.HistoryCalls;
        await core.ExecuteScriptAsync("window.steadyPin=document.querySelector('#cards [data-key=kick] .card-pin');steadyPin.focus()");
        workspace.FollowingRevision = 2;
        await core.ExecuteScriptAsync("window.lastLiveNow+=5000");
        await WaitBrowser(core, "document.querySelectorAll('#cards .card-pin[aria-pressed=true]').length===4");
        Assert.Equal("true", await core.ExecuteScriptAsync("document.activeElement===steadyPin && document.querySelector('#cards [data-key=unknown] .card-pin').getAttribute('aria-pressed')==='true'"));
        Assert.Equal(followingLoads, workspace.FollowingLoads);
        Assert.Equal(historyLoads, workspace.HistoryCalls);

        // Thirteen live cards leave two columns free at this width. All four pins
        // must start below that last live row, so scrolling past it reveals them together.
        controller.Bounds = new System.Drawing.Rectangle(0, 0, 1872, 931);
        await WaitBrowser(core, "innerWidth===1872");
        using var layout = JsonDocument.Parse(await core.ExecuteScriptAsync("""
            (() => {
              const grid = document.querySelector('#cards');
              const live = [...grid.querySelectorAll('.card:not(.is-offline)')];
              return {
                columns: getComputedStyle(grid).gridTemplateColumns.split(' ').length,
                liveCount: live.length,
                lastLiveBottom: Math.max(...live.map(card => card.getBoundingClientRect().bottom)),
                pinnedTops: [...grid.querySelectorAll('.card-pin[aria-pressed=true]')]
                  .map(pin => pin.closest('.card').getBoundingClientRect().top)
              };
            })()
            """));
        var geometry = layout.RootElement;
        Assert.Equal(5, geometry.GetProperty("columns").GetInt32());
        Assert.Equal(13, geometry.GetProperty("liveCount").GetInt32());
        var pinnedTops = geometry.GetProperty("pinnedTops").EnumerateArray().Select(top => top.GetDouble()).ToArray();
        Assert.True(pinnedTops.Distinct().Count() == 1 && pinnedTops[0] >= geometry.GetProperty("lastLiveBottom").GetDouble(),
            "The four pinned cards should share the first offline row. Browser geometry: " + geometry);
        Assert.Equal("[]", await core.ExecuteScriptAsync("window.testErrors"));
    }

    private sealed partial class BrowserWorkspace
    {
        internal bool LastLive { get; init; }
        internal DateTimeOffset LastLiveNow { get; } = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        internal TaskCompletionSource HoldHistory { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource HistoryStarted { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource HistoryCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int HistoryCalls { get; private set; }
        internal bool FailHistory { get; set; }
        internal FollowedChannelsSettings PinSettings { get; } = new();
        internal int FollowingRevision { get; set; }
        internal int FollowingLoads { get; private set; }

        private async Task<object> LastLiveResult(string action, JsonElement input, CancellationToken token)
        {
            var cases = new (string Key, string Channel, string Platform, object? Timestamp)[]
            {
                ("twitch", "creator", "Twitch", LastLiveNow.AddDays(-1)),
                ("kick", "creator", "Kick", LastLiveNow.AddHours(-2)),
                ("unknown", "unknown", "Twitch", null),
                ("invalid", "invalid", "Twitch", "<img src=x onerror=alert(1)>"),
                ("future", "future", "Twitch", LastLiveNow.AddDays(1)),
                ("sentinel", "sentinel", "Twitch", DateTimeOffset.MinValue),
                ("recent", "recent", "Twitch", LastLiveNow.AddSeconds(-59)),
                ("month", "month", "Twitch", LastLiveNow.AddDays(-30)),
                ("year", "year", "Twitch", LastLiveNow.AddDays(-365))
            };
            if (action == "following/pin")
            {
                var item = cases.Single(item => item.Key == input.GetProperty("key").GetString());
                var target = StreamInputParser.FromChannel(Enum.Parse<PlatformKind>(item.Platform), item.Channel);
                PinSettings.SetOfflineChannelPinned(target, input.GetProperty("pinned").GetBoolean());
            }
            var revision = FollowingRevision;
            if (action == "following/pin" || action == "following/pins" &&
                (revision == 0 || input.TryGetProperty("revision", out var known) && known.GetInt32() == revision))
                return new { pinnedKeys = PinSettings.PinnedOfflineChannelKeys };
            if (action is "following" or "following/pins")
            {
                if (action == "following") FollowingLoads++;
                var live = revision == 1 ? cases.Where(item => item.Key is "unknown" or "month").ToArray() : [];
                var liveKeys = live.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
                var offline = cases.Where(item => !liveKeys.Contains(item.Key));
                var otherLive = Enumerable.Range(1, revision == 0 ? 1 : 13).Select(index => new
                {
                    key = index == 1 ? "live" : $"live-{index}",
                    channel = index == 1 ? "live" : $"live-{index}",
                    displayName = index == 1 ? "Live channel" : $"Live channel {index}",
                    platform = "Twitch",
                    state = "Live"
                });
                return new
                {
                    revision,
                    pinnedKeys = PinSettings.PinnedOfflineChannelKeys
                        .Where(key => revision == 0 || offline.Any(item => key == item.Platform + ":" + item.Channel)).ToArray(),
                    items = otherLive.Concat(live.Select(item => new
                    {
                        key = item.Key,
                        channel = item.Channel,
                        displayName = item.Key + " channel",
                        platform = item.Platform,
                        state = "Live"
                    })).ToArray(),
                    offline = offline.Select(item => new
                    {
                        key = item.Key,
                        channel = item.Channel,
                        displayName = item.Key + " channel",
                        platform = item.Platform,
                        state = "Offline",
                        lastLiveAtUtc = item.Key == "twitch" ? (DateTimeOffset?)LastLiveNow.AddDays(-3) : null
                    }).ToArray()
                };
            }
            HistoryCalls++;
            HistoryStarted.TrySetResult();
            if (FailHistory) throw new IOException("Test-only history failure.");
            try { await HoldHistory.Task.WaitAsync(token); }
            catch (OperationCanceledException) { HistoryCancelled.TrySetResult(); throw; }
            return new { items = cases.Select(item => new { channel = item.Channel, platform = item.Platform, lastLiveAtUtc = item.Timestamp }).ToArray() };
        }
    }
}
