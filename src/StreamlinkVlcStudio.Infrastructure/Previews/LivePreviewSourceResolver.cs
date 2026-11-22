using System.Net;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Replay;
using StreamlinkVlcStudio.Infrastructure.Twitch;

namespace StreamlinkVlcStudio.Infrastructure.Previews;

internal sealed record LivePreviewSource(Uri PlaylistUri, (Uri Origin, string Content)? InitialPlaylist = null);

/// <summary>Authorizes the stream and fetches only the quality the preview will use.</summary>
internal sealed class LivePreviewSourceResolver
{
    private static readonly HttpClient SharedClient = HttpClientFactory.CreateForText(
        TimeSpan.FromSeconds(8), includeUserAgent: true, allowAutoRedirect: false);
    private readonly HttpClient httpClient;
    private readonly ReplayUrlSecurityValidator validator;
    private readonly Func<StreamTransportRequest, bool> canResolve;

    internal LivePreviewSourceResolver() : this(SharedClient,
        new ReplayUrlSecurityValidator(static (host, token) => Dns.GetHostAddressesAsync(host, token), LivePreviewPolicy.IsAllowedUri),
        LivePreviewPolicy.CanResolve)
    { }

    internal LivePreviewSourceResolver(HttpClient httpClient, ReplayUrlSecurityValidator validator,
        Func<StreamTransportRequest, bool> canResolve)
    {
        this.httpClient = httpClient;
        this.validator = validator;
        this.canResolve = canResolve;
    }

    internal async Task<LivePreviewPlaylistSession?> OpenAsync(StreamTransportRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!canResolve(request)) return null;
        var platform = request.Target.Platform;
        var selected = await ResolveVariantAsync(request, token).ConfigureAwait(false);
        var firstPlaylist = true;
        Uri? initializationUri = null;
        var initial = await ReadAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        // The first validated playlist is handed to VLC locally, avoiding a duplicate
        // provider request. Every later reload is validated by the same session.
        return new LivePreviewPlaylistSession(initial, ReadAsync, LivePreviewPlaylist.GetPlaybackOptions(initial, request));

        async Task<string> ReadAsync(CancellationToken cancellationToken)
        {
            var media = await ValidatedReplayHttpClient.ReadPlaylistAsync(httpClient, validator, selected, platform,
                cancellationToken).ConfigureAwait(false);
            var rewritten = LivePreviewPlaylist.Rewrite(media.Content, media.Uri, platform, out var map);
            // VLC 3 retains one initialization section for its representation. A format
            // or map change needs a new transport, before that refresh reaches VLC.
            if (!firstPlaylist && map != initializationUri)
                throw new InvalidDataException("The live preview initialization section changed.");
            initializationUri = map;
            firstPlaylist = false;
            return rewritten;
        }
    }

    internal bool CanResolve(StreamTransportRequest request) => canResolve(request);

    internal async Task<LivePreviewSource> ResolveBrowserSourceAsync(StreamTransportRequest request, string? twitchOAuthToken, CancellationToken token)
    {
        var selected = await ResolveVariantAsync(request, token, twitchOAuthToken).ConfigureAwait(false);
        var platform = request.Target.Platform;
        var media = await ValidatedReplayHttpClient.ReadPlaylistAsync(httpClient, validator, selected, platform, token).ConfigureAwait(false);
        // Check the first live media playlist before committing to direct playback.
        // The web proxy rewrites the original so browser-supported tags are retained.
        LivePreviewPlaylist.Validate(media.Content, media.Uri, platform);
        return new(selected, (media.Uri, media.Content));
    }

    private async Task<Uri> ResolveVariantAsync(StreamTransportRequest request, CancellationToken token,
        string? twitchOAuthToken = null)
    {
        var platform = request.Target.Platform;
        var masterUri = platform == PlatformKind.Twitch
            ? await new TwitchGraphQlTransport(httpClient).GetPlaybackMasterUriAsync(request.Target, token, twitchOAuthToken).ConfigureAwait(false)
            : await ResolveKickAsync(request.Target.Channel, token).ConfigureAwait(false);
        var master = await ValidatedReplayHttpClient.ReadPlaylistAsync(httpClient, validator, masterUri, platform, token).ConfigureAwait(false);
        return TwitchVodVariantPlaylist.Select(master.Content, master.Uri, request.Quality.Split(','),
            uri => LivePreviewPolicy.IsAllowedUri(uri, platform), videoPreview: true);
    }

    private async Task<Uri> ResolveKickAsync(string channel, CancellationToken token)
    {
        var uri = new Uri($"https://kick.com/api/v2/channels/{Uri.EscapeDataString(channel)}/livestream");
        using var response = await ValidatedReplayHttpClient.SendGetAsync(httpClient, validator, uri, PlatformKind.Kick, address =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) StreamStudio/0.1");
            request.Headers.Referrer = new Uri("https://kick.com/");
            return request;
        }, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await BoundedHttpContentReader.ReadJsonAsync(response.Content, token).ConfigureAwait(false));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("playback_url", out var playback) || playback.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(playback.GetString(), UriKind.Absolute, out var master))
            throw new InvalidDataException("Kick did not return a live playback URL.");
        return master;
    }
}
