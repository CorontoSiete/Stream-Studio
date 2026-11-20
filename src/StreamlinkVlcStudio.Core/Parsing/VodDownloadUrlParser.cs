using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Core.Parsing;

public static class VodDownloadUrlParser
{
    public static StreamTarget Parse(string input)
    {
        var value = StreamInputParser.WithDefaultScheme(input?.Trim() ?? "");
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !StreamInputParser.HasSupportedUrlAuthority(uri))
            throw new ArgumentException("Enter a Twitch or Kick VOD page URL.", nameof(input));

        var parts = uri.AbsolutePath.Trim('/').Split('/');
        var platform = StreamInputParser.PlatformFromHost(uri.IdnHost);
        if (platform == PlatformKind.Twitch && StreamInputParser.ParseTwitchVodPath(parts) is { } vod)
            return vod;

        if (platform == PlatformKind.Kick &&
            parts.Length == 3 && parts[1].Equals("videos", StringComparison.OrdinalIgnoreCase) &&
            parts[2].Length is > 0 and <= 128 && parts[2].All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
        {
            var channel = StreamInputParser.FromChannel(PlatformKind.Kick, parts[0]);
            return channel with
            {
                Url = $"https://kick.com/{channel.Channel}/videos/{parts[2]}",
                Kind = StreamTargetKind.KickVod,
                MediaId = parts[2],
                DisplayTitle = $"{channel.Channel} VOD {parts[2]}"
            };
        }

        throw new ArgumentException("Only Twitch /videos/{id} and Kick /{channel}/videos/{id} VOD URLs can be downloaded. Live streams and clips are not VODs.", nameof(input));
    }
}
