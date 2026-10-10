using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GmailWatcher.Archive.Analytics;
using GmailWatcher.Core.Platform;
using GmailWatcher.Desktop.Presentation;
using GmailWatcher.Desktop.Services;

namespace GmailWatcher.Desktop.ViewModels;

/// <summary>A ranked line with a bar: the bar's length is relative to the biggest line in its list.</summary>
public sealed record RankRow(string Name, string? Detail, string Value, double Percent);

/// <summary>A titled list of <see cref="RankRow"/>s.</summary>
public sealed record RankList(string Title, string Caption, IReadOnlyList<RankRow> Rows)
{
    public bool HasRows => Rows.Count > 0;
}

/// <summary>The analytics tab: answers computed by DuckDB from the archive (loaded on demand, in the background).</summary>
public sealed partial class InsightsViewModel(int accountId, ArchiveHost archive, IUiDispatcher dispatcher) : ObservableObject
{
    private bool _loadedOnce;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowContent))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowContent))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial bool HasData { get; private set; }

    [ObservableProperty]
    public partial string? Problem { get; private set; }

    [ObservableProperty]
    public partial string Headline { get; private set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<double> MonthValues { get; private set; } = [];

    [ObservableProperty]
    public partial string MonthCaption { get; private set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<RankList> Lists { get; private set; } = [];

    [ObservableProperty]
    public partial string DuplicatesText { get; private set; } = "";

    public bool ShowContent => HasData && !IsLoading;

    public bool ShowEmpty => !HasData && !IsLoading && Problem is null;

    public string DatabasePath => archive.IsOpen ? archive.Runtime.Layout.AnalyticsFile : "";

    public void EnsureLoaded()
    {
        if (!_loadedOnce)
        {
            _ = Load();
        }
    }

    [RelayCommand]
    private async Task Load()
    {
        if (!archive.IsOpen || IsLoading)
        {
            return;
        }

        _loadedOnce = true;
        IsLoading = true;
        Problem = null;
        var runtime = archive.Runtime;
        try
        {
            var insights = await Task.Run(() => runtime.Insights.Load(accountId)).ConfigureAwait(false);
            dispatcher.Post(() => Apply(insights));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            dispatcher.Post(() =>
            {
                Problem = $"The analytics couldn't be read ({ex.Message}). They are rebuilt from the archive; try again.";
                IsLoading = false;
            });
        }
    }

    private void Apply(MailboxInsights insights)
    {
        HasData = insights.Messages > 0;
        Headline = insights.Messages == 0
            ? ""
            : $"{Formatting.Plural(insights.Messages, "message")} · {Formatting.Bytes(insights.MessageBytes)} of mail" +
              (insights.Attachments > 0 ? $" · {Formatting.Plural(insights.Attachments, "attachment")} ({Formatting.Bytes(insights.AttachmentBytes)})" : "") +
              (insights.Unread > 0 ? $" · {Formatting.Count(insights.Unread)} unread" : "");

        MonthValues = [.. insights.ByMonth.Select(m => (double)m.Messages)];
        MonthCaption = insights.ByMonth.Count == 0
            ? ""
            : $"{insights.ByMonth[0].Month:MMM yyyy} to {insights.ByMonth[^1].Month:MMM yyyy} · busiest month {insights.ByMonth.MaxBy(m => m.Messages)!.Month:MMM yyyy} ({Formatting.Count(insights.ByMonth.Max(m => m.Messages))})";

        Lists =
        [
            Ranked("Who writes to you most", "Messages received", insights.TopSenders, r => r.Detail is { } d && d != r.Name ? d : null, r => Formatting.Count(r.Count), r => r.Count),
            Ranked("Who fills your mailbox", "Size of mail received, by sender domain", insights.SendersBySize, r => $"{Formatting.Plural(r.Count, "message")}", r => Formatting.Bytes(r.Bytes), r => r.Bytes),
            Ranked("Largest attachments", "Biggest files, by size", insights.LargestAttachments, r => r.Detail, r => Formatting.Bytes(r.Bytes), r => r.Bytes),
            Ranked("Attachment types", "By total size", insights.AttachmentTypes, r => Formatting.Plural(r.Count, "file"), r => Formatting.Bytes(r.Bytes), r => r.Bytes),
            Ranked("Largest messages", "Biggest single messages", insights.LargestMessages, r => r.Detail, r => Formatting.Bytes(r.Bytes), r => r.Bytes),
            Ranked("Labels", "Messages with each label", insights.Labels, _ => null, r => Formatting.Count(r.Count), r => r.Count),
            Ranked("You write to", "Most frequent recipients of your sent mail", insights.TopRecipients, _ => null, r => Formatting.Count(r.Count), r => r.Count),
        ];

        DuplicatesText = insights.DuplicateAttachmentFiles == 0
            ? ""
            : $"{Formatting.Plural(insights.DuplicateAttachmentFiles, "attachment")} repeat{(insights.DuplicateAttachmentFiles == 1 ? "s" : "")} a file that is already saved elsewhere in the archive, " +
              $"about {Formatting.Bytes(insights.DuplicateAttachmentBytes)} that could be saved by deduplicating.";
        IsLoading = false;
    }

    private static RankList Ranked(string title, string caption, IReadOnlyList<RankedItem> items, Func<RankedItem, string?> detail, Func<RankedItem, string> value, Func<RankedItem, long> measure)
    {
        var max = items.Count == 0 ? 1 : Math.Max(1, items.Max(measure));
        return new RankList(title, caption, [.. items.Select(i => new RankRow(i.Name, detail(i), value(i), 100.0 * measure(i) / max))]);
    }
}
