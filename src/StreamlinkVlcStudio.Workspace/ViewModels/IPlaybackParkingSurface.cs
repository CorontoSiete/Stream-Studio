namespace StreamlinkVlcStudio.Workspace.ViewModels;

/// <summary>A host-owned video target used while the visible video surface is detached.</summary>
public interface IPlaybackParkingSurface : IDisposable
{
    IntPtr Handle { get; }
}
