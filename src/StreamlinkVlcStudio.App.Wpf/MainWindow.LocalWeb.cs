using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;
using StreamlinkVlcStudio.Infrastructure.Vod;
using StreamlinkVlcStudio.WebClient;

namespace StreamlinkVlcStudio.App.Wpf;

public partial class MainWindow
{
    private LocalWebDependencies? localWebDependencies;
    private LocalWebServer? localWebServer;
    private Task<LocalWebUserStore>? localWebUsersLoad;
    private LocalWebCertificatePair? localWebCertificates;
    private Window? localWebWindow;
    private bool localWebWindowOpening;
    private Action? refreshLocalWebWindow;
    private CancellationTokenSource? localWebStartup;
    private Task? localWebStartupTask;
    private bool localWebStopping;
    private string? localWebStartMessage;

    private void ConfigureLocalWebServer(AppSettings settings, IStreamlinkService streamlink, IStreamSearchService search,
        IFollowedStreamsService following, IBrowseService browse, ITwitchVodService twitchVods, IKickVodService kickVods,
        IVodDownloadService downloads, IVodPlaybackHistory history, IVodChatProvider vodChat, IReplayResolver replay,
        ITwitchSubOnlyVodResolver subOnlyVods, ITwitchClipService twitchClips, IKickClipService kickClips,
        IChannelFollowService follows, IAppLogger logger)
    {
        localWebDependencies = new()
        {
            // Use the same account state as desktop services so refreshed credentials are persisted.
            GetSettingsAsync = token => Dispatcher.InvokeAsync(() => settings, DispatcherPriority.Background, token).Task,
            GetDesktopHotkeysAsync = token => Dispatcher.InvokeAsync(() => LocalWebDesktopHotkeys.Capture(
                settings.Hotkeys, static (value, fallback) => HotkeyGesture.ParseOrDefault(value, fallback).Serialize()),
                DispatcherPriority.Background, token).Task,
            ChangeSettingsAsync = (change, token) => Dispatcher.InvokeAsync(async () =>
            {
                change(settings);
                if (settingsService is not null) await settingsService.SaveAsync(settings, token);
            }, DispatcherPriority.Background, token).Task.Unwrap(),
            Streamlink = streamlink,
            Search = search,
            Following = following,
            GetDesktopFollowingAsync = (refresh, token) => Dispatcher.InvokeAsync(
                () => viewModel?.GetWebFollowingAsync(refresh, token) ?? Task.FromResult<FollowedLiveStreamsResult?>(null),
                DispatcherPriority.Background, token).Task.Unwrap(),
            Browse = browse,
            TwitchVods = twitchVods,
            KickVods = kickVods,
            Downloads = downloads,
            History = history,
            VodChat = vodChat,
            Replay = replay,
            SubOnlyVods = subOnlyVods,
            ResolveKickVodAsync = new KickVodDownloadResolver(logger).ResolveAsync,
            ChatFactory = snapshot => new ChatClientFactory(snapshot, logger),
            ChatPresentation = Chat.LocalWebChatPresentation.Shared,
            CreateClipAsync = (target, token) => Dispatcher.InvokeAsync(async () => target.Platform == PlatformKind.Twitch
                ? (await twitchClips.CreateLiveClipAsync(target, settings.Chat, token)).ClipUri
                : (await kickClips.CreateLiveClipAsync(target, token))?.ClipUri, DispatcherPriority.Normal, token).Task.Unwrap(),
            GetFollowStatusAsync = (target, token) => Dispatcher.InvokeAsync(() => follows.GetFollowStatusAsync(target, token),
                DispatcherPriority.Background, token).Task.Unwrap(),
            FollowAsync = (target, desired, token) => Dispatcher.InvokeAsync(async () =>
            {
                var result = desired ? await follows.FollowAsync(target, token) : await follows.UnfollowAsync(target, token);
                if (result?.KickFollowedChannels is { } channels)
                {
                    settings.FollowedChannels.KickImportedChannelSlugs = channels.ToList();
                    settings.FollowedChannels.KickFollowsImportedAtUtc = DateTimeOffset.UtcNow;
                    if (settingsService is not null) await settingsService.SaveAsync(settings, token);
                }
                return result;
            }, DispatcherPriority.Normal, token).Task.Unwrap()
        };
    }

