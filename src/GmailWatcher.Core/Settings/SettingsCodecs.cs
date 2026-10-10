using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GmailWatcher.Core.Settings;

/// <summary>Codecs for the persisted settings document. Bump a version only together with a migration.</summary>
public static partial class SettingsCodecs
{
    public static SettingsCodec<AppSettings> App { get; } = new(
        currentVersion: 1,
        SettingsJsonContext.Default.AppSettings,
        () => new AppSettings(),
        Normalize,
        migrations: []);

    /// <summary>Repairs values a hand-edited or older document could contain, so the UI never sees them.</summary>
    public static AppSettings Normalize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var appearance = settings.Appearance ?? new AppearanceSettings();
        var window = settings.Window ?? new WindowSettings();
        var sync = settings.Sync ?? new SyncSettings();

        return settings with
        {
            Appearance = appearance with
            {
                Theme = Defined(appearance.Theme),
                Density = Defined(appearance.Density),
                Motion = Defined(appearance.Motion),
                AccentColor = appearance.AccentColor is { } accent && AccentPattern().IsMatch(accent) ? accent : null,
            },
            Window = window with
            {
                X = Finite(window.X),
                Y = Finite(window.Y),
                Width = Finite(window.Width) is { } w and > 0 ? w : null,
                Height = Finite(window.Height) is { } h and > 0 ? h : null,
            },
            Sync = sync with { AutoSync = Defined(sync.AutoSync) },
        };
    }

    private static double? Finite(double? value) => value is { } v && double.IsFinite(v) ? v : null;

    private static TEnum Defined<TEnum>(TEnum value) where TEnum : struct, Enum =>
        Enum.IsDefined(value) ? value : default;

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex AccentPattern();
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
