namespace GmailWatcher.Core.Platform;

public enum LinkOpenResult
{
    Opened,
    /// <summary>Rejected by <see cref="ExternalLinkPolicy"/>.</summary>
    Refused,
    /// <summary>Allowed, but the system browser could not be launched.</summary>
    Failed,
}

/// <summary>Opens a URL in the user's system browser. Never throws; failures are reported in the result.</summary>
public interface IExternalBrowser
{
    Task<LinkOpenResult> OpenAsync(Uri url);
}

/// <summary>Marshals work to the UI thread.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>
/// Links come from API responses and release notes, so they are untrusted. Only HTTPS URLs on a short list of hosts
/// (GitHub for releases, Google for sign-in and setup help) are opened; anything else is refused.
/// </summary>
public static class ExternalLinkPolicy
{
    private static readonly string[] AllowedHosts =
    [
        "github.com",
        "accounts.google.com",
        "myaccount.google.com",
        "console.cloud.google.com",
        "mail.google.com",
        "developers.google.com",
        "support.google.com",
    ];

    public static bool IsAllowed(Uri? url, IEnumerable<string>? extraHosts = null)
    {
        if (url is null || !url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.UserInfo))
        {
            return false;
        }

        var host = url.IdnHost;
        return AllowedHosts.Concat(extraHosts ?? []).Any(allowed =>
            string.Equals(host, allowed, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase));
    }
}
