using StreamlinkVlcStudio.Core.Models;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Core.Settings;

public sealed class RecentStreamSettings
{
    public PlatformKind Platform { get; set; }

    public string Channel { get; set; } = "";

    public string Url { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string CategoryName { get; set; } = "";

    public string ThumbnailUrl { get; set; } = "";

    public string ProfileImageUrl { get; set; } = "";

    public string LastQuality { get; set; } = "";

    public DateTimeOffset LastWatchedAtUtc { get; set; }

    public static RecentStreamSettings FromPlayback(
        StreamTarget target,
        string quality,
        DateTimeOffset watchedAtUtc,
        RecentStreamSettings? previous,
        string? displayName = null,
        string? categoryName = null,
        string? thumbnailUrl = null,
        string? profileImageUrl = null) => new()
        {
            Platform = target.Platform,
            Channel = target.Channel,
            Url = target.Url,
            DisplayName = FirstNonEmpty(displayName, previous?.DisplayName, target.Channel),
            CategoryName = FirstNonEmpty(target.CategoryName, categoryName, previous?.CategoryName),
            ThumbnailUrl = FirstNonEmpty(thumbnailUrl, previous?.ThumbnailUrl),
            ProfileImageUrl = FirstNonEmpty(profileImageUrl, target.ProfileImageUrl, previous?.ProfileImageUrl),
            LastQuality = quality,
            LastWatchedAtUtc = watchedAtUtc
        };

    public bool Matches(StreamTarget target) =>
        Platform == target.Platform && string.Equals(Channel, target.Channel, StringComparison.OrdinalIgnoreCase);
}
