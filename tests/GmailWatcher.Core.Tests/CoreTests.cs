using GmailWatcher.Core.Configuration;
using GmailWatcher.Core.Platform;
using GmailWatcher.Core.Settings;

namespace GmailWatcher.Core.Tests;

public sealed class OptionsValidatorTests
{
    [Fact]
    public void The_shipped_defaults_are_valid_and_leave_google_unconfigured()
    {
        var options = new GmailWatcherOptions();

        Assert.Empty(GmailWatcherOptionsValidator.Validate(options));
        Assert.False(options.Google.IsConfigured);
        Assert.False(options.Archive.IsChosen);
        Assert.True(options.Updates.IsConfigured);
        Assert.Equal(new Uri("https://github.com/PantaKoda/gmailWatcher/releases"), options.Updates.ReleasesPage);
    }

    [Theory]
    [InlineData("123456789-abcdef.apps.googleusercontent.com", true)]
    [InlineData("1234567890-abc123def456.apps.googleusercontent.com", true)]
    [InlineData("client-id", false)]
    [InlineData("123456789-abcdef.apps.googleusercontent.com.evil.example", false)]
    [InlineData("123.apps.googleusercontent.com", false)]
    public void Client_ids_must_look_like_googles(string id, bool valid)
    {
        var errors = GmailWatcherOptionsValidator.Validate(new GmailWatcherOptions { Google = { ClientId = id } });

        Assert.Equal(valid, errors.All(e => e.Key != "Google:ClientId"));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(16, true)]
    [InlineData(17, false)]
    public void The_number_of_workers_is_bounded(int workers, bool valid) =>
        Assert.Equal(valid, GmailWatcherOptionsValidator.Validate(new GmailWatcherOptions { Sync = { Workers = workers } }).All(e => e.Key != "Sync:Workers"));

    [Theory]
    [InlineData(59, false)]
    [InlineData(5000, true)]
    [InlineData(60_000, true)]
    [InlineData(60_001, false)]
    public void The_quota_budget_is_bounded(int units, bool valid) =>
        Assert.Equal(valid, GmailWatcherOptionsValidator.Validate(new GmailWatcherOptions { Sync = { QuotaUnitsPerMinute = units } }).All(e => e.Key != "Sync:QuotaUnitsPerMinute"));

    [Theory]
    [InlineData("https://accounts.google.com/o/oauth2/v2/auth", true)]
    [InlineData("http://127.0.0.1:5000/auth", true)]
    [InlineData("http://accounts.google.com/o/oauth2/v2/auth", false)]
    [InlineData("https://user:pass@accounts.google.com/auth", false)]
    [InlineData("https://accounts.google.com/auth?x=1", false)]
    [InlineData("not a url", false)]
    public void Endpoints_must_be_https_without_credentials_or_queries_except_loopback_for_tests(string url, bool valid) =>
        Assert.Equal(valid, GmailWatcherOptionsValidator.Validate(new GmailWatcherOptions { Google = { AuthorizationEndpoint = url } }).All(e => e.Key != "Google:AuthorizationEndpoint"));

    [Theory]
    [InlineData("PantaKoda/gmailWatcher", true)]
    [InlineData("a/b", true)]
    [InlineData("../../etc/passwd", false)]
    [InlineData("owner/name/extra", false)]
    [InlineData("-bad/name", false)]
    public void The_update_repository_must_be_owner_slash_name(string repository, bool valid) =>
        Assert.Equal(valid, GmailWatcherOptionsValidator.Validate(new GmailWatcherOptions { Updates = { Repository = repository } }).All(e => e.Key != "Updates:Repository"));

    [Fact]
    public void An_empty_update_repository_turns_updates_off_without_an_error()
    {
        var options = new GmailWatcherOptions { Updates = { Repository = "" } };

        Assert.Empty(GmailWatcherOptionsValidator.Validate(options));
        Assert.False(options.Updates.IsConfigured);
        Assert.Null(options.Updates.ReleasesPage);
    }

    [Fact]
    public void Every_error_tells_the_user_what_to_do()
    {
        var errors = GmailWatcherOptionsValidator.Validate(new GmailWatcherOptions
        {
            Sync = { Workers = 99, QuotaUnitsPerMinute = 1 },
            Google = { ClientId = "x" },
            Archive = { Directory = "relative" },
        });

        Assert.True(errors.Count >= 4);
        Assert.All(errors, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Problem));
            Assert.False(string.IsNullOrWhiteSpace(e.Fix));
        });
    }
}

