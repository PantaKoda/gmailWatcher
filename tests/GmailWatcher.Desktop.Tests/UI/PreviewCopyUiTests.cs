using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GmailWatcher.Desktop.Platform;
using GmailWatcher.Desktop.ViewModels;

namespace GmailWatcher.Desktop.Tests.UI;

/// <summary>The real window: the message text can be selected and copied, and the right-click menu works inside the preview template.</summary>
public sealed class PreviewCopyUiTests
{
    private static async Task<(MainViewModel Main, Avalonia.Controls.Window Window, TestKit Kit, SelectableTextBlock Body)> OpenMessageAsync()
    {
        var kit = new TestKit(accounts: 1, messages: 40);
        var main = kit.CreateMain(dispatcher: new AvaloniaUiDispatcher());
        var window = ScreenshotTests.Open(main);
        var account = kit.Accounts.List()[0];
        kit.Sync.StartSync(account);
        await kit.WaitIdleAsync(account.Id);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();

        ((IAccountCommands)main).ShowDetails(main.Accounts[0]);
        main.Details!.SelectedTab = 2;
        var browse = main.Details.Browse;
        for (var i = 0; i < 300 && browse.Results.Count == 0; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }

        browse.Selected = browse.Results[0];
        for (var i = 0; i < 300 && browse.Preview is null; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }

        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var body = window.GetVisualDescendants().OfType<SelectableTextBlock>().Single(b => b.Text == browse.Preview!.Body);
        return (main, window, kit, body);
    }

    [AvaloniaFact]
    public async Task The_subject_sender_and_text_of_the_open_message_are_selectable()
    {
        var (main, window, kit, _) = await OpenMessageAsync();
        using var _kit = kit;

        var preview = main.Details!.Browse.Preview!;
        var selectable = window.GetVisualDescendants().OfType<SelectableTextBlock>().Select(b => b.Text).ToList();

        Assert.Contains(preview.Subject, selectable);
        Assert.Contains($"From: {preview.From}", selectable);
        Assert.Contains($"To: {preview.To}", selectable);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_right_click_menu_copies_the_selection_or_the_whole_text()
    {
        var (main, window, kit, body) = await OpenMessageAsync();
        using var _kit = kit;
        var clipboard = TopLevel.GetTopLevel(window)!.Clipboard!;
        var menu = body.ContextMenu!;
        menu.Open(body);
        Dispatcher.UIThread.RunJobs();
        MenuItem Item(string header) => menu.Items.OfType<MenuItem>().Single(m => (string?)m.Header == header);

        // Nothing selected: Copy means "copy this text".
        Item("Copy").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(main.Details!.Browse.Preview!.Body, await clipboard.TryGetTextAsync());

        // A selection: only that is copied.
        await clipboard.ClearAsync();
        body.SelectionStart = 0;
        body.SelectionEnd = 4;
        Item("Copy").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(main.Details!.Browse.Preview!.Body[..4], await clipboard.TryGetTextAsync());

        // Select all.
        body.ClearSelection();
        Item("Select all").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(main.Details!.Browse.Preview!.Body, body.SelectedText);

        // The whole message goes through the view model and the shell.
        Item("Copy whole message").Command!.Execute(null);
        await kit.UntilAsync(() => kit.Shell.Copied.Count > 0, what: "the whole message to be copied");
        Assert.StartsWith(main.Details!.Browse.Preview!.Subject, kit.Shell.Copied[^1], StringComparison.Ordinal);
        window.Close();
    }
}
