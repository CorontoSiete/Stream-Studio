using System.Diagnostics;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Infrastructure.Previews;
using StreamlinkVlcStudio.Infrastructure.Streamlink;
using StreamlinkVlcStudio.Infrastructure.Threading;
using static StreamlinkVlcStudio.Infrastructure.LocalWeb.LocalWebRequestValues;

namespace StreamlinkVlcStudio.Infrastructure.LocalWeb;

public sealed partial class LocalWebWorkspace
{
    private static readonly TimeSpan CardPreviewIdleTimeout = TimeSpan.FromSeconds(30);
    private readonly AsyncOperationGate cardPreviewResolution = new();
    private CardPreview? cardPreview;

    private async Task<object> OpenCardPreviewAsync(JsonElement input, CancellationToken token)
    {
        var id = CardPreviewId(input);
        var target = Target(input);
        if (target.Kind != StreamTargetKind.Live || target.Platform is not (PlatformKind.Twitch or PlatformKind.Kick))
            throw new ArgumentException("Select a live channel to preview.");

        CardPreview preview;
        CardPreview? previous;
        lock (playerGate)
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(disposed, this);
            if (cardPreview?.Id == id) throw new ArgumentException("This preview is already open.");
            preview = new(id);
            previous = cardPreview;
            cardPreview = preview;
        }
        previous?.Dispose();

        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token, preview.Token);
            startup.CancelAfter(TimeSpan.FromSeconds(12));
            using var resolution = await cardPreviewResolution.EnterAsync(startup.Token).ConfigureAwait(false);
            var settings = await services.GetSettingsAsync(startup.Token).ConfigureAwait(false);
            var request = new StreamTransportRequest(target, LivePreviewPolicy.QualityPreference, settings.StreamlinkPath ?? "",
                settings.LowLatency, CommandLineTokenizer.Tokenize(settings.CustomStreamlinkArguments));
            var source = services.Streamlink is StreamlinkService streamlink
                ? await streamlink.ResolveCardPreviewAsync(request, startup.Token).ConfigureAwait(false)
                : new LivePreviewSource((await services.Streamlink.ResolveStreamUrlAsync(request, startup.Token).ConfigureAwait(false)).StreamUri);

            string url;
            lock (playerGate)
            {
                startup.Token.ThrowIfCancellationRequested();
                if (cardPreview != preview) throw new OperationCanceledException();
                var media = services.CreateCardPreviewMedia(LocalWebAuthentication.NewToken(), source.PlaylistUri);
                if (source.InitialPlaylist is { } initial)
                    media.SetDirectPreviewPlaylist(initial.Content, initial.Origin, target.Platform);
                preview.Media = media;
                preview.LastRequest = Stopwatch.GetTimestamp();
                url = media.PathFor(media.Root);
            }
            // A lost response or closed tab can leave an open preview with no client to close it.
            _ = ExpireCardPreviewAsync(preview);
            return new { url };
        }
        catch
        {
            CloseCardPreview(expected: preview);
            throw;
        }
    }

    private static Guid CardPreviewId(JsonElement input) =>
        Guid.TryParseExact(RequiredText(input, "id", 36), "D", out var id) ? id : throw new ArgumentException("Invalid preview ID.");

    private CardPreview? FindCardPreview(string playbackId, string resourceId)
    {
        lock (playerGate)
        {
            if (cardPreview is not { Media: { } media } preview || media.PlaybackId != playbackId ||
                !media.ContainsResource(resourceId)) return null;
            preview.LastRequest = Stopwatch.GetTimestamp();
            return preview;
        }
    }

    private void CloseCardPreview(Guid? id = null, CardPreview? expected = null)
    {
        CardPreview? preview;
        lock (playerGate)
        {
            preview = cardPreview;
            if (preview is null || id.HasValue && preview.Id != id.Value || expected is not null && preview != expected)
                return;
            cardPreview = null;
        }
        preview.Dispose();
    }

    private async Task ExpireCardPreviewAsync(CardPreview preview)
    {
        try
        {
            while (true)
            {
                TimeSpan remaining;
                lock (playerGate)
                {
                    if (cardPreview != preview) return;
                    remaining = CardPreviewIdleTimeout - Stopwatch.GetElapsedTime(preview.LastRequest);
                    if (remaining <= TimeSpan.Zero) cardPreview = null;
                }
                if (remaining <= TimeSpan.Zero)
                {
                    preview.Dispose();
                    return;
                }
                await Task.Delay(remaining, preview.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (preview.Token.IsCancellationRequested) { }
    }

    private sealed class CardPreview : IDisposable
    {
        private readonly CancellationTokenSource lifetime = new();

        internal CardPreview(Guid id)
        {
            Id = id;
            Token = lifetime.Token;
        }

        internal Guid Id { get; }
        internal CancellationToken Token { get; }
        internal LocalWebMedia? Media { get; set; }
        internal long LastRequest { get; set; }

        public void Dispose()
        {
            CancellationSourceCleanup.Cancel(lifetime);
            try { Media?.Dispose(); }
            finally { lifetime.Dispose(); }
        }
    }
}
