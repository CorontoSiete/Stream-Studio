using System.Security.Cryptography;
using StreamlinkVlcStudio.Core;

namespace StreamlinkVlcStudio.Infrastructure.Vlc;

internal static class VlcReplayPausePlugin
{
    private static readonly Lazy<(string Hash, BundledVlcBinary[] Plugins)> Bundle = new(ReadBundle);

    internal static string Prepare(string? cacheDirectory = null)
    {
        cacheDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppIdentity.ProductDirectoryName, "vlc-replay-pause");
        var (hash, plugins) = Bundle.Value;
        var root = Path.Combine(cacheDirectory, hash);
        var directory = Path.Combine(root, "demux");
        foreach (var plugin in plugins) plugin.ExtractTo(directory);
        return root;
    }

    private static (string Hash, BundledVlcBinary[] Plugins) ReadBundle()
    {
        BundledVlcBinary[] plugins =
        [
            new("BundledReplayPause", "libstudio_replay_pause_plugin.dll"),
            new("BundledAdaptiveReplay", "libstudio_adaptive_plugin.dll")
        ];
        using var bundleHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var plugin in plugins) bundleHash.AppendData(Convert.FromHexString(plugin.Sha256));
        return (Convert.ToHexString(bundleHash.GetHashAndReset()), plugins);
    }
}
