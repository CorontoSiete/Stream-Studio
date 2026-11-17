using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    private static Task VodIdentityAsync() => WithWorkspaceAsync(async fixture =>
    {
        var started = DateTimeOffset.UtcNow.AddHours(-2);
        var video = new KickVodItem("numeric-id", "live-id", "video-uuid", "streamer", "Streamer", "  Broadcast  ",
            "https://kick.com/streamer/videos/video-uuid", "https://stream.kick.com/video.m3u8", "", "Music",
            started.AddHours(1), started, TimeSpan.FromHours(1), 10, "channel-id");
        var desktop = new VodViewModel(video, (_, _) => Task.CompletedTask).Target;
        Assert.Equal("video-uuid", desktop.MediaId);
        Assert.Equal("channel-id", desktop.ChatRoomId);
        Assert.Equal("Music", desktop.CategoryName);
        Assert.Equal("Broadcast", desktop.DisplayTitle);
        fixture.Dependencies.History.Remember(desktop,
            new VodPlaybackBookmark(TimeSpan.FromMinutes(2), video.Duration, DateTimeOffset.UtcNow, false));
        fixture.Dependencies.History.Remember(desktop with { MediaId = video.Id },
            new VodPlaybackBookmark(TimeSpan.FromMinutes(3), video.Duration, DateTimeOffset.UtcNow, false));
        var legacy = video with { Id = "legacy-id", Uuid = "legacy-uuid", Title = "Legacy" };
        fixture.Dependencies.History.Remember(legacy.ToStreamTarget() with { MediaId = legacy.Id },
            new VodPlaybackBookmark(TimeSpan.FromMinutes(5), legacy.Duration, DateTimeOffset.UtcNow, false));

        await using var workspace = new LocalWebWorkspace(fixture.Dependencies with
        {
            KickVods = new FakeKickVodService(new KickVodSearchResult(KickVodSearchStatus.Available, [video, legacy], "", ""))
        });
        var result = await Call(workspace, "vods", new { platform = "kick", channel = "streamer" });
        var card = result.GetProperty("items")[0];
        Assert.Equal(120d, card.GetProperty("resume").GetDouble());
        Assert.Equal("Broadcast", card.GetProperty("title").GetString());
        Assert.Equal(300d, result.GetProperty("items")[1].GetProperty("resume").GetDouble());
        var restored = new JsonVodPlaybackHistory(Path.Combine(fixture.Directory, "history.json"), new MemoryLogger());
        Assert.Equal(TimeSpan.FromMinutes(5), (await restored.GetAsync(legacy.ToStreamTarget()))!.Position);
        var opened = await Call(workspace, "open", new { key = card.GetProperty("key").GetString() });
        Assert.Equal(120d, opened.GetProperty("resume").GetDouble());
    });
}
