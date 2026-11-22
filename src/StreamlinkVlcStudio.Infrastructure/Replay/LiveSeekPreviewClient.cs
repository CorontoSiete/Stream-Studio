using System.Globalization;
using System.Net;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Security;
using StreamlinkVlcStudio.Infrastructure.Hls;
using StreamlinkVlcStudio.Infrastructure.Http;

namespace StreamlinkVlcStudio.Infrastructure.Replay;

/// <summary>Reads only the DVR segment covering a hover, never the continuous live transport.</summary>
internal sealed class LiveSeekPreviewClient
{
    private static readonly HttpClient SharedClient = HttpClientFactory.Create(
        TimeSpan.FromSeconds(8), allowAutoRedirect: false);
    private readonly HttpClient httpClient;
    private readonly ReplayUrlSecurityValidator validator;

    internal LiveSeekPreviewClient() : this(SharedClient, ReplayUrlSecurityValidator.Shared) { }

    internal LiveSeekPreviewClient(HttpClient httpClient, ReplayUrlSecurityValidator validator)
    {
        this.httpClient = httpClient;
        this.validator = validator;
    }

    internal async Task<LiveSeekPlaylist?> GetPlaylistAsync(Uri uri, PlatformKind platform,
        DateTimeOffset? startedAt, CancellationToken cancellationToken)
    {
        using var response = await GetAsync(uri, platform, cancellationToken).ConfigureAwait(false);
        var text = await BoundedHttpContentReader.ReadPlaylistAsync(response, cancellationToken,
            maxBytes: 2 * 1024 * 1024).ConfigureAwait(false);
        return LiveSeekPlaylist.Parse(text, response.RequestMessage?.RequestUri ?? uri, platform, startedAt);
    }

    internal async Task<byte[]> GetSegmentAsync(Uri uri, PlatformKind platform, CancellationToken cancellationToken,
        bool initialization = false)
    {
        using var response = await GetAsync(uri, platform, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidDataException("The seek preview server did not return a complete media segment.");
        var bytes = await BoundedByteReader.ReadOrThrowAsync(response.Content,
            initialization ? 1024 * 1024 : 16 * 1024 * 1024, cancellationToken)
            .ConfigureAwait(false);
        if (bytes.Length == 0) throw new InvalidDataException("The seek preview media segment was empty.");
        return bytes;
    }

    private Task<HttpResponseMessage> GetAsync(Uri uri, PlatformKind platform, CancellationToken token) =>
        ValidatedReplayHttpClient.SendGetAsync(httpClient, validator, uri, platform,
            static address => new HttpRequestMessage(HttpMethod.Get, address), token);
}

internal sealed record LiveSeekSegment(Uri Uri, double Start, double Duration, Uri? InitializationUri = null);

internal sealed record LiveSeekPlaylist(IReadOnlyList<LiveSeekSegment> Segments)
{
    internal LiveSeekSegment? GetSegment(double seconds) => double.IsFinite(seconds) && seconds >= 0
        ? Segments.FirstOrDefault(segment => seconds >= segment.Start && seconds < segment.Start + segment.Duration)
        : null;

    internal static LiveSeekPlaylist? Parse(string text, Uri uri, PlatformKind platform, DateTimeOffset? startedAt)
    {
        var lines = HlsPlaylistPolicy.SplitLines(text);
        if (lines.Length == 0 || lines[0] != "#EXTM3U" ||
            HlsPlaylistPolicy.HasSkippedSegments(lines) ||
            !ProviderUriPolicy.IsApprovedReplayUri(uri, platform)) return null;
        var segments = new List<LiveSeekSegment>();
        double? position = null;
        string? programDateTime = null;
        double? duration = null;
        long? sequence = null;
        var hasMediaSegment = false;
        var encrypted = false;
        var gap = false;
        Uri? initializationUri = null;
        var initializationEncrypted = false;
        foreach (var line in lines.Skip(1))
        {
            if (HlsPlaylistPolicy.TryGetTagValue(line, HlsPlaylistPolicy.MediaSequenceTag, out var sequenceValue))
            {
                if (!HlsPlaylistPolicy.TryReadSequence(sequenceValue, hasMediaSegment, ref sequence)) return null;
            }
            else if (HlsPlaylistPolicy.TryGetTagValue(line, "#EXT-X-TWITCH-ELAPSED-SECS", out var elapsedValue))
            {
                if (!TrySeconds(elapsedValue, out var elapsed)) return null;
                position = elapsed;
            }
            else if (HlsPlaylistPolicy.TryGetTagValue(line, "#EXT-X-PROGRAM-DATE-TIME", out var dateValue) &&
                position is null && startedAt.HasValue)
            {
                // The media sequence can appear after this tag and still apply to the first segment.
                programDateTime ??= dateValue.ToString();
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                if (duration is not null || !HlsPlaylistPolicy.TryReadSegmentDuration(line.AsSpan(8), 30, out var length)) return null;
                duration = (double)length;
            }
            else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
            {
                if (!HlsAttributeList.TryParse(line.AsSpan(11), out var key) ||
                    !key.TryGetValue("METHOD", out var method) || method.IsQuoted) return null;
                encrypted = method.Value != "NONE";
                if (!encrypted && key.Count != 1) return null;
            }
            else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                // A fragmented MP4 media segment needs the EXT-X-MAP which applies to it.
                // Keep that association per segment, including changes after discontinuities.
                if (!HlsPlaylistPolicy.TryReadWholeMapUri(line[11..], out var mapUri) ||
                    !ProviderUriPolicy.TryResolveReplayUri(mapUri,
                    uri, platform, out initializationUri)) return null;
                initializationEncrypted = encrypted;
            }
            else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) return null;
            else if (line == "#EXT-X-GAP") gap = true;
            else if (line[0] != '#')
            {
                if (duration is not { } length) return null;
                // Sequence numbers count segments, not seconds. A sliding playlist needs an
                // explicit clock; guessing sequence * target-duration shows the wrong frame.
                if (position is null && sequence is > 0)
                {
                    if (startedAt is not { } start || !DateTimeOffset.TryParse(programDateTime,
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)) return null;
                    position = (time - start).TotalSeconds;
                }
                position ??= 0;
                if (!ProviderUriPolicy.TryResolveReplayUri(line, uri, platform, out var segmentUri)) return null;
                var isTransportStream = segmentUri.AbsolutePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase);
                var isFragmentedMp4 = initializationUri is not null &&
                    (segmentUri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                     segmentUri.AbsolutePath.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase));
                if (!encrypted && !initializationEncrypted && !gap && (isTransportStream || isFragmentedMp4))
                    segments.Add(new(segmentUri, position.Value, length, isFragmentedMp4 ? initializationUri : null));
                position += length;
                duration = null;
                gap = false;
                hasMediaSegment = true;
            }
        }
        return segments.Count > 0 && duration is null ? new(segments) : null;
    }

    private static bool TrySeconds(ReadOnlySpan<char> value, out double seconds) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) &&
        double.IsFinite(seconds) && seconds >= 0;

}
