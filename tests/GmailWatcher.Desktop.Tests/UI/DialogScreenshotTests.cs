using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using GmailWatcher.Core.Accounts;
using GmailWatcher.Desktop.Infrastructure;
using GmailWatcher.Desktop.Presentation;
using GmailWatcher.Desktop.Services;
using GmailWatcher.Desktop.ViewModels;
using GmailWatcher.Desktop.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace GmailWatcher.Desktop.Tests.UI;

/// <summary>Renders every secondary window, in both themes where it matters, for visual review.</summary>
public sealed class DialogScreenshotTests
{
    private static void Show(Window window, string name, ThemeVariant? theme = null)
    {
        Application.Current!.RequestedThemeVariant = theme ?? ThemeVariant.Dark;
        window.Show();
        ScreenshotTests.Capture(window, name);
        window.Close();
    }

    [AvaloniaFact]
    public void Settings_window()
    {
        using var kit = new TestKit(accounts: 1);
        using var settings = new SettingsService(kit.Paths, NullLogger<SettingsService>.Instance);
        settings.Load();
        var diagnostics = new DiagnosticsService(kit.Paths, kit.Archive, kit.Sync, kit.Credentials, TimeProvider.System);
        using var viewModel = new SettingsViewModel(settings, kit.Archive, kit.Google, kit.Credentials, kit.Updates, diagnostics, kit.Shell, kit.Files, kit.Paths, new AppMode(false));

        Show(new SettingsWindow { DataContext = viewModel, Width = 520, Height = 760 }, "10-settings-dark");
        Show(new SettingsWindow { DataContext = viewModel, Width = 520, Height = 760 }, "10-settings-light", ThemeVariant.Light);
    }

    [AvaloniaFact]
    public void Google_setup_window()
    {
        using var kit = new TestKit(googleConfigured: false);
        var viewModel = new GoogleSetupViewModel(kit.Google, kit.Shell, kit.Browser);
        Show(new GoogleSetupWindow { DataContext = viewModel, Width = 640, Height = 900 }, "11-google-setup");
    }

    [AvaloniaFact]
    public void Sign_in_window_waiting_and_failed()
    {
        using var kit = new TestKit();
        var accounts = new AccountService(kit.Archive, new SlowConnector(), NullLogger<AccountService>.Instance);
        using var waiting = new ConnectViewModel(accounts, kit.Shell, new ImmediateDispatcher(), null);
        waiting.Start();
        Show(new ConnectWindow { DataContext = waiting, Width = 500, Height = 360 }, "12-sign-in-waiting");
        waiting.CancelCommand.Execute(null);

        using var failed = new ConnectViewModel(new AccountService(kit.Archive, new FailingConnector(), NullLogger<AccountService>.Instance), kit.Shell, new ImmediateDispatcher(), "me@example.com");
        failed.Start();
        Thread.Sleep(200);
        Show(new ConnectWindow { DataContext = failed, Width = 500, Height = 360 }, "12-sign-in-failed");
    }

    [AvaloniaFact]
    public void Confirmations()
    {
        var disconnect = new ConfirmViewModel(new ConfirmRequest("Disconnect me@example.com?", "Gmail Watcher will stop using Google for this account. Your archive stays on this PC.", "Disconnect"));
        Show(new ConfirmWindow { DataContext = disconnect }, "13-confirm");

        var delete = new ConfirmViewModel(new ConfirmRequest(
            "Delete the archive of me@example.com from this PC?",
            "This permanently deletes every downloaded message and attachment of this account from this PC. If Gmail no longer has the mail, these files are the only copy.",
            "Delete from this PC", true, "DELETE"));
        Show(new ConfirmWindow { DataContext = delete }, "13-confirm-delete");
        delete.TypedPhrase = "DELETE";
        Show(new ConfirmWindow { DataContext = delete }, "13-confirm-delete-ready");
        Show(new ConfirmWindow { DataContext = delete }, "13-confirm-delete-ready-light", ThemeVariant.Light);
    }

    [AvaloniaFact]
    public void Update_window_with_release_notes()
    {
        using var kit = new Updates.UpdateKit("0.1.0");
        kit.Source.Publish("0.2.0", notes: "## Added\n- **Faster** first sync\n- Search by file name\n\n## Fixed\n- A rare crash when stopping a sync");
        kit.Source.Publish("0.1.1", notes: "## Fixed\n- Verification no longer says Stale after a no-op sync");
        kit.Service.CheckNowAsync().GetAwaiter().GetResult();
        using var viewModel = new UpdateViewModel(kit.Service, new RecordingBrowser(), new ImmediateDispatcher());

        Show(new UpdateWindow { DataContext = viewModel }, "14-update");
    }

    [AvaloniaFact]
    public void The_main_window_in_compact_density_and_a_narrow_size()
    {
        using var kit = new TestKit(accounts: 3, messages: 60);
        var viewModel = kit.CreateMain(dispatcher: new GmailWatcher.Desktop.Platform.AvaloniaUiDispatcher());
        var window = ScreenshotTests.Open(viewModel, ThemeVariant.Dark, width: 580, height: 520, compact: true);

        ScreenshotTests.Capture(window, "15-compact-narrow");
        window.Close();
    }

    private sealed class SlowConnector : IAccountConnector
    {
        public bool IsAvailable => true;

        public string? UnavailableReason => null;

        public async Task<string> SignInAsync(string? loginHint, IProgress<Uri>? authorizationUrl, CancellationToken cancellationToken)
        {
            authorizationUrl?.Report(new Uri("https://accounts.google.com/o/oauth2/v2/auth?client_id=123456789-abcdef.apps.googleusercontent.com&response_type=code"));
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return "never@example.com";
        }

        public Task SignOutAsync(string email, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FailingConnector : IAccountConnector
    {
        public bool IsAvailable => true;

        public string? UnavailableReason => null;

        public Task<string> SignInAsync(string? loginHint, IProgress<Uri>? authorizationUrl, CancellationToken cancellationToken) =>
            throw new ConnectException("Gmail Watcher needs permission to read your mail. On Google's screen, leave \"View your email messages and settings\" ticked, then try again.");

        public Task SignOutAsync(string email, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
