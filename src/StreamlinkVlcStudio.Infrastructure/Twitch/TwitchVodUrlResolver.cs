using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Twitch;
using StreamlinkVlcStudio.Infrastructure.Hls;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Replay;

namespace StreamlinkVlcStudio.Infrastructure.Twitch;

/// <summary>
/// Resolves ordinary Twitch archives without starting Python or parsing every quality's
/// media playlist. Uses Twitch's playback authorization and validates the selected playlist.
/// Unsupported inputs and provider failures are handled by the caller's Streamlink fallback.
/// </summary>
internal sealed class TwitchVodUrlResolver
{
    private static readonly HttpClient SharedClient = HttpClientFactory.Create(
        TimeSpan.FromSeconds(8), includeUserAgent: true, allowAutoRedirect: false);
    private readonly HttpClient httpClient;
    private readonly ReplayUrlSecurityValidator validator;
    private readonly TwitchVodPlaylistHandoff playlistHandoff;

    internal TwitchVodUrlResolver() : this(SharedClient, ReplayUrlSecurityValidator.Shared, TwitchVodPlaylistHandoff.Shared) { }

    internal TwitchVodUrlResolver(HttpClient httpClient, ReplayUrlSecurityValidator validator,
        TwitchVodPlaylistHandoff? playlistHandoff = null)
    {
        this.httpClient = httpClient;
        this.validator = validator;
        this.playlistHandoff = playlistHandoff ?? new TwitchVodPlaylistHandoff();
    }

    internal async Task<StreamlinkResolvedUrl> ResolveAsync(StreamTransportRequest request, CancellationToken cancellationToken)
    {
        var id = request.Target.MediaId;
        if (string.IsNullOrEmpty(id) || !id.All(char.IsAsciiDigit))
            throw new InvalidDataException("A numeric Twitch video ID is required.");

        var masterUri = await new TwitchGraphQlTransport(httpClient).GetPlaybackMasterUriAsync(request.Target, cancellationToken).ConfigureAwait(false);
        var master = await ValidatedReplayHttpClient.ReadPlaylistAsync(httpClient, validator, masterUri,
            PlatformKind.Twitch, cancellationToken).ConfigureAwait(false);
        var selected = TwitchVodVariantPlaylist.Select(master.Content, master.Uri, request.Quality);
        var media = await ValidatedReplayHttpClient.ReadPlaylistAsync(httpClient, validator, selected,
            PlatformKind.Twitch, cancellationToken).ConfigureAwait(false);
        var validated = ValidateMediaPlaylist(media.Content, media.Uri);
        cancellationToken.ThrowIfCancellationRequested();
        playlistHandoff.Offer(media.Uri, validated);
        return new StreamlinkResolvedUrl(media.Uri, "Resolved selected Twitch VOD quality directly.");
    }

    private static string ValidateMediaPlaylist(string content, Uri uri)
    {
        var lines = HlsPlaylistPolicy.SplitLines(content);
        if (lines.Length == 0 || lines[0] != "#EXTM3U" || HlsPlaylistPolicy.HasSkippedSegments(lines) ||
            !TwitchMutedVodPlaylist.Inspect(content).IsMediaPlaylist)
            throw new InvalidDataException("Twitch did not return a playable media playlist.");
        var pendingSegment = false;
        var segments = 0;
        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                if (pendingSegment || !HlsPlaylistPolicy.TryReadSegmentDuration(line.AsSpan(8), 3600, out _))
                    throw new InvalidDataException("The selected VOD playlist has an invalid segment duration.");
                pendingSegment = true;
            }
            else if (!line.StartsWith('#'))
            {
                if (!pendingSegment) throw new InvalidDataException("The selected VOD playlist has an unpaired segment.");
                pendingSegment = false;
                segments++;
            }
        }
        if (pendingSegment || segments == 0) throw new InvalidDataException("The selected VOD playlist has missing segments.");
        // Reuse the existing provider policy for segment, key and initialization URLs.
        // Playback still uses the remote URL, preserving growing-playlist refreshes.
        return TwitchMutedVodPlaylist.RewriteForRepair(content, uri, static segment => segment.AbsoluteUri);
    }
}
