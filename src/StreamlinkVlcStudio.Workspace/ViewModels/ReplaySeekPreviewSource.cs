using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Workspace.ViewModels;

public sealed record ReplaySeekPreviewSource(string ReplayId, string? VideoId, Uri? PlaylistUri,
    PlatformKind Platform, DateTimeOffset? StartedAt, string VlcDirectory);
