using System.Collections.Frozen;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;

namespace StreamlinkVlcStudio.WebClient;

public static class BundledWebClient
{
    private static readonly Lazy<IReadOnlyDictionary<string, LocalWebStaticAsset>> bundledAssets = new(LoadAssets);

    public static IReadOnlyDictionary<string, LocalWebStaticAsset> Assets => bundledAssets.Value;

    private static FrozenDictionary<string, LocalWebStaticAsset> LoadAssets()
    {
        var assembly = typeof(BundledWebClient).Assembly;
        var assets = new Dictionary<string, LocalWebStaticAsset>(StringComparer.Ordinal);
        foreach (var (name, contentType) in new (string, string)[]
        {
            ("index.html", "text/html; charset=utf-8"),
            ("app.js", "text/javascript; charset=utf-8"),
            ("app.css", "text/css; charset=utf-8"),
            ("studio-logo.svg", "image/svg+xml"),
            ("twitch-logo.svg", "image/svg+xml"),
            ("kick-logo.svg", "image/svg+xml"),
            ("hls.min.js", "text/javascript; charset=utf-8"),
            ("hls.LICENSE.txt", "text/plain; charset=utf-8"),
            ("hls.APACHE-2.0.txt", "text/plain; charset=utf-8")
        })
        {
            using var stream = assembly.GetManifestResourceStream($"StreamStudio.WebClient.{name}")
                ?? throw new InvalidOperationException($"Missing bundled web client asset '{name}'.");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            assets.Add(name == "index.html" ? "/" : "/" + name, new(buffer.ToArray(), contentType));
        }
        return assets.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