public sealed class SettingsCodecTests
{
    [Fact]
    public void Settings_round_trip()
    {
        var settings = SettingsCodecs.App.CreateDefault() with
        {
            Appearance = new AppearanceSettings { Theme = ThemePreference.Dark, Density = Density.Compact, AccentColor = "#9D7CFF", Motion = MotionPreference.Off },
            Sync = new SyncSettings { AutoSync = AutoSyncInterval.EverySixHours, SyncOnStartup = true },
            Window = new WindowSettings { X = 100, Y = 50, Width = 900, Height = 700, Maximized = true },
        };

        var loaded = SettingsCodecs.App.Deserialize(SettingsCodecs.App.Serialize(settings));

        Assert.Equal(SettingsLoadStatus.Loaded, loaded.Status);
        Assert.Equal(settings.Appearance, loaded.Value.Appearance with { ExtensionData = null });
        Assert.Equal(AutoSyncInterval.EverySixHours, loaded.Value.Sync.AutoSync);
        Assert.Equal(900, loaded.Value.Window.Width);
        Assert.True(loaded.Value.Window.Maximized);
    }

    [Fact]
    public void By_default_the_app_keeps_running_in_the_tray_but_never_registers_itself_with_windows()
    {
        var background = SettingsCodecs.App.CreateDefault().Background;

        Assert.True(background.KeepRunningInTray);
        Assert.False(background.StartWithWindows);
        Assert.False(background.TrayHintShown);
    }

