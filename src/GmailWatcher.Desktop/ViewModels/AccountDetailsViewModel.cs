using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GmailWatcher.Core.Platform;
using GmailWatcher.Core.Verification;
using GmailWatcher.Desktop.Platform;
using GmailWatcher.Desktop.Presentation;
using GmailWatcher.Desktop.Services;

namespace GmailWatcher.Desktop.ViewModels;

/// <summary>One problem a verification found, in words.</summary>
public sealed record IssueItem(string Title, string Detail);

/// <summary>One account's detail screen: Overview, Insights, Browse.</summary>
public sealed partial class AccountDetailsViewModel : ObservableObject, IDisposable
{
    private readonly ArchiveHost _archive;
    private readonly IShell _shell;
    private readonly IFileLauncher _files;
    private readonly IAccountCommands _commands;
    private readonly TimeProvider _time;
    private readonly IUiDispatcher _dispatcher;
    private readonly SyncService _sync;

    public AccountDetailsViewModel(AccountRowViewModel row, ArchiveHost archive, SyncService sync, IShell shell, IFileLauncher files, IAccountCommands commands, IUiDispatcher dispatcher, TimeProvider time)
    {
        Row = row;
        _archive = archive;
        _sync = sync;
        _shell = shell;
        _files = files;
        _commands = commands;
        _dispatcher = dispatcher;
        _time = time;
        Insights = new InsightsViewModel(row.Id, archive, dispatcher);
        Browse = new BrowseViewModel(row.Id, archive, files, dispatcher, shell.CopyTextAsync);
        Row.PropertyChanged += OnRowChanged;
        Refresh();
    }

    public AccountRowViewModel Row { get; }

    public InsightsViewModel Insights { get; }

    public BrowseViewModel Browse { get; }

    [ObservableProperty]
    public partial int SelectedTab { get; set; }

    partial void OnSelectedTabChanged(int value)
    {
        if (value == 1)
        {
            Insights.EnsureLoaded();
        }
        else if (value == 2)
        {
            Browse.EnsureLoaded();
        }
    }

    // --- Overview -----------------------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial string VerifyHeadline { get; private set; } = "";

