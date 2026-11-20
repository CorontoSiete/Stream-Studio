using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace StreamlinkVlcStudio.Infrastructure.Hls;

internal static class HlsPlaylistPolicy
{
    internal const string VariantTag = "#EXT-X-STREAM-INF:";

    internal static string[] SplitLines(string content)
    {
        var lines = content.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        // UTF-8 decoded from a file can retain its BOM; normalize only the header.
        if (lines.Length > 0) lines[0] = lines[0].TrimStart('\uFEFF');
        return lines;
    }

    // Delta updates omit segment metadata, including durations and key/map state.
    // Standalone readers cannot interpret them without the previous full playlist.
    internal static bool HasSkippedSegments(IEnumerable<string> lines) =>
        lines.Any(static line => line == "#EXT-X-SKIP" ||
            line.StartsWith("#EXT-X-SKIP:", StringComparison.Ordinal));

    // A published duration needs every segment from the broadcast's beginning,
    // including growing EVENT playlists that do not have an end marker yet.
    internal static TimeSpan? ReadPublishedDuration(string[] lines)
    {
        if (lines.Length == 0 || lines[0] != "#EXTM3U" || HasSkippedSegments(lines) ||
            lines.Any(line => line.StartsWith(VariantTag, StringComparison.Ordinal))) return null;

        decimal seconds = 0;
        decimal? pending = null;
        var segments = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal) &&
                (!long.TryParse(line.AsSpan(22), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) || sequence != 0))
                return null;
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                if (pending.HasValue || !TryReadSegmentDuration(line.AsSpan(8), 3600, out var duration)) return null;
                pending = duration;
            }
            else if (!line.StartsWith('#'))
            {
                if (!pending.HasValue) return null;
                seconds += pending.Value;
                if (seconds > 14 * 24 * 60 * 60) return null;
                pending = null;
                segments++;
            }
        }

        return segments > 0 && !pending.HasValue
            ? TimeSpan.FromTicks((long)(seconds * TimeSpan.TicksPerSecond))
            : null;
    }

    internal static bool TryReadVariant(
        string[] lines,
        int index,
        Uri baseUri,
        [NotNullWhen(true)] out Dictionary<string, HlsAttributeValue>? attributes,
        [NotNullWhen(true)] out Uri? uri)
    {
        attributes = null;
        uri = null;
        if (index + 1 >= lines.Length || !lines[index].StartsWith(VariantTag, StringComparison.Ordinal))
            return false;

        var reference = lines[index + 1];
        return !reference.StartsWith('#') && !reference.Any(char.IsControl) &&
            HlsAttributeList.TryParse(lines[index].AsSpan(VariantTag.Length), out attributes) &&
            Uri.TryCreate(baseUri, reference, out uri);
    }

    internal static bool TryReadSegmentDuration(ReadOnlySpan<char> value, decimal maximumSeconds, out decimal seconds)
    {
        seconds = 0;
        var comma = value.IndexOf(',');
        if (comma >= 0) value = value[..comma];
        foreach (var character in value)
            if (!char.IsAsciiDigit(character) && character != '.') return false;
        return decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out seconds) &&
            seconds > 0 && seconds <= maximumSeconds;
    }

    // Byte-range maps need a ranged downloader. These readers accept only a whole,
    // quoted URI; provider and encryption policy remains with the caller.
    internal static bool TryReadWholeMapUri(string attributes, out string uri)
    {
        uri = "";
        if (!HlsAttributeList.TryParse(attributes, out var parsed) || parsed.Count != 1 ||
            !parsed.TryGetValue("URI", out var value) || !value.IsQuoted || value.Value.Length == 0) return false;
        uri = value.Value;
        return true;
    }
}
