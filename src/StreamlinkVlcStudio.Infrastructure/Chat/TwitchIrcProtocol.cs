using System.Globalization;
using System.Text;
using StreamlinkVlcStudio.Infrastructure.Limits;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

internal static class TwitchIrcProtocol
{
    internal static string ChatMessagePrefix(string channel) => $"PRIVMSG #{channel} :";

    internal static string TruncateChatMessage(string channel, string message)
    {
        var availableBytes = PayloadLimits.TwitchOutboundIrcBytes -
            Encoding.UTF8.GetByteCount(ChatMessagePrefix(channel)) - 2;
        if (availableBytes <= 0) return "";

        var offset = 0;
        while (offset < message.Length)
        {
            var remaining = message.AsSpan(offset);
            var elementLength = StringInfo.GetNextTextElementLength(remaining);
            availableBytes -= Encoding.UTF8.GetByteCount(remaining[..elementLength]);
            if (availableBytes < 0) return message[..offset].TrimEnd();
            offset += elementLength;
        }
        return message;
    }

    internal static bool TryReadCommand(
        string line,
        out string command,
        out string parameters)
    {
        command = "";
        parameters = "";
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var span = line.AsSpan().Trim();
        if (span[0] == '@' && !SkipToken(ref span))
        {
            return false;
        }

        if (!span.IsEmpty && span[0] == ':' && !SkipToken(ref span))
        {
            return false;
        }

        if (span.IsEmpty)
        {
            return false;
        }

        var commandEnd = span.IndexOf(' ');
        if (commandEnd < 0)
        {
            command = span.ToString();
            return true;
        }

        command = span[..commandEnd].ToString();
        parameters = span[(commandEnd + 1)..].TrimStart().ToString();
        return command.Length > 0;
    }

    internal static bool IsJoinForChannel(string parameters, string channel)
    {
        var firstParameter = parameters.AsSpan().TrimStart();
        if (!firstParameter.IsEmpty && firstParameter[0] == ':')
        {
            firstParameter = firstParameter[1..];
        }

        var end = firstParameter.IndexOf(' ');
        if (end >= 0)
        {
            firstParameter = firstParameter[..end];
        }

        return firstParameter.Equals($"#{channel}".AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool SkipToken(ref ReadOnlySpan<char> span)
    {
        var end = span.IndexOf(' ');
        if (end < 0)
        {
            span = [];
            return false;
        }

        span = span[(end + 1)..].TrimStart();
        return true;
    }
}
