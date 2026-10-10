using GmailWatcher.Archive.Demo;
using GmailWatcher.Archive.Storage;
using GmailWatcher.Core.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace GmailWatcher.Archive.Tests;

public sealed class SearchTests : IDisposable
{
    private const string Email = "me@example.com";
    private readonly ArchiveFixture _fixture = new();
    private readonly InMemoryMailbox _mailbox = new(Email);
    private readonly SearchService _search;
    private readonly MessageViewer _viewer;

    public SearchTests()
    {
        _fixture.AddAccount(Email);
        _search = new SearchService(_fixture.Database);
        _viewer = new MessageViewer(_fixture.Database, _fixture.Layout, NullLogger<MessageViewer>.Instance);
    }

    public void Dispose() => _fixture.Dispose();

    private async Task<string> DeliverAsync(string subject, string body, string from = "Ann <ann@example.com>", int day = 1, string[]? labels = null, params SyntheticAttachment[] attachments)
    {
        var id = _mailbox.Add(new MessageSpec
        {
            Subject = subject,
            Body = body,
            From = from,
            Date = new DateTimeOffset(2024, 1, day, 9, 0, 0, TimeSpan.Zero),
            Labels = labels ?? ["INBOX"],
            Attachments = attachments,
        });
        return id;
    }

    private async Task SyncAsync() =>
        Assert.Equal(SyncOutcome.Completed, (await _fixture.SyncAsync(Email, _mailbox, null, TestContext.Current.CancellationToken)).Outcome);

    private SearchPage Find(string text, int? account = null, string? label = null, bool attachments = false, int offset = 0, int limit = 50) =>
        _search.Search(new SearchQuery(text, account, label, attachments, offset, limit));

    [Fact]
    public async Task Words_in_subject_and_body_are_found_as_prefixes_case_insensitively()
    {
        await DeliverAsync("Quarterly INVOICE", "Please find the numbers attached");
        await DeliverAsync("Lunch", "Pizza on Friday");
        await SyncAsync();

        Assert.Equal("Quarterly INVOICE", Assert.Single(Find("invoice").Items).Subject);
        Assert.Equal("Lunch", Assert.Single(Find("piz").Items).Subject);
        Assert.Equal("Quarterly INVOICE", Assert.Single(Find("numb attach").Items).Subject);
        Assert.Empty(Find("pizzeria").Items);
    }

    [Fact]
    public async Task Accents_and_non_latin_text_are_searchable()
    {
        await DeliverAsync("Café Müller", "Zürich ist schön — 東京");
        await SyncAsync();

        Assert.Single(Find("cafe").Items);
        Assert.Single(Find("zurich").Items);
        Assert.Single(Find("東京").Items);
    }

    [Fact]
    public async Task Quoted_phrases_must_match_in_order()
    {
        await DeliverAsync("A", "the quick brown fox");
        await DeliverAsync("B", "the brown quick fox");
        await SyncAsync();

        Assert.Equal("A", Assert.Single(Find("\"quick brown\"").Items).Subject);
    }

    [Fact]
    public async Task Column_prefixes_narrow_the_search()
    {
        await DeliverAsync("Report", "see attached", from: "Zed <zed@corp.example>");
        await DeliverAsync("Hello", "Report is due", from: "Amy <amy@elsewhere.example>");
        await SyncAsync();

        Assert.Equal("Report", Assert.Single(Find("from:zed").Items).Subject);
        Assert.Equal("Report", Assert.Single(Find("subject:report").Items).Subject);
        Assert.Equal(2, Find("report").Items.Count);
        Assert.Equal("Report", Assert.Single(Find("from:\"zed\" subject:report").Items).Subject);
    }

    [Fact]
    public async Task Attachment_file_names_are_searchable_and_filterable()
    {
        await DeliverAsync("With file", "x", attachments: [new SyntheticAttachment("budget-2024.xlsx", "application/octet-stream", [1, 2])]);
        await DeliverAsync("Without", "y");
        await SyncAsync();

        Assert.Equal("With file", Assert.Single(Find("file:budget").Items).Subject);
        Assert.Equal("With file", Assert.Single(Find("", attachments: true).Items).Subject);
        Assert.Equal(2, Find("").Items.Count);
    }

    [Fact]
    public async Task Labels_and_accounts_filter()
    {
        await DeliverAsync("Starred one", "x", labels: ["INBOX", "STARRED"]);
        await DeliverAsync("Plain", "x");
        await SyncAsync();
        var other = _fixture.AddAccount("other@example.com");
        var otherBox = new InMemoryMailbox("other@example.com");
        otherBox.Add(new MessageSpec { Subject = "Elsewhere", Body = "x" });
        await _fixture.SyncAsync("other@example.com", otherBox, null, TestContext.Current.CancellationToken);

        Assert.Equal("Starred one", Assert.Single(Find("", label: "STARRED").Items).Subject);
        Assert.Equal("Elsewhere", Assert.Single(Find("", account: other.Id).Items).Subject);
        Assert.Equal(3, Find("x").Items.Count);
    }

