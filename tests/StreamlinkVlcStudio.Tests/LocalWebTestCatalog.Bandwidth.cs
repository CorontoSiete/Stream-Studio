using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;
using StreamlinkVlcStudio.WebClient;

internal static partial class LocalWebTestCatalog
{
    private static async Task CompressAndRevalidateAssetsAsync()
    {
        using var certificate = Certificate();
        await using var server = await Server(certificate);
        using var http = Client(certificate, server.Address);
        long originalBytes = 0;
        long compressedBytes = 0;
        foreach (var path in new[] { "/", "/app.css", "/app.js", "/hls.min.js" })
        {
            using var identity = await http.GetAsync(path);
            identity.EnsureSuccessStatusCode();
            var original = await identity.Content.ReadAsByteArrayAsync();
            Assert.True(original.AsSpan().SequenceEqual(BundledWebClient.Assets[path].Content.Span));

            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.AcceptEncoding.ParseAdd("gzip");
            using var encoded = await http.SendAsync(request);
            encoded.EnsureSuccessStatusCode();
            Assert.SequenceEqual(new[] { "gzip" }, encoded.Content.Headers.ContentEncoding);
            var bytes = await encoded.Content.ReadAsByteArrayAsync();
            var decoded = await GunzipAsync(bytes);
            Assert.True(original.AsSpan().SequenceEqual(decoded));
            originalBytes += original.Length;
            compressedBytes += bytes.Length;

            if (path == "/")
            {
                Assert.True(encoded.Headers.CacheControl?.NoStore == true);
                continue;
            }
            Assert.True(identity.Headers.CacheControl is { Private: true, NoCache: true });
            Assert.True(identity.Headers.Vary.Contains("Accept-Encoding"));
            Assert.True(encoded.Headers.Vary.Contains("Accept-Encoding"));
            var validator = encoded.Headers.ETag ?? throw new InvalidOperationException("Asset validator is missing.");
            Assert.Equal(identity.Headers.ETag, validator);
            using var recheck = new HttpRequestMessage(HttpMethod.Get, path);
            recheck.Headers.AcceptEncoding.ParseAdd("gzip");
            recheck.Headers.IfNoneMatch.Add(validator);
            using var cached = await http.SendAsync(recheck);
            Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);
            Assert.Equal(0, (await cached.Content.ReadAsByteArrayAsync()).Length);
            Assert.Equal(validator, cached.Headers.ETag);
            Assert.True(cached.Headers.Vary.Contains("Accept-Encoding"));
        }
        Assert.True(compressedBytes < originalBytes / 2);
        Console.WriteLine($"LAN page gzip payload: {compressedBytes:N0} of {originalBytes:N0} bytes.");