    [Fact]
    public void A_settings_file_from_before_the_tray_existed_loads_with_the_tray_defaults()
    {
        var loaded = SettingsCodecs.App.Deserialize("""{"schemaVersion":1,"appearance":{"theme":"Dark"},"sync":{"autoSync":"EveryHour"}}""");

        Assert.Equal(SettingsLoadStatus.Loaded, loaded.Status);
        Assert.Equal(ThemePreference.Dark, loaded.Value.Appearance.Theme);
        Assert.True(loaded.Value.Background.KeepRunningInTray);
        Assert.False(loaded.Value.Background.StartWithWindows);
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"background":{"trayHintShown":true}}""")]
    [InlineData("""{"schemaVersion":1,"background":{"startWithWindows":true}}""")]
    [InlineData("""{"schemaVersion":1,"background":{}}""")]
    public void A_background_section_that_names_only_some_choices_keeps_the_defaults_for_the_rest(string json)
    {
        var loaded = SettingsCodecs.App.Deserialize(json);

        Assert.Equal(SettingsLoadStatus.Loaded, loaded.Status);
        Assert.True(loaded.Value.Background.KeepRunningInTray);
    }

    [Fact]
    public void Background_choices_round_trip_and_a_missing_section_is_repaired()
    {
        var chosen = SettingsCodecs.App.CreateDefault() with { Background = new BackgroundSettings { KeepRunningInTray = false, StartWithWindows = true, TrayHintShown = true } };

        var loaded = SettingsCodecs.App.Deserialize(SettingsCodecs.App.Serialize(chosen));
        var nulled = SettingsCodecs.App.Deserialize("""{"schemaVersion":1,"background":null}""");

        Assert.False(loaded.Value.Background.KeepRunningInTray);
        Assert.True(loaded.Value.Background.StartWithWindows);
        Assert.True(loaded.Value.Background.TrayHintShown);
        Assert.True(nulled.Value.Background.KeepRunningInTray);
    }

    [Fact]
    public void Fields_this_version_does_not_know_survive_a_save()
    {
        const string Json = """{"schemaVersion":1,"appearance":{"theme":"Dark","futureOption":42},"somethingNew":{"a":[1,2]}}""";

        var loaded = SettingsCodecs.App.Deserialize(Json);
        var saved = SettingsCodecs.App.Serialize(loaded.Value with { Appearance = loaded.Value.Appearance with { Theme = ThemePreference.Light } });

        Assert.Contains("futureOption", saved, StringComparison.Ordinal);
        Assert.Contains("somethingNew", saved, StringComparison.Ordinal);
        Assert.Contains("\"Light\"", saved, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, SettingsLoadStatus.Missing)]
    [InlineData("", SettingsLoadStatus.Missing)]
    [InlineData("{ nope", SettingsLoadStatus.Corrupt)]
    [InlineData("[1,2]", SettingsLoadStatus.Corrupt)]
    [InlineData("{\"appearance\":{}}", SettingsLoadStatus.Corrupt)]
    [InlineData("{\"schemaVersion\":0}", SettingsLoadStatus.Corrupt)]
    [InlineData("{\"schemaVersion\":2}", SettingsLoadStatus.NewerVersion)]
    public void Unusable_documents_give_defaults_and_a_reason(string? json, SettingsLoadStatus expected)
    {
        var loaded = SettingsCodecs.App.Deserialize(json);

        Assert.Equal(expected, loaded.Status);
        Assert.Equal(ThemePreference.System, loaded.Value.Appearance.Theme);
    }

    [Fact]
    public void Hand_edited_nonsense_is_repaired_value_by_value()
    {
        var loaded = SettingsCodecs.App.Deserialize(
            """{"schemaVersion":1,"appearance":{"theme":"Neon","accentColor":"red","density":"Compact","motion":"Sometimes"},"window":{"x":"left","width":-1,"height":600},"sync":{"autoSync":"Hourly"}}""");

        Assert.Equal(SettingsLoadStatus.Repaired, loaded.Status);
        Assert.Equal(ThemePreference.System, loaded.Value.Appearance.Theme);
        Assert.Null(loaded.Value.Appearance.AccentColor);
        Assert.Equal(Density.Compact, loaded.Value.Appearance.Density);
        Assert.Equal(MotionPreference.System, loaded.Value.Appearance.Motion);
        Assert.Null(loaded.Value.Window.X);
        Assert.Null(loaded.Value.Window.Width);
        Assert.Equal(600, loaded.Value.Window.Height);
        Assert.Equal(AutoSyncInterval.Off, loaded.Value.Sync.AutoSync);
    }

    [Theory]
    [InlineData(AutoSyncInterval.Off, null)]
    [InlineData(AutoSyncInterval.EveryHour, 1)]
    [InlineData(AutoSyncInterval.EverySixHours, 6)]
    [InlineData(AutoSyncInterval.Daily, 24)]
    public void Automatic_sync_intervals(AutoSyncInterval interval, int? hours) =>
        Assert.Equal(hours is null ? null : TimeSpan.FromHours(hours.Value), SyncSettings.IntervalOf(interval));
}

public sealed class ExternalLinkPolicyTests
{
    [Theory]
    [InlineData("https://github.com/PantaKoda/gmailWatcher/releases/tag/v0.1.0")]
    [InlineData("https://accounts.google.com/o/oauth2/v2/auth?client_id=x")]
    [InlineData("https://console.cloud.google.com/apis/credentials")]
    [InlineData("https://myaccount.google.com/permissions")]
    [InlineData("https://docs.github.com/en")]
    public void Links_to_github_and_google_are_allowed(string url) => Assert.True(ExternalLinkPolicy.IsAllowed(new Uri(url)));

    [Theory]
    [InlineData("http://github.com/x")]
    [InlineData("https://evilgithub.com/x")]
    [InlineData("https://github.com.evil.example/x")]
    [InlineData("https://accounts.google.com.evil.example/")]
    [InlineData("https://user:pass@github.com/x")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:privacy")]
    [InlineData("https://example.com/")]
    public void Anything_else_is_refused(string url) => Assert.False(ExternalLinkPolicy.IsAllowed(new Uri(url, UriKind.RelativeOrAbsolute)));

    [Fact]
    public void Null_and_relative_links_are_refused()
    {
        Assert.False(ExternalLinkPolicy.IsAllowed(null));
        Assert.False(ExternalLinkPolicy.IsAllowed(new Uri("/relative", UriKind.Relative)));
    }

    [Fact]
    public void Extra_hosts_can_be_allowed_explicitly()
    {
        Assert.False(ExternalLinkPolicy.IsAllowed(new Uri("https://downloads.example.org/x")));
        Assert.True(ExternalLinkPolicy.IsAllowed(new Uri("https://downloads.example.org/x"), ["downloads.example.org"]));
    }
}
