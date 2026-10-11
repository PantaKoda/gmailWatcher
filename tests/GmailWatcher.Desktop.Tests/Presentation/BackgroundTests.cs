using Avalonia.Controls;
using GmailWatcher.Core.Settings;
using GmailWatcher.Desktop.Infrastructure;
using GmailWatcher.Desktop.Platform;
using GmailWatcher.Desktop.Platform.Windows;
using GmailWatcher.Desktop.Presentation;
using GmailWatcher.Desktop.Services;
using GmailWatcher.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace GmailWatcher.Desktop.Tests.Presentation;

public sealed class WindowLifetimeTests
{
    [Fact]
    public void Closing_the_window_hides_it_when_the_app_should_keep_running()
    {
        Assert.Equal(CloseAction.HideToTray, WindowLifetime.OnClosing(WindowCloseReason.WindowClosing, keepRunningInTray: true, quitting: false));
    }

    [Theory]
    [InlineData(WindowCloseReason.WindowClosing, false, false)] // the user turned it off: closing quits
    [InlineData(WindowCloseReason.WindowClosing, true, true)]   // Quit from the tray menu: it must really close
    [InlineData(WindowCloseReason.OSShutdown, true, false)]     // Windows is shutting down or signing out: never hold it back
    [InlineData(WindowCloseReason.ApplicationShutdown, true, false)]
    [InlineData(WindowCloseReason.OwnerWindowClosing, true, false)]
    [InlineData(WindowCloseReason.Undefined, true, false)]
    public void In_every_other_case_the_window_really_closes(WindowCloseReason reason, bool keepRunning, bool quitting)
    {
        Assert.Equal(CloseAction.Close, WindowLifetime.OnClosing(reason, keepRunning, quitting));
    }

    [Theory]
    [InlineData(WindowState.Minimized, true, false, true)]
    [InlineData(WindowState.Normal, true, false, false)]
    [InlineData(WindowState.Maximized, true, false, false)]
    [InlineData(WindowState.Minimized, false, false, false)] // setting off: minimising stays an ordinary minimise
    [InlineData(WindowState.Minimized, true, true, false)]
    public void Minimising_hides_the_window_only_when_the_app_keeps_running_in_the_tray(WindowState state, bool keepRunning, bool quitting, bool hides)
    {
        Assert.Equal(hides, WindowLifetime.HideOnMinimize(state, keepRunning, quitting));
    }

    [Theory]
    [InlineData(new[] { "--background" }, true, true)]
    [InlineData(new[] { "--BACKGROUND" }, true, true)]
    [InlineData(new[] { "--demo", "--background" }, true, true)]
    [InlineData(new string[0], true, false)]
    [InlineData(new[] { "--demo" }, true, false)]
    [InlineData(new[] { "--background" }, false, false)] // no tray behaviour wanted: a window must be shown
    public void Only_a_start_by_windows_with_the_tray_wanted_starts_without_a_window(string[] args, bool keepRunning, bool hidden)
    {
        Assert.Equal(hidden, WindowLifetime.StartHidden(args, keepRunning));
    }
}

