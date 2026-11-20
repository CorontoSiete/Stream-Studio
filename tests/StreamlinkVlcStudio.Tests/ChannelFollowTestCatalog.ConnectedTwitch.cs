using StreamlinkVlcStudio.App.Wpf.Follows;

internal static partial class ChannelFollowTestCatalog
{
    private const string TwitchValidateUrl = "https://id.twitch.tv/oauth2/validate";
    private const string TwitchFollowLookupUrl = "https://api.twitch.tv/helix/channels/followed?user_id=42&broadcaster_id=123";

    private static Task ConnectedTwitchStatusAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var requested = new List<string>();
        var followData = """{"data":[{"broadcaster_id":"123","broadcaster_login":"STREAMER"}]}""";
        using var http = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("app-token", request.Headers.Authorization?.Parameter);
            var uri = request.RequestUri!.AbsoluteUri;
            requested.Add(uri);
            if (uri == TwitchValidateUrl) return TwitchAccount("42");
            Assert.SequenceEqual(new[] { "app-client" }, request.Headers.GetValues("Client-Id"));
            return uri switch
            {
                "https://api.twitch.tv/helix/users?login=streamer" => JsonResponse("""{"data":[{"id":"123","login":"streamer"}]}"""),
                TwitchFollowLookupUrl => JsonResponse(followData),
                _ => throw new InvalidOperationException($"Unexpected status request {uri}.")
            };
        }));
        var owner = new Window();
        try
        {
            var browserOpened = false;
            var service = new ChannelFollowService(owner, controllerFactory: _ =>
            {
                browserOpened = true;
                throw new InvalidOperationException("Connected account status must not require a website browser.");
            }, twitchLookup: new(() => "app-token", http));
            await using var model = Model(service);
            foreach (var (payload, label) in new[]
            {
                (followData, "Following"),
                ("""{"data":[]}""", "Follow"),
                ("""{"data":[{"broadcaster_id":"999","broadcaster_login":"other"}]}""", "Follow\u2026"),
                ("""{"error":"Unauthorized"}""", "Follow\u2026")
            })
            {
                followData = payload;
                requested.Clear();
                await model.OpenStreamAsync(Twitch);
                await WaitForFollowStatusOperationsAsync(model);
                Assert.Equal(label, model.FollowButtonText);
                Assert.Equal(false, browserOpened);
                Assert.SequenceEqual(new[]
                {
                    TwitchValidateUrl,
                    "https://api.twitch.tv/helix/users?login=streamer",
                    TwitchFollowLookupUrl
                }, requested);
                Assert.True(model.CloseTab(model.Tabs.Single()));
            }
        }
        finally { owner.Close(); }
    });

    private static Task ConnectedTwitchMutationAsync() => WithFixtureAsync(PlatformKind.Twitch, async fixture =>
    {
        fixture.Following = true;
        var websiteUserId = "99";
        using var http = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(TwitchValidateUrl, request.RequestUri!.AbsoluteUri);
            return request.Headers.Authorization?.Parameter switch
            {
                "app-token" => TwitchAccount("42"),
                "fixture-follow-token" => TwitchAccount(websiteUserId),
                _ => throw new InvalidOperationException("Unexpected account token.")
            };
        }));
        var signIns = 0;
        var service = fixture.Service((_, _) =>
        {
            signIns++;
            return Task.FromResult(true);
        }, new(() => "app-token", http));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UnfollowAsync(Twitch));
        Assert.Contains("different account", error.Message);
        Assert.Equal(1, signIns);
        Assert.Equal(0, fixture.Mutations);
        Assert.True(fixture.Following);

        websiteUserId = "42";
        var result = await service.UnfollowAsync(Twitch);
        Assert.Equal(false, result?.IsFollowing);
        Assert.Equal(1, fixture.AcceptedMutations);
        Assert.Equal(false, fixture.Following);
    });

    private static Task TwitchAccountSwitchAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = PendingStatusService(out var reads);
        var settings = new AppSettings();
        settings.Chat.TwitchOAuthToken = "first-account";
        await using var model = Model(service, settings);
        model.SelectedTab = AddTab(model, Twitch);

        settings.Chat.TwitchOAuthToken = "second-account";
        Assert.True(reads[0].Token.IsCancellationRequested);
        Assert.Equal(2, reads.Count);
        Assert.Equal("Checking\u2026", model.FollowButtonText);
        reads[1].Completion.SetResult(new(PlatformKind.Twitch, "streamer"));
        reads[0].Completion.SetResult(new(PlatformKind.Twitch, "streamer", IsFollowing: false));
        await WaitForFollowStatusOperationsAsync(model);
        Assert.Equal("Following", model.FollowButtonText);
    });

    private static HttpResponseMessage TwitchAccount(string userId) => JsonResponse(JsonSerializer.Serialize(new
    {
        login = "viewer",
        user_id = userId,
        client_id = "app-client",
        scopes = new[] { "user:read:follows" }
    }));

    private static HttpResponseMessage JsonResponse(string payload) =>
        new(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
}
