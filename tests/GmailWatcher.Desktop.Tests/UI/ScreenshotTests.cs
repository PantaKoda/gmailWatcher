using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using GmailWatcher.Core.Sync;
using GmailWatcher.Desktop.Platform;
using GmailWatcher.Desktop.Presentation;
using GmailWatcher.Desktop.Services;
using GmailWatcher.Desktop.ViewModels;
using GmailWatcher.Desktop.Views;

[assembly: AvaloniaTestApplication(typeof(GmailWatcher.Desktop.Tests.UI.HeadlessApp))]

namespace GmailWatcher.Desktop.Tests.UI;

public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Renders the real windows with demo mail. Screenshots are written to artifacts/screenshots for visual inspection;
/// the assertions cover structure, not pixels.
/// </summary>
public sealed class ScreenshotTests
{
    private static readonly string ScreenshotDirectory = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "screenshots");

    internal static void Capture(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory(ScreenshotDirectory);
        using var frame = window.CaptureRenderedFrame();
        frame!.Save(Path.Combine(ScreenshotDirectory, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    internal static MainWindow Open(MainViewModel viewModel, ThemeVariant? theme = null, double width = 880, double height = 660, bool compact = false)
    {
        Application.Current!.RequestedThemeVariant = theme ?? ThemeVariant.Dark;
        var window = new MainWindow { DataContext = viewModel, Width = width, Height = height };
        window.Classes.Set("compact", compact);
        window.Show();
        return window;
    }

    private static async Task SettleAsync(TestKit kit, params int[] accountIds)
    {
        foreach (var id in accountIds)
        {
            await kit.WaitIdleAsync(id);
        }

        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Welcome_screen_asks_where_the_archive_should_live()
    {
        using var kit = new TestKit(openArchive: false);
        var viewModel = kit.CreateMain(dispatcher: new AvaloniaUiDispatcher());
        var window = Open(viewModel);

        Capture(window, "01-welcome");

        Assert.True(viewModel.ShowWelcome);
        Assert.False(viewModel.ShowList);
        window.Close();
    }

    [AvaloniaFact]
    public void No_accounts_invites_the_first_one()
    {
        using var kit = new TestKit();
        var window = Open(kit.CreateMain(dispatcher: new AvaloniaUiDispatcher()));

        Capture(window, "02-no-accounts");
        window.Close();
    }

    [AvaloniaFact]
    public void Accounts_before_the_first_sync_dark_and_light()
    {
        using var kit = new TestKit(accounts: 2);
        var viewModel = kit.CreateMain(dispatcher: new AvaloniaUiDispatcher());
        var dark = Open(viewModel);
        Capture(dark, "03-accounts-new-dark");
        dark.Close();

        var light = Open(viewModel, ThemeVariant.Light);
        Capture(light, "03-accounts-new-light");
        light.Close();
    }

    [AvaloniaFact]
    public async Task A_sync_in_progress_and_then_a_verified_archive()
    {
        using var kit = new TestKit(accounts: 2, latency: TimeSpan.FromMilliseconds(8), messages: 400);
        var viewModel = kit.CreateMain(dispatcher: new AvaloniaUiDispatcher());
        var window = Open(viewModel);
        var first = kit.Accounts.List()[0];

        Assert.True(kit.Sync.StartSync(first));
        for (var i = 0; i < 400 && kit.Sync.GetActivity(first.Id).Sync is not { Phase: SyncPhase.Downloading, Done: > 60 }; i++)
        {
            await Task.Delay(10);
        }

        Dispatcher.UIThread.RunJobs();
        Capture(window, "04-syncing");
        Assert.True(viewModel.AnyBusy);

        await SettleAsync(kit, first.Id);
        Assert.True(kit.Sync.StartVerify(kit.Accounts.Get(first.Id)!));
        await SettleAsync(kit, first.Id);
        Capture(window, "05-synced-and-verified");
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        Capture(window, "05-synced-and-verified-light");

        var row = viewModel.Accounts.Single(r => r.Id == first.Id);
        Assert.Equal(VerifyState.Verified, row.Display.Verify);
        Assert.Equal(StatusTone.Success, row.Display.Tone);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Overview_with_a_note_about_an_attachment_the_antivirus_blocked()
    {
        using var kit = new TestKit(accounts: 1, messages: 120);
        var viewModel = kit.CreateMain(dispatcher: new AvaloniaUiDispatcher());
        var window = Open(viewModel, ThemeVariant.Dark, height: 760);
        var account = kit.Accounts.List()[0];
        kit.Sync.StartSync(account);
        await SettleAsync(kit, account.Id);
        kit.Sync.StartVerify(kit.Accounts.Get(account.Id)!);
        await SettleAsync(kit, account.Id);
        var store = kit.Archive.Runtime.Accounts;
        var report = store.LoadVerification(account.Id)!;
        store.SaveVerification(account.Id, report with
        {
            Notices = [new Core.Verification.VerificationIssue(Core.Verification.VerificationIssueKind.AttachmentBlocked, "18c0000000000f1",
                "Windows or your antivirus blocked access to the attachment antivirus-test-file.txt: it reports that the file contains a virus or potentially unwanted software.")],
        });

        ((IAccountCommands)viewModel).ShowDetails(viewModel.Accounts[0]);
        viewModel.Details!.Refresh();
        Capture(window, "06b-details-overview-note");

        Assert.True(viewModel.Details.HasNotices);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Details_overview_insights_and_browse()
    {
        using var kit = new TestKit(accounts: 1, messages: 500);
        var viewModel = kit.CreateMain(dispatcher: new AvaloniaUiDispatcher());
        var window = Open(viewModel);
        var account = kit.Accounts.List()[0];
        kit.Sync.StartSync(account);
        await SettleAsync(kit, account.Id);
        kit.Sync.StartVerify(kit.Accounts.Get(account.Id)!);
        await SettleAsync(kit, account.Id);

        ((IAccountCommands)viewModel).ShowDetails(viewModel.Accounts[0]);
        Capture(window, "06-details-overview");

        viewModel.Details!.SelectedTab = 1;
        for (var i = 0; i < 300 && !viewModel.Details.Insights.HasData; i++)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }

        Capture(window, "07-details-insights");

        viewModel.Details.SelectedTab = 2;
        for (var i = 0; i < 300 && viewModel.Details.Browse.Results.Count == 0; i++)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }

        viewModel.Details.Browse.Selected = viewModel.Details.Browse.Results.FirstOrDefault(r => r.HasAttachments) ?? viewModel.Details.Browse.Results[0];
        for (var i = 0; i < 300 && viewModel.Details.Browse.Preview is null; i++)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }

        Capture(window, "08-details-browse");
        Assert.NotNull(viewModel.Details.Browse.Preview);
        window.Close();
    }
}
