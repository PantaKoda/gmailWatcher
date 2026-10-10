using System.Text;
using GmailWatcher.Archive.Demo;
using GmailWatcher.Archive.Ingest;
using GmailWatcher.Archive.Storage;
using GmailWatcher.Archive.Sync;
using GmailWatcher.Core.Mail;
using GmailWatcher.Core.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace GmailWatcher.Archive.Tests;

/// <summary>
/// Older mail programs, and many Greek senders, still label mail iso-8859-7 or windows-1253. .NET does not know those code
/// pages unless the app registers them, and without them the text comes out as Latin letters. The messages here are plain
/// ASCII on the wire (quoted-printable / base64), so the test itself never needs the code pages.
/// </summary>
public sealed class LegacyCharsetTests : IDisposable
{
    // "Καλημέρα κόσμε" and "Γιάννης" as iso-8859-7 / windows-1253 bytes (identical for these letters).
    private static readonly byte[] SubjectBytes = [0xCA, 0xE1, 0xEB, 0xE7, 0xEC, 0xDD, 0xF1, 0xE1];
    private static readonly byte[] SenderBytes = [0xC3, 0xE9, 0xDC, 0xED, 0xED, 0xE7, 0xF2];
    private const string BodyQuotedPrintable = "=CA=E1=EB=E7=EC=DD=F1=E1 =EA=FC=F3=EC=E5";

    private readonly ArchiveFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static RemoteMessage Greek(string charset, string id = "18c0000000000a1")
    {
        var raw = Encoding.ASCII.GetBytes(
            $"From: =?{charset}?B?{Convert.ToBase64String(SenderBytes)}?= <giannis@example.com>\r\n" +
            "To: me@example.com\r\n" +
            $"Subject: =?{charset}?B?{Convert.ToBase64String(SubjectBytes)}?=\r\n" +
            "Date: Tue, 14 May 2024 09:30:00 +0000\r\n" +
            $"Message-ID: <greek-{charset}@example.com>\r\n" +
            "MIME-Version: 1.0\r\n" +
            $"Content-Type: text/plain; charset={charset}\r\n" +
            "Content-Transfer-Encoding: quoted-printable\r\n" +
            "\r\n" +
            BodyQuotedPrintable + "\r\n");
        var date = new DateTimeOffset(2024, 5, 14, 9, 30, 0, TimeSpan.Zero);
        return new RemoteMessage(id, id, ["INBOX"], "1001", date.ToUnixTimeMilliseconds(), raw.Length, "snippet", raw);
    }

    [Theory]
    [InlineData("iso-8859-7")]
    [InlineData("windows-1253")]
    public void Greek_in_a_legacy_charset_is_read_as_greek(string charset)
    {
        var account = _fixture.AddAccount();

        var ingested = _fixture.Ingestor.Ingest(account.Folder, Greek(charset), TestContext.Current.CancellationToken);

        Assert.Equal("Καλημέρα", ingested.Subject);
        Assert.Equal("Γιάννης", ingested.From?.Name);
        Assert.Contains("κόσμε", ingested.BodyText, StringComparison.Ordinal);
        Assert.Null(ingested.ParseError);
    }

    [Theory]
    [InlineData("iso-8859-7")]
    [InlineData("windows-1253")]
    public void The_preview_of_a_legacy_charset_message_is_greek_too(string charset)
    {
        var account = _fixture.AddAccount();
        _fixture.Messages.InsertBatch(account.Id, [_fixture.Ingestor.Ingest(account.Folder, Greek(charset), TestContext.Current.CancellationToken)]);
        var summary = new SearchService(_fixture.Database).Search(new SearchQuery("", account.Id, null, false, 0, 10)).Items.Single();

        var detail = new MessageViewer(_fixture.Database, _fixture.Layout, NullLogger<MessageViewer>.Instance).Load(summary)!;

        Assert.Equal("Καλημέρα κόσμε", detail.Body);
        Assert.Contains("Γιάννης", detail.From, StringComparison.Ordinal);
    }
}

/// <summary>
/// Mail downloaded before the code pages were registered has gibberish subjects, sender names and search entries in the
/// database (the saved .eml files are fine). A one-time refresh re-reads them.
/// </summary>
public sealed class TextRefreshTests : IDisposable
{
    private const string Garbled = "ÊáëçìÝñá";
    private readonly ArchiveFixture _fixture = new();
    private readonly SearchService _search;
    private readonly TextRefresher _refresher;

    public TextRefreshTests()
    {
        _search = new SearchService(_fixture.Database);
        _refresher = new TextRefresher(_fixture.Messages, _fixture.Ingestor, _fixture.Layout, NullLogger<TextRefresher>.Instance);
    }

    public void Dispose() => _fixture.Dispose();

    private long Greek(Core.Accounts.AccountRecord account, string charset = "iso-8859-7")
    {
        var raw = Encoding.ASCII.GetBytes(
            $"From: =?{charset}?B?{Convert.ToBase64String([0xC3, 0xE9, 0xDC, 0xED, 0xED, 0xE7, 0xF2])}?= <giannis@example.com>\r\n" +
            "To: me@example.com\r\n" +
            $"Subject: =?{charset}?B?{Convert.ToBase64String([0xCA, 0xE1, 0xEB, 0xE7, 0xEC, 0xDD, 0xF1, 0xE1])}?=\r\n" +
            "Date: Tue, 14 May 2024 09:30:00 +0000\r\nMessage-ID: <g@example.com>\r\nMIME-Version: 1.0\r\n" +
            $"Content-Type: text/plain; charset={charset}\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\n" +
            "=CA=E1=EB=E7=EC=DD=F1=E1 =EA=FC=F3=EC=E5\r\n");
        var date = new DateTimeOffset(2024, 5, 14, 9, 30, 0, TimeSpan.Zero);
        var message = new RemoteMessage("18c0000000000b1", "18c0000000000b1", ["INBOX"], "1001", date.ToUnixTimeMilliseconds(), raw.Length, "snippet", raw);
        _fixture.Messages.InsertBatch(account.Id, [_fixture.Ingestor.Ingest(account.Folder, message, TestContext.Current.CancellationToken)]);
        return Execute<long>("SELECT pk FROM messages LIMIT 1");
    }

