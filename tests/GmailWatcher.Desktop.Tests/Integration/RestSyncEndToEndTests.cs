using GmailWatcher.Archive;
using GmailWatcher.Archive.Demo;
using GmailWatcher.Core.Accounts;
using GmailWatcher.Core.Configuration;
using GmailWatcher.Core.Mail;
using GmailWatcher.Core.Sync;
using GmailWatcher.Core.Verification;
using GmailWatcher.Google;
using GmailWatcher.Google.Auth;
using GmailWatcher.Google.Gmail;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace GmailWatcher.Desktop.Tests.Integration;

/// <summary>
/// The real Gmail REST client and the real sync engine, joined over HTTP. Everything the product does between "ask Google"
/// and "bytes on disk" runs here; only Google itself is replaced by <see cref="FakeGmailHandler"/>.
/// </summary>
public sealed class RestSyncEndToEndTests : IDisposable
{
    private const string Email = "me@example.com";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gmailwatcher-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMailbox _gmail = new(Email);
    private readonly FakeGmailHandler _handler;
    private readonly ArchiveRuntime _archive;
    private readonly IMailbox _rest;
    private int _counter;

    public RestSyncEndToEndTests()
    {
        _handler = new FakeGmailHandler(_gmail, pageSize: 40); // small pages: every listing and history read must page
        _archive = ArchiveRuntime.Open(_root, new SyncOptions { Workers = 4 }, NullLoggerFactory.Instance);
        _archive.Accounts.Add(Email);

        var options = new GoogleOptions { ClientId = "123456789-abcdef.apps.googleusercontent.com" };
        var http = new HttpClient(_handler);
        var oauth = new GoogleOAuthClient(http, options, TimeProvider.System);
        var tokens = new AccessTokenProvider(Email, new NoCredentials(), oauth, TimeProvider.System);
        tokens.Prime(new TokenSet(FakeGmailHandler.Token, DateTimeOffset.UtcNow.AddHours(1), "refresh", GoogleOAuthClient.GmailReadOnlyScope));
        _rest = new GmailRestMailbox(http, options, tokens, new QuotaGovernor(600_000, TimeProvider.System), TimeProvider.System, NullLogger<GmailRestMailbox>.Instance);
    }

