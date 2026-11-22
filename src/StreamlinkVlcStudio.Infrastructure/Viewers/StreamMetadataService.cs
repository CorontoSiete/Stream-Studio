using System.Text.Json;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Twitch;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Infrastructure.Viewers;

public sealed class StreamMetadataService : IStreamMetadataService
{
    private static readonly HttpClient SharedHttpClient = HttpClientFactory.Create(
        TimeSpan.FromSeconds(12),
        includeUserAgent: true,
        acceptJson: true);
    private readonly IAppLogger logger;
    private readonly HttpClient httpClient;
    private readonly LiveChannelSnapshotProvider snapshotProvider;
    private readonly IKickTokenProvider kickTokenProvider;
    private readonly KickWebsiteJsonReader kickWebsiteJsonReader;

    public StreamMetadataService(IAppLogger logger)
        : this(
            logger,
            SharedHttpClient,
            LiveChannelSnapshotProvider.Shared,
            KickTokenProvider.Shared)
    {
    }

    public StreamMetadataService(IAppLogger logger, HttpClient httpClient)
        : this(
            logger,
            httpClient,
            new LiveChannelSnapshotProvider(httpClient),
            KickTokenProvider.Shared)
    {
    }

    internal StreamMetadataService(
        IAppLogger logger,
        HttpClient httpClient,
        LiveChannelSnapshotProvider snapshotProvider,
        IKickTokenProvider kickTokenProvider)
    {
        this.logger = logger;
        this.httpClient = httpClient;
        this.snapshotProvider = snapshotProvider;
        this.kickTokenProvider = kickTokenProvider;
        kickWebsiteJsonReader = new KickWebsiteJsonReader(httpClient, logger, "Search", TimeSpan.FromSeconds(12));
    }

    public Task<StreamMetadataResult> GetLiveStreamMetadataAsync(
        StreamTarget target,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        return target.Platform switch
        {
            PlatformKind.Twitch => GetTwitchStreamMetadataAsync(target, settings.Chat, cancellationToken),
            PlatformKind.Kick => GetKickStreamMetadataAsync(target, settings.Chat, cancellationToken),
            _ => Task.FromResult(new StreamMetadataResult(
                StreamMetadataState.Unavailable,
                "",
                "",
                $"Stream metadata is not supported for {target.Platform}."))
        };
    }

