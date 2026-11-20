using System.Collections.Immutable;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Workspace.ViewModels;

/// <summary>Optional host integration for rendering and interacting with chat over native video.</summary>
public interface INativeChatOverlay : IAsyncDisposable
{
    string PositionStatePath { get; }
    bool TryGetLastVideoSize(out int width, out int height);
    bool ShouldUseNativeOverlayController(AppSettings settings);
    bool IsNativeOverlayChatCurrent(AppSettings settings);
    Task<bool> StartNativeOverlayChatTrackedAsync(AppSettings settings, CancellationToken cancellationToken, bool startCaptureChatClient);
    void StartNativeOverlayChatInBackground(AppSettings settings, CancellationToken cancellationToken, bool startCaptureChatClient);
    Task StopNativeOverlayChatAsync(bool clearOverlay = false);
    Task StopNativeOverlayChatAfterReplayTransitionAsync();
    /// <summary>Detaches the current overlay and returns its deferred cleanup, if one was detached.</summary>
    Func<Task>? DetachForReplayTransition();
    Task BlankNativeOverlayAsync(string? pipeName, CancellationToken cancellationToken);
    Task<bool> TryReleaseNativeOverlayChatInputFocusAsync();
    void ClearNativeReplayOverlayForReplayTransition(ReplaySessionInfo replay, bool targetWindowHasReplayMessages);
    void ClearNativeReplayOverlayForEmptyReplayWindowInBackground();
    void QueueNativeChatOverlayUpdateAfterReplayWindowApply();
    void MarkNativeReplayOverlayRefreshPendingAfterSeek();
    void FlushNativeReplayOverlayRefreshAfterSeek();
    void UpdateNativeChatOverlay();
    void ResetNativeReplayOverlayScrollState();
    void SuspendNativeReplayOverlayResizePersistence();
    void StopNativeReplayOverlayEventHost();
    Task StopNativeReplayOverlayEventHostAsync();
    void InvalidateNativeReplayOverlayFrame();
    void CancelNativeReplayOverlayAnimationState();
    void InvalidatePendingFrame();
    void RefreshNativeReplayOverlayForVideoSize(int width, int height);
}

/// <summary>State and actions available to a tab's overlay for the duration of that tab.</summary>
public sealed record NativeOverlayContext(
    StreamTarget Target,
    IAppLogger Logger,
    Action<Action> Dispatch,
    Action<string> AddSystemMessage,
    Func<NativeOverlayPlaybackSnapshot?> CapturePlayback,
    Func<NativeOverlayChatSnapshot> CaptureChat,
    Func<ImmutableArray<ChatMessage>> CaptureMessages,
    Func<AppSettings?> GetSettings,
    Func<CancellationToken, Task> StartChat,
    Func<CancellationToken, Task> EnsureChatConnected,
    Func<AppSettings, bool> ShouldCaptureChat,
    Action<Uri>? OpenChatLink,
    CancellationToken CancellationToken);
