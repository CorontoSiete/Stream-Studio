using System.Net.Http.Json;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    private static Task DiscoveryLanguageConcurrentSavesAsync() => WithWorkspaceAsync(async fixture =>
    {
        var path = Path.Combine(fixture.Directory, "concurrent-language-users.json");
        var users = await LocalWebUserStore.OpenAsync(path);
        var user = await users.AddAsync("Alice", "Alice password 123!");
        const string key = "Twitch:1";
        await users.SaveCategoryLanguageAsync(user.Id, key, "en", default);
        var writes = (SemaphoreSlim)typeof(LocalWebUserStore)
            .GetField("writes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(users)!;
        Task first;
        Task latest;
        await writes.WaitAsync();
        try
        {
            first = users.SaveCategoryLanguageAsync(user.Id, key, "fr", default);
            latest = users.SaveCategoryLanguageAsync(user.Id, key, "en", default);
        }
        finally { writes.Release(); }
        await Task.WhenAll(first, latest).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("en", users.PreferencesFor(user.Id).CategoryLanguages[key]);
        Assert.Equal("en", (await LocalWebUserStore.OpenAsync(path)).PreferencesFor(user.Id).CategoryLanguages[key]);

        // An unchanged selection does not need file access; pagination makes this
        // request repeatedly even when the account file is temporarily locked.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await users.SaveCategoryLanguageAsync(user.Id, key, " EN ", default);
            await users.SavePreferencesAsync(user.Id, JsonSerializer.SerializeToElement(new { browseDirectoryLanguage = "" }), default);
        }
    });

    private static Task DiscoveryLanguagePersistenceAsync() => WithWorkspaceAsync(async fixture =>
    {
        var path = Path.Combine(fixture.Directory, "category-language-users.json");
        var users = await LocalWebUserStore.OpenAsync(path);
        var alice = await users.AddAsync("Alice", "Alice password 123!");
        var bob = await users.AddAsync("Bob", "Bob password 456!");
        var browse = new FakeBrowseService();
        var dependencies = fixture.Dependencies with { Browse = browse };
        await using (var session = new LocalWebUserWorkspace(new LocalWebWorkspace(dependencies), users, alice.Id))
        {
            await Call(session, "streams", new { platform = "twitch", categoryId = "1", language = "en" });
            await Call(session, "streams", new { platform = "twitch", categoryId = "2", language = "fr" });
            await Call(session, "streams", new { platform = "kick", categoryId = "1", language = "es" });
            await Call(session, "settings/save", new { browseDirectoryLanguage = " DE " });
            await Call(session, "settings/save", new { size = 20 });
            foreach (var invalid in new object?[] { "unknown", 123, null })
                await Assert.ThrowsAsync<ArgumentException>(() => Call(session, "settings/save", new { browseDirectoryLanguage = invalid }));
        }
        users = await LocalWebUserStore.OpenAsync(path);
        await using (var session = new LocalWebUserWorkspace(new LocalWebWorkspace(dependencies), users, bob.Id))
        {
            var settings = await Call(session, "settings", new { });
            Assert.Equal("", settings.GetProperty("preferences").GetProperty("browseDirectoryLanguage").GetString());
            await Call(session, "streams", new { platform = "twitch", categoryId = "1" });
            Assert.Equal("", browse.StreamRequests[^1].Language);
            Assert.Equal(0, users.PreferencesFor(bob.Id).CategoryLanguages.Count);
        }
        await using (var session = new LocalWebUserWorkspace(new LocalWebWorkspace(dependencies), users, alice.Id))
        {
            var settings = await Call(session, "settings", new { });
            var preferences = settings.GetProperty("preferences");
            Assert.Equal("de", preferences.GetProperty("browseDirectoryLanguage").GetString());
            var languages = preferences.GetProperty("categoryLanguages");
            Assert.Equal("en", languages.GetProperty("Twitch:1").GetString());
            Assert.Equal("fr", languages.GetProperty("Twitch:2").GetString());
            Assert.Equal("es", languages.GetProperty("Kick:1").GetString());
            await Call(session, "streams", new { platform = "twitch", categoryId = "1" });
            Assert.Equal("en", browse.StreamRequests[^1].Language);
            // An older tab can keep paging with its previous language after the
            // preference has changed in another tab.
            await Call(session, "streams", new { platform = "twitch", categoryId = "1", language = "fr", cursor = "next" });
            Assert.Equal("fr", browse.StreamRequests[^1].Language);
            await Call(session, "streams", new { platform = "twitch", categoryId = "1" });
            Assert.Equal("en", browse.StreamRequests[^1].Language);
            await Call(session, "streams", new { platform = "twitch", categoryId = "3" });
            Assert.Equal("de", browse.StreamRequests[^1].Language);
            await Call(session, "streams", new { platform = "twitch", categoryId = "2", language = "" });
            await Call(session, "streams", new { platform = "twitch", categoryId = "2" });
            Assert.Equal("", browse.StreamRequests[^1].Language);
            await Call(session, "settings/save", new { browseDirectoryLanguage = "" });
        }
        users = await LocalWebUserStore.OpenAsync(path);
        Assert.Equal("", users.PreferencesFor(alice.Id).BrowseDirectoryLanguage);
        Assert.Equal("", users.PreferencesFor(alice.Id).CategoryLanguages["Twitch:2"]);
        Assert.Equal("en", users.PreferencesFor(alice.Id).CategoryLanguages["Twitch:1"]);
        Assert.Equal("", fixture.Settings.BrowseDirectoryLanguage);
        Assert.Equal(0, fixture.Settings.BrowseCategoryLanguages.Count);
    });

    private static Task DiscoveryLanguageSaveWhileBusyAsync() => WithWorkspaceAsync(async fixture =>
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var following = new FakeFollowedStreamsService();
        following.EnqueueResult(async token =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return new([], []);
        });
        using var certificate = Certificate();
        await using var server = await Server(certificate, () => new LocalWebWorkspace(fixture.Dependencies with { Following = following }));
        using var http = Client(certificate, server.Address);
        var auth = await Login(http);
        var pending = Post(http, "following", new { }, auth.Cookie, auth.Csrf);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var save = await Post(http, "settings/save", new { browseDirectoryLanguage = "en" }, auth.Cookie, auth.Csrf);
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
            Assert.Equal(false, pending.IsCompleted);
        }
        finally { release.TrySetResult(); }
        using var followingResponse = await pending;
        Assert.Equal(HttpStatusCode.OK, followingResponse.StatusCode);
        using var settings = await Post(http, "settings", new { }, auth.Cookie, auth.Csrf);
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        var body = await settings.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("en", body.GetProperty("preferences").GetProperty("browseDirectoryLanguage").GetString());
    });

    private static Task DiscoveryLanguagesAsync() => WithWorkspaceAsync(async fixture =>
    {
        var browse = new FakeBrowseService();
        await using var workspace = new LocalWebWorkspace(fixture.Dependencies with { Browse = browse });
        var settings = await Call(workspace, "settings", new { });
        var languages = settings.GetProperty("browseLanguages").EnumerateArray().ToArray();
        Assert.Equal(BrowseLanguages.Options.Count, languages.Length);
        Assert.True(languages.Any(language => language.GetProperty("code").GetString() == "en"));
        foreach (var platform in new[] { "twitch", "kick" })
        {
            await Call(workspace, "streams", new { platform, categoryId = "1", language = " EN ", cursor = "next" });
            Assert.Equal("en", browse.StreamRequests[^1].Language);
            Assert.Equal("next", browse.StreamRequests[^1].Cursor);
            await Call(workspace, "streams", new { platform, categoryId = "1" });
            Assert.Equal("", browse.StreamRequests[^1].Language);
        }
        var requestCount = browse.StreamRequests.Count;
        foreach (var language in new object[] { "invalid", "e", "en&cursor=other", 123 })
            await Assert.ThrowsAsync<ArgumentException>(() => Call(workspace, "streams", new { categoryId = "1", language }));
        Assert.Equal(requestCount, browse.StreamRequests.Count);
    });
}
