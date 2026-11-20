using System.Globalization;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Infrastructure.Hls;

namespace StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal sealed partial class LocalWebMedia
{
    internal async Task<double> ReadArchiveDurationAsync(string quality, CancellationToken token, bool selectVariant = false)
    {
        var uri = resources[Root];
        (string Playlist, Uri Uri)? selectedMaster = null;
        for (var depth = 0; depth < 4; depth++)
        {
            string content;
            // Measure the same pinned master that the player receives. Only its
            // selected media playlist needs to be fetched again as it grows.
            if (depth == 0 && trustedPlaylist is { } rootPlaylist) content = rootPlaylist;
            else
            {
                using var response = await FetchAsync(uri, "", token).ConfigureAwait(false);
                content = await ReadRemotePlaylistAsync(response, token).ConfigureAwait(false);
                uri = response.RequestMessage!.RequestUri!;
            }
            RewritePlaylist(content, uri); // Validate every resource through the normal proxy policy.
            var lines = HlsPlaylistPolicy.SplitLines(content);
            if (lines.Any(line => line.StartsWith("#EXTINF:", StringComparison.Ordinal)))
            {
                var duration = ArchiveDuration(lines);
                // Only a narrowed master needs pinning. Direct media playlists must
                // keep following their original URL to refresh expiring CDN redirects.
                if (selectedMaster is { } master)
                {
                    lock (gate)
                    {
                        ids.Remove(resources[Root].AbsoluteUri);
                        resources[Root] = master.Uri;
                        ids[master.Uri.AbsoluteUri] = Root;
                        trustedPlaylist = master.Playlist;
                    }
                }
                return duration;
            }
            var variants = new List<(Uri Uri, int Height, double FrameRate, long Bandwidth, bool AudioOnly, int Line)>();
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith(HlsPlaylistPolicy.VariantTag, StringComparison.Ordinal)) continue;
                if (!HlsPlaylistPolicy.TryReadVariant(lines, i, uri, out var attributes, out var variant) ||
                    !IsSafeUri(variant))
                    throw new InvalidDataException("Invalid archive variants.");
                attributes.TryGetValue("RESOLUTION", out var resolution);
                attributes.TryGetValue("BANDWIDTH", out var bandwidth);
                attributes.TryGetValue("FRAME-RATE", out var frameRate);
                int.TryParse(resolution.Value?.Split('x').LastOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var height);
                long.TryParse(bandwidth.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var rate);
                double.TryParse(frameRate.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var fps);
                variants.Add((variant, height, fps, rate, IsAudioOnlyVariant(attributes), i));
                i++;
            }
            if (variants.Count == 0) throw new InvalidDataException("No archive video playlist.");
            var ordered = variants.OrderByDescending(v => v.AudioOnly).ThenBy(v => v.Height).ThenBy(v => v.Bandwidth).ToArray();
            var digits = new string(quality.TakeWhile(char.IsAsciiDigit).ToArray());
            var selected = ordered[^1];
            if (QualityOption.IsAudioOnly(quality)) selected = ordered[0];
            else if (quality == "worst") selected = ordered.FirstOrDefault(v => !v.AudioOnly, ordered[0]);
            if (int.TryParse(digits, out var wanted))
            {
                var matches = ordered.Where(v => v.Height == wanted);
                selected = quality.EndsWith("p60", StringComparison.Ordinal)
                    ? matches.OrderBy(v => Math.Abs(v.FrameRate - 60)).ThenByDescending(v => v.Bandwidth).FirstOrDefault(selected)
                    : matches.LastOrDefault(selected);
            }
            if (selectVariant)
            {
                var narrowed = new List<string>();
                for (var i = 0; i < lines.Length; i++)
                {
                    if (lines[i].StartsWith(HlsPlaylistPolicy.VariantTag, StringComparison.Ordinal))
                    {
                        if (i == selected.Line) { narrowed.Add(lines[i]); narrowed.Add(lines[i + 1]); }
                        i++;
                    }
                    else narrowed.Add(lines[i]);
                }
                selectedMaster = (string.Join('\n', narrowed) + "\n", uri);
            }
            uri = selected.Uri;
        }
        throw new InvalidDataException("Archive playlist nesting exceeds the limit.");
    }

    private static double ArchiveDuration(string[] lines) =>
        HlsPlaylistPolicy.ReadPublishedDuration(lines)?.TotalSeconds ??
        throw new InvalidDataException("Empty, incomplete or unsupported archive timeline.");

    private static bool IsAudioOnlyVariant(Dictionary<string, HlsAttributeValue> attributes)
    {
        if (attributes.ContainsKey("RESOLUTION")) return false;
        if (attributes.TryGetValue("IVS-NAME", out var name) && QualityOption.IsAudioOnly(name.Value)) return true;
        return attributes.TryGetValue("CODECS", out var codecs) && codecs.Value.Split(',', StringSplitOptions.TrimEntries)
            .All(codec => codec.StartsWith("mp4a.", StringComparison.OrdinalIgnoreCase) || codec is "ac-3" or "ec-3" or "opus" or "flac");
    }

    private static string GrowingArchivePlaylist(string content)
    {
        var lines = HlsPlaylistPolicy.SplitLines(content);
        ArchiveDuration(lines);
        // In-progress VOD endpoints can mark each snapshot as VOD/ENDLIST. An EVENT
        // manifest lets both native HLS and hls.js poll it for new archived segments.
        return string.Join('\n', lines.Where(line => line != "#EXT-X-ENDLIST" &&
            !line.StartsWith("#EXT-X-PLAYLIST-TYPE:", StringComparison.Ordinal))
            .SelectMany(line => line == "#EXTM3U" ? new[] { line, "#EXT-X-PLAYLIST-TYPE:EVENT" } : [line])) + "\n";
    }
}
