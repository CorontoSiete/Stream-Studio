using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

internal sealed partial class VodDownloadsViewModel
{
    private readonly ITwitchVodService? twitchVodService;
    private readonly IKickVodService? kickVodService;
    private readonly IVodDownloadCompletionProbe? completionProbe;
    private readonly TimeSpan autoDownloadRefreshInterval;
    private readonly object autoDownloadTimerGate = new();
    private System.Threading.Timer? autoDownloadTimer;
    private Task autoDownloadCheckTask = Task.CompletedTask;
    private bool initialized;

    private bool CanAutoDownload(StreamTarget target) => completionProbe is not null &&
        !string.IsNullOrWhiteSpace(target.Channel) &&
        (target.Platform == PlatformKind.Twitch ? twitchVodService is not null : kickVodService is not null);

    private AutoDownloadChannelSettings? FindAutoDownloadChannel(StreamTarget target) =>
        Settings.Downloads.AutoDownloadChannels.FirstOrDefault(channel =>
            channel.Platform == target.Platform && channel.Channel.Equals(target.Channel, StringComparison.OrdinalIgnoreCase));

    private AutoDownloadChannelSettings? CurrentAutoDownloadChannel(AutoDownloadChannelSettings expected) =>
        disposed ? null : Settings.Downloads.AutoDownloadChannels.FirstOrDefault(channel =>
            channel.Platform == expected.Platform && channel.Channel.Equals(expected.Channel, StringComparison.OrdinalIgnoreCase) &&
            channel.EnabledAtUtc == expected.EnabledAtUtc);

    private void SetAutoDownloadChannel(StreamTarget target, bool enabled)
    {
        if (disposed || !CanAutoDownload(target)) return;
        var channels = Settings.Downloads.AutoDownloadChannels.Where(channel =>
            channel.Platform != target.Platform || !channel.Channel.Equals(target.Channel, StringComparison.OrdinalIgnoreCase)).ToList();
        if (enabled) channels.Add(new AutoDownloadChannelSettings(target.Platform, target.Channel, DateTimeOffset.UtcNow));
        Settings.Downloads.AutoDownloadChannels = channels;
        StatusMessage = enabled
            ? $"Auto-download enabled for {target.Channel}. New completed VODs will download while Stream Studio is open."
            : $"Auto-download disabled for {target.Channel}.";
        if (enabled && initialized) Track(CheckAutoDownloadsAsync());
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

    internal Task CheckAutoDownloadsAsync()
    {
        if (disposed || completionProbe is null) return Task.CompletedTask;
        if (!autoDownloadCheckTask.IsCompleted) return autoDownloadCheckTask;
        return autoDownloadCheckTask = CheckAutoDownloadsCoreAsync();
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
                if (!video.Channel.Equals(channel.Channel, StringComparison.OrdinalIgnoreCase)) continue;
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
            if (allVideosAreOlder || videos.Count == 0) return;
        } while (!string.IsNullOrWhiteSpace(cursor) && cursors.Add(cursor));
    }
}