    public void Dispose()
    {
        _archive.Database.ReleaseConnections();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private AccountRecord Account => _archive.Accounts.Find(Email)!;

    private string Deliver(string[]? labels = null, params SyntheticAttachment[] attachments) =>
        _gmail.Add(new MessageSpec
        {
            Subject = $"Message {++_counter} café",
            Body = $"Body {_counter}",
            Date = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddHours(_counter),
            Labels = labels ?? ["INBOX"],
            Attachments = attachments,
        });

    private Task<SyncResult> SyncAsync() => _archive.Engine.RunAsync(Account, _rest, null, TestContext.Current.CancellationToken);

    private Task<VerificationReport> VerifyAsync() => _archive.Verifier.VerifyAsync(Account, _rest, null, TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_first_sync_over_http_archives_every_message_page_by_page_with_exact_bytes_and_labels()
    {
        _gmail.AddLabel("Label_7", "Receipts");
        var blob = new byte[70_000];
        new Random(5).NextBytes(blob); // every byte value, so base64url padding and characters are exercised
        for (var i = 0; i < 130; i++)
        {
            Deliver(i % 5 == 0 ? ["INBOX", "Label_7"] : i % 7 == 0 ? ["SPAM"] : ["INBOX", "UNREAD"],
                i % 20 == 0 ? [new SyntheticAttachment("data.bin", "application/octet-stream", blob)] : []);
        }

        var result = await SyncAsync();

        Assert.Equal(SyncOutcome.Completed, result.Outcome);
        Assert.Equal(130, result.MessagesAdded);
        Assert.Equal(_gmail.Ids.ToHashSet(), _archive.Messages.GetIds(Account.Id));
        Assert.True(_handler.Requests.Count(r => r.Contains("/users/me/messages?", StringComparison.Ordinal)) >= 4, "the listing must have been paged");
        Assert.Contains("Label_7", _archive.Accounts.GetLabels(Account.Id).Select(l => l.Id));
        Assert.Equal(26, _archive.Messages.GetIdsWithLabel(Account.Id, "Label_7").Count);

        // The bytes on disk are exactly what "Gmail" held.
        foreach (var page in _archive.Messages.EnumerateFiles(Account.Id))
        {
            foreach (var file in page)
            {
                var remote = await _gmail.GetMessageAsync(file.GmailId, TestContext.Current.CancellationToken);
                Assert.Equal(remote!.Raw, await File.ReadAllBytesAsync(_archive.Layout.Resolve(Account.Folder, file.EmlPath), TestContext.Current.CancellationToken));
            }
        }

        var report = await VerifyAsync();
        Assert.Equal(VerificationVerdict.Verified, report.Verdict);
        Assert.Equal(130, report.GmailMessages);
    }

    [Fact]
    public async Task Later_syncs_read_history_over_http_including_label_changes_deletions_and_paging()
    {
        for (var i = 0; i < 20; i++)
        {
            Deliver();
        }

        await SyncAsync();
        var getsBefore = _handler.MessageGets;
        var starred = _gmail.Ids[3];
        var gone = _gmail.Ids[5];
        _gmail.ChangeLabels(starred, add: ["STARRED"], remove: ["INBOX"]);
        _gmail.Delete(gone);
        for (var i = 0; i < 100; i++)
        {
            Deliver(); // more than two history pages
        }

        var result = await SyncAsync();

        Assert.True(result.Outcome == SyncOutcome.Completed, result.Error);
        Assert.True(result.UsedHistory);
        Assert.Equal(100, result.MessagesAdded);
        Assert.Equal(100, _handler.MessageGets - getsBefore); // only the new mail was downloaded
        Assert.Equal(1, result.MarkedRemoteDeleted);
        Assert.Contains(starred, _archive.Messages.GetIdsWithLabel(Account.Id, "STARRED"));
        Assert.DoesNotContain(starred, _archive.Messages.GetIdsWithLabel(Account.Id, "INBOX"));
        Assert.Contains(gone, _archive.Messages.GetIds(Account.Id)); // deleted in Gmail, kept here
        Assert.Equal(120, _archive.Messages.Count(Account.Id));
    }

    [Fact]
    public async Task When_gmail_forgets_the_history_the_404_leads_to_a_full_pass_that_loses_nothing()
    {
        for (var i = 0; i < 15; i++)
        {
            Deliver();
        }

        await SyncAsync();
        Deliver();
        _gmail.ExpireHistory();

        var result = await SyncAsync();

        Assert.Equal(SyncOutcome.Completed, result.Outcome);
        Assert.False(result.UsedHistory);
        Assert.Equal(1, result.MessagesAdded);
        Assert.Equal(16, _archive.Messages.Count(Account.Id));
    }

    [Fact]
    public async Task Emptying_gmail_leaves_the_archive_whole_and_it_still_verifies()
    {
        for (var i = 0; i < 60; i++)
        {
            Deliver(i % 3 == 0 ? ["TRASH"] : ["INBOX"]);
        }

        await SyncAsync();
        _gmail.DeleteAll();

        var incremental = await SyncAsync();
        _gmail.ExpireHistory();
        var full = await SyncAsync(); // the listing is now empty: Gmail omits the "messages" array
        var report = await VerifyAsync();

        Assert.Equal(SyncOutcome.Completed, incremental.Outcome);
        Assert.Equal(SyncOutcome.Completed, full.Outcome);
        Assert.Equal(60, _archive.Messages.Count(Account.Id));
        Assert.Equal(60, _archive.Messages.GetStats(Account.Id).RemoteDeleted);
        Assert.Equal(VerificationVerdict.Verified, report.Verdict);
        Assert.Equal(0, report.GmailMessages);
    }

    [Fact]
    public async Task Mailbox_settings_arrive_and_a_missing_setting_is_simply_left_out()
    {
        Deliver();

        await SyncAsync();

        Assert.Equal(["filters", "sendAs"], _archive.Accounts.LoadSettings(Account.Id).Select(d => d.Name)); // imap, pop, vacation... returned 404 and were skipped
    }

    [Fact]
    public async Task A_rejected_token_means_reconnect_and_leaves_the_archive_alone()
    {
        Deliver();
        await SyncAsync();
        var options = new GoogleOptions { ClientId = "123456789-abcdef.apps.googleusercontent.com" };
        var http = new HttpClient(_handler);
        var tokens = new AccessTokenProvider(Email, new NoCredentials(), new GoogleOAuthClient(http, options, TimeProvider.System), TimeProvider.System);
        tokens.Prime(new TokenSet("wrong-token", DateTimeOffset.UtcNow.AddHours(1), null, GoogleOAuthClient.GmailReadOnlyScope));
        var rejected = new GmailRestMailbox(http, options, tokens, new QuotaGovernor(600_000, TimeProvider.System), TimeProvider.System, NullLogger<GmailRestMailbox>.Instance);

        var result = await _archive.Engine.RunAsync(Account, rejected, null, TestContext.Current.CancellationToken);

        Assert.Equal(SyncOutcome.ReconnectRequired, result.Outcome);
        Assert.Equal(AccountConnection.ReconnectRequired, Account.Connection);
        Assert.Equal(1, _archive.Messages.Count(Account.Id));
    }
}

/// <summary>A credential store with nothing in it: these tests give the token provider an access token directly.</summary>
internal sealed class NoCredentials : ICredentialStore
{
    public bool IsPersistent => false;

    public string Description => "nothing";

    public Task<StoredCredential?> ReadAsync(string accountEmail, CancellationToken cancellationToken = default) => Task.FromResult<StoredCredential?>(null);

    public Task WriteAsync(string accountEmail, StoredCredential credential, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task DeleteAsync(string accountEmail, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
