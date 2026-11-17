using System.Text.Json.Serialization;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;

namespace StreamlinkVlcStudio.Core.Settings;

public sealed class DownloadSettings : NotifyPropertyChangedObject
{
    private string quality = "best";
    private VodDownloadFormat format;
    private double bandwidthLimitMegabytesPerSecond;
    private string? directory;
    private List<string> previousDirectories = [];
    private List<AutoDownloadChannelSettings> autoDownloadChannels = [];
    private List<AutomaticVodDownloadSettings> automaticDownloads = [];

    public List<AutomaticVodDownloadSettings> AutomaticDownloads
    {
        get => automaticDownloads;
        set
        {
            var normalized = new Dictionary<Guid, AutomaticVodDownloadSettings>();
            foreach (var entry in value ?? [])
            {
                if (entry is null || entry.DownloadId == Guid.Empty || !Enum.IsDefined(entry.Platform)) continue;
                try
                {
                    var target = StreamInputParser.FromChannel(entry.Platform, entry.Channel);
                    normalized[entry.DownloadId] = entry with
                    {
                        Channel = target.Channel,
                        Completed = entry.Completed || normalized.GetValueOrDefault(entry.DownloadId)?.Completed == true
                    };
                }
                catch (ArgumentException) { }
            }
            SetProperty(ref automaticDownloads, normalized.Values.ToList());
        }
    }

    public List<AutoDownloadChannelSettings> AutoDownloadChannels
    {
        get => autoDownloadChannels;
        set
        {
            var normalized = new Dictionary<string, AutoDownloadChannelSettings>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in value ?? [])
            {
                if (entry is null || !Enum.IsDefined(entry.Platform) || entry.EnabledAtUtc == default) continue;
                try
                {
                    var target = StreamInputParser.FromChannel(entry.Platform, entry.Channel);
                    normalized[target.StateKey] = entry with
                    {
                        Channel = target.Channel,
                        EnabledAtUtc = entry.EnabledAtUtc.ToUniversalTime(),
                        QueuedVideoIds = (entry.QueuedVideoIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id))
                            .Select(id => id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                    };
                }
                catch (ArgumentException) { }
            }
            SetProperty(ref autoDownloadChannels, normalized.Values.ToList());
        }
    }

    public string Quality
    {
        get => quality;
        set => SetProperty(ref quality, string.IsNullOrWhiteSpace(value) ? "best" : value.Trim());
    }

    public VodDownloadFormat Format
    {
        get => format;
        set => SetProperty(ref format, Enum.IsDefined(value) ? value : VodDownloadFormat.Hls);
    }

    public double BandwidthLimitMegabytesPerSecond
    {
        get => bandwidthLimitMegabytesPerSecond;
        set => SetProperty(ref bandwidthLimitMegabytesPerSecond, double.IsFinite(value) && value >= 0 ? value : 0);
    }

    [JsonIgnore]
    public long BandwidthLimitBytesPerSecond => BandwidthLimitMegabytesPerSecond >= long.MaxValue / 1_000_000d
        ? long.MaxValue : (long)Math.Ceiling(BandwidthLimitMegabytesPerSecond * 1_000_000);

    public string? Directory
    {
        get => directory;
        set => SetProperty(ref directory, string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    public List<string> PreviousDirectories
    {
        get => previousDirectories;
        set => SetProperty(ref previousDirectories, (value ?? []).Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }
}
