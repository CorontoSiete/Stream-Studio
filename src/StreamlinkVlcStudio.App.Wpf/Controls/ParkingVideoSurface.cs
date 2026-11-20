using System.Windows.Threading;
using StreamlinkVlcStudio.Workspace.ViewModels;
using static StreamlinkVlcStudio.App.Wpf.Controls.VideoWindowInterop;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

internal sealed class ParkingVideoSurface : IPlaybackParkingSurface
{
    private readonly Dispatcher ownerDispatcher = Dispatcher.CurrentDispatcher;
    private IntPtr handle;

    public ParkingVideoSurface()
    {
        handle = CreateVideoWindow("STATIC", WsPopup, 0, -32000, -32000, IntPtr.Zero,
            "Failed to create the parking video surface window.");
    }

    public IntPtr Handle => handle;

    public void Dispose()
    {
        var window = Interlocked.Exchange(ref handle, IntPtr.Zero);
        if (window == IntPtr.Zero) return;

        // Win32 requires the thread that created the window to destroy it.
        // Playback teardown runs on a worker, so queue cleanup without blocking it.
        if (ownerDispatcher.CheckAccess())
        {
            DestroyWindow(window);
        }
        else
        {
            _ = ownerDispatcher.InvokeAsync(() => DestroyWindow(window));
        }
    }
}
