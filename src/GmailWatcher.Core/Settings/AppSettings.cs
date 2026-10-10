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
