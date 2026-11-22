internal static partial class StreamHoverPreviewSourceTestCatalog
{
    private static async Task BrowserPreviewKickCaptionsAsync()
    {
        using var fixture = new Fixture
        {
            MasterText = Master + "\n#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"captions\",NAME=\"English\",URI=\"captions.m3u8\"",
            MediaText = Media + "\n#EXT-X-PREFETCH:upcoming.ts"
        };
        var service = new StreamlinkService(new MemoryLogger(), null, livePreviewSourceResolver: fixture.Resolver,
            resolveWithStreamlink: (_, _) => throw new InvalidOperationException("A supported preview should resolve directly."));
        var preview = await service.ResolveCardPreviewAsync(Request(PlatformKind.Kick), CancellationToken.None);
        Assert.Equal(3, fixture.Requests.Count);
        Assert.True(preview.PlaylistUri.AbsolutePath.EndsWith("/360p/index.m3u8"));
        Assert.Equal(fixture.Requests[2], preview.InitialPlaylist!.Value.Origin);
        Assert.Contains("#EXT-X-PREFETCH:upcoming.ts", preview.InitialPlaylist.Value.Content);
    }

    private static async Task BrowserPreviewTwitchFallbackAsync()
    {
        using var fixture = new Fixture(canResolve: request => request.CustomArguments.Count == 0);
        var fallbackUri = new Uri("https://video.ttvnw.net/fallback.m3u8");
        var request = Request();
        var websiteTokenReads = 0;
        var fallbackRequests = new List<StreamTransportRequest>();
        var service = new StreamlinkService(new MemoryLogger(), null, livePreviewSourceResolver: fixture.Resolver,
            twitchPlaybackTokenProvider: _ => { websiteTokenReads++; return Task.FromResult<string?>("website-token"); },
            resolveWithStreamlink: (fallback, _) =>
            {
                fallbackRequests.Add(fallback);
                return Task.FromResult(new StreamlinkResolvedUrl(fallbackUri, ""));
            });
        fixture.Override = (message, _) =>
        {
            if (message.RequestUri!.Host == "gql.twitch.tv")
                Assert.Equal("OAuth website-token", message.Headers.Authorization?.ToString());
            return Task.FromResult<HttpResponseMessage?>(null);
        };

        var direct = await service.ResolveCardPreviewAsync(request, CancellationToken.None);
        Assert.True(direct.InitialPlaylist.HasValue);
        Assert.Equal(3, fixture.Requests.Count);
        Assert.Equal(0, fallbackRequests.Count);
        Assert.Equal(1, websiteTokenReads);

        fixture.MasterText = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=160000,IVS-NAME=\"audio_only\"\naudio/index.m3u8";
        var fallback = await service.ResolveCardPreviewAsync(request, CancellationToken.None);
        Assert.Equal(fallbackUri, fallback.PlaylistUri);
        Assert.Equal(false, fallback.InitialPlaylist.HasValue);
        Assert.Equal(request, fallbackRequests.Single());
        Assert.Equal(5, fixture.Requests.Count);
        Assert.Equal(2, websiteTokenReads);

        var custom = request with { CustomArguments = ["--twitch-api-header", "Authorization=OAuth custom-token"] };
        Assert.Equal(fallbackUri, (await service.ResolveCardPreviewAsync(custom, CancellationToken.None)).PlaylistUri);
        Assert.Equal(custom, fallbackRequests[1]);
        Assert.Equal(5, fixture.Requests.Count);
        Assert.Equal(2, websiteTokenReads);
    }

    private static async Task BrowserPreviewCancellationAsync()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var pending = Signal();
        fixture.Override = async (_, token) =>
        {
            pending.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        };
        var service = new StreamlinkService(new MemoryLogger(), null, livePreviewSourceResolver: fixture.Resolver,
            resolveWithStreamlink: (_, _) => throw new InvalidOperationException("Cancellation should not start Streamlink."));
        var lookup = service.ResolveCardPreviewAsync(Request(), cancellation.Token);
        await pending.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => lookup);
    }
}
