using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using StreamlinkVlcStudio.App.Wpf.Services;
using StreamlinkVlcStudio.App.Wpf.Twitch;
using StreamlinkVlcStudio.Core;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Infrastructure.Viewers;

namespace StreamlinkVlcStudio.App.Wpf.Kick;

internal sealed class KickFollowedChannelsImporter(
    Window owner, string? profileDirectory = null, Action<CoreWebView2>? configureBrowser = null) : IKickFollowedChannelsImporter
{
    private const string KickHome = "https://kick.com/";
    private static readonly TimeSpan BrowserTimeout = TimeSpan.FromSeconds(30);

    internal static string ProfileDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppIdentity.ProductDirectoryName, "KickFollowsWebView2");

    public async Task<IReadOnlyList<string>?> ImportAsync(
        CancellationToken cancellationToken = default, bool openSignInWindow = false)
    {
        owner.Dispatcher.VerifyAccess();
        cancellationToken.ThrowIfCancellationRequested();
        CoreWebView2Environment environment;
        try
        {
            environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profileDirectory ?? ProfileDirectory)
                .WaitAsync(BrowserTimeout, cancellationToken);
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            throw new InvalidOperationException("Install or repair Microsoft Edge WebView2 Runtime to detect Kick follows.", ex);
        }

        var instruction = "Sign in to Kick or clear your saved sign-in here. Select Import follows to update your followed channels.";
        if (!openSignInWindow)
        {
            try { return await ImportFromSavedSessionAsync(environment, cancellationToken); }
            catch (KickFollowAccessRequiredException ex) { instruction = ex.Message; }
        }

        return await ShowSignInWindowAsync(environment, instruction, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> ImportFromSavedSessionAsync(
        CoreWebView2Environment environment, CancellationToken token)
    {
        var host = new HwndSource(new HwndSourceParameters("Kick follow detection background host")
        {
            Width = 1100,
            Height = 800,
            WindowStyle = 0
        });
        var controller = await WebView2ControllerLifetime.CreateAsync(
            host, handle => environment.CreateCoreWebView2ControllerAsync(handle), BrowserTimeout, token);
        try
        {
            token.ThrowIfCancellationRequested();
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 1100, 800);
            controller.IsVisible = true;
            var core = controller.CoreWebView2;
            PrepareBrowser(core);
            await OpenKickAsync(core, token);
            return await ReadFollowsAsync(core, progress: null, token);
        }
        finally
        {
            try { controller.Close(); }
            finally { host.Dispose(); }
        }
    }

    private static async Task OpenKickAsync(CoreWebView2 core, CancellationToken token)
    {
        var navigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? navigationId = null;
        void Starting(object? sender, CoreWebView2NavigationStartingEventArgs args)
        {
            // Kick can start a new document before WebView2 finishes reporting the previous load.
            if (!args.Cancel && (navigationId is not null || args.Uri == KickHome))
                navigationId = args.NavigationId;
        }
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (navigationId != args.NavigationId) return;
            if (args.HttpStatusCode is 401 or 403 ||
                (args.IsSuccess && Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) && uri.Host == "id.kick.com"))
                navigation.TrySetException(new KickFollowAccessRequiredException(
                    "Complete any Kick sign-in or browser check, then select Import follows."));
            else if (args.IsSuccess && KickFollowedChannelsBrowserClient.IsKickOrigin(core.Source))
                navigation.TrySetResult();
            else
                navigation.TrySetException(new InvalidOperationException(args.HttpStatusCode switch
                {
                    429 => "Kick is limiting website requests. Wait a little before trying detection again.",
                    >= 400 => $"Kick's website returned HTTP {args.HttpStatusCode}. Try detection again.",
                    _ when args.IsSuccess => "Kick opened a different address. Use Manage Kick sign-in to check the Kick website.",
                    _ => $"WebView2 could not load Kick ({args.WebErrorStatus}). Try detection again."
                }));
        }

        core.NavigationStarting += Starting;
        core.NavigationCompleted += Completed;
        try
        {
            token.ThrowIfCancellationRequested();
            core.Navigate(KickHome);
            await navigation.Task.WaitAsync(BrowserTimeout, token);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("Kick took too long to load. Check your connection and try detection again.");
        }
        finally
        {
            core.NavigationStarting -= Starting;
            core.NavigationCompleted -= Completed;
            _ = navigation.Task.Exception;
        }
    }

    private Task<IReadOnlyList<string>> ReadFollowsAsync(CoreWebView2 core, IProgress<int>? progress, CancellationToken token)
    {
        var client = new KickFollowedChannelsBrowserClient(core);
        return KickFollowedChannelsReader.ReadAllAsync(
            (url, ct) => owner.Dispatcher.InvokeAsync(() => client.ReadPageAsync(url, ct), DispatcherPriority.Normal, ct).Task.Unwrap(),
            progress, token);
    }

    private async Task<IReadOnlyList<string>?> ShowSignInWindowAsync(
        CoreWebView2Environment environment, string instruction, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = lifetime.Token;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyList<string>? imported = null;
        var closed = false;
        var busy = false;
        var pageReady = false;
        using var browser = new WebView2();
        var status = new TextBlock
        {
            Text = instruction,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12)
        };
        var import = new Button { Content = "Import follows", IsEnabled = false, Margin = new Thickness(6), Padding = new Thickness(14, 7, 14, 7) };
        var signOut = new Button { Content = "Clear Kick sign-in", IsEnabled = false, Margin = new Thickness(6), Padding = new Thickness(14, 7, 14, 7) };
        var cancel = new Button { Content = "Close", Margin = new Thickness(6), Padding = new Thickness(14, 7, 14, 7) };
        var buttons = new WrapPanel { Margin = new Thickness(6) };
        buttons.Children.Add(import);
        buttons.Children.Add(signOut);
        buttons.Children.Add(cancel);
        var layout = new DockPanel();
        DockPanel.SetDock(status, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Top);
        layout.Children.Add(status);
        layout.Children.Add(buttons);
        layout.Children.Add(browser);
        var window = new Window
        {
            Owner = owner,
            Title = "Kick sign-in",
            Width = 1100,
            Height = 800,
            MinWidth = 640,
            MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };
        void SetBusy(bool value)
        {
            busy = value;
            import.IsEnabled = !busy && pageReady;
            signOut.IsEnabled = !busy;
            browser.IsEnabled = !busy;
        }
        window.Closed += (_, _) =>
        {
            closed = true;
            lifetime.Cancel();
            completion.TrySetResult();
        };
        cancel.Click += (_, _) => window.Close();
        import.Click += async (_, _) =>
        {
            SetBusy(true);
            status.Text = "Detecting followed channels…";
            try
            {
                var result = await ReadFollowsAsync(browser.CoreWebView2,
                    new Progress<int>(count => { if (!closed) status.Text = $"Detected {count} follows. Checking for more…"; }),
                    token);
                token.ThrowIfCancellationRequested();
                imported = result;
                window.Close();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { if (!closed) status.Text = ex.Message; }
            finally { if (!closed) SetBusy(false); }
        };
        signOut.Click += async (_, _) =>
        {
            SetBusy(true);
            try
            {
                browser.CoreWebView2.Navigate("about:blank");
                await browser.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile)
                    .WaitAsync(BrowserTimeout, token);
                token.ThrowIfCancellationRequested();
                browser.CoreWebView2.Navigate(KickHome);
                status.Text = "Kick sign-in cleared. Sign in to the account whose follows you want to import.";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { if (!closed) status.Text = ex.Message; }
            finally { if (!closed) SetBusy(false); }
        };
        window.Loaded += async (_, _) =>
        {
            try
            {
                await browser.EnsureCoreWebView2Async(environment).WaitAsync(BrowserTimeout, token);
                token.ThrowIfCancellationRequested();
                var core = browser.CoreWebView2;
                PrepareBrowser(core);
                ulong? navigationId = null;
                core.NavigationStarting += (_, args) =>
                {
                    if (args.Cancel) return;
                    navigationId = args.NavigationId;
                    pageReady = false;
                    import.IsEnabled = false;
                };
                core.NavigationCompleted += (_, args) =>
                {
                    if (navigationId != args.NavigationId) return;
                    pageReady = args.IsSuccess && KickFollowedChannelsBrowserClient.IsKickOrigin(core.Source);
                    import.IsEnabled = !busy && pageReady;
                };
                core.Navigate(KickHome);
                SetBusy(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception)
            {
                if (!closed) status.Text = "The Kick sign-in browser could not start. Install or repair Microsoft Edge WebView2 Runtime, then try again.";
            }
        };
        using var registration = token.Register(() => owner.Dispatcher.BeginInvoke(() => { if (!closed) window.Close(); }));
        window.Show();
        await completion.Task;
        cancellationToken.ThrowIfCancellationRequested();
        return imported;
    }

    private void PrepareBrowser(CoreWebView2 core)
    {
        ConfigureBrowser(core);
        configureBrowser?.Invoke(core);
    }

    internal static void ConfigureBrowser(CoreWebView2 core, bool blockMedia = true)
    {
        core.IsMuted = true;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = true;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.NavigationStarting += (_, args) => args.Cancel = !IsAllowedNavigation(args.Uri);
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (args.IsUserInitiated && IsAllowedNavigation(args.Uri)) core.Navigate(args.Uri);
        };
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (_, args) =>
        {
            if (blockMedia && TwitchBonusBrowser.IsStreamResource(args.Request.Uri, args.ResourceContext))
                args.Response = core.Environment.CreateWebResourceResponse(null, 403, "Media disabled", "Cache-Control: no-store");
        };
    }

    internal static bool IsAllowedNavigation(string address) => address == "about:blank" ||
        (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
         uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Host is "kick.com" or "www.kick.com" or "id.kick.com");
}
