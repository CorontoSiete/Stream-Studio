using System.ComponentModel;
using System.Runtime.InteropServices;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

internal static partial class VideoWindowInterop
{
    internal const int WsPopup = unchecked((int)0x80000000);
    internal const int ClipChildrenAndSiblings = 0x02000000 | 0x04000000;

    internal static IntPtr CreateVideoWindow(
        string windowClass, int style, int extendedStyle, int x, int y, IntPtr parent, string failureMessage)
    {
        var window = CreateWindowEx(extendedStyle, windowClass, "", style | ClipChildrenAndSiblings,
            x, y, 1, 1, parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (window == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), failureMessage);
        }

        return window;
    }

    [LibraryImport("user32", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateWindowEx(
        int extendedStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(IntPtr hwnd);
}
