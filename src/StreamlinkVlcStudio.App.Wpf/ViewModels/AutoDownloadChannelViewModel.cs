using System.Globalization;
using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public sealed record AutoDownloadChannelViewModel(PlatformKind Platform, string Channel, int CompletedCount, long StorageBytes)
{
    public string ChannelText => $"{Platform} · {Channel}";
    public string UsageText => $"{CompletedCount:N0} completed · {FormatStorage(StorageBytes)} saved";

    internal static string FormatStorage(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        var size = (double)Math.Max(0, bytes);
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size.ToString(unit == 0 ? "N0" : "N1", CultureInfo.CurrentCulture)} {units[unit]}";
    }
}
