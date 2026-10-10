using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GmailWatcher.Archive.Storage;

/// <summary>
/// The archive's SQLite database: the authoritative record of every message, label and attachment, and the search
/// index. Never holds tokens or other secrets.
/// <para>
/// Schema migrations are ordered SQL scripts; <c>PRAGMA user_version</c> records how many have been applied. Each runs
/// in its own transaction. A database from a newer app version is never modified.
/// </para>
/// <para>
/// Durability: <c>synchronous = FULL</c> in WAL mode. A backup tool should not lose a committed message on power loss,
/// and the message files are flushed before the row that points at them is committed.
/// </para>
/// </summary>
public sealed class ArchiveDatabase
{
    private static readonly string[] Migrations =
    [
        // 1: the whole v1 schema.
        """
        CREATE TABLE meta (
            key   TEXT    NOT NULL PRIMARY KEY,
            value INTEGER NOT NULL
        );
        INSERT INTO meta (key, value) VALUES ('rev', 0);

        CREATE TABLE accounts (
            id                        INTEGER PRIMARY KEY AUTOINCREMENT,
            email                     TEXT    NOT NULL UNIQUE COLLATE NOCASE,
            display_name              TEXT,
            folder                    TEXT    NOT NULL UNIQUE COLLATE NOCASE,
            connection                TEXT    NOT NULL DEFAULT 'connected',
            added_at                  TEXT    NOT NULL,
            history_id                TEXT,
            initial_sync_completed_at TEXT,
            last_sync_started_at      TEXT,
            last_sync_completed_at    TEXT,
            last_sync_outcome         TEXT,
            last_error                TEXT,
            remote_messages_total     INTEGER,
            last_verified_at          TEXT,
            last_verification_verdict TEXT,
            verification_json         TEXT
        );

        CREATE TABLE labels (
            account_id       INTEGER NOT NULL REFERENCES accounts (id) ON DELETE CASCADE,
            label_id         TEXT    NOT NULL,
            name             TEXT    NOT NULL,
            kind             TEXT    NOT NULL,
            background_color TEXT,
            text_color       TEXT,
            PRIMARY KEY (account_id, label_id)
        ) WITHOUT ROWID;

        CREATE TABLE messages (
            pk                INTEGER PRIMARY KEY AUTOINCREMENT,
            account_id        INTEGER NOT NULL REFERENCES accounts (id) ON DELETE CASCADE,
            gmail_id          TEXT    NOT NULL,
            thread_id         TEXT    NOT NULL,
            internal_date     INTEGER NOT NULL,
            subject           TEXT,
            from_name         TEXT,
            from_address      TEXT,
            message_id_header TEXT,
            in_reply_to       TEXT,
            snippet           TEXT,
            size_bytes        INTEGER NOT NULL,
            eml_path          TEXT    NOT NULL,
            eml_sha256        TEXT    NOT NULL,
            attachment_count  INTEGER NOT NULL DEFAULT 0,
            attachment_bytes  INTEGER NOT NULL DEFAULT 0,
            gmail_history_id  TEXT,
            parse_error       TEXT,
            archived_at       TEXT    NOT NULL,
            remote_deleted_at TEXT,
            rev               INTEGER NOT NULL,
            UNIQUE (account_id, gmail_id)
        );
        CREATE INDEX messages_by_date   ON messages (account_id, internal_date DESC);
        CREATE INDEX messages_by_thread ON messages (account_id, thread_id);
        CREATE INDEX messages_by_rev    ON messages (account_id, rev);

        CREATE TABLE message_labels (
            message_pk INTEGER NOT NULL REFERENCES messages (pk) ON DELETE CASCADE,
            label_id   TEXT    NOT NULL,
            PRIMARY KEY (message_pk, label_id)
        ) WITHOUT ROWID;
        CREATE INDEX message_labels_by_label ON message_labels (label_id, message_pk);

        CREATE TABLE message_addresses (
            message_pk INTEGER NOT NULL REFERENCES messages (pk) ON DELETE CASCADE,
            role       TEXT    NOT NULL,
            position   INTEGER NOT NULL,
            name       TEXT,
            address    TEXT    NOT NULL,
            PRIMARY KEY (message_pk, role, position)
        ) WITHOUT ROWID;
        CREATE INDEX message_addresses_by_address ON message_addresses (address COLLATE NOCASE, role);

        CREATE TABLE attachments (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            message_pk   INTEGER NOT NULL REFERENCES messages (pk) ON DELETE CASCADE,
            part_index   INTEGER NOT NULL,
            file_name    TEXT    NOT NULL,
            content_type TEXT    NOT NULL,
            size_bytes   INTEGER NOT NULL,
            sha256       TEXT    NOT NULL,
            is_inline    INTEGER NOT NULL,
            content_id   TEXT,
            path         TEXT,
            UNIQUE (message_pk, part_index)
        );
        CREATE INDEX attachments_by_sha ON attachments (sha256);

        CREATE TABLE account_settings (
            account_id INTEGER NOT NULL REFERENCES accounts (id) ON DELETE CASCADE,
            name       TEXT    NOT NULL,
            json       TEXT    NOT NULL,
            saved_at   TEXT    NOT NULL,
            PRIMARY KEY (account_id, name)
        ) WITHOUT ROWID;

        -- Search index. Its rowid is messages.pk. Contentless: the text lives in the .eml files, so the index adds
        -- only the searchable terms. contentless_delete lets a re-downloaded message replace its row.
        CREATE VIRTUAL TABLE message_fts USING fts5 (
            subject, sender, recipients, attachments, body,
            tokenize = 'unicode61 remove_diacritics 2',
            content = '',
            contentless_delete = 1
        );
        """,
    ];

