using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using StreamlinkVlcStudio.Workspace.ViewModels;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

/// <summary>
/// Presents the tab's one native video surface in whichever window currently owns the tab.
/// Keeping the HwndHost instance lets WPF reparent its existing HWND between the main and
/// detached windows without making the playback engine recreate VLC's video output.
/// </summary>
public sealed class VideoSurfacePresenter : ContentControl
{
    private static readonly DependencyPropertyKey SurfacePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Surface),
        typeof(VideoSurface),
        typeof(VideoSurfacePresenter),
        new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty SurfaceProperty = SurfacePropertyKey.DependencyProperty;

    public static readonly DependencyProperty TabProperty = DependencyProperty.Register(
        nameof(Tab),
        typeof(StreamTabViewModel),
        typeof(VideoSurfacePresenter),
        new FrameworkPropertyMetadata(null, OnTabChanged));

    private static readonly ConditionalWeakTable<StreamTabViewModel, TabVideoSurface> TabSurfaces = new();

    private StreamTabViewModel? attachedTab;
    private VideoSurface? attachedSurface;
    private bool attachedSurfaceIsLoaded;

    public VideoSurfacePresenter()
    {
        Focusable = false;
        IsTabStop = false;
        Loaded += PresenterOnLoaded;
        Unloaded += PresenterOnUnloaded;
    }

    public StreamTabViewModel? Tab
    {
        get => (StreamTabViewModel?)GetValue(TabProperty);
        set => SetValue(TabProperty, value);
    }

    public VideoSurface? Surface => (VideoSurface?)GetValue(SurfaceProperty);

    /// <summary>Raised with the underlying VideoSurface as sender after its HWND is ready.</summary>
    public event RoutedEventHandler? SurfaceLoaded;

    /// <summary>Raised with the underlying VideoSurface as sender when it leaves this presenter.</summary>
    public event RoutedEventHandler? SurfaceUnloaded;

    internal static VideoSurfacePresenter? GetOwner(StreamTabViewModel tab) =>
        TabSurfaces.TryGetValue(tab, out var entry) ? entry.Owner : null;

    internal void DetachSurfaceForTransfer()
    {
        Dispatcher.VerifyAccess();
        if (attachedSurface is not null)
        {
            DetachSurface(preserveVideoHandle: true);
        }
    }

    private static void OnTabChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var presenter = (VideoSurfacePresenter)dependencyObject;
        if (presenter.attachedTab is not null && !ReferenceEquals(presenter.attachedTab, e.NewValue))
        {
            presenter.DetachSurface(preserveVideoHandle: presenter.attachedSurface?.IsHostTransferPending == true);
        }

        if (presenter.IsLoaded)
        {
            presenter.AttachSurface();
        }
    }

    private void PresenterOnLoaded(object sender, RoutedEventArgs e) => AttachSurface();

    private void PresenterOnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachSurface(preserveVideoHandle: attachedSurface?.IsHostTransferPending == true);
    }

    private void AttachSurface()
    {
        Dispatcher.VerifyAccess();
        if (Tab is not { } tab)
        {
            DetachSurface(preserveVideoHandle: attachedSurface?.IsHostTransferPending == true);
            return;
        }

        var entry = TabSurfaces.GetValue(tab, static _ => new TabVideoSurface());
        var surface = entry.Surface;
        if (ReferenceEquals(attachedTab, tab) && ReferenceEquals(attachedSurface, surface))
        {
            return;
        }

        DetachSurface(preserveVideoHandle: attachedSurface?.IsHostTransferPending == true);

        if (entry.Owner is { } previousOwner && !ReferenceEquals(previousOwner, this))
        {
            previousOwner.DetachSurfaceForTransfer();
        }

        attachedTab = tab;
        attachedSurface = surface;
        surface.Tag = tab;
        surface.Loaded += VideoSurfaceOnLoaded;
        surface.Unloaded += VideoSurfaceOnUnloaded;
        entry.Owner = this;
        SetValue(SurfacePropertyKey, surface);
        Content = surface;

        // A HwndHost can be moved between PresentationSources without raising a fresh
        // Loaded event. Check after WPF has completed the new parent/layout pass as well.
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                if (ReferenceEquals(attachedSurface, surface) && surface.IsLoaded)
                {
                    RaiseSurfaceLoaded(surface);
                }
            }));
    }

    private void DetachSurface(bool preserveVideoHandle)
    {
        var tab = attachedTab;
        var surface = attachedSurface;
        if (surface is null)
        {
            ClearOwner(tab);
            attachedTab = null;
            attachedSurfaceIsLoaded = false;
            SetValue(SurfacePropertyKey, null);
            Content = null;
            return;
        }

        surface.IsHostTransferPending = preserveVideoHandle;
        RaiseSurfaceUnloaded(surface);
        surface.Loaded -= VideoSurfaceOnLoaded;
        surface.Unloaded -= VideoSurfaceOnUnloaded;
        ClearOwner(tab);

        attachedTab = null;
        attachedSurface = null;
        attachedSurfaceIsLoaded = false;
        SetValue(SurfacePropertyKey, null);
        Content = null;
    }

    private void ClearOwner(StreamTabViewModel? tab)
    {
        if (tab is not null && TabSurfaces.TryGetValue(tab, out var entry) && ReferenceEquals(entry.Owner, this))
            entry.Owner = null;
    }

    private void VideoSurfaceOnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is VideoSurface surface)
        {
            RaiseSurfaceLoaded(surface);
        }
    }

    private void VideoSurfaceOnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is VideoSurface surface && !surface.IsLoaded)
        {
            RaiseSurfaceUnloaded(surface);
        }
    }

    private void RaiseSurfaceLoaded(VideoSurface surface)
    {
        if (!ReferenceEquals(attachedSurface, surface) || attachedSurfaceIsLoaded || surface.Handle == IntPtr.Zero)
        {
            return;
        }

        surface.IsHostTransferPending = false;
        surface.RefreshNativeWindowRouting();
        attachedSurfaceIsLoaded = true;
        SurfaceLoaded?.Invoke(surface, new RoutedEventArgs());
    }

    private void RaiseSurfaceUnloaded(VideoSurface surface)
    {
        if (!ReferenceEquals(attachedSurface, surface) || !attachedSurfaceIsLoaded)
        {
            return;
        }

        attachedSurfaceIsLoaded = false;
        SurfaceUnloaded?.Invoke(surface, new RoutedEventArgs());
    }

    private sealed class TabVideoSurface
    {
        public VideoSurface Surface { get; } = new();
        public VideoSurfacePresenter? Owner { get; set; }
    }
}
