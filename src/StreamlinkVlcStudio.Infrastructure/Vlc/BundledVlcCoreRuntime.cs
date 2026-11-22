using StreamlinkVlcStudio.Core;
using StreamlinkVlcStudio.Infrastructure.Io;

namespace StreamlinkVlcStudio.Infrastructure.Vlc;

internal sealed record LibVlcCoreSelection(string Path, string Sha256, bool IsBundledAddressWaitBuild);

/// <summary>Selects the matching source-built core only for the verified VLC 3.0.23 DLL pair.</summary>
internal static class BundledVlcCoreRuntime
{
    private const string ReferenceLibVlcSha256 =
        "8ae9f16a72441f43fb4ae8f72c843736726e067ea4a8def2646748631cc4e872";
    private const string ReferenceCoreSha256 =
        "d3475b834dd3eb77910f37f71b0341d358bcbdda5b9f04cc4a3a8e2be1bc8e35";
    private const string BundledCoreSha256 =
        "12b0a9eb0cc10099a69182fbb553f521cc90251d411460c9f3ff8ba093326de2";
    private static readonly Lazy<BundledVlcBinary> BundledCore =
        new(() => new("BundledVlcCore", "libvlccore.dll", BundledCoreSha256));

    internal static LibVlcCoreSelection Select(string vlcDirectory)
    {
        var installedCorePath = Path.Combine(vlcDirectory, "libvlccore.dll");
        var installedLibVlcPath = Path.Combine(vlcDirectory, "libvlc.dll");
        if (!File.Exists(installedCorePath))
            throw new FileNotFoundException("The selected VLC core library was not found.", installedCorePath);
        if (!File.Exists(installedLibVlcPath))
            throw new FileNotFoundException("The selected libVLC library was not found.", installedLibVlcPath);

        var installedCoreSha256 = FileHash.GetSha256(installedCorePath);
        var installedLibVlcSha256 = FileHash.GetSha256(installedLibVlcPath);
        if (!installedCoreSha256.Equals(ReferenceCoreSha256, StringComparison.OrdinalIgnoreCase) ||
            !installedLibVlcSha256.Equals(ReferenceLibVlcSha256, StringComparison.OrdinalIgnoreCase))
        {
            return new LibVlcCoreSelection(installedCorePath, installedCoreSha256, IsBundledAddressWaitBuild: false);
        }

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppIdentity.ProductDirectoryName,
            "vlc-native-core",
            BundledCoreSha256);
        var bundledPath = BundledCore.Value.ExtractTo(directory);
        return new LibVlcCoreSelection(bundledPath, BundledCoreSha256, IsBundledAddressWaitBuild: true);
    }
}
