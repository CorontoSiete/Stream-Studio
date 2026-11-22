using System.Net;

namespace StreamlinkVlcStudio.Infrastructure.Http;

/// <summary>
/// Creates the shared infrastructure HTTP clients with consistent timeout and optional headers.
/// </summary>
public static class HttpClientFactory
{
    public const string ApplicationUserAgent = "StreamStudio/0.1";

    public static HttpClient CreateDefault() => Create(
        TimeSpan.FromSeconds(20),
        includeUserAgent: true,
        acceptJson: true);

    public static HttpClient Create(
        TimeSpan timeout,
        bool includeUserAgent = false,
        bool acceptJson = false,
        bool allowAutoRedirect = true) =>
        Create(timeout, includeUserAgent, acceptJson, allowAutoRedirect, decompress: acceptJson);

    internal static HttpClient CreateForText(TimeSpan timeout, bool includeUserAgent = false, bool allowAutoRedirect = true) =>
        Create(timeout, includeUserAgent, acceptJson: false, allowAutoRedirect, decompress: true);

    private static HttpClient Create(
        TimeSpan timeout, bool includeUserAgent, bool acceptJson, bool allowAutoRedirect, bool decompress)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = allowAutoRedirect,
            CheckCertificateRevocationList = true,
            AutomaticDecompression = decompress
                ? DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
                : DecompressionMethods.None
        };
        HttpClient client;
        try
        {
            client = new HttpClient(handler, disposeHandler: true);
        }
        catch
        {
            handler.Dispose();
            throw;
        }

        try
        {
            client.Timeout = timeout;

            if (includeUserAgent)
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(ApplicationUserAgent);
            }

            if (acceptJson)
            {
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            }

            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
