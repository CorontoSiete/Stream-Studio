using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Core.Settings;

public sealed record AutomaticVodDownloadSettings(Guid DownloadId, PlatformKind Platform, string Channel, bool Completed = false);