public sealed class TrayStatusTests
{
    [Theory]
    [InlineData("SYNCING", StatusTone.Running, true, "Syncing…", TrayIconKind.Busy)]
    [InlineData("CHECKING", StatusTone.Running, true, "Checking your archive…", TrayIconKind.Busy)]
    [InlineData("3 WORKING", StatusTone.Running, true, "3 accounts working…", TrayIconKind.Busy)]
    [InlineData("ALL VERIFIED", StatusTone.Success, false, "All accounts verified", TrayIconKind.Normal)]
    [InlineData("PROBLEMS", StatusTone.Failure, false, "Problems found", TrayIconKind.Attention)]
    [InlineData("RECONNECT", StatusTone.Warning, false, "Sign-in needed", TrayIconKind.Attention)]
    [InlineData("READY", StatusTone.Unknown, false, "Ready", TrayIconKind.Normal)]
    [InlineData("SET UP", StatusTone.Unknown, false, "Needs set-up", TrayIconKind.Normal)]
    [InlineData("1 ACCOUNT", StatusTone.Neutral, false, "1 account", TrayIconKind.Normal)]
    [InlineData("2 ACCOUNTS", StatusTone.Neutral, false, "2 accounts", TrayIconKind.Normal)]
    [InlineData("", StatusTone.Unknown, false, "Starting…", TrayIconKind.Normal)]
    public void The_tray_says_what_the_window_header_says(string text, StatusTone tone, bool busy, string line, TrayIconKind icon)
    {
        var status = TrayStatus.From(text, tone, busy, updateAvailable: false, demo: false);

        Assert.Equal(line, status.StatusLine);
        Assert.Equal("Gmail Watcher: " + line, status.Tooltip);
        Assert.Equal(icon, status.Icon);
    }

    [Fact]
    public void Working_beats_problems_so_the_dot_shows_activity_while_a_fix_is_running()
    {
        Assert.Equal(TrayIconKind.Busy, TrayStatus.From("SYNCING", StatusTone.Failure, anyBusy: true, updateAvailable: false, demo: false).Icon);
    }

    [Fact]
    public void An_available_update_and_the_demo_are_mentioned_in_the_tooltip()
    {
        Assert.Equal("Gmail Watcher: Ready · update available", TrayStatus.From("READY", StatusTone.Unknown, false, updateAvailable: true, demo: false).Tooltip);
        Assert.StartsWith("Gmail Watcher (demo): ", TrayStatus.From("READY", StatusTone.Unknown, false, false, demo: true).Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tooltip_never_exceeds_what_windows_shows()
    {
        var status = TrayStatus.From(new string('x', 400), StatusTone.Neutral, false, updateAvailable: true, demo: true);

        Assert.True(status.Tooltip.Length <= TrayStatus.MaxTooltipLength);
        Assert.EndsWith("…", status.Tooltip, StringComparison.Ordinal);
    }
}

public sealed class StartupRegistrationTests
{
    [Fact]
    public void The_command_starts_the_program_hidden_in_the_tray_and_survives_spaces_in_the_path()
    {
        Assert.Equal("\"C:\\Program Files\\Gmail Watcher\\GmailWatcher.exe\" --background", StartupCommand.For(@"C:\Program Files\Gmail Watcher\GmailWatcher.exe"));
    }

    [Fact]
    public void The_demo_and_other_systems_use_a_registration_that_does_nothing()
    {
        var none = new NoStartupRegistration("the demo never registers");

        Assert.False(none.IsAvailable);
        Assert.False(none.Apply(true));
        Assert.False(none.IsEnabled);
        Assert.Equal("the demo never registers", none.WhyUnavailable);
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsStartupRegistrationTests : IDisposable
{
    private readonly string _subKey = @"Software\GmailWatcherTests\" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            Registry.CurrentUser.DeleteSubKeyTree(_subKey, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Registering_and_removing_the_entry_changes_exactly_one_value_under_the_users_own_key()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The start-up entry is a Windows registry value.");
        var registration = new WindowsStartupRegistration(_subKey, "GmailWatcher", () => @"C:\Apps\Gmail Watcher\GmailWatcher.exe");
        using (var other = Registry.CurrentUser.CreateSubKey(_subKey))
        {
            other.SetValue("SomethingElse", "keep me");
        }

        Assert.True(registration.IsAvailable);
        Assert.False(registration.IsEnabled);

        Assert.True(registration.Apply(true));
        Assert.True(registration.IsEnabled);
        using (var key = Registry.CurrentUser.OpenSubKey(_subKey))
        {
            Assert.Equal("\"C:\\Apps\\Gmail Watcher\\GmailWatcher.exe\" --background", key!.GetValue("GmailWatcher"));
        }

        Assert.True(registration.Apply(false));
        Assert.False(registration.IsEnabled);
        using var after = Registry.CurrentUser.OpenSubKey(_subKey);
        Assert.Null(after!.GetValue("GmailWatcher"));
        Assert.Equal("keep me", after.GetValue("SomethingElse"));
    }

    [Theory]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe")] // running from source: registering this would start "dotnet.exe --background"
    [InlineData(@"C:\Apps\SomethingElse.exe")]
    [InlineData("")]
    public void A_copy_that_is_not_the_installed_program_cannot_register_but_can_still_clean_up(string path)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The start-up entry is a Windows registry value.");
        using (var key = Registry.CurrentUser.CreateSubKey(_subKey))
        {
            key.SetValue("GmailWatcher", "stale entry");
        }

        var registration = new WindowsStartupRegistration(_subKey, "GmailWatcher", () => path);

        Assert.False(registration.IsAvailable);
        Assert.Contains("GmailWatcher.exe", registration.WhyUnavailable, StringComparison.Ordinal);
        Assert.False(registration.Apply(true));
        using (var key = Registry.CurrentUser.OpenSubKey(_subKey))
        {
            Assert.Equal("stale entry", key!.GetValue("GmailWatcher")); // refused: nothing was overwritten
        }

        Assert.True(registration.Apply(false));
        Assert.False(registration.IsEnabled);
    }

}

public sealed class BackgroundSettingsViewModelTests
{
    private static (TestKit Kit, SettingsService Settings, SettingsViewModel ViewModel) Create(bool startupAvailable = true)
    {
        var kit = new TestKit(accounts: 1);
        var settings = new SettingsService(kit.Paths, NullLogger<SettingsService>.Instance);
        settings.Load();
        var diagnostics = new DiagnosticsService(kit.Paths, kit.Archive, kit.Sync, kit.Credentials, TimeProvider.System);
        var viewModel = new SettingsViewModel(settings, kit.Archive, kit.Google, kit.Credentials, kit.Updates, diagnostics, kit.Shell, kit.Files, kit.Paths,
            new AppMode(false), startupAvailable ? kit.Startup : new FakeStartup(available: false));
        return (kit, settings, viewModel);
    }

