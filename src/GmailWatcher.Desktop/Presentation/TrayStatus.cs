namespace GmailWatcher.Desktop.Presentation;

/// <summary>Which tray icon to show: the plain mark, or with a status dot.</summary>
public enum TrayIconKind
{
    Normal,
    /// <summary>Syncing or checking right now.</summary>
    Busy,
    /// <summary>Something needs the user: problems found, or an account must sign in again.</summary>
    Attention,
}

/// <summary>What the tray icon says: its tooltip, the status line at the top of its menu, and which icon.</summary>
public sealed record TrayStatus(string Tooltip, string StatusLine, TrayIconKind Icon)
{
    /// <summary>Windows cuts a notification-area tooltip at 127 characters.</summary>
    public const int MaxTooltipLength = 127;

    /// <summary>
    /// Built from the same state as the pill in the window header (<paramref name="statusText"/> and <paramref name="tone"/>), so the
    /// tray never disagrees with the window.
    /// </summary>
    public static TrayStatus From(string statusText, StatusTone tone, bool anyBusy, bool updateAvailable, bool demo)
    {
        var line = Humanize(statusText);
        var icon = anyBusy ? TrayIconKind.Busy
            : tone is StatusTone.Failure or StatusTone.Warning ? TrayIconKind.Attention
            : TrayIconKind.Normal;
        var tooltip = (demo ? "Gmail Watcher (demo): " : "Gmail Watcher: ") + line + (updateAvailable ? " · update available" : "");
        if (tooltip.Length > MaxTooltipLength)
        {
            tooltip = tooltip[..(MaxTooltipLength - 1)] + "…";
        }

        return new TrayStatus(tooltip, line, icon);
    }

    private static string Humanize(string statusText)
    {
        var text = statusText.Trim();
        return text switch
        {
            "" => "Starting…",
            "SYNCING" => "Syncing…",
            "CHECKING" => "Checking your archive…",
            "READY" => "Ready",
            "SET UP" => "Needs set-up",
            "RECONNECT" => "Sign-in needed",
            "ALL VERIFIED" => "All accounts verified",
            "PROBLEMS" => "Problems found",
            _ when text.EndsWith(" WORKING", StringComparison.Ordinal) => $"{text[..^" WORKING".Length]} accounts working…",
            _ when text.EndsWith(" ACCOUNTS", StringComparison.Ordinal) => $"{text[..^" ACCOUNTS".Length]} accounts",
            "1 ACCOUNT" => "1 account",
            _ => char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant(),
        };
    }
}
