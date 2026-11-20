using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Core.Parsing;

public static partial class StreamInputParser
{
    public static IReadOnlyList<StreamTarget> ParseCandidates(string input)
    {
        if (ParseUrl(input) is { } target)
        {
            return [target];
        }

        var candidates = new List<StreamTarget>(2);
        if (TryFromChannel(PlatformKind.Twitch, input, out var twitchTarget))
        {
            candidates.Add(twitchTarget);
        }

        if (TryFromChannel(PlatformKind.Kick, input, out var kickTarget))
        {
            candidates.Add(kickTarget);
        }

        if (candidates.Count == 0)
        {
            throw new ArgumentException("The channel name is not valid for Twitch or Kick.", nameof(input));
        }

        return candidates;
    }

    public static StreamTarget Parse(string input, PlatformKind defaultPlatform) =>
        ParseUrl(input) ?? FromChannel(defaultPlatform, input);

    public static bool TryParsePlatformUrl(string input, [NotNullWhen(true)] out StreamTarget? target)
    {
        try
        {
            target = ParseUrl(input);
            return target is not null;
        }
        catch (ArgumentException)
        {
            target = null;
            return false;
        }
    }

    public static bool TryParseTwitchVodUrl(string input, [NotNullWhen(true)] out StreamTarget? target)
    {
        if (TryParsePlatformUrl(input, out target) && target.IsExplicitTwitchVod) return true;
        target = null;
        return false;
    }

    internal static StreamTarget? ParseTwitchVodPath(string[] segments)
    {
        if (segments.Length != 2 || !segments[0].Equals("videos", StringComparison.OrdinalIgnoreCase))
            return null;

        var vodId = segments[1];
        if (vodId.Length is 0 or > 32 || !vodId.All(char.IsAsciiDigit)) return null;

        return new StreamTarget(
            PlatformKind.Twitch,
            vodId,
            $"https://www.twitch.tv/videos/{vodId}",
            StreamTargetKind.TwitchVod,
            vodId,
            $"VOD {vodId}");
    }

    // A bare channel name has no explicit platform and is resolved by the caller.
    private static StreamTarget? ParseUrl(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("Enter a Twitch or Kick channel URL/name.", nameof(input));
        }

        var trimmed = input.Trim();
        var urlCandidate = WithDefaultScheme(trimmed);
        Uri.TryCreate(urlCandidate, UriKind.Absolute, out var uri);
        var platform = uri is null ? null : PlatformFromHost(uri.IdnHost);
        if (urlCandidate != trimmed && !trimmed.StartsWith("//", StringComparison.Ordinal) &&
            platform is null)
        {
            return null;
        }

        if (uri is null)
        {
            throw new ArgumentException("The stream URL is not valid.", nameof(input));
        }

        if (!HasSupportedUrlAuthority(uri))
        {
            throw new ArgumentException("Use an HTTP or HTTPS stream URL with the default port and no embedded credentials.", nameof(input));
        }

        if (platform is null)
        {
            throw new ArgumentException("Only Twitch and Kick URLs are supported.", nameof(input));
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            throw new ArgumentException("The stream URL does not include a channel name.", nameof(input));
        }

        if (platform == PlatformKind.Twitch && ParseTwitchVodPath(segments) is { } vod)
        {
            return vod;
        }

        if (segments.Length != 1)
        {
            throw new ArgumentException("The stream URL must point directly to a channel.", nameof(input));
        }

        return FromChannel(platform.Value, segments[0]);
    }

    public static StreamTarget FromChannel(PlatformKind platform, string? channel)
    {
        if (!Enum.IsDefined(platform))
        {
            throw new ArgumentOutOfRangeException(nameof(platform), platform, "The stream platform is not supported.");
        }

        var normalized = NormalizeChannel(channel);
        if (GetChannelError(platform, normalized) is { } error)
        {
            throw new ArgumentException(error, nameof(channel));
        }

        return CreateChannelTarget(platform, normalized);
    }

    public static bool TryFromChannel(
        PlatformKind platform,
        string? channel,
        [NotNullWhen(true)] out StreamTarget? target)
    {
        if (TryNormalizeChannelSlug(platform, channel, out var normalized))
        {
            target = CreateChannelTarget(platform, normalized);
            return true;
        }

        target = null;
        return false;
    }

    public static bool TryNormalizeChannelSlug(
        PlatformKind platform,
        string? channel,
        out string normalized)
    {
        normalized = NormalizeChannel(channel);
        if (Enum.IsDefined(platform) && GetChannelError(platform, normalized) is null) return true;

        normalized = "";
        return false;
    }

    private static StreamTarget CreateChannelTarget(PlatformKind platform, string channel)
    {
        var url = platform == PlatformKind.Twitch
            ? $"https://www.twitch.tv/{channel}"
            : $"https://kick.com/{channel}";
        return new StreamTarget(platform, channel, url);
    }

    private static string? GetChannelError(PlatformKind platform, string channel)
    {
        if (channel.Length == 0) return "Channel name cannot be empty.";
        if (channel is "." or ".." || PlatformRoutePolicy.IsNonChannelRoute(platform, channel))
            return "The channel name points to a platform page, not a channel.";

        if (platform == PlatformKind.Twitch)
            return TwitchChannelPattern().IsMatch(channel)
                ? null : "Twitch channel names must contain 3 to 25 letters, numbers, or underscores.";

        return ChannelPattern().IsMatch(channel)
            ? null : "Kick channel names may contain letters, numbers, underscores, hyphens, and dots.";
    }

    private static string NormalizeChannel(string? channel)
    {
        return (channel ?? "").Trim().Trim('@', '/');
    }

    private static string NormalizeHost(string host)
    {
        var normalized = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? host[4..]
            : host;

        return normalized.Equals("m.twitch.tv", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("m.kick.com", StringComparison.OrdinalIgnoreCase)
            ? normalized[2..]
            : normalized;
    }

    internal static string WithDefaultScheme(string input)
    {
        if (input.StartsWith("//", StringComparison.Ordinal)) return "https:" + input;
        var separator = input.IndexOf("://", StringComparison.Ordinal);
        return separator > 0 && Uri.CheckSchemeName(input[..separator]) ? input : "https://" + input;
    }

    internal static bool HasSupportedUrlAuthority(Uri uri) =>
        uri.Scheme is ("https" or "http") && uri.IsDefaultPort && uri.UserInfo.Length == 0;

    internal static PlatformKind? PlatformFromHost(string host)
    {
        return NormalizeHost(host).ToLowerInvariant() switch
        {
            "twitch.tv" => PlatformKind.Twitch,
            "kick.com" => PlatformKind.Kick,
            _ => null
        };
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,80}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_]{3,25}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TwitchChannelPattern();
}