    [Fact]
    public async Task Results_are_newest_first_with_paging_and_totals()
    {
        for (var day = 1; day <= 25; day++)
        {
            await DeliverAsync($"Day {day}", "common word", day: day);
        }

        await SyncAsync();

        var first = Find("common", limit: 10);
        var third = Find("common", offset: 20, limit: 10);

        Assert.Equal(25, first.Total);
        Assert.Equal("Day 25", first.Items[0].Subject);
        Assert.Equal("Day 16", first.Items[^1].Subject);
        Assert.True(first.HasMore(new SearchQuery("common", Limit: 10)));
        Assert.Equal(5, third.Items.Count);
        Assert.Equal("Day 1", third.Items[^1].Subject);
        Assert.False(third.HasMore(new SearchQuery("common", Offset: 20, Limit: 10)));
    }

    [Fact]
    public async Task Search_results_carry_label_names()
    {
        _mailbox.AddLabel("Label_9", "Receipts");
        await DeliverAsync("Bought a thing", "x", labels: ["INBOX", "Label_9"]);
        await SyncAsync();

        var item = Assert.Single(Find("bought").Items);

        Assert.Equal(["INBOX", "Receipts"], item.Labels);
        Assert.Equal(Email, item.AccountEmail);
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("\"unbalanced")]
    [InlineData("*")]
    [InlineData("AND")]
    [InlineData("OR NOT")]
    [InlineData("NEAR(a b)")]
    [InlineData("a AND (b OR")]
    [InlineData("col:umn:")]
    [InlineData("from:")]
    [InlineData("from:\"")]
    [InlineData("-minus +plus ^caret")]
    [InlineData("'; DROP TABLE messages; --")]
    [InlineData("   ")]
    [InlineData("😀")]
    public async Task No_input_can_break_the_search(string text)
    {
        await DeliverAsync("Hello", "world");
        await SyncAsync();

        var page = Find(text);

        Assert.NotNull(page);
        Assert.Equal(1, _fixture.Messages.Count(_fixture.Accounts.Find(Email)!.Id)); // nothing was dropped by the "query"
    }

    [Theory]
    [InlineData("invoice march", "\"invoice\"* \"march\"*")]
    [InlineData("a", "\"a\"")]
    [InlineData("\"exact phrase\"", "\"exact phrase\"")]
    [InlineData("from:zed", "sender : \"zed\"*")]
    [InlineData("from:\"zed z\"", "sender : \"zed z\"")]
    [InlineData("to:bob file:x.pdf", "recipients : \"bob\"* attachments : \"x.pdf\"*")]
    [InlineData("unknown:thing", "\"unknown:thing\"*")]
    public void Match_strings(string input, string expected) => Assert.Equal(expected, SearchService.ToMatch(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    [InlineData("*** ---")]
    public void Nothing_searchable_gives_no_match_string(string input) => Assert.Null(SearchService.ToMatch(input));

    [Fact]
    public async Task The_viewer_shows_headers_body_and_attachments_without_rendering_html()
    {
        await DeliverAsync("Plain", "Hello there", attachments: [new SyntheticAttachment("note.txt", "text/plain", [65, 66])]);
        _mailbox.Add(new MessageSpec
        {
            Subject = "Html only",
            Body = "",
            Html = "<p>Visible <b>text</b></p><script>steal()</script><img src='http://tracker.example/pixel.gif'>",
            Date = new DateTimeOffset(2024, 1, 5, 0, 0, 0, TimeSpan.Zero),
        });
        await SyncAsync();

        var plain = _viewer.Load(Assert.Single(Find("plain").Items))!;
        var html = _viewer.Load(Assert.Single(Find("html only").Items))!;

        Assert.Contains("Hello there", plain.Body);
        Assert.Contains("ann@example.com", plain.From);
        Assert.Equal("note.txt", Assert.Single(plain.Attachments).FileName);
        Assert.True(plain.Attachments[0].IsSaved);
        Assert.True(File.Exists(plain.EmlPath));
        Assert.Null(plain.Problem);

        Assert.True(html.BodyWasHtml);
        Assert.Contains("Visible", html.Body);
        Assert.DoesNotContain("steal", html.Body);
        Assert.DoesNotContain("tracker", html.Body);
        Assert.DoesNotContain("<", html.Body);
    }

    [Fact]
    public async Task The_viewer_reports_a_missing_file_instead_of_failing()
    {
        await DeliverAsync("Will lose file", "body");
        await SyncAsync();
        var item = Assert.Single(Find("lose").Items);
        var detail = _viewer.Load(item)!;
        File.Delete(detail.EmlPath);

        var again = _viewer.Load(item)!;

        Assert.NotNull(again.Problem);
        Assert.Contains("Verify", again.Problem);
    }
}
