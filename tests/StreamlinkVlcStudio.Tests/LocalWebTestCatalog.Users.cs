using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    private static async Task UnicodeShortcutsAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "StreamStudioTests", "web-shortcuts-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "web-users.json");
        try
        {
            var users = await LocalWebUserStore.OpenAsync(path);
            var user = await users.AddAsync("shortcuts", "Shortcut password 123!");
            await using var workspace = new LocalWebUserWorkspace(new ProfileFixture(), users, user.Id);
            foreach (var key in new[] { "é", "𐐨", "🎮", "F1", "F24", "Numpad4", "Space" })
            {
                await Call(workspace, "settings/save", new { hotkeys = new { mute = new { key, ctrl = false, alt = false, shift = false, meta = false } } });
                var reopened = await LocalWebUserStore.OpenAsync(path);
                Assert.Equal(key, reopened.PreferencesFor(user.Id).Hotkeys["mute"]!.Key);
            }
            foreach (var key in new[] { "", "\u200d", "\ue000", "ab", "F01", "F+1", "F25", " " })
                await Assert.ThrowsAsync<ArgumentException>(() => Call(workspace, "settings/save",
                    new { hotkeys = new { mute = new { key, ctrl = false, alt = false, shift = false, meta = false } } }));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static Task DesktopShortcutsAsync() => WithWorkspaceAsync(async fixture =>
    {
        var path = Path.Combine(fixture.Directory, "web-shortcuts.json");
        var users = await LocalWebUserStore.OpenAsync(path);
        var user = await users.AddAsync("shortcuts", "Shortcut password 123!");
        fixture.Settings.Hotkeys.TogglePause = "Ctrl+P";
        fixture.Settings.Hotkeys.SkipBackwardSeconds = 17;

        // Old clients wrote all six resolved shortcuts even when only one changed.
        var legacyFile = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var preferences = legacyFile["users"]![0]!["preferences"]!.AsObject();
        foreach (var field in new[] { "skipBackwardSeconds", "skipForwardSeconds", "volumeUpStep", "volumeDownStep" })
            preferences.Remove(field);
        var legacy = new Dictionary<string, LocalWebHotkey?>
        {
            ["back"] = new("Escape", false, false, false, false),
            ["nextTab"] = new("]", false, false, false, false),
            ["previousTab"] = new("[", false, false, false, false),
            ["grid"] = new("g", false, false, false, false),
            ["keepAudio"] = new("b", true, false, false, false),
            ["mute"] = null
        };
        preferences["hotkeys"] = JsonSerializer.SerializeToNode(legacy, LocalWebUserStore.JsonOptions);
        await File.WriteAllTextAsync(path, legacyFile.ToJsonString());
        users = await LocalWebUserStore.OpenAsync(path);

        await using var workspace = new LocalWebUserWorkspace(new LocalWebWorkspace(fixture.Dependencies), users, user.Id);
        var initial = await Call(workspace, "settings", new { });
        var inherited = initial.GetProperty("desktopHotkeys");
        Assert.Equal(11, inherited.GetProperty("bindings").EnumerateObject().Count());
        Assert.Equal("Ctrl+P", inherited.GetProperty("bindings").GetProperty("togglePause").GetString());
        Assert.Equal(17, inherited.GetProperty("skipBackwardSeconds").GetInt32());
        var account = initial.GetProperty("preferences");
        Assert.Equal(JsonValueKind.Null, account.GetProperty("skipBackwardSeconds").ValueKind);
        Assert.Equal(JsonValueKind.Null, account.GetProperty("volumeUpStep").ValueKind);
        var saved = account.GetProperty("hotkeys");
        Assert.Equal(2, saved.EnumerateObject().Count());
        Assert.Equal("b", saved.GetProperty("keepAudio").GetProperty("key").GetString());
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("mute").ValueKind);

        fixture.Settings.Hotkeys.TogglePause = "Mouse5";
        var refreshed = await Call(workspace, "hotkeys", new { });
        Assert.Equal("Mouse5", refreshed.GetProperty("bindings").GetProperty("togglePause").GetString());

        var overrideBinding = new LocalWebHotkey("Escape", false, false, false, false);
        var overrides = new Dictionary<string, LocalWebHotkey?>(legacy)
        {
            ["togglePause"] = new("Space", false, false, false, false)
        };
        await Call(workspace, "settings/save", new { hotkeys = JsonSerializer.SerializeToElement(overrides, LocalWebUserStore.JsonOptions) });
        await Task.WhenAll(
            Call(workspace, "settings/save", new { skipBackwardSeconds = 11, volumeUpStep = 16 }),
            Call(workspace, "settings/save", new { skipForwardSeconds = 27, volumeDownStep = 9 }));
        await Assert.ThrowsAsync<ArgumentException>(() => Call(workspace, "settings/save", new { skipForwardSeconds = 301 }));
        await Assert.ThrowsAsync<ArgumentException>(() => Call(workspace, "settings/save", new { volumeUpStep = 0 }));
        await Assert.ThrowsAsync<ArgumentException>(() => Call(workspace, "settings/save", new { volumeDownStep = 1.5 }));

        users = await LocalWebUserStore.OpenAsync(path);
        var restored = users.PreferencesFor(user.Id);
        Assert.Equal(overrideBinding, restored.Hotkeys["back"]);
        Assert.Equal("Space", restored.Hotkeys["togglePause"]!.Key);
        Assert.Equal<int?>(11, restored.SkipBackwardSeconds);
        Assert.Equal<int?>(27, restored.SkipForwardSeconds);
        Assert.Equal<int?>(16, restored.VolumeUpStep);
        Assert.Equal<int?>(9, restored.VolumeDownStep);
        Assert.Equal("Mouse5", fixture.Settings.Hotkeys.TogglePause);
        Assert.Equal(17, fixture.Settings.Hotkeys.SkipBackwardSeconds);

        await Call(workspace, "settings/save", new { skipBackwardSeconds = (int?)null, volumeUpStep = (int?)null });
        var reset = (await LocalWebUserStore.OpenAsync(path)).PreferencesFor(user.Id);
        Assert.Equal<int?>(null, reset.SkipBackwardSeconds);
        Assert.Equal<int?>(null, reset.VolumeUpStep);
        Assert.Equal<int?>(27, reset.SkipForwardSeconds);
        Assert.Equal<int?>(9, reset.VolumeDownStep);
    });

    private static async Task UserAccountsAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "StreamStudioTests", "web-accounts-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "web-users.json");
        try
        {
            var users = await LocalWebUserStore.OpenAsync(path);
            Assert.Equal(0, users.ListUsers().Count);
            var alice = await users.AddAsync("Alice", "Alice password 123!");
            var bob = await users.AddAsync("Bob", "Bob password 456!");
            await Assert.ThrowsAsync<ArgumentException>(() => users.AddAsync("alice", "Another password 123!"));
            await Assert.ThrowsAsync<ArgumentException>(() => users.AddAsync("bad name", "Another password 123!"));
            await Assert.ThrowsAsync<ArgumentException>(() => users.ChangePasswordAsync(alice.Id, "short"));
            var saved = await File.ReadAllTextAsync(path);
            Assert.Equal(false, saved.Contains("Alice password", StringComparison.Ordinal));
            Assert.Equal(false, saved.Contains("Bob password", StringComparison.Ordinal));
            Assert.Contains("600000", saved);
            Assert.True(await users.VerifyAsync("ALICE", "Alice password 123!", CancellationToken.None) is not null);
            Assert.True(await users.VerifyAsync("alice", "wrong password", CancellationToken.None) is null);
            Assert.True(await users.VerifyAsync("unknown", "Alice password 123!", CancellationToken.None) is null);

            await using (var aliceSession = new LocalWebUserWorkspace(new ProfileFixture(), users, alice.Id))
            await using (var bobSession = new LocalWebUserWorkspace(new ProfileFixture(), users, bob.Id))
            {
                var initial = await Call(aliceSession, "settings", new { });
                Assert.Equal("best", initial.GetProperty("defaultQuality").GetString());
                Assert.Equal(true, initial.GetProperty("preferences").GetProperty("keepInactive").GetBoolean());
                await Call(aliceSession, "settings/save", new
                {
                    quality = "720p",
                    lowLatency = false,
                    layout = "overlay",
                    size = 22,
                    opacity = 35,
                    keepInactive = false,
                    pauseInactiveVod = false,
                    hotkeys = new { back = new { key = "Escape", ctrl = false, alt = false, shift = false, meta = false } }
                });
                var alicePlayer = await Call(aliceSession, "open", new { });
                var alicePlayerId = alicePlayer.GetProperty("id").GetString();
                Assert.Equal("720p", alicePlayer.GetProperty("quality").GetString());
                await Call(aliceSession, "volume/save", new { player = alicePlayerId, volume = 0.37 });
                await Call(aliceSession, "chat/layout", new
                {
                    player = alicePlayerId,
                    layout = new { width = 320, height = 240, x = 0.2, y = 0.7 }
                });
                await Assert.ThrowsAsync<ArgumentException>(() => Call(bobSession, "volume/save", new { player = alicePlayerId, volume = 0.9 }));
                await Assert.ThrowsAsync<ArgumentException>(() => Call(aliceSession, "volume/save", new { player = alicePlayerId, volume = 2 }));
                await Assert.ThrowsAsync<ArgumentException>(() => Call(aliceSession, "settings/save", new
                {
                    hotkeys = new { back = new { key = "Escape", ctrl = false } }
                }));
                await Assert.ThrowsAsync<ArgumentException>(() => Call(aliceSession, "settings/save", new { size = 999 }));
                var bobSettings = await Call(bobSession, "settings", new { });
                Assert.Equal("best", bobSettings.GetProperty("defaultQuality").GetString());
                Assert.Equal("docked", bobSettings.GetProperty("preferences").GetProperty("layout").GetString());
                Assert.Equal(0, bobSettings.GetProperty("preferences").GetProperty("volumes").EnumerateObject().Count());
                var bobPlayer = await Call(bobSession, "open", new { });
                Assert.Equal(JsonValueKind.Null, bobPlayer.GetProperty("chatLayout").ValueKind);
            }

            users = await LocalWebUserStore.OpenAsync(path);
            Assert.Equal(2, users.ListUsers().Count);
            await using (var restored = new LocalWebUserWorkspace(new ProfileFixture(), users, alice.Id))
            {
                var settings = await Call(restored, "settings", new { });
                Assert.Equal("720p", settings.GetProperty("defaultQuality").GetString());
                var prefs = settings.GetProperty("preferences");
                Assert.Equal(false, prefs.GetProperty("keepInactive").GetBoolean());
                Assert.Equal(false, prefs.GetProperty("lowLatency").GetBoolean());
                Assert.Equal("overlay", prefs.GetProperty("layout").GetString());
                Assert.Equal(22, prefs.GetProperty("size").GetInt32());
                Assert.Equal(0.37, prefs.GetProperty("volumes").GetProperty("Twitch:streamer").GetDouble());
                var player = await Call(restored, "open", new { });
                Assert.Equal(320, player.GetProperty("chatLayout").GetProperty("width").GetInt32());
                await using var secondAliceSession = new LocalWebUserWorkspace(new ProfileFixture(), users, alice.Id);
                await Task.WhenAll(
                    Call(restored, "settings/save", new { quality = "1080p" }),
                    Call(secondAliceSession, "settings/save", new { opacity = 36 }));
                var merged = await Call(secondAliceSession, "settings", new { });
                Assert.Equal("1080p", merged.GetProperty("defaultQuality").GetString());
                Assert.Equal(36, merged.GetProperty("preferences").GetProperty("opacity").GetInt32());
            }
            var version = (await users.VerifyAsync("Alice", "Alice password 123!", CancellationToken.None))!.CredentialVersion;
            await users.ChangePasswordAsync(alice.Id, "New Alice password 789!");
            Assert.Equal(false, users.IsCurrent(alice.Id, version));
            Assert.True(await users.VerifyAsync("Alice", "Alice password 123!", CancellationToken.None) is null);
            Assert.True(await users.VerifyAsync("Alice", "New Alice password 789!", CancellationToken.None) is not null);
            await users.RemoveAsync(bob.Id);
            Assert.True(await users.VerifyAsync("Bob", "Bob password 456!", CancellationToken.None) is null);
            await Assert.ThrowsAsync<InvalidOperationException>(() => users.RemoveAsync(alice.Id));
            var malformed = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            malformed["users"]![0]!["preferences"]!["volumes"] = null;
            await File.WriteAllTextAsync(path, malformed.ToJsonString());
            await Assert.ThrowsAsync<InvalidDataException>(() => LocalWebUserStore.OpenAsync(path));
            malformed["users"]![0]!["preferences"]!["volumes"] = JsonNode.Parse("{}");
            malformed["users"]![0]!["salt"] = null;
            await File.WriteAllTextAsync(path, malformed.ToJsonString());
            await Assert.ThrowsAsync<InvalidDataException>(() => LocalWebUserStore.OpenAsync(path));
            await File.WriteAllTextAsync(path, "{\"version\":1,\"users\":[");
            await Assert.ThrowsAsync<JsonException>(() => LocalWebUserStore.OpenAsync(path));
            Assert.Equal("{\"version\":1,\"users\":[", await File.ReadAllTextAsync(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private sealed class ProfileFixture : ILocalWebWorkspace
    {
        private int nextPlayer;
        public Task<object?> InvokeAsync(string action, JsonElement input, CancellationToken cancellationToken) =>
            Task.FromResult<object?>(action switch
            {
                "settings" => new
                {
                    defaultQuality = "best",
                    lowLatency = true,
                    theme = "Dark",
                    qualities = LocalWebWorkspace.Qualities,
                    accounts = new { twitch = true, kick = true }
                },
                "open" => new
                {
                    id = "player" + ++nextPlayer,
                    platform = "Twitch",
                    channel = "Streamer",
                    title = "Stream",
                    quality = input.GetProperty("quality").GetString(),
                    chatLayout = new { width = 99 }
                },
                _ => new { ok = true }
            });
        public Task ServeMediaAsync(HttpContext context, string playbackId, string resourceId) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
