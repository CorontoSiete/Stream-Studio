using System.Diagnostics.CodeAnalysis;
using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Core.Parsing;

public static class ChannelFollowTarget
{
    public static bool TryResolve(StreamTarget? target, [NotNullWhen(true)] out StreamTarget? channel)
    {
        // A pasted Twitch VOD initially uses its numeric video ID as Channel. It is not a login.
        if (target is null || target.Platform is not (PlatformKind.Twitch or PlatformKind.Kick) ||
            target.IsExplicitTwitchVod && target.Channel == target.MediaId && string.IsNullOrEmpty(target.BroadcasterId))
        {
            channel = null;
            return false;
        }

        return StreamInputParser.TryFromChannel(target.Platform, target.Channel, out channel);
    }
}
