using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.Infrastructure.Vod;

public sealed partial class VodDownloadService
{
    private sealed record ResolvedVodSource(Uri PlaylistUri, bool AllowLocalPlaylist = false,
        StreamTarget? RefreshedTarget = null);

    private async Task<ResolvedVodSource> ResolveVodSourceAsync(StreamTarget target, StreamTarget canonicalTarget,
        string quality, VodDownloadOptions options, CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(target.Url, UriKind.Absolute, out var source) && source.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            await validator.ValidateAsync(source, target.Platform, cancellationToken).ConfigureAwait(false);
        else
        {
            var page = VodDownloadUrlParser.Parse(target.Url);
            if (page.Platform != canonicalTarget.Platform || !page.MediaId.Equals(canonicalTarget.MediaId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The VOD URL does not match the selected video.");
        }

        var request = new StreamTransportRequest(target, quality, options.StreamlinkPath, false, options.CustomArguments);
        try
        {
            if (target.IsExplicitKickVod && resolveKick is not null)
                return await ResolveKickPlaylistAsync(target, canonicalTarget, request, options, cancellationToken).ConfigureAwait(false);
            return new((await streamlink.ResolveStreamUrlAsync(request, cancellationToken).ConfigureAwait(false)).StreamUri);
        }
        catch (Exception exception) when (exception is not OperationCanceledException && target.IsExplicitTwitchVod && twitchFallback is not null)
        {
            try
            {
                var fallback = await twitchFallback.ResolveAsync(new TwitchSubOnlyVodRequest(canonicalTarget.MediaId, quality), cancellationToken).ConfigureAwait(false);
                return new(fallback.PlaybackUri, fallback.PlaybackUri.IsFile && !fallback.PlaybackUri.IsUnc);
            }
            catch (Exception fallbackException) when (fallbackException is not OperationCanceledException)
            {
                throw new InvalidOperationException($"Twitch VOD resolution failed: {exception.Message} Fallback: {fallbackException.Message}", fallbackException);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException && resolveKick is null &&
            target.IsExplicitKickVod && target.Url != canonicalTarget.Url)
        {
            return new((await streamlink.ResolveStreamUrlAsync(request with { Target = canonicalTarget }, cancellationToken).ConfigureAwait(false)).StreamUri);
        }
    }

    private async Task<ResolvedVodSource> ResolveKickPlaylistAsync(StreamTarget target, StreamTarget canonicalTarget,
        StreamTransportRequest request, VodDownloadOptions options, CancellationToken cancellationToken)
    {
        if (target.Url != canonicalTarget.Url)
        {
            try { return new((await streamlink.ResolveStreamUrlAsync(request, cancellationToken).ConfigureAwait(false)).StreamUri); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.WriteSafely(AppLogLevel.Info, "VOD downloads", "Refreshing the Kick VOD source after resolution failed: " + ErrorText(exception, options));
            }
        }
        try
        {
            var resolvedTarget = await resolveKick!(canonicalTarget, cancellationToken).ConfigureAwait(false);
            await validator.ValidateAsync(new Uri(resolvedTarget.Url), PlatformKind.Kick, cancellationToken).ConfigureAwait(false);
            var result = await streamlink.ResolveStreamUrlAsync(request with { Target = resolvedTarget }, cancellationToken).ConfigureAwait(false);
            return new(result.StreamUri, RefreshedTarget: resolvedTarget);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.WriteSafely(AppLogLevel.Info, "VOD downloads", "Kick website resolution failed; trying the configured Streamlink Kick plugin: " + ErrorText(exception, options));
            try { return new((await streamlink.ResolveStreamUrlAsync(request with { Target = canonicalTarget }, cancellationToken).ConfigureAwait(false)).StreamUri); }
            catch (Exception fallbackException) when (fallbackException is not OperationCanceledException)
            {
                throw new InvalidOperationException($"Kick VOD resolution failed: {exception.Message} Streamlink: {fallbackException.Message}", fallbackException);
            }
        }
    }
}
