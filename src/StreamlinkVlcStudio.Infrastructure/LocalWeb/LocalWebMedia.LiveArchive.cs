using System.Text;
using StreamlinkVlcStudio.Infrastructure.Hls;
using StreamlinkVlcStudio.Infrastructure.Http;

namespace StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal sealed partial class LocalWebMedia
{
    internal async Task<double> ReadArchiveDurationAsync(string quality, CancellationToken token, bool selectVariant = false)
    {
        var uri = resources[Root];
        string? selectedMaster = null;
        Uri? selectedMasterUri = null;
        for (var depth = 0; depth < 4; depth++)
        {
            string content;
            // Measure the same pinned master that the player receives. Only its
            // selected media playlist needs to be fetched again as it grows.
            if (depth == 0 && trustedPlaylist is { } rootPlaylist) content = rootPlaylist;
            else
            {
                using var response = await FetchAsync(uri, "", token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var bytes = await BoundedByteReader.ReadOrThrowAsync(response.Content, 16 * 1024 * 1024, token).ConfigureAwait(false);
                content = new UTF8Encoding(false, true).GetString(bytes);
                uri = response.RequestMessage!.RequestUri!;
            }
            RewritePlaylist(content, uri); // Validate every resource through the normal proxy policy.
            var lines = HlsPlaylistPolicy.SplitLines(content);
            if (lines.Any(line => line.StartsWith("#EXTINF:", StringComparison.Ordinal)))
            {
                var duration = ArchiveDuration(lines);
                if (selectVariant)
                {
                    // Pin the measured rendition, retaining a narrowed master when needed
                    // so its external audio/subtitle groups and session keys still work.
                    lock (gate)
                    {
                        ids.Remove(resources[Root].AbsoluteUri);
                        var root = selectedMasterUri ?? uri;
                        resources[Root] = root;
                        ids[root.AbsoluteUri] = Root;
                        if (selectedMaster is not null) trustedPlaylist = selectedMaster;
                    }
                }
                return duration;
            }
            var variants = new List<(Uri Uri, int Height, long Bandwidth, int Line)>();
            for (var i = 0; i + 1 < lines.Length; i++)
            {
                const string tag = "#EXT-X-STREAM-INF:";
                if (!lines[i].StartsWith(tag, StringComparison.Ordinal)) continue;
                if (!HlsAttributeList.TryParse(lines[i].AsSpan(tag.Length), out var attributes) || lines[i + 1].StartsWith('#'))
                    throw new InvalidDataException("Invalid archive variants.");
                attributes.TryGetValue("RESOLUTION", out var resolution);
                attributes.TryGetValue("BANDWIDTH", out var bandwidth);
                int.TryParse(resolution.Value?.Split('x').LastOrDefault(), out var height);
                long.TryParse(bandwidth.Value, out var rate);
                var variantLine = i;
                var variant = new Uri(uri, lines[++i]);
                if (!IsSafeUri(variant)) throw new InvalidDataException("Invalid archive variant endpoint.");
                variants.Add((variant, height, rate, variantLine));
            }
            if (variants.Count == 0) throw new InvalidDataException("No archive video playlist.");
            var ordered = variants.OrderBy(v => v.Height).ThenBy(v => v.Bandwidth).ToArray();
            var digits = new string(quality.TakeWhile(char.IsAsciiDigit).ToArray());
            var selected = quality == "worst" ? ordered[0] :
                int.TryParse(digits, out var wanted) && ordered.Any(v => v.Height == wanted)
                    ? ordered.Last(v => v.Height == wanted) : ordered[^1];
            if (selectVariant)
            {
                var narrowed = new List<string>();
                for (var i = 0; i < lines.Length; i++)
                {
                    if (lines[i].StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
                    {
                        if (i == selected.Line) { narrowed.Add(lines[i]); narrowed.Add(lines[i + 1]); }
                        i++;
                    }
                    else narrowed.Add(lines[i]);
                }
                selectedMaster = string.Join('\n', narrowed) + "\n";
                selectedMasterUri = uri;
            }
            uri = selected.Uri;
        }
        throw new InvalidDataException("Archive playlist nesting exceeds the limit.");
    }

    private static double ArchiveDuration(string[] lines) =>
        HlsPlaylistPolicy.ReadPublishedDuration(lines)?.TotalSeconds ??
        throw new InvalidDataException("Empty, incomplete or unsupported archive timeline.");

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