    private readonly string _connectionString;

    public ArchiveDatabase(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
    }

    public string Path { get; }

    public static int LatestSchemaVersion => Migrations.Length;

    /// <summary>
    /// Creates the file if needed and applies pending migrations. Safe when several threads or processes
    /// initialize the same new database at once: they take turns through a named mutex for this file.
    /// </summary>
    /// <exception cref="ArchiveVersionException">The database was created by a newer version of the app.</exception>
    public void Initialize()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        using var turn = InitializationLock.Acquire(Path);
        using var connection = Open();

        while (true)
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            var version = GetUserVersion(connection, transaction);
            if (version > Migrations.Length)
            {
                throw new ArchiveVersionException(Path, version, Migrations.Length);
            }

            if (version == Migrations.Length)
            {
                transaction.Commit();
                break;
            }

            Execute(connection, Migrations[version], transaction);
            Execute(connection, $"PRAGMA user_version = {version + 1};", transaction);
            transaction.Commit();
        }

        // Persistent header change: only after confirming this version owns the schema.
        Execute(connection, "PRAGMA journal_mode = WAL;");
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Execute(connection, "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 15000; PRAGMA synchronous = FULL;");
        return connection;
    }

    /// <summary>
    /// Closes the pooled connections of this database file only, so the file can be moved or deleted. Other databases are
    /// not affected (unlike <c>SqliteConnection.ClearAllPools</c>, which is process-wide).
    /// </summary>
    public void ReleaseConnections()
    {
        using var connection = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(connection);
    }

    public static int GetUserVersion(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A revision number for one transaction. Every row a transaction touches carries it, so a reader that has seen
    /// revision N has seen everything committed up to N. Call it inside the transaction that writes the rows.
    /// </summary>
    public static long NextRevision(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE meta SET value = value + 1 WHERE key = 'rev' RETURNING value;";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    internal static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

/// <summary>Small helpers so the stores read as SQL, not as plumbing.</summary>
internal static class SqliteExtensions
{
    public static SqliteCommand Command(this SqliteConnection connection, string sql, SqliteTransaction? transaction = null, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    public static int Run(this SqliteConnection connection, string sql, SqliteTransaction? transaction = null, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.Command(sql, transaction, parameters);
        return command.ExecuteNonQuery();
    }

    public static T? Scalar<T>(this SqliteConnection connection, string sql, SqliteTransaction? transaction = null, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.Command(sql, transaction, parameters);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture);
    }

    public static string? GetNullableString(this SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static long? GetNullableInt64(this SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    public static string Iso(this DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset? ParseIso(string? text) =>
        text is null ? null : DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal);
}

/// <summary>One initializer at a time per database file, across threads and processes.</summary>
internal sealed class InitializationLock : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly Mutex _mutex;

    private InitializationLock(Mutex mutex) => _mutex = mutex;

    public static InitializationLock Acquire(string path)
    {
        // Named after a hash of the normalized path: mutex names can't contain path separators.
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..32];
        var mutex = new Mutex(false, $"GmailWatcher-db-{key}");
        try
        {
            if (!mutex.WaitOne(Timeout))
            {
                throw new TimeoutException($"Another Gmail Watcher process kept the database '{path}' busy for {Timeout.TotalSeconds:0} seconds.");
            }
        }
        catch (AbandonedMutexException)
        {
            // A previous holder exited mid-initialization; migrations are transactional, so continue.
        }
        catch
        {
            mutex.Dispose();
            throw;
        }

        return new InitializationLock(mutex);
    }

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}

public sealed class ArchiveVersionException(string path, int found, int supported)
    : Exception($"The archive database '{path}' uses schema version {found}, but this version of Gmail Watcher supports up to {supported}. " +
                "Update Gmail Watcher; the archive was not modified.")
{
    public int FoundVersion { get; } = found;

    public int SupportedVersion { get; } = supported;
}
