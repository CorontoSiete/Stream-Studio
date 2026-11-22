using System.Collections.Frozen;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StreamlinkVlcStudio.Core.Json;
using StreamlinkVlcStudio.Infrastructure.Threading;
using EntityTagHeaderValue = Microsoft.Net.Http.Headers.EntityTagHeaderValue;
using HeaderNames = Microsoft.Net.Http.Headers.HeaderNames;

namespace StreamlinkVlcStudio.Infrastructure.LocalWeb;

public interface ILocalWebWorkspace : IAsyncDisposable
{
    Task<object?> InvokeAsync(string action, JsonElement input, CancellationToken cancellationToken);
    Task ServeMediaAsync(HttpContext context, string playbackId, string resourceId);
    Task ServeCategoryArtworkAsync(HttpContext context, string assetId)
    {
        context.Response.StatusCode = 404;
        return Task.CompletedTask;
    }
    Task ServeChatAssetAsync(HttpContext context, string assetId)
    {
        context.Response.StatusCode = 404;
        return Task.CompletedTask;
    }
}

public sealed class LocalWebServer : IAsyncDisposable
{
    internal const string CookieName = "__Host-StreamStudio";
    private readonly WebApplication app;
    private readonly LocalWebAuthentication auth = new();
    private readonly LocalWebUserStore users;
    private readonly Func<ILocalWebWorkspace> createWorkspace;
    private readonly LocalWebInterface network;
    private readonly Func<bool> networkIsCurrent;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object disposalGate = new();
    private Task? maintenance;
    private bool disposed;
    private Task? disposalTask;
    private readonly FrozenDictionary<string, (LocalWebStaticAsset Asset, EntityTagHeaderValue? Validator)> assets;
    public Uri Address { get; private set; } = null!;
    internal LocalWebUserStore Users => users;

