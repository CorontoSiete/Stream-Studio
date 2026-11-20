using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Workspace.ViewModels;

/// <summary>Named composition for the reusable per-stream workspace boundary.</summary>
public sealed record StreamTabViewModelDependencies
{
    public required StreamTarget Target { get; init; }
    public required string Quality { get; init; }
    public required IStreamlinkService StreamlinkService { get; init; }
    public required IPlaybackEngineFactory PlaybackFactory { get; init; }
    public required IChatClientFactory ChatFactory { get; init; }
    public required IAppLogger Logger { get; init; }
    /// <summary>Runs or queues callbacks on the host thread that owns the tab state.</summary>
    public required Action<Action> Dispatch { get; init; }
    public Func<NativeOverlayContext, INativeChatOverlay>? CreateNativeChatOverlay { get; init; }
    public Func<IPlaybackParkingSurface>? CreateParkingVideoSurface { get; init; }
    public Action<ChatSettings>? ConfigureChatPresentation { get; init; }
    public Action<Uri>? OpenChatLink { get; init; }
    public int InitialVolume { get; init; } = StreamTabViewModel.DefaultVolume;
    public IViewerCountService? ViewerCountService { get; init; }
    public IReplayResolver? ReplayResolver { get; init; }
    public IVodChatProvider? VodChatProvider { get; init; }
    public IVodPlaybackHistory? VodPlaybackHistory { get; init; }
    public IVodDownloadPlayback? OfflinePlayback { get; init; }
    public TimeSpan? TwitchLiveDvrPromotionPollInterval { get; init; }
    public ITwitchSubOnlyVodResolver? TwitchSubOnlyVodResolver { get; init; }
}
