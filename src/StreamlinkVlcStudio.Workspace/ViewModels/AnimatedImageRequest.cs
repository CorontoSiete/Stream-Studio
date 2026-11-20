namespace StreamlinkVlcStudio.Workspace.ViewModels;

/// <summary>An image URL and its refresh version for client-side caching.</summary>
public sealed record AnimatedImageRequest(string Url, long CacheVersion);
