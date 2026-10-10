using System.Globalization;
using DuckDB.NET.Data;
using GmailWatcher.Archive.Storage;
using GmailWatcher.Archive.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace GmailWatcher.Archive.Analytics;

/// <summary>
/// Keeps <c>analytics.duckdb</c> in step with the SQLite archive, for fast analytical queries.
/// <list type="bullet">
/// <item><b>Derived, never authoritative.</b> SQLite is the record. If the DuckDB file is missing, damaged or from another
/// schema it is simply rebuilt from SQLite. Nothing is ever written back.</item>
/// <item><b>Incremental.</b> Every archived message carries a revision number that rises with each change (new message,
/// label change, "no longer in Gmail"). An update copies only rows newer than the last revision it has seen.</item>
/// <item><b>Open only while working.</b> DuckDB locks its file exclusively, so the connection is opened for each
/// operation and released: you can open the file in the DuckDB CLI, Python or DBeaver whenever the app is idle.
/// If another program has it open, an update is skipped (and retried next time) rather than failing a sync.</item>
/// </list>
/// </summary>
public sealed class AnalyticsMirror(ArchiveDatabase sqlite, AccountStore accounts, string path, ILogger<AnalyticsMirror> logger) : IAnalyticsSink
{
    /// <summary>Bump together with any change to the DuckDB tables: the file is then rebuilt from SQLite.</summary>
    public const string SchemaVersion = "1";

    private static readonly string[] Tables = ["messages", "message_labels", "attachments", "recipients", "mirror_state", "accounts"];
    private readonly Lock _gate = new();

    public string Path { get; } = path;

    /// <summary>Creates the file and tables if needed, so it exists (and can be opened externally) before the first sync.</summary>
    public void EnsureCreated()
    {
        lock (_gate)
        {
            using var connection = OpenReadWrite();
        }
    }

