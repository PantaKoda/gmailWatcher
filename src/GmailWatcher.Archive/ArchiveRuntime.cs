using GmailWatcher.Archive.Analytics;
using GmailWatcher.Archive.Ingest;
using GmailWatcher.Archive.Storage;
using GmailWatcher.Archive.Sync;
using GmailWatcher.Archive.Verification;
using GmailWatcher.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace GmailWatcher.Archive;

/// <summary>
/// Everything that works on one archive folder, wired together: database, files, sync, verification, analytics, search.
/// The app creates one when the archive folder is known; tests create them in temporary folders.
/// </summary>
public sealed class ArchiveRuntime
{
    private ArchiveRuntime(string root, SyncOptions sync, ILoggerFactory loggers, TimeProvider time)
    {
        Layout = new ArchiveLayout(root);
        Directory.CreateDirectory(Layout.Root);
        Database = new ArchiveDatabase(Layout.DatabaseFile);
        try
        {
            Database.Initialize();
        }
        catch
        {
            Database.ReleaseConnections(); // a database this version cannot use must not stay locked by the pool
            throw;
        }

        Accounts = new AccountStore(Database, time);
        Messages = new MessageStore(Database, time);
        Mirror = new AnalyticsMirror(Database, Accounts, Layout.AnalyticsFile, loggers.CreateLogger<AnalyticsMirror>());
        Ingestor = new MessageIngestor(Layout, new IngestOptions(sync.ExtractAttachments), loggers.CreateLogger<MessageIngestor>());
        Engine = new SyncEngine(Accounts, Messages, Ingestor, Layout, sync, loggers.CreateLogger<SyncEngine>(), Mirror);
        Verifier = new Verifier(Accounts, Messages, Layout, time, loggers.CreateLogger<Verifier>());
        Maintenance = new ArchiveMaintenance(Accounts, Messages, Layout, loggers.CreateLogger<ArchiveMaintenance>(), Mirror);
        Insights = new InsightsService(Mirror, Accounts);
        Search = new SearchService(Database);
        Viewer = new MessageViewer(Database, Layout, loggers.CreateLogger<MessageViewer>());
    }

    public ArchiveLayout Layout { get; }

    public ArchiveDatabase Database { get; }

    public AccountStore Accounts { get; }

    public MessageStore Messages { get; }

    public MessageIngestor Ingestor { get; }

    public SyncEngine Engine { get; }

    public Verifier Verifier { get; }

    public ArchiveMaintenance Maintenance { get; }

    public AnalyticsMirror Mirror { get; }

    public InsightsService Insights { get; }

    public SearchService Search { get; }

    public MessageViewer Viewer { get; }

    /// <summary>Opens (creating if needed) the archive in <paramref name="root"/>.</summary>
    /// <exception cref="ArchiveVersionException">The archive was written by a newer version of the app.</exception>
    public static ArchiveRuntime Open(string root, SyncOptions sync, ILoggerFactory loggers, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(loggers);
        return new ArchiveRuntime(root, sync, loggers, time ?? TimeProvider.System);
    }
}
