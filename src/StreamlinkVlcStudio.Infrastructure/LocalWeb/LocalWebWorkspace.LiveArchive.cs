using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Infrastructure.LocalWeb;

public sealed partial class LocalWebWorkspace
{
    private sealed record LiveArchiveState(bool Available = false, bool Pending = false, string Key = "", double Duration = 0, string Message = "", string Url = "");
    private sealed record LiveArchivePreview(LocalWebMedia Media, string Key);

    private LiveArchiveState GetLiveArchive(JsonElement input, AppSettings settings)
    {
        var player = GetPlayer(input);
        if (player.Target.IsExplicitVod) throw new ArgumentException("Select a live stream.");
        // Like follow/status, lookup belongs to the player and never holds the session's
        // command gate while the provider or Streamlink is working.
        if (player.ArchiveLookup is null || player.ArchiveLookup.IsCompleted && DateTimeOffset.UtcNow >= player.ArchiveRetryAt)
        {
            player.ArchiveRetryAt = DateTimeOffset.MaxValue;
            player.ArchiveLookup = Task.Run(() => ResolveLiveArchiveAsync(player, settings));
        }
        return player.ArchiveLookup.IsCompletedSuccessfully ? player.ArchiveLookup.Result : new(Pending: true);
    }

    private async Task<LiveArchiveState> ResolveLiveArchiveAsync(Player player, AppSettings settings)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(player.Lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(75));
        var token = timeout.Token;
        LocalWebMedia? media = null;
        try
        {
            if (player.ArchivePreview is { } existing)
            {
                try
                {
                    var currentDuration = await existing.Media.ReadArchiveDurationAsync(player.Quality, token).ConfigureAwait(false);
                    return new(true, Key: existing.Key, Duration: currentDuration, Url: existing.Media.PathFor(existing.Media.Root));
                }
                catch (OperationCanceledException) { throw; }
                catch { /* Re-resolve an expired or replaced provider URL. */ }
            }
            var replay = await services.Replay.ResolveCurrentReplayAsync(player.Target, player.Quality, settings, token).ConfigureAwait(false);
            if (!replay.IsAvailable) return new(Message: replay.UnavailableReason);
            if (replay.Platform != player.Target.Platform || !replay.Channel.Equals(player.Target.Channel, StringComparison.OrdinalIgnoreCase))
                return new(Message: "The archive does not match this broadcast.");
            var target = player.Target with
            {
                Kind = replay.Platform == PlatformKind.Twitch ? StreamTargetKind.TwitchVod : StreamTargetKind.KickVod,
                Url = replay.ReplayUrl,
                MediaId = replay.ReplayId,
                MediaStartedAtUtc = replay.StreamStartedAtUtc,
                ChatRoomId = replay.ChatRoomId,
                DisplayTitle = player.Target.Channel + " replay"
            };
            var resolved = await ResolveAsync(target, replay.GetStreamlinkQuality(player.Quality), false, settings, token, liveArchive: true).ConfigureAwait(false);
            media = resolved.TrustedLocal
                ? await LocalWebMedia.FromTrustedPlaylistAsync(player.Media.PlaybackId, resolved.Uri, token).ConfigureAwait(false)
                : services.CreateArchiveMedia(player.Media.PlaybackId, resolved.Uri);
            media.RewriteTwitchArchive = resolved.RewriteTwitchArchive;
            // Provider metadata sometimes contains an estimated duration. Only advertise
            // history backed by a complete playlist starting at the broadcast's first segment.
            var duration = await media.ReadArchiveDurationAsync(player.Quality, token, selectVariant: true).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var key = Register(target with { MediaDuration = TimeSpan.FromSeconds(duration) });
            lock (playerGate) liveArchiveKeys.Add(key);
            var state = new LiveArchiveState(true, Key: key, Duration: duration, Url: media.PathFor(media.Root));
            var previous = player.ArchivePreview;
            player.ArchivePreview = new(media, key);
            media = null; // Owned by the live player, including all preview segment IDs.
            previous?.Media.Dispose();
            return state;
        }
        catch (OperationCanceledException) when (player.Lifetime.IsCancellationRequested) { return new(); }
        catch { return new(Message: "Full broadcast history is unavailable. Retrying automatically."); }
        finally
        {
            media?.Dispose();
            player.ArchiveRetryAt = DateTimeOffset.UtcNow.AddSeconds(30);
        }
    }
}
