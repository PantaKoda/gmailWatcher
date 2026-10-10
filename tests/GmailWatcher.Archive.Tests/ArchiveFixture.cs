using GmailWatcher.Archive.Demo;
using GmailWatcher.Archive.Ingest;
using GmailWatcher.Archive.Storage;
using GmailWatcher.Archive.Sync;
using GmailWatcher.Core.Accounts;
using GmailWatcher.Core.Configuration;
using GmailWatcher.Core.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace GmailWatcher.Archive.Tests;

/// <summary>The whole archive stack in a temporary folder, deleted afterwards.</summary>
public sealed class ArchiveFixture : IDisposable
{
    public ArchiveFixture(SyncOptions? options = null, IngestOptions? ingest = null, IAnalyticsSink? analytics = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "gmailwatcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Layout = new ArchiveLayout(Root);
        Database = new ArchiveDatabase(Layout.DatabaseFile);
        Database.Initialize();
        Time = TimeProvider.System;
        Accounts = new AccountStore(Database, Time);
        Messages = new MessageStore(Database, Time);
        Ingestor = new MessageIngestor(Layout, ingest ?? new IngestOptions(), NullLogger<MessageIngestor>.Instance);
        Options = options ?? new SyncOptions { Workers = 4 };
        Engine = new SyncEngine(Accounts, Messages, Ingestor, Layout, Options, NullLogger<SyncEngine>.Instance, analytics);
    }

    public string Root { get; }

    public ArchiveLayout Layout { get; }

    public ArchiveDatabase Database { get; }

    public TimeProvider Time { get; }

    public AccountStore Accounts { get; }

    public MessageStore Messages { get; }

    public MessageIngestor Ingestor { get; }

    public SyncOptions Options { get; }

    public SyncEngine Engine { get; }

    public AccountRecord AddAccount(string email = "me@example.com") => Accounts.Add(email);

    /// <summary>Syncs with the current database state of the account (as the app does before every sync).</summary>
    public Task<SyncResult> SyncAsync(string email, InMemoryMailbox mailbox, IProgress<SyncProgress>? progress, CancellationToken cancellationToken) =>
        Engine.RunAsync(Accounts.Find(email) ?? throw new InvalidOperationException("No such account."), mailbox, progress, cancellationToken);

    public string EmlPath(AccountRecord account, string messageId, long internalDateMs) =>
        Layout.Resolve(account.Folder, ArchiveLayout.EmlRelativePath(messageId, internalDateMs));

    public void Dispose()
    {
        Database.ReleaseConnections();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(Root, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
