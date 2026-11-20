namespace StreamlinkVlcStudio.Infrastructure.LocalWeb;

/// <summary>A public, same-origin asset supplied by the browser UI.</summary>
public sealed record LocalWebStaticAsset(ReadOnlyMemory<byte> Content, string ContentType);