    /// <summary>Puts the database back into the state the first release left it in for such a message.</summary>
    private void DamageAsTheFirstReleaseDid(long pk)
    {
        Execute($"UPDATE messages SET subject = '{Garbled}', from_name = 'Ãéüííçò' WHERE pk = {pk}");
        Execute($"DELETE FROM message_fts WHERE rowid = {pk}");
        Execute($"INSERT INTO message_fts (rowid, subject, sender, recipients, attachments, body) VALUES ({pk}, '{Garbled}', 'Ãéüííçò', '', '', 'êüóìå')");
    }

    private T Execute<T>(string sql)
    {
        using var connection = _fixture.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private void Execute(string sql)
    {
        using var connection = _fixture.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private SearchPage Find(int accountId, string text) => _search.Search(new SearchQuery(text, accountId, null, false, 0, 10));

    [Fact]
    public void Gibberish_saved_by_the_first_release_is_repaired_once_and_found_by_search_again()
    {
        var account = _fixture.AddAccount();
        var pk = Greek(account);
        DamageAsTheFirstReleaseDid(pk);
        var revisionBefore = Execute<long>($"SELECT rev FROM messages WHERE pk = {pk}");
        Assert.Equal(0, Find(account.Id, "κόσμε").Total);

        var changed = _refresher.RefreshIfNeeded(account, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, changed);
        var hit = Assert.Single(Find(account.Id, "κόσμε").Items);
        Assert.Equal("Καλημέρα", hit.Subject);
        Assert.Equal("Γιάννης", hit.FromName);
        Assert.Equal(1, Find(account.Id, "from:Γιάννης").Total);
        Assert.True(Execute<long>($"SELECT rev FROM messages WHERE pk = {pk}") > revisionBefore, "the analytics copy must notice the change");
        Assert.Equal("Γιάννης", Execute<string>($"SELECT name FROM message_addresses WHERE message_pk = {pk} AND role = 'from'"));
        Assert.Equal(TextRefresher.CurrentVersion, _fixture.Messages.GetTextVersion(account.Id));

        // Done is done: damaged again, it is left alone (and cheap).
        DamageAsTheFirstReleaseDid(pk);
        Assert.Equal(0, _refresher.RefreshIfNeeded(account, null, TestContext.Current.CancellationToken));
        Assert.Equal(Garbled, Execute<string>($"SELECT subject FROM messages WHERE pk = {pk}"));
    }

    [Fact]
    public void Messages_that_were_already_right_are_not_touched_but_still_indexed()
    {
        var account = _fixture.AddAccount();
        var pk = Greek(account);
        var revisionBefore = Execute<long>($"SELECT rev FROM messages WHERE pk = {pk}");

        Assert.Equal(0, _refresher.RefreshIfNeeded(account, null, TestContext.Current.CancellationToken));

        Assert.Equal(revisionBefore, Execute<long>($"SELECT rev FROM messages WHERE pk = {pk}"));
        Assert.Equal(1, Find(account.Id, "κόσμε").Total);
    }

    [Fact]
    public void A_missing_file_does_not_stop_the_refresh_of_the_others()
    {
        var account = _fixture.AddAccount();
        var pk = Greek(account);
        DamageAsTheFirstReleaseDid(pk);
        File.Delete(Path.Combine(_fixture.Root, "accounts", account.Folder, "mail", "2024", "05", "18c0000000000b1.eml"));

        Assert.Equal(0, _refresher.RefreshIfNeeded(account, null, TestContext.Current.CancellationToken));

        Assert.Equal(TextRefresher.CurrentVersion, _fixture.Messages.GetTextVersion(account.Id));
        Assert.Equal(Garbled, Execute<string>($"SELECT subject FROM messages WHERE pk = {pk}"));
    }

    [Fact]
    public async Task A_sync_refreshes_earlier_downloads_first_and_reports_it()
    {
        var account = _fixture.AddAccount();
        DamageAsTheFirstReleaseDid(Greek(account));
        var engine = new SyncEngine(_fixture.Accounts, _fixture.Messages, _fixture.Ingestor, _fixture.Layout, _fixture.Options, NullLogger<SyncEngine>.Instance, null, _refresher);
        var seen = new Collected();

        await engine.RunAsync(account, new InMemoryMailbox(account.Email), seen, TestContext.Current.CancellationToken);

        Assert.Contains(seen.Items, p => p is { Phase: SyncPhase.Indexing, Total: 1 });
        Assert.Equal("Καλημέρα", Assert.Single(Find(account.Id, "κόσμε").Items).Subject);
    }

    private sealed class Collected : IProgress<SyncProgress>
    {
        public System.Collections.Concurrent.ConcurrentQueue<SyncProgress> Items { get; } = new();

        public void Report(SyncProgress value) => Items.Enqueue(value);
    }
}