    /// <summary>Copies everything that changed in an account since the last update.</summary>
    public void Update(int accountId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var account = accounts.Get(accountId);
            if (account is null)
            {
                return;
            }

            try
            {
                using var connection = OpenReadWrite();
                UpdateCore(connection, account.Id, account.Email, cancellationToken);
            }
            catch (DuckDBException ex) when (IsLockedByAnotherProcess(ex))
            {
                logger.LogInformation("analytics.duckdb is open in another program; it will catch up at the next update");
            }
        }
    }

    /// <summary>Forgets what was copied for an account and copies everything again (all accounts when null).</summary>
    public void Rebuild(int? accountId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            using var connection = OpenReadWrite();
            foreach (var account in accounts.List().Where(a => accountId is null || a.Id == accountId))
            {
                Execute(connection, $"DELETE FROM mirror_state WHERE account_id = {account.Id}");
                UpdateCore(connection, account.Id, account.Email, cancellationToken);
            }
        }
    }

    public void Remove(int accountId)
    {
        lock (_gate)
        {
            try
            {
                using var connection = OpenReadWrite();
                Execute(connection, "BEGIN TRANSACTION");
                foreach (var table in Tables)
                {
                    Execute(connection, $"DELETE FROM {table} WHERE account_id = {accountId}");
                }

                Execute(connection, "COMMIT");
            }
            catch (DuckDBException ex) when (IsLockedByAnotherProcess(ex))
            {
                logger.LogInformation("analytics.duckdb is open in another program; an account's rows stay until the next rebuild");
            }
        }
    }

    /// <summary>Runs a read-only query function against the analytics file (serialized with updates). Null when the file does not exist yet.</summary>
    internal T? Read<T>(Func<DuckDBConnection, T> query)
    {
        lock (_gate)
        {
            if (!File.Exists(Path))
            {
                return default;
            }

            using var connection = new DuckDBConnection($"Data Source={Path};access_mode=READ_ONLY");
            connection.Open();
            return query(connection);
        }
    }

    private void UpdateCore(DuckDBConnection duck, int accountId, string email, CancellationToken cancellationToken)
    {
        var last = ScalarLong(duck, $"SELECT COALESCE(MAX(rev), 0) FROM mirror_state WHERE account_id = {accountId}");

        using var source = sqlite.Open();
        using var snapshot = source.BeginTransaction(deferred: true); // one consistent view for all the reads below
        var ceiling = source.Scalar<long?>("SELECT MAX(rev) FROM messages WHERE account_id = @a", snapshot, ("@a", accountId)) ?? 0;
        Execute(duck, "BEGIN TRANSACTION");
        try
        {
            Execute(duck, $"DELETE FROM accounts WHERE account_id = {accountId}");
            using (var command = duck.CreateCommand())
            {
                command.CommandText = "INSERT INTO accounts VALUES (?, ?)";
                command.Parameters.Add(new DuckDBParameter(accountId));
                command.Parameters.Add(new DuckDBParameter(email));
                command.ExecuteNonQuery();
            }

            if (ceiling > last)
            {
                var range = (("@a", (object?)accountId), ("@from", (object?)last), ("@to", (object?)ceiling));
                ReplaceChanged(duck, source, snapshot, accountId, range, cancellationToken);
                Execute(duck, $"DELETE FROM mirror_state WHERE account_id = {accountId}");
                Execute(duck, string.Create(CultureInfo.InvariantCulture, $"INSERT INTO mirror_state VALUES ({accountId}, {ceiling})"));
            }

            Execute(duck, "COMMIT");
        }
        catch
        {
            TryExecute(duck, "ROLLBACK");
            throw;
        }
    }

    private void ReplaceChanged(DuckDBConnection duck, SqliteConnection source, SqliteTransaction snapshot, int accountId,
        ((string, object?), (string, object?), (string, object?)) range, CancellationToken cancellationToken)
    {
        (string, object?)[] parameters = [range.Item1, range.Item2, range.Item3];
        const string Changed = "m.account_id = @a AND m.rev > @from AND m.rev <= @to";

        // 1. Drop the old copies of every changed message (a changed message is replaced, never patched).
        Execute(duck, "CREATE OR REPLACE TEMP TABLE changed_ids (gmail_id VARCHAR)");
        using (var appender = duck.CreateAppender("changed_ids"))
        using (var command = source.Command($"SELECT m.gmail_id FROM messages m WHERE {Changed}", snapshot, parameters))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                appender.CreateRow().AppendValue(reader.GetString(0)).EndRow();
            }
        }

        foreach (var table in new[] { "messages", "message_labels", "attachments", "recipients" })
        {
            Execute(duck, $"DELETE FROM {table} WHERE account_id = {accountId} AND gmail_id IN (SELECT gmail_id FROM changed_ids)");
        }

        Execute(duck, "DROP TABLE changed_ids");
        cancellationToken.ThrowIfCancellationRequested();

        // 2. Copy the current rows.
        using (var appender = duck.CreateAppender("messages"))
        using (var command = source.Command(
            $"""
            SELECT m.gmail_id, m.thread_id, m.internal_date, m.subject, m.from_address, m.from_name, m.size_bytes, m.attachment_count, m.attachment_bytes,
                   EXISTS (SELECT 1 FROM message_labels l WHERE l.message_pk = m.pk AND l.label_id = 'UNREAD'),
                   EXISTS (SELECT 1 FROM message_labels l WHERE l.message_pk = m.pk AND l.label_id = 'STARRED'),
                   EXISTS (SELECT 1 FROM message_labels l WHERE l.message_pk = m.pk AND l.label_id = 'SENT'),
                   EXISTS (SELECT 1 FROM message_labels l WHERE l.message_pk = m.pk AND l.label_id = 'INBOX'),
                   EXISTS (SELECT 1 FROM message_labels l WHERE l.message_pk = m.pk AND l.label_id = 'TRASH'),
                   EXISTS (SELECT 1 FROM message_labels l WHERE l.message_pk = m.pk AND l.label_id = 'SPAM'),
                   m.remote_deleted_at IS NOT NULL
            FROM messages m WHERE {Changed}
            """, snapshot, parameters))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var address = reader.GetNullableString(4) ?? "";
                appender.CreateRow()
                    .AppendValue(accountId)
                    .AppendValue(reader.GetString(0))
                    .AppendValue(reader.GetString(1))
                    .AppendValue(Utc(reader.GetInt64(2)))
                    .AppendValue(reader.GetNullableString(3) ?? "")
                    .AppendValue(address.ToLowerInvariant())
                    .AppendValue(reader.GetNullableString(5) ?? "")
                    .AppendValue(DomainOf(address))
                    .AppendValue(reader.GetInt64(6))
                    .AppendValue(reader.GetInt32(7))
                    .AppendValue(reader.GetInt64(8))
                    .AppendValue(reader.GetBoolean(9))
                    .AppendValue(reader.GetBoolean(10))
                    .AppendValue(reader.GetBoolean(11))
                    .AppendValue(reader.GetBoolean(12))
                    .AppendValue(reader.GetBoolean(13))
                    .AppendValue(reader.GetBoolean(14))
                    .AppendValue(reader.GetBoolean(15))
                    .EndRow();
            }
        }

        using (var appender = duck.CreateAppender("message_labels"))
        using (var command = source.Command(
            $"""
            SELECT m.gmail_id, ml.label_id, COALESCE(lb.name, ml.label_id)
            FROM messages m
            JOIN message_labels ml ON ml.message_pk = m.pk
            LEFT JOIN labels lb ON lb.account_id = m.account_id AND lb.label_id = ml.label_id
            WHERE {Changed}
            """, snapshot, parameters))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                appender.CreateRow().AppendValue(accountId).AppendValue(reader.GetString(0)).AppendValue(reader.GetString(1)).AppendValue(reader.GetString(2)).EndRow();
            }
        }

        using (var appender = duck.CreateAppender("attachments"))
        using (var command = source.Command(
            $"""
            SELECT m.gmail_id, m.internal_date, a.file_name, a.content_type, a.size_bytes, a.sha256, a.is_inline
            FROM messages m JOIN attachments a ON a.message_pk = m.pk
            WHERE {Changed}
            """, snapshot, parameters))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var fileName = reader.GetString(2);
                appender.CreateRow()
                    .AppendValue(accountId)
                    .AppendValue(reader.GetString(0))
                    .AppendValue(Utc(reader.GetInt64(1)))
                    .AppendValue(fileName)
                    .AppendValue(ExtensionOf(fileName))
                    .AppendValue(reader.GetString(3))
                    .AppendValue(reader.GetInt64(4))
                    .AppendValue(reader.GetString(5))
                    .AppendValue(reader.GetInt64(6) != 0)
                    .EndRow();
            }
        }

        using (var appender = duck.CreateAppender("recipients"))
        using (var command = source.Command(
            $"""
            SELECT m.gmail_id, ad.role, ad.address
            FROM messages m JOIN message_addresses ad ON ad.message_pk = m.pk
            WHERE {Changed} AND ad.role IN ('to', 'cc', 'bcc')
            """, snapshot, parameters))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var address = reader.GetString(2).ToLowerInvariant();
                appender.CreateRow().AppendValue(accountId).AppendValue(reader.GetString(0)).AppendValue(reader.GetString(1)).AppendValue(address).AppendValue(DomainOf(address)).EndRow();
            }
        }
    }

    private DuckDBConnection OpenReadWrite()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        DuckDBConnection connection;
        try
        {
            connection = Prepare();
        }
        catch (DuckDBException ex) when (!IsLockedByAnotherProcess(ex))
        {
            // Damaged or unreadable: it holds only derived data, so start over and let it be rebuilt from SQLite.
            logger.LogWarning(ex, "analytics.duckdb could not be opened; recreating it from the archive");
            MoveAside();
            connection = Prepare();
        }

        return connection;
    }

    private DuckDBConnection Prepare()
    {
        var connection = new DuckDBConnection($"Data Source={Path}");
        try
        {
            connection.Open();
            Execute(connection, "SET threads TO 4");
            Execute(connection, "SET memory_limit = '1GB'");
            Execute(connection, "CREATE TABLE IF NOT EXISTS mirror_meta (key VARCHAR PRIMARY KEY, value VARCHAR NOT NULL)");
            var version = ScalarString(connection, "SELECT value FROM mirror_meta WHERE key = 'schema'");
            if (version is not null && version != SchemaVersion)
            {
                logger.LogInformation("analytics.duckdb has schema {Found}; rebuilding for {Current}", version, SchemaVersion);
                foreach (var table in Tables)
                {
                    Execute(connection, $"DROP TABLE IF EXISTS {table}");
                }
            }

            CreateTables(connection);
            Execute(connection, "DELETE FROM mirror_meta WHERE key = 'schema'");
            Execute(connection, $"INSERT INTO mirror_meta VALUES ('schema', '{SchemaVersion}')");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void CreateTables(DuckDBConnection connection) => Execute(connection,
        """
        CREATE TABLE IF NOT EXISTS accounts (account_id INTEGER NOT NULL, email VARCHAR NOT NULL);
        CREATE TABLE IF NOT EXISTS messages (
            account_id INTEGER NOT NULL, gmail_id VARCHAR NOT NULL, thread_id VARCHAR NOT NULL, received TIMESTAMP NOT NULL,
            subject VARCHAR NOT NULL, from_address VARCHAR NOT NULL, from_name VARCHAR NOT NULL, from_domain VARCHAR NOT NULL,
            size_bytes BIGINT NOT NULL, attachment_count INTEGER NOT NULL, attachment_bytes BIGINT NOT NULL,
            is_unread BOOLEAN NOT NULL, is_starred BOOLEAN NOT NULL, is_sent BOOLEAN NOT NULL, is_inbox BOOLEAN NOT NULL,
            is_trash BOOLEAN NOT NULL, is_spam BOOLEAN NOT NULL, remote_deleted BOOLEAN NOT NULL);
        CREATE TABLE IF NOT EXISTS message_labels (account_id INTEGER NOT NULL, gmail_id VARCHAR NOT NULL, label_id VARCHAR NOT NULL, label_name VARCHAR NOT NULL);
        CREATE TABLE IF NOT EXISTS attachments (
            account_id INTEGER NOT NULL, gmail_id VARCHAR NOT NULL, received TIMESTAMP NOT NULL, file_name VARCHAR NOT NULL, extension VARCHAR NOT NULL,
            content_type VARCHAR NOT NULL, size_bytes BIGINT NOT NULL, sha256 VARCHAR NOT NULL, is_inline BOOLEAN NOT NULL);
        CREATE TABLE IF NOT EXISTS recipients (account_id INTEGER NOT NULL, gmail_id VARCHAR NOT NULL, role VARCHAR NOT NULL, address VARCHAR NOT NULL, domain VARCHAR NOT NULL);
        CREATE TABLE IF NOT EXISTS mirror_state (account_id INTEGER NOT NULL, rev BIGINT NOT NULL);
        """);

    private void MoveAside()
    {
        foreach (var file in new[] { Path, Path + ".wal" })
        {
            if (File.Exists(file))
            {
                File.Move(file, $"{file}.damaged-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
            }
        }
    }

    private static bool IsLockedByAnotherProcess(DuckDBException ex) =>
        ex.Message.Contains("lock", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("in use", StringComparison.OrdinalIgnoreCase);

    internal static DateTime Utc(long unixMilliseconds) =>
        DateTime.SpecifyKind(DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).UtcDateTime, DateTimeKind.Unspecified);

    internal static string DomainOf(string address)
    {
        var at = address.LastIndexOf('@');
        return at < 0 ? "" : address[(at + 1)..].ToLowerInvariant();
    }

    internal static string ExtensionOf(string fileName)
    {
        var extension = System.IO.Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return extension.Length is > 0 and <= 10 ? extension : "(none)";
    }

    internal static void Execute(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void TryExecute(DuckDBConnection connection, string sql)
    {
        try
        {
            Execute(connection, sql);
        }
        catch (DuckDBException)
        {
            // Nothing to roll back (the transaction never started, or already ended).
        }
    }

    internal static long ScalarLong(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static string? ScalarString(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }
}
