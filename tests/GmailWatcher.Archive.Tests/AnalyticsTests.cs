using DuckDB.NET.Data;
using GmailWatcher.Archive.Analytics;
using GmailWatcher.Archive.Demo;
using GmailWatcher.Core.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace GmailWatcher.Archive.Tests;

public sealed class AnalyticsTests : IDisposable
{
    private const string Email = "me@example.com";
    private readonly ArchiveFixture _fixture;
    private readonly AnalyticsMirror _mirror;
    private readonly InsightsService _insights;
    private readonly InMemoryMailbox _mailbox = new(Email);

    public AnalyticsTests()
    {
        _fixture = new ArchiveFixture();
        _mirror = new AnalyticsMirror(_fixture.Database, _fixture.Accounts, Path.Combine(_fixture.Root, "analytics.duckdb"), NullLogger<AnalyticsMirror>.Instance);
        _insights = new InsightsService(_mirror, _fixture.Accounts);
        _fixture.AddAccount(Email);
    }

    public void Dispose() => _fixture.Dispose();

    private int AccountId => _fixture.Accounts.Find(Email)!.Id;

    private void Deliver(string from, int year, int month, string[]? labels = null, params SyntheticAttachment[] attachments) =>
        _mailbox.Add(new MessageSpec
        {
            From = from,
            Subject = $"From {from}",
            Date = new DateTimeOffset(year, month, 15, 10, 0, 0, TimeSpan.Zero),
            Labels = labels ?? ["INBOX"],
            Attachments = attachments,
        });

    private async Task SyncAsync() =>
        Assert.Equal(SyncOutcome.Completed, (await _fixture.SyncAsync(Email, _mailbox, null, TestContext.Current.CancellationToken)).Outcome);

    private long Scalar(string sql) => _mirror.Read(c => AnalyticsMirror.ScalarLong(c, sql));

    [Fact]
    public async Task Update_copies_messages_labels_and_attachments_to_duckdb()
    {
        Deliver("Ann <ann@news.example.com>", 2023, 1, ["INBOX", "UNREAD"], new SyntheticAttachment("a.pdf", "application/pdf", [1, 2, 3]));
        Deliver("Bob <bob@example.org>", 2023, 2, ["SENT"]);
        await SyncAsync();

        _mirror.Update(AccountId, TestContext.Current.CancellationToken);

        Assert.Equal(2, Scalar("SELECT count(*) FROM messages"));
        Assert.Equal(1, Scalar("SELECT count(*) FROM messages WHERE is_unread AND from_domain = 'news.example.com'"));
        Assert.Equal(1, Scalar("SELECT count(*) FROM messages WHERE is_sent"));
        Assert.Equal(1, Scalar("SELECT count(*) FROM attachments WHERE extension = 'pdf' AND size_bytes = 3"));
        Assert.Equal(3, Scalar("SELECT count(*) FROM message_labels"));
        Assert.Equal(1, Scalar("SELECT count(*) FROM accounts WHERE email = 'me@example.com'"));
    }

    [Fact]
    public async Task A_second_update_copies_only_what_changed_and_never_duplicates()
    {
        Deliver("Ann <ann@example.com>", 2023, 1, ["INBOX", "UNREAD"]);
        Deliver("Bob <bob@example.com>", 2023, 2);
        await SyncAsync();
        _mirror.Update(AccountId, TestContext.Current.CancellationToken);

        var changed = _mailbox.Ids[0];
        _mailbox.ChangeLabels(changed, add: ["STARRED"], remove: ["UNREAD"]);
        Deliver("Cy <cy@example.com>", 2023, 3);
        await SyncAsync();
        _mirror.Update(AccountId, TestContext.Current.CancellationToken);
        _mirror.Update(AccountId, TestContext.Current.CancellationToken); // idempotent

        Assert.Equal(3, Scalar("SELECT count(*) FROM messages"));
        Assert.Equal(0, Scalar("SELECT count(*) FROM messages WHERE is_unread"));
        Assert.Equal(1, Scalar("SELECT count(*) FROM messages WHERE is_starred"));
        Assert.Equal(1, Scalar("SELECT count(*) FROM message_labels WHERE label_id = 'STARRED'"));
        Assert.Equal(0, Scalar("SELECT count(*) FROM message_labels WHERE label_id = 'UNREAD'"));
    }

    [Fact]
    public async Task Messages_gone_from_gmail_are_flagged_not_removed()
    {
        Deliver("Ann <ann@example.com>", 2023, 1);
        Deliver("Bob <bob@example.com>", 2023, 2);
        await SyncAsync();
        _mailbox.DeleteAll();
        await SyncAsync();

        _mirror.Update(AccountId, TestContext.Current.CancellationToken);

        Assert.Equal(2, Scalar("SELECT count(*) FROM messages"));
        Assert.Equal(2, Scalar("SELECT count(*) FROM messages WHERE remote_deleted"));
    }

    [Fact]
    public async Task A_rebuild_gives_exactly_what_incremental_updates_gave()
    {
        for (var i = 1; i <= 6; i++)
        {
            Deliver($"S{i} <s{i}@example.com>", 2022, i, ["INBOX"], new SyntheticAttachment($"f{i}.txt", "text/plain", [(byte)i]));
            await SyncAsync();
            _mirror.Update(AccountId, TestContext.Current.CancellationToken);
        }

        _mailbox.ChangeLabels(_mailbox.Ids[2], add: ["IMPORTANT"]);
        await SyncAsync();
        _mirror.Update(AccountId, TestContext.Current.CancellationToken);
        var before = Snapshot();

        _mirror.Rebuild(null, TestContext.Current.CancellationToken);

        Assert.Equal(before, Snapshot());
        Assert.Equal(6, Scalar("SELECT count(*) FROM messages"));
    }

