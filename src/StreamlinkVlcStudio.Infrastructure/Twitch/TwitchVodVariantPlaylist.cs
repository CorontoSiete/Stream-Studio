using System.Globalization;
using System.Text.RegularExpressions;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Security;
using StreamlinkVlcStudio.Infrastructure.Hls;

namespace StreamlinkVlcStudio.Infrastructure.Twitch;

internal static partial class TwitchVodVariantPlaylist
{
    internal static Uri Select(string content, Uri baseUri, string quality)
        => Select(content, baseUri, [quality], uri => ProviderUriPolicy.IsApprovedReplayUri(uri, PlatformKind.Twitch));

    // Twitch and Kick both publish these IVS variant names. Preview selection uses
    // the same parser, its ordered quality fallbacks, and its own provider URL policy.
    internal static Uri Select(string content, Uri baseUri, IReadOnlyList<string> qualities, Func<Uri, bool> isAllowedUri,
        bool videoPreview = false)
    {
        var lines = HlsPlaylistPolicy.SplitLines(content);
        if (lines.Length == 0 || lines[0] != "#EXTM3U") throw Unsupported();
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines.Where(line => line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal)))
        {
            if (!HlsAttributeList.TryParse(line.AsSpan(line.IndexOf(':') + 1), out var attributes)) throw Unsupported();
            attributes.TryGetValue("TYPE", out var type);
            // Muted card previews use only the selected video playlist. Kick can
            // advertise separate captions even when all video variants are muxed.
            if (videoPreview && type.Value == "SUBTITLES") continue;
            // External audio requires Streamlink's muxing and language selection.
            if (attributes.ContainsKey("URI")) throw Unsupported();
            if (type.Value == "VIDEO" &&
                attributes.TryGetValue("GROUP-ID", out var group) && attributes.TryGetValue("NAME", out var name))
                if (!groups.TryAdd(group.Value, name.Value)) throw Unsupported();
        }

        var variants = new List<(string Name, int Weight, Uri Uri)>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith(HlsPlaylistPolicy.VariantTag, StringComparison.Ordinal)) continue;
            if (!HlsPlaylistPolicy.TryReadVariant(lines, i, baseUri, out var attributes, out var uri) || !isAllowedUri(uri))
                throw Unsupported();
            i++;
            var name = attributes.TryGetValue("VIDEO", out var group) && groups.TryGetValue(group.Value, out var groupName)
                ? groupName : attributes.TryGetValue("IVS-NAME", out var identifier) ? identifier.Value : null;
            if (name is null) throw Unsupported();
            name = name.ToLowerInvariant();
            if (videoPreview && QualityOption.IsAudioOnly(name)) continue;
            var match = ResolutionName().Match(name);
            var weight = name == "source" ? int.MaxValue : 0;
            if (match.Success)
            {
                if (!int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var height) ||
                    !int.TryParse(match.Groups[2].Success ? match.Groups[2].Value : "0", CultureInfo.InvariantCulture, out var fps) ||
                    height is <= 0 or > 16384 || fps is < 0 or > 1000) throw Unsupported();
                weight = height + fps;
            }
            else if (name != "source" && !QualityOption.IsAudioOnly(name)) throw Unsupported();

            if (variants.Any(variant => variant.Name == name)) throw Unsupported();
            variants.Add((name, weight, uri));
        }
        if (variants.Count == 0) throw Unsupported();
        foreach (var quality in qualities)
        {
            if (quality is "best" or "worst")
            {
                // Streamlink excludes unweighted audio from best/worst when video exists.
                var ranked = variants.Where(variant => variant.Weight > 0 || variants.Count == 1)
                    .OrderBy(variant => variant.Weight).ToArray();
                if (ranked.Length == 0) throw Unsupported();
                return quality == "best" ? ranked[^1].Uri : ranked[0].Uri;
            }
            if (variants.FirstOrDefault(variant => variant.Name == quality).Uri is { } selected) return selected;
            if (QualityOption.IsAudioOnly(quality) && variants.FirstOrDefault(v => QualityOption.IsAudioOnly(v.Name)).Uri is { } audio)
                return audio;
        }
        throw Unsupported();
    }

    private static InvalidDataException Unsupported() => new("The Twitch master playlist or requested quality requires Streamlink resolution.");

    [GeneratedRegex(@"^(\d+)p(\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ResolutionName();
}
