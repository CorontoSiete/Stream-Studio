using System.Globalization;
using System.Text;
using StreamlinkVlcStudio.Core;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Infrastructure.Hls;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Io;
using StreamlinkVlcStudio.Infrastructure.Limits;
using StreamlinkVlcStudio.Infrastructure.Replay;
using static StreamlinkVlcStudio.Infrastructure.Hls.HlsPlaylistPolicy;

namespace StreamlinkVlcStudio.Infrastructure.Vlc;

/// <summary>
/// VLC 3's adaptive demuxer interprets a large MPEG-TS seek as a 33-bit timestamp
/// rollover. Start completed TS replays on a segment near the requested position,
/// keeping their original timeline in the engine instead of exposing that jump to VLC.
/// </summary>
internal static class HlsReplayTimeline
{
    // Below half of the 33-bit / 90 kHz timestamp period (13h 15m 21.859s).
    internal static readonly TimeSpan RebaseThreshold = TimeSpan.FromHours(12);
    private static readonly HttpClient Client = HttpClientFactory.CreateForText(TimeSpan.FromSeconds(15), allowAutoRedirect: false);
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    internal static bool IsPlaylist(Uri uri) => uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

    internal static async Task<TimeSpan?> ReadPublishedDurationAsync(
        Uri uri, PlatformKind platform, CancellationToken cancellationToken)
    {
        if (!IsPlaylist(uri)) return null;
        var (content, _) = await ReadPlaylistAsync(uri, Client, platform, cancellationToken).ConfigureAwait(false);
        return content is null ? null : ParsePublishedDuration(content);
    }

    internal static TimeSpan? ParsePublishedDuration(string content) =>
        HlsPlaylistPolicy.ReadPublishedDuration(HlsPlaylistPolicy.SplitLines(content));

