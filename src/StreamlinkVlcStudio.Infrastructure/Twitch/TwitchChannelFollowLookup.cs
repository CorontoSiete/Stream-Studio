using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.Http;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;

namespace StreamlinkVlcStudio.Infrastructure.Twitch;

internal sealed class TwitchChannelFollowLookup(Func<string> getAccessToken, HttpClient? httpClient = null)
{
    private static readonly HttpClient SharedHttpClient = HttpClientFactory.Create(
        TimeSpan.FromSeconds(12), includeUserAgent: true, acceptJson: true, allowAutoRedirect: false);
    private readonly HttpClient client = httpClient ?? SharedHttpClient;

    internal string AccessToken => TwitchOAuthService.NormalizeOAuthToken(getAccessToken());

    internal Task<TwitchTokenInfo> ValidateAccountAsync(string token, CancellationToken cancellationToken) =>
        TwitchOAuthService.ValidateTokenAsync(client, token, cancellationToken);

    internal async Task<ChannelFollowResult?> GetFollowStatusAsync(
        StreamTarget channel, string token, CancellationToken cancellationToken)
    {
        var account = await ValidateAccountAsync(token, cancellationToken).ConfigureAwait(false);
        if (!account.CanReadFollows || string.IsNullOrWhiteSpace(account.ClientId))
            throw new InvalidOperationException("Reconnect Twitch to read your followed channels.");

        using var users = await ReadAsync($"users?login={Uri.EscapeDataString(channel.Channel)}").ConfigureAwait(false);
        if (!TwitchUserPayloadReader.TryRead(users.RootElement, channel.Channel, out var user)) return null;
        var broadcasterId = GetOptionalString(user, "id");
        if (!string.IsNullOrEmpty(channel.BroadcasterId) && channel.BroadcasterId != broadcasterId) return null;

        using var follows = await ReadAsync(
            $"channels/followed?user_id={Uri.EscapeDataString(account.UserId)}&broadcaster_id={Uri.EscapeDataString(broadcasterId)}")
            .ConfigureAwait(false);
        if (!TryGetArray(follows.RootElement, "data", out var data))
            throw new InvalidOperationException("Twitch returned an invalid follow status.");
        var following = data.GetArrayLength() switch
        {
            0 => false,
            1 when TryGetNonEmptyString(data[0], "broadcaster_id", out var followedId) && followedId == broadcasterId &&
                TryGetNonEmptyString(data[0], "broadcaster_login", out var followedLogin) &&
                string.Equals(followedLogin, channel.Channel, StringComparison.OrdinalIgnoreCase) => true,
            _ => throw new InvalidOperationException("Twitch returned a follow status for a different broadcaster.")
        };
        return new(PlatformKind.Twitch, channel.Channel, IsFollowing: following);

        async Task<JsonDocument> ReadAsync(string path)
        {
            using var request = TwitchApiRequest.Create(HttpMethod.Get, $"https://api.twitch.tv/helix/{path}", token, account.ClientId);
            using var response = await BoundedHttpResponseSender.SendAsync(client, request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var body = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
            return JsonDocument.Parse(body);
        }
    }
}
