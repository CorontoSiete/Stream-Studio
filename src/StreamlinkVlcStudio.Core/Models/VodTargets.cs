using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Core.Models;

/// <summary>Preserves the same VOD identity and metadata across desktop, web, and downloads.</summary>
public static class VodTargets
{
    public static StreamTarget ToStreamTarget(this TwitchVodItem video) => new(
        PlatformKind.Twitch, video.ChannelLogin, video.Url, StreamTargetKind.TwitchVod, video.Id,
        DisplayTitle(video.Title), video.BroadcasterId, video.Duration, video.CreatedAtUtc,
        CategoryName: video.CategoryName, ProfileImageUrl: video.ProfileImageUrl,
        MediaThumbnailUrl: video.ThumbnailUrl, MediaPublishedAtUtc: video.PublishedAtUtc ?? video.CreatedAtUtc);

    public static StreamTarget ToStreamTarget(this KickVodItem video) => new(
        PlatformKind.Kick, video.ChannelSlug, video.Source, StreamTargetKind.KickVod,
        FirstNonEmpty(video.Uuid, video.Id, video.LiveStreamId), DisplayTitle(video.Title), "",
        video.Duration, video.StartedAtUtc, video.ChannelId, video.CategoryName, video.ProfileImageUrl,
        MediaThumbnailUrl: video.ThumbnailUrl, MediaPublishedAtUtc: video.CreatedAtUtc ?? video.StartedAtUtc);

    public static string DisplayTitle(string? title) => string.IsNullOrWhiteSpace(title) ? "Untitled VOD" : title.Trim();
}
