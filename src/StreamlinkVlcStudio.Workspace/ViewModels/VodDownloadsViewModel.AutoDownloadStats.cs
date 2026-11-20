using System.Collections.ObjectModel;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Workspace.ViewModels;

internal sealed partial class VodDownloadsViewModel
{
    private readonly HashSet<string> legacyAutoDownloadVideoKeys;
    private int autoDownloadCompletedCount;
    private long autoDownloadStorageBytes;

    public ObservableCollection<AutoDownloadChannelViewModel> AutoDownloadChannels { get; } = [];
    public bool HasAutoDownloadChannels => AutoDownloadChannels.Count > 0;
    public string AutoDownloadChannelStatus => AutoDownloadChannels.Count == 1 ? "1 channel enabled" : $"{AutoDownloadChannels.Count:N0} channels enabled";
    public int AutoDownloadCompletedCount => autoDownloadCompletedCount;
    public long AutoDownloadStorageBytes => autoDownloadStorageBytes;
    public string AutoDownloadCompletedText => $"{AutoDownloadCompletedCount:N0} VOD{(AutoDownloadCompletedCount == 1 ? "" : "s")}";
    public string AutoDownloadStorageText => AutoDownloadChannelViewModel.FormatStorage(AutoDownloadStorageBytes);

    private void TrackAutomaticDownloads(IEnumerable<VodDownloadItem> items)
    {
        var records = Settings.Downloads.AutomaticDownloads.ToDictionary(entry => entry.DownloadId);
        var changed = false;
        foreach (var item in items)
        {
            var previous = records.GetValueOrDefault(item.Id);
            var updated = GetAutomaticDownloadRecord(item, previous);
            if (updated is null || updated == previous) continue;
            records[item.Id] = updated;
            changed = true;
        }
        if (changed) Settings.Downloads.AutomaticDownloads = records.Values.ToList();
    }

    private AutomaticVodDownloadSettings? GetAutomaticDownloadRecord(VodDownloadItem item, AutomaticVodDownloadSettings? previous)
    {
        if (!item.IsAutomatic && previous is null && !legacyAutoDownloadVideoKeys.Contains(VideoKey(item.Target))) return null;
        return new(item.Id, item.Target.Platform, previous?.Channel ?? item.Target.Channel,
            HasSavedMedia(item) || previous?.Completed == true);
    }

    private static string VideoKey(StreamTarget target) => $"{target.StateKey}:{target.MediaId.Trim()}";

    private static bool HasSavedMedia(VodDownloadItem item) =>
        item.State is VodDownloadState.Completed or VodDownloadState.ConversionQueued or VodDownloadState.Converting &&
        !string.IsNullOrWhiteSpace(item.LocalMediaPath);

    private void RefreshAutoDownloadStats()
    {
        var records = Settings.Downloads.AutomaticDownloads;
        var ids = records.Select(entry => entry.DownloadId).ToHashSet();
        var saved = VodDownloads.Select(card => card.Item).Where(item => ids.Contains(item.Id) && HasSavedMedia(item)).ToArray();
        var count = records.Count(entry => entry.Completed);
        var bytes = StoredBytes(saved);
        if (SetProperty(ref autoDownloadCompletedCount, count, nameof(AutoDownloadCompletedCount)))
            OnPropertyChanged(nameof(AutoDownloadCompletedText));
        if (SetProperty(ref autoDownloadStorageBytes, bytes, nameof(AutoDownloadStorageBytes)))
            OnPropertyChanged(nameof(AutoDownloadStorageText));

        var channels = Settings.Downloads.AutoDownloadChannels.OrderBy(channel => channel.Platform)
            .ThenBy(channel => channel.Channel, StringComparer.OrdinalIgnoreCase).Select(channel =>
                new AutoDownloadChannelViewModel(channel.Platform, channel.Channel,
                    records.Count(entry => entry.Completed && entry.Platform == channel.Platform &&
                        entry.Channel.Equals(channel.Channel, StringComparison.OrdinalIgnoreCase)),
                    StoredBytes(saved.Where(item => item.Target.Platform == channel.Platform &&
                        item.Target.Channel.Equals(channel.Channel, StringComparison.OrdinalIgnoreCase))))).ToArray();
        for (var index = 0; index < channels.Length; index++)
        {
            if (index == AutoDownloadChannels.Count) AutoDownloadChannels.Add(channels[index]);
            else if (AutoDownloadChannels[index] != channels[index]) AutoDownloadChannels[index] = channels[index];
        }
        while (AutoDownloadChannels.Count > channels.Length) AutoDownloadChannels.RemoveAt(AutoDownloadChannels.Count - 1);
        OnPropertyChanged(nameof(HasAutoDownloadChannels));
        OnPropertyChanged(nameof(AutoDownloadChannelStatus));
    }

    private static long StoredBytes(IEnumerable<VodDownloadItem> items)
    {
        long total = 0;
        foreach (var item in items)
        {
            var bytes = Math.Max(0, item.BytesDownloaded);
            total = bytes > long.MaxValue - total ? long.MaxValue : total + bytes;
        }
        return total;
    }
}