    private string Snapshot() => _mirror.Read(c =>
    {
        var parts = new List<string>();
        foreach (var sql in new[]
        {
            "SELECT gmail_id || '|' || size_bytes || '|' || is_unread || '|' || remote_deleted FROM messages ORDER BY gmail_id",
            "SELECT gmail_id || '|' || label_id FROM message_labels ORDER BY 1",
            "SELECT gmail_id || '|' || file_name || '|' || sha256 FROM attachments ORDER BY 1",
        })
        {
            using var command = c.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                parts.Add(reader.GetString(0));
            }

            parts.Add("--");
        }

        return string.Join('\n', parts);
    })!;

    [Fact]
    public async Task A_damaged_analytics_file_is_replaced_and_rebuilt_from_the_archive()
    {
        Deliver("Ann <ann@example.com>", 2023, 1);
        await SyncAsync();
        _mirror.Update(AccountId, TestContext.Current.CancellationToken);
        File.WriteAllText(_mirror.Path, "this is not a duckdb file at all");

        _mirror.Update(AccountId, TestContext.Current.CancellationToken);

        Assert.Equal(1, Scalar("SELECT count(*) FROM messages"));
        Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(_mirror.Path)!, "analytics.duckdb.damaged-*"));
    }

    [Fact]
    public async Task The_file_can_be_opened_by_another_tool_between_updates()
    {
        Deliver("Ann <ann@example.com>", 2023, 1);
        await SyncAsync();
        _mirror.Update(AccountId, TestContext.Current.CancellationToken);

        using var other = new DuckDBConnection($"Data Source={_mirror.Path};access_mode=READ_ONLY");
        other.Open();
        using var command = other.CreateCommand();
        command.CommandText = "SELECT count(*) FROM messages";

        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
    }

    [Fact]
    public async Task Removing_an_account_removes_its_rows_only()
    {
        Deliver("Ann <ann@example.com>", 2023, 1);
        await SyncAsync();
        var other = _fixture.AddAccount("other@example.com");
        var otherBox = new InMemoryMailbox("other@example.com");
        otherBox.Add(new MessageSpec { Subject = "other" });
        await _fixture.SyncAsync("other@example.com", otherBox, null, TestContext.Current.CancellationToken);
        _mirror.Update(AccountId, TestContext.Current.CancellationToken);
        _mirror.Update(other.Id, TestContext.Current.CancellationToken);
        Assert.Equal(2, Scalar("SELECT count(*) FROM messages"));

        _mirror.Remove(other.Id);

        Assert.Equal(1, Scalar("SELECT count(*) FROM messages"));
        Assert.Equal(1, Scalar($"SELECT count(*) FROM messages WHERE account_id = {AccountId}"));
    }

    [Fact]
    public async Task Insights_answer_the_questions_the_app_shows()
    {
        var pdf = new SyntheticAttachment("contract.pdf", "application/pdf", new byte[5000]);
        Deliver("Newsletter <news@shop.example.com>", 2022, 11, ["INBOX", "CATEGORY_PROMOTIONS"]);
        Deliver("Newsletter <news@shop.example.com>", 2022, 12, ["INBOX", "CATEGORY_PROMOTIONS"]);
        Deliver("Newsletter <news@shop.example.com>", 2023, 1, ["INBOX", "CATEGORY_PROMOTIONS"]);
        Deliver("Dana <dana@work.example.org>", 2023, 1, ["INBOX", "UNREAD"], pdf);
        Deliver("Dana <dana@work.example.org>", 2023, 2, ["INBOX"], pdf); // the same file twice
        await SyncAsync();

        var insights = _insights.Load(AccountId, TestContext.Current.CancellationToken);

        Assert.Equal(5, insights.Messages);
        Assert.Equal(2, insights.Attachments);
        Assert.Equal(10_000, insights.AttachmentBytes);
        Assert.Equal(1, insights.Unread);
        Assert.Equal(new DateOnly(2022, 11, 1), insights.ByMonth[0].Month);
        Assert.Equal(4, insights.ByMonth.Count);
        Assert.Equal(new DateTimeOffset(2022, 11, 15, 10, 0, 0, TimeSpan.Zero), insights.Oldest);
        Assert.Equal("news@shop.example.com", insights.TopSenders[0].Detail);
        Assert.Equal(3, insights.TopSenders[0].Count);
        Assert.Equal("pdf", insights.AttachmentTypes[0].Name);
        Assert.Equal(1, insights.DuplicateAttachmentFiles);
        Assert.Equal(5000, insights.DuplicateAttachmentBytes);
        Assert.Equal("contract.pdf", insights.LargestAttachments[0].Name);
        Assert.Contains(insights.Labels, l => l is { Name: "CATEGORY_PROMOTIONS", Count: 3 });
        Assert.Equal("shop.example.com", insights.SendersBySize.Single(d => d.Count == 3).Name);
    }

    [Fact]
    public void Insights_of_an_empty_archive_are_empty_not_an_error()
    {
        var insights = _insights.Load(AccountId, TestContext.Current.CancellationToken);

        Assert.Equal(0, insights.Messages);
        Assert.Empty(insights.ByMonth);
        Assert.Empty(insights.TopSenders);
    }
}