    public async Task<StreamMetadataResult> GetLiveSearchMetadataAsync(
        StreamTarget target,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var metadata = target.Platform switch
            {
                PlatformKind.Twitch => await ReadTwitchSearchMetadataAsync(target.Channel, cancellationToken).ConfigureAwait(false),
                PlatformKind.Kick => await ReadKickSearchMetadataAsync(target.Channel, cancellationToken).ConfigureAwait(false),
                _ => null
            };
            if (metadata is not null)
            {
                return metadata;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Search", $"Public stream details could not be loaded for {target.DisplayName}.", ex);
        }

        return await GetLiveStreamMetadataAsync(target, settings, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StreamMetadataResult?> ReadTwitchSearchMetadataAsync(string login, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            query = """
                query StreamStudioSearchMetadata($login: String!) {
                  user(login: $login) {
                    login displayName profileImageURL(width: 150)
                    stream { title game { name } }
                    broadcastSettings { title game { name } }
                  }
                }
                """,
            variables = new { login }
        });
        using var document = await new TwitchGraphQlTransport(httpClient).SendAsync(
            payload, TwitchGraphQlTransport.PublicClientId, TwitchGraphQlTransport.CreateDeviceId(), cancellationToken).ConfigureAwait(false);
        var user = GetObjectProperty(GetObjectProperty(document.RootElement, "data"), "user");
        if (user.ValueKind != JsonValueKind.Object ||
            !string.Equals(GetOptionalString(user, "login"), login, StringComparison.OrdinalIgnoreCase) ||
            !user.TryGetProperty("stream", out var stream))
        {
            return null;
        }

        if (stream.ValueKind == JsonValueKind.Null)
        {
            return new(StreamMetadataState.Offline, "", "", "Twitch stream is offline.");
        }
        if (stream.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var broadcast = GetObjectProperty(user, "broadcastSettings");
        return new(StreamMetadataState.Available, "", GetOptionalString(user, "displayName"), "Twitch stream details loaded.",
            FirstNonEmpty(TryReadNestedString(stream, "game", "name"), TryReadNestedString(broadcast, "game", "name")),
            GetOptionalString(user, "profileImageURL"),
            FirstNonEmpty(GetOptionalString(stream, "title"), GetOptionalString(broadcast, "title")));
    }

    private async Task<StreamMetadataResult?> ReadKickSearchMetadataAsync(string channel, CancellationToken cancellationToken)
    {
        var slug = Uri.EscapeDataString(channel);
        var body = await kickWebsiteJsonReader.ReadAsync(
            $"https://kick.com/api/v2/channels/{slug}", $"https://kick.com/{slug}", cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !string.Equals(GetOptionalString(root, "slug"), channel, StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty("livestream", out var stream))
        {
            return null;
        }

        if (stream.ValueKind == JsonValueKind.Null || TryGetBool(stream, "is_live") == false)
        {
            return new(StreamMetadataState.Offline, "", "", "Kick stream is offline.");
        }
        if (stream.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var user = GetObjectProperty(root, "user");
        return new(StreamMetadataState.Available, "", GetOptionalString(user, "username"), "Kick stream details loaded.",
            TryReadFirstArrayObjectString(stream, "categories", "name"), NormalizeImageUrl(GetOptionalString(user, "profile_pic")),
            GetOptionalString(stream, "session_title"));
    }

    private async Task<StreamMetadataResult> GetTwitchStreamMetadataAsync(
        StreamTarget target,
        ChatSettings settings,
        CancellationToken cancellationToken)
    {
        var token = TwitchOAuthService.NormalizeOAuthToken(settings.TwitchOAuthToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return new StreamMetadataResult(
                StreamMetadataState.NotConfigured,
                "",
                "",
                "Twitch stream thumbnails require a Twitch OAuth token.");
        }

        var clientId = await TwitchClientIdResolver.ResolveAsync(
            settings,
            httpClient,
            token,
            logger,
            "Recent",
            "Could not resolve Twitch Client ID from the OAuth token.",
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return new StreamMetadataResult(
                StreamMetadataState.NotConfigured,
                "",
                "",
                "Twitch stream thumbnails require a Twitch Client ID that matches the OAuth token.");
        }

        var response = await snapshotProvider
            .GetTwitchAsync(target.Channel, token, clientId, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.Write(
                AppLogLevel.Warning,
                "Recent",
                $"Twitch stream metadata request failed for {target.DisplayName}: {(int)response.StatusCode} {response.ReasonPhrase}. {ApiErrorMessage.Extract(response.Body)}");
            return new StreamMetadataResult(
                StreamMetadataState.Unavailable,
                "",
                "",
                "Twitch stream metadata unavailable. Check the Twitch Client ID and OAuth token.");
        }

        using var document = JsonDocument.Parse(response.Body);
        var metadata = ReadMetadata(target, LiveChannelPayloadReader.Read(target.Platform, target.Channel, document.RootElement));
        if (metadata.State != StreamMetadataState.Available ||
            !string.IsNullOrWhiteSpace(metadata.ProfileImageUrl))
        {
            return metadata;
        }

        return await AddProfileImageAsync(target, metadata, target.Channel,
            () => ProfileImageLookup.GetTwitchAsync(httpClient, token, clientId, [target.Channel], cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<StreamMetadataResult> GetKickStreamMetadataAsync(
        StreamTarget target,
        ChatSettings settings,
        CancellationToken cancellationToken)
    {
        var accessToken = await kickTokenProvider
            .ResolveAsync(settings, logger, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new StreamMetadataResult(
                StreamMetadataState.NotConfigured,
                "",
                "",
                "Kick stream thumbnails require a Kick user token or Kick Client ID and Client Secret.");
        }

        var response = await snapshotProvider
            .GetKickAsync(target.Channel, accessToken, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.Write(
                AppLogLevel.Warning,
                "Recent",
                $"Kick stream metadata request failed for {target.DisplayName}: {(int)response.StatusCode} {response.ReasonPhrase}. {ApiErrorMessage.Extract(response.Body)}");
            return new StreamMetadataResult(
                StreamMetadataState.Unavailable,
                "",
                "",
                "Kick stream metadata unavailable. Check Kick API credentials.");
        }

        using var document = JsonDocument.Parse(response.Body);
        var payload = LiveChannelPayloadReader.Read(target.Platform, target.Channel, document.RootElement);
        var metadata = ReadMetadata(target, payload);
        if (metadata.State != StreamMetadataState.Available ||
            !string.IsNullOrWhiteSpace(metadata.ProfileImageUrl))
        {
            return metadata;
        }

        var broadcasterUserId = GetOptionalString(payload.Channel, "broadcaster_user_id");
        if (string.IsNullOrWhiteSpace(broadcasterUserId))
        {
            return metadata;
        }

        return await AddProfileImageAsync(target, metadata, broadcasterUserId,
            () => ProfileImageLookup.GetKickAsync(httpClient, accessToken, [broadcasterUserId], cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<StreamMetadataResult> AddProfileImageAsync(
        StreamTarget target,
        StreamMetadataResult metadata,
        string profileKey,
        Func<Task<IReadOnlyDictionary<string, string>>> getProfileImages,
        CancellationToken cancellationToken)
    {
        try
        {
            var profileImages = await getProfileImages().ConfigureAwait(false);
            return profileImages.TryGetValue(profileKey, out var profileImage)
                ? metadata with { ProfileImageUrl = profileImage }
                : metadata;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Recent", $"{target.Platform} profile image lookup failed for {target.DisplayName}.", ex);
            return metadata;
        }
    }

    private static StreamMetadataResult ReadMetadata(StreamTarget target, LiveChannelPayload payload)
    {
        if (payload.State == LiveChannelState.Offline)
        {
            return new(StreamMetadataState.Offline, "", "", $"{target.Platform} stream is offline.");
        }

        if (payload.State != LiveChannelState.Available)
        {
            return new(StreamMetadataState.Unavailable, "", "",
                $"{target.Platform} stream metadata response did not include valid stream data.");
        }

        var item = payload.Channel;
        if (target.Platform == PlatformKind.Twitch)
        {
            return new(
                StreamMetadataState.Available,
                NormalizeImageUrl(GetOptionalString(item, "thumbnail_url"), "440", "248"),
                FirstNonEmpty(GetOptionalString(item, "user_name"), target.Channel),
                "Twitch stream metadata updated.",
                GetOptionalString(item, "game_name"),
                GetOptionalString(item, "profile_image_url"),
                GetOptionalString(item, "title"));
        }

        return new(
            StreamMetadataState.Available,
            NormalizeImageUrl(FirstNonEmpty(
                GetOptionalString(payload.Stream, "thumbnail"),
                GetOptionalString(item, "thumbnail"))),
            FirstNonEmpty(GetOptionalString(item, "slug"), target.Channel),
            "Kick stream metadata updated.",
            TryReadNestedString(item, "category", "name"),
            NormalizeImageUrl(FirstNonEmpty(
                GetOptionalString(item, "profile_picture"),
                GetOptionalString(item, "profile_pic"))),
            FirstNonEmpty(
                GetOptionalString(item, "stream_title"),
                GetOptionalString(payload.Stream, "stream_title"),
                GetOptionalString(payload.Stream, "title")));
    }
}
