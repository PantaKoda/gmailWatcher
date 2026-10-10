using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using GmailWatcher.Core.Configuration;
using GmailWatcher.Core.Updates;

namespace GmailWatcher.Desktop.Updates;

/// <summary>Releases, or why they couldn't be read (shown to the user as-is).</summary>
public sealed record ReleaseListResult(IReadOnlyList<ReleaseInfo>? Releases, string? Error)
{
    public bool IsSuccess => Releases is not null;
}

/// <summary>Where updates come from. Implemented by <see cref="ReleaseClient"/>; tests substitute their own.</summary>
public interface IReleaseSource
{
    Task<ReleaseListResult> GetReleasesAsync(CancellationToken cancellationToken);

    Task DownloadAsync(Uri url, Stream destination, long maxBytes, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// Reads a public repository's GitHub releases without signing in (no credentials are ever sent), and downloads release
/// files. Downloads are accepted only from that repository's release download URLs on the GitHub web host; GitHub then
/// redirects to its file storage over HTTPS.
/// </summary>
public sealed class ReleaseClient(HttpClient http, UpdatesOptions options) : IReleaseSource
{
    /// <summary>GitHub REST API version sent with release requests.</summary>
    public const string ApiVersion = "2026-03-10";

    private const int MaxNotesLength = 20_000;
    private readonly string _repository = (options.Repository ?? "").Trim();
    private readonly Uri _apiBase = new(options.ApiBaseUrl.TrimEnd('/') + "/");
    private readonly Uri _webBase = new(options.WebBaseUrl.TrimEnd('/') + "/");

    /// <summary>
    /// A download that receives nothing for this long is abandoned. HttpClient's timeout ends once the headers arrive, and
    /// a socket can stall silently (Wi-Fi switch, sleep, a proxy dropping it).
    /// </summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public Uri ReleasesApi => new(_apiBase, $"repos/{_repository}/releases?per_page=30");

    public static string UserAgent { get; } =
        "GmailWatcher/" + (typeof(ReleaseClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0");

    /// <summary>One long-lived client for the process; connections are recycled for DNS changes.</summary>
    public static HttpClient CreateClient(HttpMessageHandler? handler = null)
    {
        var client = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    public async Task<ReleaseListResult> GetReleasesAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new(null, $"GitHub has no public releases for {_repository} (the repository is private, renamed or does not exist yet).");
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                return new(null, "GitHub's limit for checks without signing in was reached. Try again in an hour.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new(null, $"GitHub answered {(int)response.StatusCode} when checking for updates.");
            }

            var dtos = await response.Content.ReadFromJsonAsync(ReleaseJsonContext.Default.ListReleaseDto, cancellationToken).ConfigureAwait(false) ?? [];
            return new(dtos.Select(Map).OfType<ReleaseInfo>().ToList(), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or (TaskCanceledException and not OperationCanceledException { CancellationToken.IsCancellationRequested: true }))
        {
            return new(null, "Couldn't reach GitHub to check for updates.");
        }
    }

    /// <summary>True when <paramref name="url"/> is a release download of the configured repository.</summary>
    public bool IsReleaseDownload(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return url.Scheme == _webBase.Scheme && string.IsNullOrEmpty(url.UserInfo)
            && string.Equals(url.IdnHost, _webBase.IdnHost, StringComparison.OrdinalIgnoreCase)
            && url.Port == _webBase.Port
            && url.AbsolutePath.StartsWith($"/{_repository}/releases/download/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Downloads a release file into <paramref name="destination"/>, refusing anything larger than <paramref name="maxBytes"/>.</summary>
    public async Task DownloadAsync(Uri url, Stream destination, long maxBytes, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!IsReleaseDownload(url))
        {
            throw new InvalidOperationException($"Refusing to download '{url}': it isn't a release file of {_repository}.");
        }

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        if (total > maxBytes)
        {
            throw new InvalidOperationException($"The download is larger ({total} bytes) than expected.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[81920];
        long received = 0;
        while (true)
        {
            int read;
            using (var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                stall.CancelAfter(StallTimeout);
                try
                {
                    read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"the download stalled (nothing received for {StallTimeout.TotalSeconds:0} seconds)");
                }
            }

            if (read == 0)
            {
                break;
            }

            received += read;
            if (received > maxBytes)
            {
                throw new InvalidOperationException("The download is larger than expected.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            if (total is > 0)
            {
                progress?.Report((double)received / total.Value);
            }
        }
    }

    private static ReleaseInfo? Map(ReleaseDto dto)
    {
        if (!AppVersion.TryParse(dto.TagName, out var version) || !Uri.TryCreate(dto.HtmlUrl, UriKind.Absolute, out var html) || html.Scheme != Uri.UriSchemeHttps)
        {
            return null; // not a version tag (e.g. "nightly"): not an update
        }

        var notes = dto.Body ?? "";
        return new ReleaseInfo
        {
            Version = version,
            Tag = dto.TagName!,
            Title = string.IsNullOrWhiteSpace(dto.Name) ? $"Gmail Watcher {version}" : dto.Name.Trim(),
            Notes = notes.Length > MaxNotesLength ? notes[..MaxNotesLength] + "\n…" : notes,
            HtmlUrl = html,
            PublishedAt = dto.PublishedAt,
            IsDraft = dto.Draft,
            IsPreRelease = dto.Prerelease,
            Assets = (dto.Assets ?? [])
                .Where(a => !string.IsNullOrEmpty(a.Name) && Uri.TryCreate(a.BrowserDownloadUrl, UriKind.Absolute, out _))
                .Select(a => new ReleaseAsset(a.Name!, new Uri(a.BrowserDownloadUrl!), a.Size))
                .ToList(),
        };
    }
}

internal sealed record ReleaseDto
{
    [JsonPropertyName("tag_name")] public string? TagName { get; init; }

    [JsonPropertyName("name")] public string? Name { get; init; }

    [JsonPropertyName("body")] public string? Body { get; init; }

    [JsonPropertyName("html_url")] public string? HtmlUrl { get; init; }

    [JsonPropertyName("draft")] public bool Draft { get; init; }

    [JsonPropertyName("prerelease")] public bool Prerelease { get; init; }

    [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; init; }

    [JsonPropertyName("assets")] public List<ReleaseAssetDto>? Assets { get; init; }
}

internal sealed record ReleaseAssetDto
{
    [JsonPropertyName("name")] public string? Name { get; init; }

    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; init; }

    [JsonPropertyName("size")] public long Size { get; init; }
}

[JsonSerializable(typeof(List<ReleaseDto>))]
internal sealed partial class ReleaseJsonContext : JsonSerializerContext;
