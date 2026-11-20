using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Workspace.ViewModels;

internal sealed partial class VodDownloadsViewModel
{
    private readonly ITwitchVodService? twitchVodService;
    private readonly IKickVodService? kickVodService;
    private readonly IVodDownloadCompletionProbe? completionProbe;
    private readonly TimeSpan autoDownloadRefreshInterval;
    private readonly object autoDownloadTimerGate = new();
    private System.Threading.Timer? autoDownloadTimer;
    private Task autoDownloadCheckTask = Task.CompletedTask;
    private bool autoDownloadCheckRequested;
    private bool initialized;

    private bool CanAutoDownload(StreamTarget target) => completionProbe is not null &&
        !string.IsNullOrWhiteSpace(target.Channel) &&
        (target.Platform == PlatformKind.Twitch ? twitchVodService is not null : kickVodService is not null);

    private AutoDownloadChannelSettings? FindAutoDownloadChannel(StreamTarget target) =>
        FindAutoDownloadChannel(target.Platform, target.Channel);

    private AutoDownloadChannelSettings? FindAutoDownloadChannel(PlatformKind platform, string name) =>
        Settings.Downloads.AutoDownloadChannels.FirstOrDefault(channel =>
            channel.Platform == platform && channel.Channel.Equals(name, StringComparison.OrdinalIgnoreCase));

    private AutoDownloadChannelSettings? CurrentAutoDownloadChannel(AutoDownloadChannelSettings expected) =>
        !disposed && FindAutoDownloadChannel(expected.Platform, expected.Channel) is { } current &&
        current.EnabledAtUtc == expected.EnabledAtUtc ? current : null;

    private void SetAutoDownloadChannel(StreamTarget target, bool enabled)
    {
        if (disposed || !CanAutoDownload(target)) return;
        var current = FindAutoDownloadChannel(target);
        if ((current is not null) == enabled) return;
        var channels = Settings.Downloads.AutoDownloadChannels.ToList();
        if (current is not null) channels.Remove(current);
        if (enabled) channels.Add(new AutoDownloadChannelSettings(target.Platform, target.Channel, DateTimeOffset.UtcNow));
        Settings.Downloads.AutoDownloadChannels = channels;
        StatusMessage = enabled
            ? $"Auto-download enabled for {target.Channel}. New completed VODs will download while Stream Studio is open."
            : $"Auto-download disabled for {target.Channel}.";
        if (enabled && initialized) Track(CheckAutoDownloadsAsync(checkAgainIfRunning: true));
    }

    private void UpdateAutoDownloadTimer()
    {
        if (!initialized || disposed || completionProbe is null || Settings.Downloads.AutoDownloadChannels.Count == 0)
        {
            StopRefreshTimer(autoDownloadTimerGate, ref autoDownloadTimer);
            return;
        }
        EnsureRefreshTimerStarted(autoDownloadTimerGate, ref autoDownloadTimer, autoDownloadRefreshInterval,
            () => dispatch(() => { if (!disposed) Track(CheckAutoDownloadsAsync()); }));
    }

    internal Task CheckAutoDownloadsAsync(bool checkAgainIfRunning = false)
    {
        if (disposed || completionProbe is null) return Task.CompletedTask;
        if (!autoDownloadCheckTask.IsCompleted)
        {
            autoDownloadCheckRequested |= checkAgainIfRunning;
            return autoDownloadCheckTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Publish before provider calls, which can complete synchronously and trigger UI callbacks.
        autoDownloadCheckTask = completion.Task;
        _ = CompleteAutoDownloadChecksAsync(completion);
        return completion.Task;
    }

    private async Task CompleteAutoDownloadChecksAsync(TaskCompletionSource completion)
    {
        try
        {
            do
            {
                autoDownloadCheckRequested = false;
                await CheckAutoDownloadsCoreAsync();
            }
            while (autoDownloadCheckRequested && !disposed);

            completion.SetResult();
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
        }
    }

    private async Task CheckAutoDownloadsCoreAsync()
    {
        foreach (var channel in Settings.Downloads.AutoDownloadChannels.ToArray())
        {
            if (disposed) return;
            try { await CheckAutoDownloadChannelAsync(channel); }
            catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested) { return; }
            catch (Exception)
            {
                // Do not persist signed media URLs or authentication arguments from provider errors.
                logger.WriteSafely(AppLogLevel.Warning, "VOD auto-download",
                    $"Could not check {channel.Platform} VODs for {channel.Channel}; the next check will retry.");
            }
        }
    }