    private async void LocalWebServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel?.Settings is not { } settings || localWebDependencies is null || shutdownStarted || localWebWindowOpening) return;
        if (localWebWindow is not null) { localWebWindow.Activate(); return; }
        localWebWindowOpening = true;
        LocalWebUserStore localWebUsers;
        try { localWebUsers = await GetLocalWebUsersAsync(); }
        catch (Exception error)
        {
            if (!shutdownStarted)
                MessageBox.Show(this, $"Could not load web accounts: {error.Message}", "LAN browser server", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally { localWebWindowOpening = false; }
        if (shutdownStarted) return;
        var window = new LocalWebServerWindow { Owner = this };
        localWebWindow = window;
        var networks = window.Networks;
        var availableNetworks = LocalWebInterface.GetAvailable();
        var savedEndpoint = settings.LocalWebServerEndpoint;
        networks.ItemsSource = availableNetworks;
        networks.SelectedItem = savedEndpoint is null ? availableNetworks.FirstOrDefault() : FindLocalWebNetwork(availableNetworks, savedEndpoint);
        var port = window.Port;
        if (savedEndpoint is not null) port.Text = savedEndpoint.Port.ToString(CultureInfo.InvariantCulture);
        var status = window.Status;
        var start = window.Start;
        var stop = window.Stop;
        var rotate = window.Rotate;
        var export = window.Export;
        var users = window.Users;
        var accountStatus = window.AccountStatus;
        var addUser = window.AddUser;
        var changePassword = window.ChangePassword;
        var removeUser = window.RemoveUser;
        var accountBusy = false;
        void RefreshUsers(string? selectedId = null)
        {
            selectedId ??= (users.SelectedItem as LocalWebUserSummary)?.Id;
            var entries = localWebUsers.ListUsers();
            users.ItemsSource = entries;
            users.SelectedItem = entries.FirstOrDefault(entry => entry.Id == selectedId);
            if (users.SelectedItem is null && entries.Count > 0) users.SelectedIndex = 0;
        }
        void Refresh()
        {
            var starting = localWebStartup is not null;
            var canConfigure = !starting && localWebServer is null;
            start.IsEnabled = canConfigure && networks.SelectedItem is LocalWebInterface && localWebUsers.ListUsers().Count > 0;
            stop.IsEnabled = rotate.IsEnabled = !starting && !localWebStopping && localWebServer is not null;
            export.IsEnabled = !starting && localWebCertificates is not null;
            networks.IsEnabled = port.IsEnabled = canConfigure;
            addUser.IsEnabled = !accountBusy;
            changePassword.IsEnabled = removeUser.IsEnabled = !accountBusy && users.SelectedItem is LocalWebUserSummary;
            if (starting) status.Text = "Starting the browser server…";
            else if (localWebStopping) status.Text = "Stopping the browser server…";
            else if (localWebServer is null) status.Text = "Stopped.";
            else
            {
                if (settings.LocalWebServerEndpoint is { } runningEndpoint)
                    networks.SelectedItem = FindLocalWebNetwork(availableNetworks, runningEndpoint);
                port.Text = localWebServer.Address.Port.ToString(CultureInfo.InvariantCulture);
                status.Text = $"Address: {localWebServer.Address}\nCA SHA-256: {LocalWebCertificate.Fingerprint(localWebCertificates!.Authority)}\nServer SHA-256: {LocalWebCertificate.Fingerprint(localWebCertificates.Server)}";
            }
            if (localWebStartMessage is not null) status.Text += "\n\n" + localWebStartMessage;
        }
        async Task ManageAccountAsync(Func<Task<string?>> operation)
        {
            accountBusy = true; accountStatus.Text = "Saving web account…"; Refresh();
            try
            {
                var selectedId = await operation();
                window.Password.Clear(); window.ConfirmPassword.Clear();
                RefreshUsers(selectedId);
                accountStatus.Text = "Web account saved.";
            }
            catch (Exception error) { accountStatus.Text = error.Message; }
            finally { accountBusy = false; Refresh(); }
        }
        void CheckPasswords()
        {
            if (window.Password.Password != window.ConfirmPassword.Password)
                throw new ArgumentException("Passwords do not match.");
        }
        addUser.Click += async (_, _) => await ManageAccountAsync(async () =>
        {
            CheckPasswords();
            var added = await localWebUsers.AddAsync(window.Username.Text.Trim(), window.Password.Password);
            window.Username.Clear();
            return added.Id;
        });
        changePassword.Click += async (_, _) => await ManageAccountAsync(async () =>
        {
            if (users.SelectedItem is not LocalWebUserSummary selected) throw new ArgumentException("Choose an account.");
            CheckPasswords();
            await localWebUsers.ChangePasswordAsync(selected.Id, window.Password.Password);
            if (localWebServer is not null) await localWebServer.DisconnectUserAsync(selected.Id);
            return selected.Id;
        });
        removeUser.Click += async (_, _) =>
        {
            if (users.SelectedItem is not LocalWebUserSummary selected) return;
            if (MessageBox.Show(window, $"Remove web account '{selected.Username}' and its saved browser preferences?",
                "Remove web account", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await ManageAccountAsync(async () =>
            {
                await localWebUsers.RemoveAsync(selected.Id);
                if (localWebServer is not null) await localWebServer.DisconnectUserAsync(selected.Id);
                return null;
            });
        };
        users.SelectionChanged += (_, _) => Refresh();
        networks.SelectionChanged += (_, _) => { if (localWebServer is null) Refresh(); };
        refreshLocalWebWindow = Refresh;
        start.Click += async (_, _) =>
        {
            int.TryParse(port.Text, out var number);
            var endpoint = networks.SelectedItem is LocalWebInterface network
                ? new LocalWebServerEndpoint(network.Id, network.Address.ToString(), network.PrefixLength, number)
                : null;
            await StartLocalWebServerAsync(settings, endpoint, automatic: false);
        };
        stop.Click += async (_, _) => await StopLocalWebServerAsync();
        rotate.Click += async (_, _) =>
        {
            rotate.IsEnabled = false;
            try { if (localWebServer is not null) await localWebServer.DisconnectAllAsync(); }
            finally { Refresh(); }
        };
        export.Click += (_, _) =>
        {
            var dialog = new SaveFileDialog { FileName = "StreamStudio-LAN-CA.cer", Filter = "Public CA certificate (*.cer)|*.cer" };
            if (localWebCertificates is not null && dialog.ShowDialog(window) == true)
                File.WriteAllBytes(dialog.FileName, localWebCertificates.Authority.Export(X509ContentType.Cert));
        };
        window.Closed += (_, _) => { localWebWindow = null; refreshLocalWebWindow = null; };
        RefreshUsers();
        Refresh();
        window.Show();
    }

    private async Task<LocalWebUserStore> GetLocalWebUsersAsync()
    {
        var load = localWebUsersLoad ??= LocalWebUserStore.OpenAsync();
        try { return await load; }
        catch
        {
            if (ReferenceEquals(localWebUsersLoad, load)) localWebUsersLoad = null;
            throw;
        }
    }

    internal static LocalWebInterface? FindLocalWebNetwork(IEnumerable<LocalWebInterface> networks, LocalWebServerEndpoint endpoint) =>
        networks.FirstOrDefault(network => network.Id == endpoint.InterfaceId &&
            network.Address.ToString() == endpoint.Address && network.PrefixLength == endpoint.PrefixLength);

    private Task StartLocalWebServerAsync(AppSettings settings, LocalWebServerEndpoint? endpoint, bool automatic)
    {
        if (localWebDependencies is not { } dependencies || shutdownStarted || localWebStartup is not null || localWebServer is not null)
            return Task.CompletedTask;

        var task = StartAsync();
        localWebStartupTask = task.IsCompleted ? null : task;
        return task;

        async Task StartAsync()
        {
            using var startup = new CancellationTokenSource();
            localWebStartup = startup;
            localWebStartMessage = null;
            try
            {
                refreshLocalWebWindow?.Invoke();
                if (endpoint is null)
                    throw new InvalidOperationException(automatic
                        ? "Start the server once to save its network address and HTTPS port."
                        : "Choose a connected LAN interface.");
                if (endpoint.Port is < 1024 or > 65535)
                    throw new ArgumentException("Choose an HTTPS port from 1024 to 65535.");
                var network = FindLocalWebNetwork(LocalWebInterface.GetAvailable(), endpoint)
                    ?? throw new InvalidOperationException("The LAN address is unavailable. Choose a connected interface and start the server again.");

                var users = await GetLocalWebUsersAsync().WaitAsync(startup.Token);
                if (users.ListUsers().Count == 0)
                    throw new InvalidOperationException("Create a web account before starting the server.");

                localWebCertificates?.Dispose();
                localWebCertificates = null;
                localWebCertificates = await Task.Run(() => LocalWebCertificate.GetOrCreate(network.Address), startup.Token);
                startup.Token.ThrowIfCancellationRequested();
                localWebServer = await LocalWebServer.StartAsync(network, localWebCertificates.Server, endpoint.Port,
                    () => new LocalWebWorkspace(dependencies), users, startup.Token, BundledWebClient.Assets);
                if (automatic || startup.IsCancellationRequested) return;

                settings.LocalWebServerEndpoint = endpoint;
                try { OpenLocalWebBrowser(localWebServer, info => Process.Start(info)?.Dispose()); }
                catch (Exception)
                {
                    localWebStartMessage = "The server is running, but the default browser could not open. Open the address above and sign in.";
                }
            }
            catch (OperationCanceledException) when (startup.IsCancellationRequested) { }
            catch (Exception error)
            {
                localWebStartMessage = automatic ? $"Automatic startup failed. {error.Message}" : $"Could not start the server. {error.Message}";
                if (automatic) appLogger?.Write(AppLogLevel.Warning, "LocalWeb", "Automatic browser server startup failed.", error);
            }
            finally
            {
                localWebStartup = null;
                localWebStartupTask = null;
                refreshLocalWebWindow?.Invoke();
            }
        }
    }

    internal static void OpenLocalWebBrowser(LocalWebServer server, Action<ProcessStartInfo> startProcess)
    {
        startProcess(new ProcessStartInfo(server.Address.AbsoluteUri) { UseShellExecute = true });
    }

    private async Task StopLocalWebServerAsync()
    {
        localWebStartup?.Cancel();
        if (localWebStartupTask is { } startup) await startup;
        if (localWebServer is not { } server) return;
        localWebStopping = true;
        localWebStartMessage = null;
        refreshLocalWebWindow?.Invoke();
        try { await server.DisposeAsync(); }
        catch (Exception error) { appLogger?.Write(AppLogLevel.Warning, "LocalWeb", "Browser server shutdown failed.", error); }
        finally
        {
            if (ReferenceEquals(localWebServer, server))
            {
                localWebServer = null;
                localWebStopping = false;
                refreshLocalWebWindow?.Invoke();
            }
        }
    }
}
