using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using StreamlinkVlcStudio.Core;
using StreamlinkVlcStudio.Infrastructure.Io;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

internal sealed class HomeArtworkCache(string directory, TimeProvider timeProvider)
{
    private const int SlotCount = 512;
    private const int MaximumImageBytes = 256 * 1024;
    private const int KeyBytes = 32;
    private const int HeaderBytes = KeyBytes + sizeof(long);

    internal static HomeArtworkCache Shared { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppIdentity.ProductDirectoryName, "HomeArtwork"), TimeProvider.System);

    internal DateTimeOffset UtcNow => timeProvider.GetUtcNow();

    internal async Task<byte[]?> ReadAsync(Uri uri, int maxImageBytes, CancellationToken cancellationToken)
    {
        var key = GetKey(uri);
        var path = GetPath(key);
        if (!File.Exists(path)) return null;
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, bufferSize: 4096, FileOptions.Asynchronous);
            var length = file.Length - HeaderBytes;
            if (length <= 0 || length > Math.Min(maxImageBytes, MaximumImageBytes)) return null;

            var header = new byte[HeaderBytes];
            await file.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            if (!header.AsSpan(0, KeyBytes).SequenceEqual(key) ||
                BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(KeyBytes)) <= UtcNow.UtcTicks)
                return null;

            var bytes = new byte[(int)length];
            await file.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal async Task StoreAsync(Uri uri, HttpResponseMessage response, byte[] bytes, DateTimeOffset receivedAt)
    {
        if (bytes.Length is 0 or > MaximumImageBytes ||
            GetExpiration(uri, response, receivedAt) is not { } expiration || expiration <= UtcNow)
            return;

        var key = GetKey(uri);
        var header = new byte[HeaderBytes];
        key.CopyTo(header, 0);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(KeyBytes), expiration.UtcTicks);
        try
        {
            await AtomicFile.WriteAsync(GetPath(key), async (stream, token) =>
            {
                await stream.WriteAsync(header, token).ConfigureAwait(false);
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Artwork is already available. A failing cache must not affect its display.
        }
    }

    private static DateTimeOffset? GetExpiration(Uri uri, HttpResponseMessage response, DateTimeOffset receivedAt)
    {
        var cache = response.Headers.CacheControl;
        if (response.StatusCode != HttpStatusCode.OK || response.RequestMessage?.RequestUri != uri ||
            cache is { NoStore: true } or { NoCache: true } or { Private: true } ||
            response.Headers.Contains("Set-Cookie") || !response.Headers.Vary.All(IsStableRequestHeader))
            return null;

        var date = response.Headers.Date ?? receivedAt;
        var lifetime = cache?.MaxAge ?? response.Content.Headers.Expires - date;
        if (lifetime is not { } freshness || freshness <= TimeSpan.Zero) return null;

        var age = response.Headers.Age.GetValueOrDefault();
        if (receivedAt - date > age) age = receivedAt - date;
        var remaining = freshness - age;
        if (remaining <= TimeSpan.Zero) return null;
        return receivedAt + TimeSpan.FromTicks(Math.Min(remaining.Ticks, TimeSpan.FromDays(30).Ticks));
    }

    private static bool IsStableRequestHeader(string header) =>
        // These values are fixed by the shared artwork client and the requested URL.
        header.Equals("Accept", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("Accept-Language", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("Referer", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("User-Agent", StringComparison.OrdinalIgnoreCase);

    private static byte[] GetKey(Uri uri) => SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri));

    // A fixed number of slots bounds disk use without scanning the cache during startup.
    // The full URL hash in each header makes a collision a cache miss.
    private string GetPath(byte[] key) => Path.Combine(directory,
        (BinaryPrimitives.ReadUInt16LittleEndian(key) % SlotCount).ToString("D3", CultureInfo.InvariantCulture));
}
