using System.Net;
using System.Net.Http.Headers;
using System.Reflection;

namespace GmailWatcher.Google;

/// <summary>The one place HttpClients for Google are configured.</summary>
public static class GoogleHttp
{
    /// <summary>
    /// A client to share for the lifetime of the app. The timeout covers connecting and waiting for response headers;
    /// it is generous because a message with attachments can be tens of megabytes. Pass <paramref name="handler"/> to test without a network.
    /// </summary>
    public static HttpClient CreateClient(HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler
        {
            // Re-resolve DNS now and then instead of keeping connections to one address forever.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),

            // Gmail compresses responses when asked; message bodies are base64 text, which compresses by about a quarter.
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };

        // Google only compresses responses for clients that say "gzip" in the user agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"GmailWatcher/{AppVersion} (gzip)");
        return client;
    }

    private static string AppVersion { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var informational = typeof(GoogleHttp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = informational?.Split('+')[0];
        return string.IsNullOrWhiteSpace(version) || !ProductHeaderValue.TryParse("x/" + version, out _) ? "0.0.0" : version;
    }
}