    private async Task CheckAutoDownloadChannelAsync(AutoDownloadChannelSettings channel)
    {
        var cursor = "";
        var cursors = new HashSet<string>(StringComparer.Ordinal) { cursor };
        var token = lifetimeCancellation.Token;
        do
        {
            if (CurrentAutoDownloadChannel(channel) is null) return;
            List<StreamTarget> videos;
            if (channel.Platform == PlatformKind.Twitch && twitchVodService is not null)
            {
                var result = await twitchVodService.SearchAsync(new TwitchVodSearchRequest(channel.Channel,
                    TwitchVodTypeFilter.Archive, cursor), Settings, token);
                if (!result.IsAvailable) return;
                videos = result.Videos.Where(video => video.Type == TwitchVodTypeFilter.Archive)
                    .Select(video => video.ToStreamTarget()).ToList();
                cursor = result.NextCursor;
            }
            else if (channel.Platform == PlatformKind.Kick && kickVodService is not null)
            {
                var result = await kickVodService.SearchAsync(new KickVodSearchRequest(channel.Channel, cursor, 100), Settings, token);
                if (!result.IsAvailable) return;
                videos = result.Videos.Select(video => video.ToStreamTarget()).ToList();
                cursor = result.NextCursor;
            }
            else return;

            var allVideosAreOlder = videos.Count > 0;
            foreach (var video in videos)
            {
                token.ThrowIfCancellationRequested();
                var current = CurrentAutoDownloadChannel(channel);
                if (current is null) return;
                if (!video.Channel.Equals(channel.Channel, StringComparison.OrdinalIgnoreCase))
                {
                    allVideosAreOlder = false;
                    continue;
                }
                var started = video.MediaStartedAtUtc ?? video.MediaPublishedAtUtc;
                if (started is null || video.MediaDuration <= TimeSpan.Zero)
                {
                    allVideosAreOlder = false;
                    continue;
                }
                // Include broadcasts already recording when enabled, without downloading the old library.
                if (started.Value < channel.EnabledAtUtc && channel.EnabledAtUtc - started.Value > video.MediaDuration)
                {
                    continue;
                }
                allVideosAreOlder = false;
                if (current.QueuedVideoIds.Contains(video.MediaId, StringComparer.OrdinalIgnoreCase)) continue;
                try
                {
                    var request = new VodDownloadRequest(video, getQuality(), Options(), IsAutomatic: true);
                    if (!await completionProbe!.IsCompletedAsync(request, token)) continue;
                    current = CurrentAutoDownloadChannel(channel);
                    if (current is null) return;
                    var item = await service!.EnqueueAsync(request, token);
                    if (disposed) return;
                    Apply(item);
                    current = CurrentAutoDownloadChannel(channel);
                    if (current is null) return;
                    var updated = current with { QueuedVideoIds = [.. current.QueuedVideoIds, video.MediaId] };
                    Settings.Downloads.AutoDownloadChannels = Settings.Downloads.AutoDownloadChannels.Select(entry =>
                        ReferenceEquals(entry, current) ? updated : entry).ToList();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    logger.WriteSafely(AppLogLevel.Warning, "VOD auto-download",
                        $"A VOD for {channel.Platform}:{channel.Channel} is not ready to queue; the next check will retry.");
                }
            }
            // A filtered or empty page does not establish that the next page is old.
            if (allVideosAreOlder) return;
        } while (!string.IsNullOrWhiteSpace(cursor) && cursors.Add(cursor));
    }
}
