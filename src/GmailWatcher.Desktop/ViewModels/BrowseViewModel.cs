using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GmailWatcher.Archive.Storage;
using GmailWatcher.Core.Platform;
using GmailWatcher.Desktop.Platform;
using GmailWatcher.Desktop.Presentation;
using GmailWatcher.Desktop.Services;

namespace GmailWatcher.Desktop.ViewModels;

/// <summary>A choice in the label filter ("All labels" has a null <see cref="Id"/>).</summary>
public sealed record LabelChoice(string? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One line in the results list.</summary>
public sealed class MessageRowViewModel(MessageSummary summary, DateTimeOffset now)
{
    public MessageSummary Summary { get; } = summary;

    public string Sender => Summary.Sender;

    public string Subject => Summary.Subject;

    public string Snippet => Summary.Snippet;

    public bool HasAttachments => Summary.AttachmentCount > 0;

    public bool RemoteDeleted => Summary.RemoteDeleted;

    public string LabelsText => string.Join(" · ", Summary.Labels.Where(l => l is not ("UNREAD" or "IMPORTANT" or "CATEGORY_PERSONAL")));

    public string DateText
    {
        get
        {
            var local = Summary.Received.ToLocalTime();
            var today = now.ToLocalTime();
            return local.Date == today.Date ? local.ToString("t", CultureInfo.CurrentCulture)
                : local.Year == today.Year ? local.ToString("d MMM", CultureInfo.CurrentCulture)
                : local.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
        }
    }

    public string AutomationName => $"{Sender}: {Subject}, {DateText}";
}

/// <summary>An attachment of the open message, with what can safely be done with it.</summary>
public sealed partial class AttachmentViewModel(AttachmentInfo info, IFileLauncher files) : ObservableObject
{
    public string FileName => info.FileName;

    public string Detail => $"{Formatting.Bytes(info.SizeBytes)} · {info.ContentType}" + (info.IsInline ? " · embedded" : "");

    public bool IsSaved => info.IsSaved;

    public bool CanOpen => info.IsSaved && FileLauncher.IsSafeToOpen(info.FileName);

    public string OpenTooltip => CanOpen ? "Open with the default program" : "Not opened directly: this type of file can run code. Use Show in folder and decide there.";

    [RelayCommand]
    private void Open() => files.OpenFile(info.FullPath!);

    [RelayCommand]
    private void Reveal() => files.Reveal(info.FullPath!);
}

/// <summary>The message open on the right: headers, body as plain text, attachments.</summary>
public sealed partial class MessagePreviewViewModel(MessageDetail detail, IFileLauncher files) : ObservableObject
{
    public string Subject => detail.Summary.Subject;

    public string From => detail.From;

    public string To => detail.To;

    public string Cc => detail.Cc;

    public bool HasCc => detail.Cc.Length > 0;

    public string Date => detail.Date;

    public string Body => detail.Body;

    public string? Problem => detail.Problem;

    public bool HasProblem => detail.Problem is not null;

    public bool GoneFromGmail => detail.Summary.RemoteDeleted;

    public string LabelsText => string.Join(" · ", detail.Summary.Labels);

    public IReadOnlyList<AttachmentViewModel> Attachments { get; } = [.. detail.Attachments.Select(a => new AttachmentViewModel(a, files))];

    public bool HasAttachments => Attachments.Count > 0;

    [RelayCommand]
    private void ShowFile() => files.Reveal(detail.EmlPath);

    [RelayCommand]
    private void OpenInMailApp() => files.OpenFile(detail.EmlPath);
}

/// <summary>
/// The Browse tab: full-text search over everything archived for the account, with a plain-text preview. HTML is never
/// rendered, and risky attachment types are shown in Explorer instead of being opened.
/// </summary>
public sealed partial class BrowseViewModel(int accountId, ArchiveHost archive, IFileLauncher files, IUiDispatcher dispatcher) : ObservableObject, IDisposable
{
    private const int PageSize = 50;
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    private CancellationTokenSource? _pending;
    private int _version;
    private bool _loadedOnce;

    public ObservableCollection<MessageRowViewModel> Results { get; } = [];

    public ObservableCollection<LabelChoice> Labels { get; } = [new LabelChoice(null, "All labels")];

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial bool OnlyWithAttachments { get; set; }

    [ObservableProperty]
    public partial LabelChoice? SelectedLabel { get; set; }

    [ObservableProperty]
    public partial MessageRowViewModel? Selected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    public partial MessagePreviewViewModel? Preview { get; private set; }

    [ObservableProperty]
    public partial string ResultText { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsSearching { get; private set; }

    [ObservableProperty]
    public partial bool HasMore { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNothingFound))]
    public partial bool HasResults { get; private set; }

    public bool HasPreview => Preview is not null;

    public bool ShowNothingFound => !HasResults && !IsSearching && _loadedOnce;

    public void Dispose() => _pending?.Cancel();

    public void EnsureLoaded()
    {
        if (_loadedOnce || !archive.IsOpen)
        {
            return;
        }

        foreach (var label in archive.Runtime.Accounts.GetLabels(accountId).Where(l => l.Id is not ("UNREAD" or "CHAT" or "CATEGORY_PERSONAL")))
        {
            Labels.Add(new LabelChoice(label.Id, label.Name));
        }

        SelectedLabel = Labels[0]; // before _loadedOnce is set, so this does not start a search of its own
        _loadedOnce = true;
        Schedule(immediately: true);
    }

    partial void OnSearchTextChanged(string value) => Schedule();

    partial void OnOnlyWithAttachmentsChanged(bool value) => Schedule(immediately: true);

    partial void OnSelectedLabelChanged(LabelChoice? value)
    {
        if (_loadedOnce)
        {
            Schedule(immediately: true);
        }
    }

    partial void OnSelectedChanged(MessageRowViewModel? value) => _ = ShowAsync(value);

    [RelayCommand]
    private void LoadMore() => _ = RunAsync(append: true, Interlocked.Increment(ref _version), CancellationToken.None);

    [RelayCommand]
    private void Clear() => SearchText = "";

    private void Schedule(bool immediately = false)
    {
        if (!_loadedOnce)
        {
            return;
        }

        _pending?.Cancel();
        var cts = _pending = new CancellationTokenSource();
        var version = Interlocked.Increment(ref _version);
        _ = Task.Run(async () =>
        {
            try
            {
                if (!immediately)
                {
                    await Task.Delay(Debounce, cts.Token).ConfigureAwait(false);
                }

                await RunAsync(append: false, version, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer search.
            }
        });
    }

    internal async Task RunAsync(bool append, int version, CancellationToken token)
    {
        if (!archive.IsOpen)
        {
            return;
        }

        var runtime = archive.Runtime;
        var offset = append ? Results.Count : 0;
        var query = new SearchQuery(SearchText, accountId, SelectedLabel?.Id, OnlyWithAttachments, offset, PageSize);

        // A search that was cancelled before it got a thread (a busy machine can run the newer one first) must leave no trace:
        // nothing would ever switch the spinner it turned on off again.
        token.ThrowIfCancellationRequested();

        // Only the newest search owns the spinner; an outdated one must not switch it on or off.
        dispatcher.Post(() =>
        {
            if (IsLatest(version))
            {
                IsSearching = true;
            }
        });
        SearchPage page;
        try
        {
            page = await Task.Run(() => runtime.Search.Search(query), token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            dispatcher.Post(() =>
            {
                if (IsLatest(version))
                {
                    ResultText = $"The search failed: {ex.Message}";
                    IsSearching = false;
                }
            });
            return;
        }

        var now = DateTimeOffset.UtcNow;
        dispatcher.Post(() =>
        {
            if (!append && !IsLatest(version))
            {
                return; // a newer search is already on its way, and it will switch the spinner off
            }

            if (!append)
            {
                Results.Clear();
                Selected = null;
                Preview = null;
            }

            foreach (var item in page.Items)
            {
                Results.Add(new MessageRowViewModel(item, now));
            }

            HasResults = Results.Count > 0;
            HasMore = page.HasMore(query);
            ResultText = page.Total == 0
                ? (string.IsNullOrWhiteSpace(SearchText) ? "Nothing archived yet." : "No messages match.")
                : $"{Formatting.Plural(page.Total, "message")}" + (string.IsNullOrWhiteSpace(SearchText) ? "" : " match") + (HasMore ? $" · showing the newest {Formatting.Count(Results.Count)}" : "");
            if (IsLatest(version))
            {
                IsSearching = false;
            }

            OnPropertyChanged(nameof(ShowNothingFound));
        });
    }

    private bool IsLatest(int version) => version == Volatile.Read(ref _version);

    private async Task ShowAsync(MessageRowViewModel? row)
    {
        if (row is null || !archive.IsOpen)
        {
            Preview = null;
            return;
        }

        var viewer = archive.Runtime.Viewer;
        var detail = await Task.Run(() => viewer.Load(row.Summary)).ConfigureAwait(false);
        dispatcher.Post(() =>
        {
            if (Selected == row)
            {
                Preview = detail is null ? null : new MessagePreviewViewModel(detail, files);
            }
        });
    }
}
