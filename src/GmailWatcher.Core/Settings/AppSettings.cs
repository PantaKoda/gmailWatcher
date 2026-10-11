using System.Text.Json;
using System.Text.Json.Serialization;

namespace GmailWatcher.Core.Settings;

/// <summary>
/// Base for persisted settings records. Properties this version does not know are kept in
/// <see cref="ExtensionData"/> and written back, so saving never erases a newer version's fields.
/// </summary>
public abstract record SettingsRecord
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Machine-wide user preferences. Contains no secrets and no mail.</summary>
public sealed record AppSettings : SettingsRecord
{
    public AppearanceSettings Appearance { get; init; } = new();

    public WindowSettings Window { get; init; } = new();

    public SyncSettings Sync { get; init; } = new();

    public BackgroundSettings Background { get; init; } = new();
}

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public enum Density
{
    Comfortable,
    Compact,
}

/// <summary>Whether activity animations run. System follows the Windows "Show animations" setting.</summary>
public enum MotionPreference
{
    System,
    On,
    Off,
}

public sealed record AppearanceSettings : SettingsRecord
{
    public ThemePreference Theme { get; init; } = ThemePreference.System;

    public Density Density { get; init; } = Density.Comfortable;

    /// <summary>Optional accent as #RRGGBB, one of the presets; null uses the default cyan. Status colors never follow it.</summary>
    public string? AccentColor { get; init; }

    /// <summary>Activity animations (pulses, scan lines). Never run while nothing is active.</summary>
    public MotionPreference Motion { get; init; } = MotionPreference.System;
}

/// <summary>Where the main window was, so it reopens there. Size is in device-independent pixels.</summary>
public sealed record WindowSettings : SettingsRecord
{
    public double? X { get; init; }

    public double? Y { get; init; }

    public double? Width { get; init; }

    public double? Height { get; init; }

    public bool Maximized { get; init; }
}

public enum AutoSyncInterval
{
    Off,
    EveryHour,
    EverySixHours,
    Daily,
}

public sealed record SyncSettings : SettingsRecord
{
    /// <summary>
    /// Sync every connected account while the app is open. Off by default: Gmail Watcher does nothing in the
    /// background unless asked. Accounts that need to reconnect are skipped.
    /// </summary>
    public AutoSyncInterval AutoSync { get; init; } = AutoSyncInterval.Off;

    /// <summary>Also sync when the app starts (only meaningful with a connected account).</summary>
    public bool SyncOnStartup { get; init; }

    public static TimeSpan? IntervalOf(AutoSyncInterval interval) => interval switch
    {
        AutoSyncInterval.EveryHour => TimeSpan.FromHours(1),
        AutoSyncInterval.EverySixHours => TimeSpan.FromHours(6),
        AutoSyncInterval.Daily => TimeSpan.FromDays(1),
        _ => null,
    };
}

/// <summary>
/// How Gmail Watcher behaves when its window is not in use. The tray icon is always there while the app runs; these decide
/// what closing or minimising the window does, and whether the app starts with Windows.
/// </summary>
public sealed record BackgroundSettings : SettingsRecord
{
    /// <summary>
    /// Closing or minimising the window hides it to the tray (notification area) and Gmail Watcher keeps running, so scheduled
    /// syncs carry on, like Docker Desktop or Tailscale. Off: closing the window quits. Quit from the tray menu either way.
    /// </summary>
    // "set", not "init", on purpose: the source-generated JSON reader fills a missing init-only property with the type's zero value
    // (false), which would silently turn this default off for every settings file that does not name it. Test: Core SettingsCodecTests.
    public bool KeepRunningInTray { get; set; } = true;

    /// <summary>Start Gmail Watcher when you sign in to Windows, straight into the tray. Off by default: nothing is registered unless asked.</summary>
    public bool StartWithWindows { get; init; }

    /// <summary>The one-time explanation of "still running in the tray" has been shown.</summary>
    public bool TrayHintShown { get; init; }
}
