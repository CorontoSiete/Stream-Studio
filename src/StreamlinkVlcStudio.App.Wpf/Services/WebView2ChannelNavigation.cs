using Microsoft.Web.WebView2.Core;

namespace StreamlinkVlcStudio.App.Wpf.Services;

/// <summary>Invalidates channel operations across document changes and client-side routing.</summary>
internal sealed class WebView2ChannelNavigation : IDisposable
{
    private readonly CoreWebView2 core;
    private readonly Func<string, bool> isChannelPage;
    private readonly Action reset;
    private bool wasOnChannel;

    internal WebView2ChannelNavigation(CoreWebView2 core, Func<string, bool> isChannelPage, Action reset)
    {
        this.core = core;
        this.isChannelPage = isChannelPage;
        this.reset = reset;
        wasOnChannel = IsOnChannel;
        core.NavigationStarting += OnNavigation;
        core.SourceChanged += OnSourceChanged;
    }

    internal int Version { get; private set; }
    internal bool IsOnChannel => isChannelPage(core.Source);
    internal bool IsCurrent(int version) => version == Version && IsOnChannel;

    internal async Task<bool> NavigateAsync(Uri destination, TimeSpan timeout, CancellationToken token)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? navigationId = null;
        void Starting(object? sender, CoreWebView2NavigationStartingEventArgs args)
        {
            if (navigationId is null && isChannelPage(args.Uri)) navigationId = args.NavigationId;
        }
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            // A previous navigation can finish after this call starts a new one.
            if (navigationId == args.NavigationId) completion.TrySetResult(args.IsSuccess && IsOnChannel);
        }

        core.NavigationStarting += Starting;
        core.NavigationCompleted += Completed;
        try
        {
            token.ThrowIfCancellationRequested();
            core.Navigate(destination.AbsoluteUri);
            return await completion.Task.WaitAsync(timeout, token);
        }
        finally
        {
            core.NavigationStarting -= Starting;
            core.NavigationCompleted -= Completed;
        }
    }

    private void OnNavigation(object? sender, CoreWebView2NavigationStartingEventArgs args) => Invalidate();

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs args)
    {
        var isOnChannel = IsOnChannel;
        var leftChannel = wasOnChannel && !isOnChannel;
        wasOnChannel = isOnChannel;
        if (leftChannel) Invalidate();
    }

    private void Invalidate()
    {
        Version++;
        reset();
    }

    public void Dispose()
    {
        core.NavigationStarting -= OnNavigation;
        core.SourceChanged -= OnSourceChanged;
    }
}
