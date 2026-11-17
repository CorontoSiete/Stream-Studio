using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Infrastructure.Vod;

public sealed partial class VodDownloadService
{
    public async Task<bool> IsCompletedAsync(VodDownloadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        cancellationToken = cancellation.Token;
        var canonical = CanonicalTarget(request.Target);
        var options = CopyOptions(request.Options);
        var source = await ResolveVodSourceAsync(request.Target, canonical, NormalizeQuality(request.Quality), options,
            cancellationToken).ConfigureAwait(false);
        return await new HlsVodDownloader(client, validator, bandwidthLimiter)
            .IsCompletedAsync(source.PlaylistUri, canonical.Platform, cancellationToken, source.AllowLocalPlaylist).ConfigureAwait(false);
    }
}