    private LocalWebServer(LocalWebInterface network, X509Certificate2 certificate, int port,
        Func<ILocalWebWorkspace> createWorkspace, LocalWebUserStore users, Func<bool> networkIsCurrent,
        IReadOnlyDictionary<string, LocalWebStaticAsset>? assets)
    {
        this.network = network;
        this.createWorkspace = createWorkspace;
        this.users = users;
        this.networkIsCurrent = networkIsCurrent;
        this.assets = (assets ?? FrozenDictionary<string, LocalWebStaticAsset>.Empty).ToFrozenDictionary(
            entry => entry.Key,
            entry => (entry.Value, AssetValidator(entry.Key, entry.Value)),
            StringComparer.Ordinal);
        // Empty configuration prevents environment variables/appsettings from adding public listeners.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { ApplicationName = typeof(LocalWebServer).Assembly.FullName });
        builder.Logging.ClearProviders(); // Never log passwords, cookies, signed URLs, or account data.
        builder.WebHost.UseKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 16 * 1024;
            options.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            options.Limits.MaxRequestLineSize = 4096;
            options.Limits.MaxConcurrentConnections = 96;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            options.Listen(network.Address, port, endpoint =>
            {
                endpoint.Protocols = HttpProtocols.Http1AndHttp2;
                endpoint.UseHttps(certificate, https => https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13);
            });
        });
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        builder.Services.AddResponseCompression(options => options.MimeTypes =
            ["text/html", "text/css", "text/javascript", "application/javascript", "image/svg+xml", LocalWebMedia.PlaylistContentType]);
        app = builder.Build();
        app.UseResponseCompression();
        app.Run(HandleAsync);
    }

    public static Task<LocalWebServer> StartAsync(LocalWebInterface network, X509Certificate2 certificate,
        int port, Func<ILocalWebWorkspace> createWorkspace, LocalWebUserStore users, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, LocalWebStaticAsset>? assets = null)
    {
        if (!network.IsCurrent()) throw new InvalidOperationException("Choose a currently connected private Ethernet or Wi-Fi interface.");
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        return StartCoreAsync(network, certificate, port, createWorkspace, users, network.IsCurrent, cancellationToken, assets);
    }

    internal static async Task<LocalWebServer> StartCoreAsync(LocalWebInterface network, X509Certificate2 certificate,
        int port, Func<ILocalWebWorkspace> createWorkspace, LocalWebUserStore users, Func<bool> networkIsCurrent,
        CancellationToken cancellationToken = default, IReadOnlyDictionary<string, LocalWebStaticAsset>? assets = null)
    {
        if (users.ListUsers().Count == 0) throw new InvalidOperationException("Create a web account before starting the server.");
        var server = new LocalWebServer(network, certificate, port, createWorkspace, users, networkIsCurrent, assets);
        try
        {
            await server.app.StartAsync(cancellationToken).ConfigureAwait(false);
            server.Address = new Uri(server.app.Urls.Single());
            server.maintenance = server.MaintainAsync();
            return server;
        }
        catch { await server.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async Task DisconnectAllAsync()
    {
        LocalWebSession[] sessions;
        lock (auth.Gate)
        {
            sessions = auth.Sessions.Values.ToArray();
            auth.Sessions.Clear();
        }
        await Task.WhenAll(sessions.Select(s => s.DisposeAsync().AsTask())).ConfigureAwait(false);
    }

    public async Task DisconnectUserAsync(string userId)
    {
        LocalWebSession[] sessions;
        lock (auth.Gate)
        {
            sessions = auth.Sessions.Values.Where(s => s.UserId == userId).ToArray();
            foreach (var session in sessions) auth.Sessions.Remove(session.Token);
        }
        await Task.WhenAll(sessions.Select(s => s.DisposeAsync().AsTask())).ConfigureAwait(false);
    }

    private async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.XFrameOptions = "DENY";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' https:; connect-src 'self'; media-src 'self' blob:; worker-src blob:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        var peer = context.Connection.RemoteIpAddress;
        if (peer?.IsIPv4MappedToIPv6 == true) peer = peer.MapToIPv4();
        if (Volatile.Read(ref disposed) || Address is not { } address ||
            peer is null || !network.Allows(peer) || !networkIsCurrent() ||
            !request.IsHttps || request.Host.Value != address.Authority ||
            request.QueryString.HasValue && (!request.Path.StartsWithSegments("/media") || !ValidMediaQuery(request.Query)))
        { response.StatusCode = 403; return; }
        var origin = request.Headers.Origin;
        if (origin.Count > 1 || origin.Count == 1 && origin[0] != address.GetLeftPart(UriPartial.Authority) ||
            request.Headers["Sec-Fetch-Site"].ToString() is "cross-site")
        { response.StatusCode = 403; return; }
        var path = request.Path.Value ?? "";
        if (request.Method is "GET" or "HEAD" && assets.TryGetValue(path, out var entry) &&
            !request.Path.StartsWithSegments("/api") && !request.Path.StartsWithSegments("/media") &&
            !request.Path.StartsWithSegments("/category-artwork") && !request.Path.StartsWithSegments("/chat-assets"))
        {
            var (asset, validator) = entry;
            context.Features.Get<IHttpsCompressionFeature>()?.Mode = HttpsCompressionMode.Compress;
            if (validator is not null)
            {
                response.Headers.CacheControl = "private, no-cache";
                response.Headers.Vary = HeaderNames.AcceptEncoding;
            }
            await Results.Bytes(asset.Content, asset.ContentType, entityTag: validator).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }
        if (request.Method == "POST" && (origin.Count != 1 || !request.HasJsonContentType() ||
            request.Headers["X-StreamStudio"].ToString() != "1"))
        { response.StatusCode = 403; return; }
        try
        {
            if (path == "/api/login" && request.Method == "POST")
            {
                using var loginDeadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.Token);
                loginDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                var body = await request.ReadFromJsonAsync<JsonElement>(loginDeadline.Token).ConfigureAwait(false);
                JsonElementReader.TryGetNonEmptyString(body, "username", out var username);
                JsonElementReader.TryGetNonEmptyString(body, "password", out var password);
                if (!auth.TryConsumeLoginAttempt(peer))
                {
                    response.StatusCode = 429;
                    response.Headers.RetryAfter = "300";
                    return;
                }
                var user = await users.VerifyAsync(username, password, loginDeadline.Token).ConfigureAwait(false);
                if (user is null) { response.StatusCode = 401; return; }
                LocalWebSession session;
                lock (auth.Gate)
                {
                    loginDeadline.Token.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref disposed)) { response.StatusCode = 403; return; }
                    if (auth.Sessions.Count >= 32) { response.StatusCode = 429; return; }
                    if (!users.IsCurrent(user.Id, user.CredentialVersion)) { response.StatusCode = 401; return; }
                    session = new(peer, user.Id, user.Username, user.CredentialVersion,
                        new LocalWebUserWorkspace(createWorkspace(), users, user.Id), DateTimeOffset.UtcNow);
                    auth.Sessions.Add(session.Token, session);
                }
                response.Cookies.Append(CookieName, session.Token, CookieOptions());
                await response.WriteAsJsonAsync(new { csrf = session.Csrf, username = session.Username }, context.RequestAborted).ConfigureAwait(false);
                return;
            }
            var current = auth.Find(request.Cookies[CookieName], peer);
            if (current is null || !users.IsCurrent(current.UserId, current.CredentialVersion)) { response.StatusCode = 401; return; }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, current.Lifetime.Token, lifetime.Token);
            context.RequestAborted = linked.Token;
            if (!await current.Requests.WaitAsync(0, linked.Token).ConfigureAwait(false)) { response.StatusCode = 429; return; }
            try
            {
                if (path == "/api/session" && request.Method == "GET")
                {
                    await response.WriteAsJsonAsync(new { csrf = current.Csrf, username = current.Username }, linked.Token).ConfigureAwait(false);
                    return;
                }
                if (path.StartsWith("/category-artwork/", StringComparison.Ordinal) && request.Method is "GET" or "HEAD")
                {
                    await current.Workspace.ServeCategoryArtworkAsync(context, path[18..]).ConfigureAwait(false);
                    return;
                }
                if (path.StartsWith("/media/", StringComparison.Ordinal) && request.Method is "GET" or "HEAD")
                {
                    var pieces = path.Split('/');
                    if (pieces.Length != 4) { response.StatusCode = 404; return; }
                    await current.Workspace.ServeMediaAsync(context, pieces[2], pieces[3]).ConfigureAwait(false);
                    return;
                }
                if (path.StartsWith("/chat-assets/", StringComparison.Ordinal) && request.Method is "GET" or "HEAD")
                {
                    await current.Workspace.ServeChatAssetAsync(context, path[13..]).ConfigureAwait(false);
                    return;
                }
                if (request.Method != "POST") { response.StatusCode = 405; return; }
                if (request.Headers["X-CSRF-Token"].ToString() != current.Csrf) { response.StatusCode = 403; return; }
                if (path == "/api/logout")
                {
                    lock (auth.Gate) auth.Sessions.Remove(current.Token);
                    response.Cookies.Delete(CookieName, CookieOptions());
                    await current.DisposeAsync().ConfigureAwait(false);
                    return;
                }
                if (!path.StartsWith("/api/", StringComparison.Ordinal)) { response.StatusCode = 404; return; }
                // Preview resolution and cleanup have their own lifetime and must not hold the command queue.
                var independent = path is "/api/categories/viewers" or "/api/following/history" or "/api/hotkeys" or "/api/settings/save" or
                    "/api/preview/open" or "/api/preview/close";
                if (!independent && !await current.Commands.WaitAsync(0, linked.Token).ConfigureAwait(false)) { response.StatusCode = 429; return; }
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(90));
                    var input = await request.ReadFromJsonAsync<JsonElement>(timeout.Token).ConfigureAwait(false);
                    if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected a JSON object.");
                    var result = await current.Workspace.InvokeAsync(path[5..], input, timeout.Token).ConfigureAwait(false);
                    await response.WriteAsJsonAsync(result, timeout.Token).ConfigureAwait(false);
                }
                finally { if (!independent) current.Commands.Release(); }
            }
            finally { current.Requests.Release(); }
        }
        catch (OperationCanceledException) { if (!response.HasStarted) response.StatusCode = 408; }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidDataException or Microsoft.AspNetCore.Http.BadHttpRequestException)
        { if (!response.HasStarted) { response.StatusCode = 400; await response.WriteAsJsonAsync(new { error = "The request or media is invalid." }); } }
        catch (UnauthorizedAccessException) { if (!response.HasStarted) response.StatusCode = 401; }
        catch (Exception)
        { if (!response.HasStarted) { response.StatusCode = 502; await response.WriteAsJsonAsync(new { error = "The provider could not complete this request. Check the desktop account connection and try again." }); } }
    }

    private static EntityTagHeaderValue? AssetValidator(string path, LocalWebStaticAsset asset) =>
        path == "/" || asset.ContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
            ? null
            : new EntityTagHeaderValue($"\"{Convert.ToHexString(SHA256.HashData(asset.Content.Span))}\"", isWeak: true);

    private static CookieOptions CookieOptions() => new() { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true };

    private static bool ValidMediaQuery(IQueryCollection query) => query.Count is > 0 and <= 3 && query.All(pair =>
        pair.Value.Count == 1 && (pair.Key is "_HLS_msn" or "_HLS_part"
            ? pair.Value[0] is { Length: > 0 and <= 20 } number && number.All(char.IsAsciiDigit)
            : pair.Key == "_HLS_skip" && pair.Value[0] is "YES" or "v2"));

    private async Task MaintainAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
            {
                LocalWebSession[] expired;
                lock (auth.Gate)
                {
                    expired = auth.Sessions.Values.Where(s => s.IsExpired(DateTimeOffset.UtcNow) ||
                        !users.IsCurrent(s.UserId, s.CredentialVersion) || !networkIsCurrent()).ToArray();
                    foreach (var session in expired) auth.Sessions.Remove(session.Token);
                }
                // Failed backend cleanup cannot stop future expiry checks or restore authentication.
                foreach (var session in expired)
                    try { await session.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception) { }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    public ValueTask DisposeAsync() => new(AsyncDisposal.Begin(disposalGate, ref disposed, ref disposalTask, DisposeCoreAsync));

    private async Task DisposeCoreAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            if (maintenance is not null) await maintenance.ConfigureAwait(false);
        }
        finally
        {
            LocalWebSession[] sessions;
            lock (auth.Gate) { sessions = auth.Sessions.Values.ToArray(); auth.Sessions.Clear(); }
            try
            {
                await Task.WhenAll(sessions.Select(s => s.DisposeAsync().AsTask())).ConfigureAwait(false);
            }
            finally
            {
                try { await app.StopAsync(deadline.Token).ConfigureAwait(false); }
                finally
                {
                    try { await app.DisposeAsync().ConfigureAwait(false); }
                    finally { lifetime.Dispose(); }
                }
            }
        }
    }
}
