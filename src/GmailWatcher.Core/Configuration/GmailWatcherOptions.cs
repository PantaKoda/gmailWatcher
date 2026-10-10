namespace GmailWatcher.Core.Configuration;

/// <summary>A configuration problem with the setting key that caused it and how to fix it.</summary>
public sealed record ConfigurationError(string Key, string Problem, string Fix)
{
    public override string ToString() => $"{Key}: {Problem} {Fix}";
}

/// <summary>
/// Deployment and per-user configuration. Bound from appsettings.json, the per-user override file
/// (gmailwatcher.config.json in the data folder) and GMAILWATCHER__-prefixed environment variables.
/// Holds no mail and no tokens. The Google client secret of a "Desktop app" OAuth client is, by Google's
/// own documentation, not confidential, but it is still kept out of the shipped defaults and out of git.
/// </summary>
public sealed class GmailWatcherOptions
{
    public GoogleOptions Google { get; set; } = new();

    public SyncOptions Sync { get; set; } = new();

    public ArchiveOptions Archive { get; set; } = new();

    public UpdatesOptions Updates { get; set; } = new();
}

/// <summary>
/// The OAuth client Gmail Watcher signs in with. Google requires every app to have one; it is created once in
/// the Google Cloud console (docs/google-cloud-setup.md) and is not part of the download.
/// </summary>
public sealed class GoogleOptions
{
    public const string DefaultAuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string DefaultTokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string DefaultRevocationEndpoint = "https://oauth2.googleapis.com/revoke";
    public const string DefaultApiBaseUrl = "https://gmail.googleapis.com/gmail/v1/";

    /// <summary>The "Desktop app" OAuth client ID, e.g. 1234567890-abc.apps.googleusercontent.com. Not secret.</summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// The client secret that goes with the ID. Google's token endpoint asks for it even for desktop apps that
    /// use PKCE; Google documents it as not confidential for the "Desktop app" type.
    /// </summary>
    public string? ClientSecret { get; set; }

    public string AuthorizationEndpoint { get; set; } = DefaultAuthorizationEndpoint;

    public string TokenEndpoint { get; set; } = DefaultTokenEndpoint;

    public string RevocationEndpoint { get; set; } = DefaultRevocationEndpoint;

    /// <summary>Base of the Gmail REST API (users/me/... is appended).</summary>
    public string ApiBaseUrl { get; set; } = DefaultApiBaseUrl;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);
}

/// <summary>How hard a sync works. Gmail's quota, not bandwidth, is the limit (see docs/architecture.md).</summary>
public sealed class SyncOptions
{
    public const int DefaultWorkers = 6;
    public const int DefaultQuotaUnitsPerMinute = 5000;
    public const int DefaultMinFreeDiskMegabytes = 1024;

    /// <summary>Messages downloaded at the same time for one account.</summary>
    public int Workers { get; set; } = DefaultWorkers;

    /// <summary>
    /// Gmail quota units one account may use per minute. Google's documented per-user limit is 6,000 and a
    /// message download costs 20, so the default leaves headroom for bursts and for other apps using the same account.
    /// </summary>
    public int QuotaUnitsPerMinute { get; set; } = DefaultQuotaUnitsPerMinute;

    /// <summary>A sync stops cleanly when the archive drive has less free space than this.</summary>
    public int MinFreeDiskMegabytes { get; set; } = DefaultMinFreeDiskMegabytes;

    /// <summary>Save attachments as separate files (in addition to the complete .eml, which always contains them).</summary>
    public bool ExtractAttachments { get; set; } = true;
}

public sealed class ArchiveOptions
{
    /// <summary>The folder that holds archive.db, analytics.duckdb and every account's mail. Null until chosen on first run.</summary>
    public string? Directory { get; set; }

    public bool IsChosen => !string.IsNullOrWhiteSpace(Directory);
}

/// <summary>
/// Where updates come from: the public GitHub releases of one repository. Gmail Watcher reads them without
/// signing in, shows what changed, and installs a release only when the user asks (docs/updates.md).
/// </summary>
public sealed class UpdatesOptions
{
    public const string DefaultRepository = "PantaKoda/gmailWatcher";
    public const string DefaultWebBaseUrl = "https://github.com";
    public const string DefaultApiBaseUrl = "https://api.github.com";

    /// <summary>"owner/name" of the public repository whose GitHub releases are Gmail Watcher's updates. Empty turns updates off.</summary>
    public string? Repository { get; set; } = DefaultRepository;

    /// <summary>Hours between automatic checks. 0 checks only when asked (Settings › Check for updates).</summary>
    public int CheckIntervalHours { get; set; } = 24;

    public string WebBaseUrl { get; set; } = DefaultWebBaseUrl;

    public string ApiBaseUrl { get; set; } = DefaultApiBaseUrl;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Repository);

    /// <summary>The repository's releases page on the GitHub web host.</summary>
    public Uri? ReleasesPage => IsConfigured ? new Uri($"{WebBaseUrl.TrimEnd('/')}/{Repository!.Trim()}/releases") : null;
}