        using var updated = new HttpRequestMessage(HttpMethod.Get, "/app.js");
        updated.Headers.IfNoneMatch.ParseAdd("W/\"old-version\"");
        updated.Headers.AcceptEncoding.ParseAdd("gzip;q=0, br;q=0");
        using var fresh = await http.SendAsync(updated);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Equal(0, fresh.Content.Headers.ContentEncoding.Count);
        Assert.Equal(BundledWebClient.Assets["/app.js"].Content.Length, (await fresh.Content.ReadAsByteArrayAsync()).Length);
    }

    private static async Task CompressPlaylistsAsync()
    {
        var playlist = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n" +
            string.Concat(Enumerable.Range(0, 120).Select(i => $"#EXTINF:6,\nsegments/{i}.ts?secret=upstream\n")) +
            "#EXT-X-ENDLIST\n";
        using var provider = new HttpClient(new AsyncHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".m3u8", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(playlist)
                });
            Assert.Equal("bytes=1-3", request.Headers.Range?.ToString());
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                RequestMessage = request,
                Content = new StringContent("123")
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(1, 3, 5);
            return Task.FromResult(response);
        }));
        using var media = new LocalWebMedia("playback", new Uri("https://cdn.example/index.m3u8"), provider);
        using var certificate = Certificate();
        await using var server = await Server(certificate, () => new TestWorkspace(media));
        using var http = Client(certificate, server.Address);
        var (cookie, csrf) = await Login(http);
        http.DefaultRequestHeaders.Add("Cookie", cookie);
        var path = media.PathFor(media.Root);
        var expected = await http.GetStringAsync(path);

        http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip");
        using var encoded = await http.GetAsync(path);
        encoded.EnsureSuccessStatusCode();
        Assert.True(encoded.Headers.CacheControl?.NoStore == true);
        Assert.SequenceEqual(new[] { "gzip" }, encoded.Content.Headers.ContentEncoding);
        var bytes = await encoded.Content.ReadAsByteArrayAsync();
        Assert.Equal(expected, Encoding.UTF8.GetString(await GunzipAsync(bytes)));
        var originalLength = Encoding.UTF8.GetByteCount(expected);
        Assert.True(bytes.Length < originalLength * 3 / 4);
        Console.WriteLine($"LAN HLS gzip payload: {bytes.Length:N0} of {originalLength:N0} bytes.");

        var segment = expected.Split('\n').First(line => line.StartsWith("/media/", StringComparison.Ordinal));
        using var request = new HttpRequestMessage(HttpMethod.Get, segment);
        request.Headers.Range = new RangeHeaderValue(1, 3);
        using var ranged = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
        Assert.Equal("bytes 1-3/5", ranged.Content.Headers.ContentRange?.ToString());
        Assert.Equal(0, ranged.Content.Headers.ContentEncoding.Count);
        Assert.Equal("123", await ranged.Content.ReadAsStringAsync());

        using var api = await Post(http, "settings", new { }, csrf: csrf);
        Assert.Equal(HttpStatusCode.OK, api.StatusCode);
        Assert.Equal(0, api.Content.Headers.ContentEncoding.Count);
        Assert.True(api.Headers.CacheControl?.NoStore == true);
    }

    private static async Task ProviderTextCompressionAsync()
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore();
        builder.Logging.ClearProviders();
        builder.Services.AddResponseCompression();
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UseResponseCompression();
        var text = new string('x', 8192);
        app.Run(context =>
        {
            if (context.Request.Path == "/redirect")
            {
                context.Response.Redirect("/");
                return Task.CompletedTask;
            }
            context.Response.Headers["X-Fixture-Accept-Encoding"] = context.Request.Headers.AcceptEncoding.ToString();
            context.Response.ContentType = "text/plain";
            return context.Response.WriteAsync(text);
        });
        await app.StartAsync();
        var url = app.Urls.Single();

        using var json = HttpClientFactory.CreateDefault();
        using var metadata = HttpClientFactory.CreateForText(TimeSpan.FromSeconds(5), allowAutoRedirect: false);
        using var media = HttpClientFactory.Create(TimeSpan.FromSeconds(5));
        foreach (var client in new[] { json, metadata, media })
        {
            using var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var encodings = response.Headers.GetValues("X-Fixture-Accept-Encoding").Single();
            Assert.Equal(client != media, encodings.Contains("gzip", StringComparison.Ordinal));
            if (client != media) Assert.True(response.Headers.Vary.Contains("Accept-Encoding"));
            Assert.Equal(0, response.Content.Headers.ContentEncoding.Count);
            Assert.Equal(text, await response.Content.ReadAsStringAsync());
        }
        using var redirect = await metadata.GetAsync(url + "/redirect");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
    }

    private static async Task RevalidateArchivePlaylistAsync()
    {
        var entry = new Uri("https://cdn.example/archive");
        var signature = 1;
        var revision = 1;
        var noStore = false;
        var transferredBytes = 0;
        string Playlist() => "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:0\n" +
            string.Concat(Enumerable.Range(0, 120 + revision).Select(i => $"#EXTINF:6,\nsegments/{i}.ts?secret=provider\n")) +
            "#EXT-X-ENDLIST\n";
        using var provider = new HttpClient(new AsyncHttpMessageHandler((request, _) =>
        {
            var response = new HttpResponseMessage { RequestMessage = request };
            if (request.RequestUri!.AbsolutePath == "/archive")
            {
                Assert.Equal(0, request.Headers.IfNoneMatch.Count);
                response.StatusCode = HttpStatusCode.Redirect;
                response.Headers.Location = new Uri($"/asset.m3u8?signature={signature}", UriKind.Relative);
            }
            else
            {
                var validator = new EntityTagHeaderValue($"\"archive-{revision}\"");
                response.Headers.ETag = validator;
                if (noStore) response.Headers.CacheControl = new() { NoStore = true };
                var conditional = request.Headers.IfNoneMatch.SingleOrDefault();
                if (noStore)
                    Assert.True(conditional is null, "The previous signed URL's validator must not follow a redirect or be stored here.");
                if (validator.Equals(conditional))
                {
                    response.StatusCode = HttpStatusCode.NotModified;
                }
                else
                {
                    var content = Playlist();
                    transferredBytes += Encoding.UTF8.GetByteCount(content);
                    response.Content = new StringContent(content);
                }
            }
            return Task.FromResult(response);
        }));
        using var media = new LocalWebMedia("archive", entry, provider) { GrowingArchive = true };

        async Task<string> ServePlaylistAsync(string query = "")
        {
            var context = new DefaultHttpContext();
            context.Request.Method = "GET";
            context.Request.QueryString = new QueryString(query);
            using var body = new MemoryStream();
            context.Response.Body = body;
            await media.ServeAsync(context, media.Root);
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            return Encoding.UTF8.GetString(body.ToArray());
        }

        Assert.Equal(726d, await media.ReadArchiveDurationAsync("best", default));
        var initialBytes = Encoding.UTF8.GetByteCount(Playlist());
        Assert.Equal(initialBytes, transferredBytes);
        var first = await ServePlaylistAsync();
        Assert.Contains("#EXT-X-PLAYLIST-TYPE:EVENT", first);
        Assert.DoesNotContain("secret=provider", first);
        Assert.Equal(726d, await media.ReadArchiveDurationAsync("best", default));
        Assert.Equal(initialBytes, transferredBytes);
        Assert.Equal(first, await ServePlaylistAsync("?_HLS_msn=121"));
        Assert.Equal(2 * initialBytes, transferredBytes);

        revision++;
        Assert.Equal(732d, await media.ReadArchiveDurationAsync("best", default));
        var nextBytes = Encoding.UTF8.GetByteCount(Playlist());
        var updated = await ServePlaylistAsync();
        Assert.Equal(122, updated.Split('\n').Count(line => line.StartsWith("#EXTINF:", StringComparison.Ordinal)));
        Assert.Equal(2 * initialBytes + nextBytes, transferredBytes);

        signature++;
        noStore = true;
        Assert.Equal(732d, await media.ReadArchiveDurationAsync("best", default));
        Assert.Equal(updated, await ServePlaylistAsync());
        Assert.Equal(2 * initialBytes + 3 * nextBytes, transferredBytes);
        Console.WriteLine($"Archive fixture saved {2 * initialBytes:N0} provider body bytes across two unchanged requests.");
    }

    private static async Task<byte[]> GunzipAsync(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        await gzip.CopyToAsync(output);
        return output.ToArray();
    }
}
