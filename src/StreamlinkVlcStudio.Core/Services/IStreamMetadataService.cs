using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Core.Services;

public interface IStreamMetadataService
{
    Task<StreamMetadataResult> GetLiveStreamMetadataAsync(
        StreamTarget target,
        AppSettings settings,
        CancellationToken cancellationToken = default);

    Task<StreamMetadataResult> GetLiveSearchMetadataAsync(
        StreamTarget target,
        AppSettings settings,
        CancellationToken cancellationToken = default) =>
        GetLiveStreamMetadataAsync(target, settings, cancellationToken);
}

public enum StreamMetadataState
{
    Available,
    Offline,
    NotConfigured,
    Unavailable
}

public sealed record StreamMetadataResult(
    StreamMetadataState State,
    string ThumbnailUrl,
    string DisplayName,
    string Message,
    string CategoryName = "",
    string ProfileImageUrl = "",
    string Title = "");