    internal static async Task<PlaybackMediaSource> PrepareAsync(
        PlaybackMediaSource source, TimeSpan position, CancellationToken cancellationToken,
        HttpClient? httpClient = null)
    {
        if (position < RebaseThreshold || !IsPlaylist(source.PlaybackUri)) return source;
        var (content, uri) = await ReadPlaylistAsync(source.PlaybackUri, httpClient ?? Client,
            platform: null, cancellationToken).ConfigureAwait(false);
        if (content is null) throw new InvalidDataException("The replay playlist is missing or too large.");
        var timeline = Rebase(content, uri, position);
        if (timeline is null) return source;
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppIdentity.ProductDirectoryName, "ReplayTimelines", Guid.NewGuid().ToString("N") + ".m3u8");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await File.WriteAllTextAsync(path, timeline.Value.Playlist, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            return new PlaybackMediaSource(new Uri(path), new TimelineLease(path, source), timeline.Value.Offset,
                source.UseAvformatDemuxer, source.ReplaySeekPreroll, source.UseLiveReplayDemuxer,
                source.LiveReplaySegmentDuration);
        }
        catch
        {
            AtomicFile.TryDeleteTemporaryFile(path);
            throw;
        }
    }

    private static async Task<(string? Content, Uri Uri)> ReadPlaylistAsync(
        Uri uri, HttpClient client, PlatformKind? platform, CancellationToken cancellationToken)
    {
        if (uri.IsFile)
        {
            var bytes = await BoundedByteReader.ReadFileAsync(uri.LocalPath, PayloadLimits.PlaylistBytes, cancellationToken)
                .ConfigureAwait(false);
            return (bytes is null ? null : StrictUtf8.GetString(bytes), uri);
        }

        if (uri.IsLoopback)
        {
            // Loopback media belongs to the muted-segment proxy or a local transport.
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await BoundedHttpResponseSender.SendAsync(client, request, cancellationToken)
                .ConfigureAwait(false);
            return (await BoundedHttpContentReader.ReadPlaylistAsync(response, cancellationToken).ConfigureAwait(false), uri);
        }

        var provider = platform ?? (ReplayUrlSecurityValidator.TryValidateProviderUri(uri, PlatformKind.Twitch)
            ? PlatformKind.Twitch : PlatformKind.Kick);
        return await ValidatedReplayHttpClient.ReadPlaylistAsync(
            client, ReplayUrlSecurityValidator.Shared, uri, provider, cancellationToken).ConfigureAwait(false);
    }

    internal static (string Playlist, TimeSpan Offset)? Rebase(string content, Uri uri, TimeSpan position)
    {
        var lines = HlsPlaylistPolicy.SplitLines(content);
        // Growing playlists must retain their refresh URL; fMP4 and encrypted/byte-range
        // playlists have different initialization semantics and do not use this TS adapter.
        if (lines.FirstOrDefault() != "#EXTM3U" || !lines.Contains("#EXT-X-ENDLIST") ||
            HlsPlaylistPolicy.HasSkippedSegments(lines) ||
            lines.Any(line => line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal) ||
                (line.StartsWith('#') && line.Contains("URI=", StringComparison.OrdinalIgnoreCase)))) return null;

        var segments = new List<(int End, TimeSpan Time)>();
        var elapsed = TimeSpan.Zero;
        var waitingForSegment = false;
        var encounteredDiscontinuity = false;
        var duration = TimeSpan.Zero;
        long? sequence = null, discontinuitySequence = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (TryGetTagValue(line, MediaSequenceTag, out var mediaSequenceValue))
            {
                if (!TryReadSequence(mediaSequenceValue, segments.Count > 0, ref sequence))
                    throw new InvalidDataException("Replay playlist has an invalid media sequence number.");
            }
            else if (TryGetTagValue(line, DiscontinuitySequenceTag, out var discontinuitySequenceValue))
            {
                if (!TryReadSequence(discontinuitySequenceValue, segments.Count > 0 || encounteredDiscontinuity,
                    ref discontinuitySequence))
                    throw new InvalidDataException("Replay playlist has an invalid discontinuity sequence number.");
            }
            else if (line == "#EXT-X-DISCONTINUITY")
                encounteredDiscontinuity = true;
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                if (waitingForSegment) throw new InvalidDataException("Replay playlist has a missing media segment.");
                if (!HlsPlaylistPolicy.TryReadSegmentDuration(line.AsSpan(8), 3600, out var seconds))
                    throw new InvalidDataException("Replay playlist has an invalid segment duration.");
                duration = TimeSpan.FromTicks(checked((long)(seconds * TimeSpan.TicksPerSecond)));
                waitingForSegment = true;
            }
            else if (line[0] != '#')
            {
                if (!waitingForSegment) throw new InvalidDataException("Replay playlist has an unpaired media segment.");
                var segmentUri = new Uri(uri, line);
                if (!segmentUri.AbsolutePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)) return null;
                ValidateSegment(uri, segmentUri);
                lines[i] = segmentUri.AbsoluteUri;
                segments.Add((i, elapsed));
                elapsed += duration;
                waitingForSegment = false;
            }
        }
        if (waitingForSegment) throw new InvalidDataException("Replay playlist has a missing media segment.");
        if (segments.Count == 0) return null;
        // Keep one preceding segment for decoder preroll, including exact segment boundaries.
        var selected = segments.FindLastIndex(segment => segment.Time <= position);
        selected = Math.Max(0, selected - 1);
        if (selected == 0) return null;
        var first = segments[selected];
        var boundary = segments[selected - 1].End + 1;
        var discontinuities = lines.Take(boundary).Count(line => line == "#EXT-X-DISCONTINUITY");
        var result = new StringBuilder("#EXTM3U\n");
        foreach (var line in lines.Where(IsPlaybackHeader)) result.AppendLine(line);
        result.AppendLine("#EXT-X-PLAYLIST-TYPE:VOD");
        AppendSequence(result, MediaSequenceTag, sequence, selected);
        AppendSequence(result, DiscontinuitySequenceTag, discontinuitySequence, discontinuities);
        foreach (var line in lines.Skip(boundary))
            if (!IsPlaybackHeader(line) && line != "#EXT-X-ENDLIST" &&
                !line.StartsWith("#EXT-X-PLAYLIST-TYPE:", StringComparison.Ordinal)) result.AppendLine(line);
        result.AppendLine("#EXT-X-ENDLIST");
        return (result.ToString(), first.Time);
    }

    private static void AppendSequence(StringBuilder playlist, string tag, long? sequence, int offset)
    {
        var start = sequence.GetValueOrDefault();
        if (start > long.MaxValue - offset)
            throw new InvalidDataException("Replay playlist sequence exceeds the supported range.");
        playlist.Append(tag).Append(':').AppendLine((start + offset).ToString(CultureInfo.InvariantCulture));
    }

    private static bool IsPlaybackHeader(string line) =>
        line.StartsWith("#EXT-X-VERSION:", StringComparison.Ordinal) ||
        line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal) || line == "#EXT-X-INDEPENDENT-SEGMENTS";

    private static void ValidateSegment(Uri playlist, Uri segment)
    {
        if (ReplayUrlSecurityValidator.TryValidateProviderUri(segment, PlatformKind.Twitch) ||
            ReplayUrlSecurityValidator.TryValidateProviderUri(segment, PlatformKind.Kick)) return;
        if (playlist.IsLoopback && !playlist.IsFile && segment.Scheme == playlist.Scheme &&
            segment.Authority == playlist.Authority) return;
        if (playlist.IsFile && segment.IsFile && string.Equals(Path.GetDirectoryName(playlist.LocalPath),
            Path.GetDirectoryName(segment.LocalPath), StringComparison.OrdinalIgnoreCase)) return;
        throw new InvalidDataException("Replay playlist contains an unapproved media URL.");
    }

    private sealed class TimelineLease(string path, PlaybackMediaSource source) : IDisposable
    {
        public void Dispose() { AtomicFile.TryDeleteTemporaryFile(path); source.Dispose(); }
    }
}
