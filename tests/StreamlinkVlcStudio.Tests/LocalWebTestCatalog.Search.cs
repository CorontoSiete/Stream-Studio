using System.Collections.Concurrent;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    private static Task ChannelSearchAsync() => WithWorkspaceAsync(async fixture =>
    {
        var profiles = new ConcurrentBag<string>();
        using var client = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "gql.twitch.tv")
            {
                var edges = Enumerable.Range(0, 30).Select(index => new
                {
                    item = new
                    {
                        login = index == 0 ? "creator" : $"creator{index}",
                        stream = new { viewersCount = 90, game = new { name = "Adventure games" } },
                        broadcastSettings = new { title = "Twitch broadcast title" }
                    }
                });
                return Json(JsonSerializer.Serialize(new { data = new { searchFor = new { channels = new { edges } } } }));
            }

            Assert.Equal("kick.com", uri.Host);
            if (uri.AbsolutePath == "/api/search")
                return Json("""
                    {"channels":[
                        {"slug":"creator","isLive":true},
                        {"slug":"creatorzero","isLive":true},
                        {"slug":"creatorknown","isLive":true,"livestream":{"viewer_count":7,"session_title":"Live from Kick","categories":[{"name":"Music"}]},"recentCategories":[{"name":"Older category"}]},
                        {"slug":"creatornocategory","isLive":true,"stream_title":"Title from search","livestream":{"viewer_count":8}},
                        {"slug":"creatornotitle","isLive":true,"category":{"name":"Category from search"},"livestream":{"viewer_count":9}},
                        {"slug":"creatorunknown","isLive":true},
                        {"slug":"creatoroffline","isLive":false,"recentCategories":[{"name":"Art"}]}
                    ]}
                    """);

            profiles.Add(uri.AbsolutePath);
            return uri.AbsolutePath switch
            {
                "/api/v2/channels/creator" => Json("""{"livestream":{"viewer_count":1200,"session_title":"Kick broadcast title","categories":[{"name":"Just Chatting"}]}}"""),
                "/api/v2/channels/creatorzero" => Json("""{"livestream":{"viewer_count":0}}"""),
                "/api/v2/channels/creatornocategory" => Json("""{"livestream":{"viewer_count":800,"session_title":"Title from profile","categories":[{"name":"Category from profile"}]}}"""),
                "/api/v2/channels/creatornotitle" => Json("""{"livestream":{"viewer_count":900,"session_title":"Title from profile","categories":[{"name":"Category from profile"}]}}"""),
                _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            };
        }));
        var streamlink = new FakeStreamlinkService();
        await using var workspace = new LocalWebWorkspace(fixture.Dependencies with
        {
            Search = new StreamSearchService(new MemoryLogger(), streamlink, client),
            ChannelProfiles = new(client)
        });
        var result = await Call(workspace, "search", new { query = "creator" });
        var items = result.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(30, items.Length);
        var twitch = items.First(item => item.GetProperty("platform").GetString() == "Twitch");
        var kick = items.Where(item => item.GetProperty("platform").GetString() == "Kick")
            .ToDictionary(item => item.GetProperty("channel").GetString()!);
        Assert.Equal(7, kick.Count);
        Assert.Equal("creator", twitch.GetProperty("channel").GetString());
        Assert.True(twitch.GetProperty("key").GetString() != kick["creator"].GetProperty("key").GetString());
        Assert.Equal(90, twitch.GetProperty("viewers").GetInt32());
        AssertMetadata(twitch, "Twitch broadcast title", "Adventure games");
        Assert.Equal(1200, kick["creator"].GetProperty("viewers").GetInt32());
        AssertMetadata(kick["creator"], "Kick broadcast title", "Just Chatting");
        Assert.Equal(0, kick["creatorzero"].GetProperty("viewers").GetInt32());
        Assert.Equal(7, kick["creatorknown"].GetProperty("viewers").GetInt32());
        AssertMetadata(kick["creatorknown"], "Live from Kick", "Music");
        Assert.Equal(8, kick["creatornocategory"].GetProperty("viewers").GetInt32());
        AssertMetadata(kick["creatornocategory"], "Title from search", "Category from profile");
        Assert.Equal(9, kick["creatornotitle"].GetProperty("viewers").GetInt32());
        AssertMetadata(kick["creatornotitle"], "Title from profile", "Category from search");
        Assert.Equal("Live", kick["creatorunknown"].GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, kick["creatorunknown"].GetProperty("viewers").ValueKind);
        AssertMetadata(kick["creatorunknown"], "", "");
        Assert.Equal(JsonValueKind.Null, kick["creatoroffline"].GetProperty("viewers").ValueKind);
        AssertMetadata(kick["creatoroffline"], "", "Art");
        Assert.SequenceEqual(["/api/v2/channels/creator", "/api/v2/channels/creatornocategory", "/api/v2/channels/creatornotitle",
            "/api/v2/channels/creatorunknown", "/api/v2/channels/creatorzero"],
            profiles.OrderBy(path => path, StringComparer.Ordinal));
        Assert.Equal(0, streamlink.ProbeRequests.Count);

        static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

        static void AssertMetadata(JsonElement item, string title, string category)
        {
            Assert.Equal(title, item.GetProperty("title").GetString());
            Assert.Equal(category, item.GetProperty("categoryName").GetString());
        }
    });
}