    [ObservableProperty]
    public partial string VerifyDetail { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIssues))]
    public partial IReadOnlyList<IssueItem> Issues { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMoreIssues))]
    public partial int IssuesNotListed { get; private set; }

    [ObservableProperty]
    public partial bool CanRepair { get; private set; }

    [ObservableProperty]
    public partial string? RepairResult { get; private set; }

    [ObservableProperty]
    public partial string StatsText { get; private set; } = "";

    [ObservableProperty]
    public partial string SavedSettingsText { get; private set; } = "";

    [ObservableProperty]
    public partial string FolderText { get; private set; } = "";

    public bool HasIssues => Issues.Count > 0;

    public bool HasMoreIssues => IssuesNotListed > 0;

    public ObservableCollection<string> StatLines { get; } = [];

    public void Dispose()
    {
        Row.PropertyChanged -= OnRowChanged;
        Browse.Dispose();
    }

    /// <summary>Reads the verification report, statistics and saved settings again.</summary>
    public void Refresh()
    {
        if (!_archive.IsOpen)
        {
            return;
        }

        var runtime = _archive.Runtime;
        var report = runtime.Accounts.LoadVerification(Row.Id);
        FolderText = runtime.Layout.AccountDirectory(Row.Record.Folder);
        var settings = runtime.Accounts.LoadSettings(Row.Id).Select(d => d.Name).ToList();
        SavedSettingsText = settings.Count == 0
            ? "Mailbox settings (filters, aliases, forwarding) are saved at the next sync."
            : $"Also saved with this archive: {string.Join(", ", settings.Select(Friendly))}.";
        Describe(report);
        StatLines.Clear();
        if (Row.Stats is { } stats)
        {
            foreach (var line in StatsOf(stats))
            {
                StatLines.Add(line);
            }
        }
    }

    [RelayCommand]
    private void Close() => _commands.CloseDetails();

    [RelayCommand]
    private Task ReconnectAsync() => _commands.ReconnectAsync(Row.Record);

    [RelayCommand]
    private Task DisconnectAsync() => _commands.DisconnectAsync(Row.Record);

    [RelayCommand]
    private Task DeleteArchiveAsync() => _commands.DeleteArchiveAsync(Row.Record);

    [RelayCommand]
    private void Verify() => _commands.Verify(Row.Record);

    [RelayCommand]
    private void Sync() => _commands.Sync(Row.Record);

    [RelayCommand]
    private void OpenFolder() => _commands.OpenFolder(Row.Record);

    [RelayCommand]
    private void Recheck() => _commands.RecheckEverything(Row.Record);

    [RelayCommand]
    private async Task CopyFolderAsync() => await _shell.CopyTextAsync(FolderText);

    [RelayCommand]
    private void Repair()
    {
        if (_archive.Runtime.Accounts.LoadVerification(Row.Id) is not { } report)
        {
            return;
        }

        var plan = _archive.Runtime.Maintenance.Repair(Row.Record, report);
        RepairResult = plan.Redownloadable == 0
            ? "None of the damaged messages can be downloaded again: Gmail no longer has them."
            : $"{Formatting.Plural(plan.Redownloadable, "message")} will be downloaded again." +
              (plan.Unrecoverable > 0 ? $" {Formatting.Plural(plan.Unrecoverable, "other message")} can't be recovered because Gmail no longer has {(plan.Unrecoverable == 1 ? "it" : "them")}." : "");
        if (plan.Redownloadable > 0)
        {
            _commands.Sync(Row.Record);
        }
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AccountRowViewModel.Stats) or nameof(AccountRowViewModel.Record))
        {
            _dispatcher.Post(Refresh);
        }
    }

    private void Describe(VerificationReport? report)
    {
        var display = Row.Display;
        if (report is null || display.Verify == VerifyState.Never)
        {
            VerifyHeadline = display.HasMail ? "Not verified yet" : "Nothing to verify yet";
            VerifyDetail = display.HasMail
                ? "Verify checks two things: that every message Gmail lists is downloaded here, and that every saved file is still intact. Do it before you delete anything in Gmail."
                : "Sync this account first. Then Verify tells you whether the archive can replace the Gmail mailbox.";
            Issues = [];
            IssuesNotListed = 0;
            CanRepair = false;
            return;
        }

        var ago = Formatting.Ago(report.CompletedAt, _time.GetUtcNow());
        (VerifyHeadline, VerifyDetail) = display.Verify switch
        {
            VerifyState.Verified => ($"Verified {ago}",
                $"Gmail listed {Formatting.Plural(report.GmailMessages, "message")} and every one is archived. " +
                $"All {Formatting.Plural(report.FilesChecked, "file")} ({Formatting.Bytes(report.BytesChecked)}) match the checksums saved when they were downloaded. " +
                "Mail that arrives after this check is not covered: sync and verify again right before you delete."),
            VerifyState.Stale => ($"Verified {ago}, but you've synced since",
                "More mail was downloaded after that check. Verify again so the result covers everything."),
            VerifyState.FilesOnly => ($"Files intact {ago}; Gmail wasn't checked",
                $"All {Formatting.Plural(report.FilesChecked, "file")} are intact, but Gmail couldn't be asked what it holds" +
                (report.GmailCheckError is { } why ? $" ({why})" : "") + ", so nothing says the archive is complete. Connect the account and verify again."),
            _ => ("Problems found", ProblemSummary(report)),
        };

        Issues = [.. report.Issues.Take(50).Select(Describe)];
        IssuesNotListed = report.IssuesNotListed + Math.Max(0, report.Issues.Count - 50);
        CanRepair = report.Issues.Any(i => i.Kind is VerificationIssueKind.FileMissing or VerificationIssueKind.FileCorrupt
            or VerificationIssueKind.AttachmentMissing or VerificationIssueKind.AttachmentCorrupt);
    }

    private static string ProblemSummary(VerificationReport report)
    {
        var parts = new List<string>();
        if (report.MissingFromArchive > 0)
        {
            parts.Add($"{Formatting.Plural(report.MissingFromArchive, "message")} in Gmail {(report.MissingFromArchive == 1 ? "isn't" : "aren't")} downloaded yet (sync, then verify again)");
        }

        if (report.FilesMissing > 0)
        {
            parts.Add($"{Formatting.Plural(report.FilesMissing, "file")} missing from the archive folder");
        }

        if (report.FilesCorrupt > 0)
        {
            parts.Add($"{Formatting.Plural(report.FilesCorrupt, "file")} changed or damaged");
        }

        if (!report.DatabaseIntact)
        {
            parts.Add("the archive database reported a problem");
        }

        return "Don't delete anything in Gmail yet. " + string.Join("; ", parts) + ".";
    }

    private static IssueItem Describe(VerificationIssue issue) => new(issue.Kind switch
    {
        VerificationIssueKind.MissingFromArchive => "Not downloaded yet",
        VerificationIssueKind.FileMissing => "Message file missing",
        VerificationIssueKind.FileCorrupt => "Message file damaged",
        VerificationIssueKind.AttachmentMissing => "Attachment missing",
        VerificationIssueKind.AttachmentCorrupt => "Attachment damaged",
        _ => "Database problem",
    }, issue.Detail);

    private static IEnumerable<string> StatsOf(GmailWatcher.Core.Accounts.AccountStats stats)
    {
        yield return Formatting.Plural(stats.Messages, "message") + $" in {Formatting.Plural(stats.Threads, "conversation")}";
        yield return $"{Formatting.Bytes(stats.TotalBytes)} on disk";
        if (stats.Attachments > 0)
        {
            yield return $"{Formatting.Plural(stats.Attachments, "attachment")} ({Formatting.Bytes(stats.AttachmentBytes)})";
        }

        yield return Formatting.Plural(stats.Labels, "label");
        if (stats.Oldest is { } oldest && stats.Newest is { } newest)
        {
            yield return $"From {oldest.LocalDateTime:d MMM yyyy} to {newest.LocalDateTime:d MMM yyyy}";
        }

        if (stats.RemoteDeleted > 0)
        {
            yield return $"{Formatting.Count(stats.RemoteDeleted)} no longer in Gmail (kept here)";
        }
    }

    private static string Friendly(string name) => name switch
    {
        "filters" => "filters",
        "sendAs" => "send-as aliases and signatures",
        "forwardingAddresses" => "forwarding addresses",
        "autoForwarding" => "auto-forwarding",
        "vacation" => "vacation reply",
        "imap" => "IMAP settings",
        "pop" => "POP settings",
        "language" => "language",
        _ => name,
    };
}
