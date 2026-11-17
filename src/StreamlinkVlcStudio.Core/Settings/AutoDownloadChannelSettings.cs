using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Core.Settings;

public sealed record AutoDownloadChannelSettings(
    PlatformKind Platform,
    string Channel,
    DateTimeOffset EnabledAtUtc)
{
    public List<string> QueuedVideoIds { get; init; } = [];
}
