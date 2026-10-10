using System.Text.RegularExpressions;

namespace GmailWatcher.Core.Configuration;

public static partial class GmailWatcherOptionsValidator
{
    public static IReadOnlyList<ConfigurationError> Validate(GmailWatcherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<ConfigurationError>();

        if (options.Google.ClientId is { } clientId && !string.IsNullOrWhiteSpace(clientId) && !ClientIdPattern().IsMatch(clientId.Trim()))
        {
            errors.Add(new("Google:ClientId", "is not a Google OAuth client ID.",
                "Copy the Client ID of a \"Desktop app\" OAuth client from the Google Cloud console (it ends in .apps.googleusercontent.com). See docs/google-cloud-setup.md."));
        }

        if (options.Google.ClientSecret is { } secret && !string.IsNullOrWhiteSpace(secret) && !ClientSecretPattern().IsMatch(secret.Trim()))
        {
            errors.Add(new("Google:ClientSecret", "does not look like a Google OAuth client secret.",
                "Copy the Client secret next to the Client ID in the Google Cloud console, or leave it out."));
        }

        ValidateHttpsUrl(errors, "Google:AuthorizationEndpoint", options.Google.AuthorizationEndpoint, required: true, allowLoopbackHttp: true);
        ValidateHttpsUrl(errors, "Google:TokenEndpoint", options.Google.TokenEndpoint, required: true, allowLoopbackHttp: true);
        ValidateHttpsUrl(errors, "Google:RevocationEndpoint", options.Google.RevocationEndpoint, required: true, allowLoopbackHttp: true);
        ValidateHttpsUrl(errors, "Google:ApiBaseUrl", options.Google.ApiBaseUrl, required: true, allowLoopbackHttp: true);

        if (options.Sync.Workers is < 1 or > 16)
        {
            errors.Add(new("Sync:Workers", $"{options.Sync.Workers} is outside the allowed range.", "Use a value between 1 and 16."));
        }

        if (options.Sync.QuotaUnitsPerMinute is < 60 or > 60_000)
        {
            errors.Add(new("Sync:QuotaUnitsPerMinute", $"{options.Sync.QuotaUnitsPerMinute} is outside the allowed range.",
                "Use a value between 60 and 60000. Google's documented limit is 6000 per account and minute; the default is 5000."));
        }

        if (options.Sync.MinFreeDiskMegabytes is < 64 or > 1_000_000)
        {
            errors.Add(new("Sync:MinFreeDiskMegabytes", $"{options.Sync.MinFreeDiskMegabytes} is outside the allowed range.", "Use a value between 64 and 1000000 (megabytes)."));
        }

        if (options.Archive.Directory is { } directory && !string.IsNullOrWhiteSpace(directory))
        {
            if (!Path.IsPathFullyQualified(directory.Trim()))
            {
                errors.Add(new("Archive:Directory", $"'{directory}' is not a full path.", "Use a complete path such as D:\\Mail\\GmailWatcher."));
            }
            else if (directory.Trim().IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                errors.Add(new("Archive:Directory", "contains characters that are not allowed in a path.", "Choose a different folder."));
            }
        }

        ValidateHttpsUrl(errors, "Updates:WebBaseUrl", options.Updates.WebBaseUrl, required: true, allowLoopbackHttp: true);
        ValidateHttpsUrl(errors, "Updates:ApiBaseUrl", options.Updates.ApiBaseUrl, required: true, allowLoopbackHttp: true);

        if (options.Updates.IsConfigured && !RepositoryPattern().IsMatch(options.Updates.Repository!.Trim()))
        {
            errors.Add(new("Updates:Repository", $"'{options.Updates.Repository}' is not an owner/name repository.",
                "Use the repository whose GitHub releases are Gmail Watcher's updates, e.g. PantaKoda/gmailWatcher, or leave it empty to turn updates off."));
        }

        if (options.Updates.CheckIntervalHours is < 0 or > 168)
        {
            errors.Add(new("Updates:CheckIntervalHours", $"{options.Updates.CheckIntervalHours} is outside the allowed range.",
                "Use 0 (check only when asked) up to 168 hours."));
        }

        return errors;
    }

    private static void ValidateHttpsUrl(List<ConfigurationError> errors, string key, string? value, bool required, bool allowLoopbackHttp)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
            {
                errors.Add(new(key, "is empty.", "Remove the setting to use the default, or provide an absolute https:// URL."));
            }

            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            errors.Add(new(key, $"'{value}' is not an absolute URL.", "Provide an absolute https:// URL."));
            return;
        }

        var isHttps = uri.Scheme == Uri.UriSchemeHttps;
        var isLoopbackHttp = uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
        if (!isHttps && !(allowLoopbackHttp && isLoopbackHttp))
        {
            errors.Add(new(key, $"'{value}' must use https.", "Use an https:// URL (http://localhost is accepted only for local testing)."));
            return;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            errors.Add(new(key, "must not contain credentials, a query string or a fragment.", "Provide only the scheme, host and path."));
        }
    }

    // 1234567890-abcdef.apps.googleusercontent.com
    [GeneratedRegex(@"^\d{6,}-[a-z0-9]{6,}\.apps\.googleusercontent\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex ClientIdPattern();

    // GOCSPX-... for current clients; older secrets are 24 characters of letters, digits, '-' and '_'.
    [GeneratedRegex(@"^[A-Za-z0-9_\-]{16,128}$")]
    private static partial Regex ClientSecretPattern();

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepositoryPattern();
}