    [Fact]
    public void The_switches_start_from_the_saved_settings_and_save_every_change()
    {
        var (kit, settings, viewModel) = Create();
        using var _ = kit;
        using var __ = settings;
        using var ___ = viewModel;
        Assert.True(viewModel.KeepRunningInTray);
        Assert.False(viewModel.StartWithWindows);

        viewModel.KeepRunningInTray = false;
        viewModel.StartWithWindows = true;

        Assert.False(settings.App.Background.KeepRunningInTray);
        Assert.True(settings.App.Background.StartWithWindows);
    }

    [Fact]
    public void Starting_with_windows_is_switched_off_with_a_reason_when_this_copy_cannot_register()
    {
        var (kit, settings, viewModel) = Create(startupAvailable: false);
        using var _ = kit;
        using var __ = settings;
        using var ___ = viewModel;

        Assert.False(viewModel.CanStartWithWindows);
        Assert.True(viewModel.HasStartWithWindowsNote);
        Assert.Contains("GmailWatcher.exe", viewModel.StartWithWindowsNote, StringComparison.Ordinal);
    }

    [Fact]
    public void A_notice_has_no_cancel_and_questions_still_do()
    {
        var notice = new ConfirmViewModel(new ConfirmRequest("Still running", "…", "Got it", InfoOnly: true));
        var question = new ConfirmViewModel(new ConfirmRequest("Quit?", "…", "Quit"));
        var danger = new ConfirmViewModel(new ConfirmRequest("Delete?", "…", "Delete", IsDangerous: true));

        Assert.False(notice.ShowCancel);
        Assert.False(notice.CancelIsDefault);
        Assert.True(question.ShowCancel);
        Assert.True(question.CancelIsDefault);
        Assert.True(danger.ShowCancel);
        Assert.False(danger.CancelIsDefault); // Enter must never confirm a dangerous question
    }
}
