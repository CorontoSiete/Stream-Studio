using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Core.Services;

public interface IVodDownloadCompletionProbe
{
    Task<bool> IsCompletedAsync(VodDownloadRequest request, CancellationToken cancellationToken = default);
}
