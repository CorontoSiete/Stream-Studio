using System.Diagnostics;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Infrastructure.Previews;

namespace StreamlinkVlcStudio.Infrastructure.Streamlink;

public sealed partial class StreamlinkService
{
    private static readonly TimeSpan CardPreviewLookupTimeout = TimeSpan.FromMilliseconds(1200);

    internal async Task<LivePreviewSource> ResolveCardPreviewAsync(StreamTransportRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!livePreviewSourceResolver.CanResolve(request))
            return new((await ResolveStreamUrlAsync(request, token).ConfigureAwait(false)).StreamUri);

        // Use the same Twitch website identity for both attempts.
        var playbackToken = await GetTwitchPlaybackTokenAsync(request, token).ConfigureAwait(false);
        try
        {
            var elapsed = Stopwatch.StartNew();
            using var lookup = CancellationTokenSource.CreateLinkedTokenSource(token);
            lookup.CancelAfter(CardPreviewLookupTimeout);
            var source = await livePreviewSourceResolver.ResolveBrowserSourceAsync(request, playbackToken, lookup.Token).ConfigureAwait(false);
            lookup.Token.ThrowIfCancellationRequested();
            logger.WriteSafely(AppLogLevel.Debug, "Web preview",
                $"Direct {request.Target.Platform} preview ready in {elapsed.ElapsedMilliseconds} ms.");
            return source;
        }
        catch (Exception error) when (LivePreviewPlaylistSession.IsSourceFailure(error) && !token.IsCancellationRequested)
        {
            logger.WriteSafely(AppLogLevel.Debug, "Web preview",
                $"Using Streamlink for {request.Target.Platform} preview ({error.GetType().Name}).");
        }

        token.ThrowIfCancellationRequested();
        return new((await resolveWithStreamlink(request, playbackToken, token).ConfigureAwait(false)).StreamUri);
    }
}
