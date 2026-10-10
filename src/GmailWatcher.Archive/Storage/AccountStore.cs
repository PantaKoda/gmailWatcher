using System.Text.Json;
using GmailWatcher.Core.Accounts;
using GmailWatcher.Core.Mail;
using GmailWatcher.Core.Sync;
using GmailWatcher.Core.Verification;
using Microsoft.Data.Sqlite;

namespace GmailWatcher.Archive.Storage;

/// <summary>Accounts, their labels and mailbox settings, and sync/verification bookkeeping.</summary>
public sealed class AccountStore(ArchiveDatabase database, TimeProvider time)
{
    private const string Columns =
        "id, email, display_name, folder, connection, added_at, initial_sync_completed_at, last_sync_started_at, last_sync_completed_at, " +
        "last_sync_outcome, last_error, remote_messages_total, last_verified_at, last_verification_verdict";

    private static readonly JsonSerializerOptions ReportJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>
    /// Adds an account, or returns the existing one with the same address (connected again if it was archive-only).
    /// An account's folder never changes after it is created.
    /// </summary>
    public AccountRecord Add(string email, string? displayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        email = email.Trim();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var existing = Read(connection, transaction, "email = @email", ("@email", email));
        if (existing is not null)
        {
            connection.Run("UPDATE accounts SET connection = 'connected', last_error = NULL WHERE id = @id", transaction, ("@id", existing.Id));
            transaction.Commit();
            return existing with { Connection = AccountConnection.Connected, LastError = null };
        }

        var folder = ArchiveLayout.AccountFolderName(email);
        // Two different addresses can sanitize to the same folder name (rare); keep them apart.
        for (var suffix = 2; connection.Scalar<long>("SELECT COUNT(*) FROM accounts WHERE folder = @folder", transaction, ("@folder", folder)) > 0; suffix++)
        {
            folder = $"{ArchiveLayout.AccountFolderName(email)}-{suffix}";
        }

        connection.Run(
            "INSERT INTO accounts (email, display_name, folder, connection, added_at) VALUES (@email, @name, @folder, 'connected', @added)",
            transaction, ("@email", email), ("@name", displayName), ("@folder", folder), ("@added", time.GetUtcNow().Iso()));
        var added = Read(connection, transaction, "email = @email", ("@email", email))!;
        transaction.Commit();
        return added;
    }

    public IReadOnlyList<AccountRecord> List()
    {
        using var connection = database.Open();
        using var command = connection.Command($"SELECT {Columns} FROM accounts ORDER BY added_at, id");
        using var reader = command.ExecuteReader();
        var accounts = new List<AccountRecord>();
        while (reader.Read())
        {
            accounts.Add(Map(reader));
        }

        return accounts;
    }

    public AccountRecord? Get(int id)
    {
        using var connection = database.Open();
        return Read(connection, null, "id = @id", ("@id", id));
    }

    public AccountRecord? Find(string email)
    {
        using var connection = database.Open();
        return Read(connection, null, "email = @email", ("@email", email.Trim()));
    }

    public void SetConnection(int id, AccountConnection connectionState, string? error = null)
    {
        using var connection = database.Open();
        connection.Run("UPDATE accounts SET connection = @connection, last_error = COALESCE(@error, last_error) WHERE id = @id", null,
            ("@id", id), ("@connection", ConnectionName(connectionState)), ("@error", error));
    }

    public void SetDisplayName(int id, string? displayName)
    {
        using var connection = database.Open();
        connection.Run("UPDATE accounts SET display_name = @name WHERE id = @id", null, ("@id", id), ("@name", displayName));
    }

    /// <summary>The history ID the next incremental sync continues from, or null before the first complete sync.</summary>
    public string? GetHistoryId(int id)
    {
        using var connection = database.Open();
        return connection.Scalar<string>("SELECT history_id FROM accounts WHERE id = @id", null, ("@id", id));
    }

    /// <summary>Makes the next sync a full pass (list everything, download what is missing) instead of a history-based one.</summary>
    public void ClearHistory(int id)
    {
        using var connection = database.Open();
        connection.Run("UPDATE accounts SET history_id = NULL WHERE id = @id", null, ("@id", id));
    }

    public void SyncStarted(int id)
    {
        using var connection = database.Open();
        connection.Run("UPDATE accounts SET last_sync_started_at = @now, last_sync_outcome = 'running' WHERE id = @id", null,
            ("@id", id), ("@now", time.GetUtcNow().Iso()));
    }

    /// <param name="historyId">Saved only for a completed sync: the point the next sync continues from.</param>
    /// <param name="initialComplete">Marks the first full download as finished (only for a completed full pass).</param>
    public void SyncFinished(int id, SyncOutcome outcome, string? error, string? historyId, bool initialComplete, long? remoteTotal)
    {
        var now = time.GetUtcNow().Iso();
        using var connection = database.Open();
        connection.Run(
            """
            UPDATE accounts SET
                last_sync_outcome = @outcome,
                last_error = @error,
                last_sync_completed_at = CASE WHEN @completed = 1 THEN @now ELSE last_sync_completed_at END,
                history_id = CASE WHEN @completed = 1 AND @history IS NOT NULL THEN @history ELSE history_id END,
                initial_sync_completed_at = CASE WHEN @completed = 1 AND @initial = 1 AND initial_sync_completed_at IS NULL THEN @now ELSE initial_sync_completed_at END,
                remote_messages_total = COALESCE(@total, remote_messages_total)
            WHERE id = @id
            """,
            null,
            ("@id", id), ("@outcome", outcome.ToString()), ("@error", error), ("@now", now), ("@completed", outcome == SyncOutcome.Completed ? 1 : 0),
            ("@history", historyId), ("@initial", initialComplete ? 1 : 0), ("@total", remoteTotal));
    }

    public void UpsertLabels(int accountId, IEnumerable<RemoteLabel> labels)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var label in labels)
        {
            connection.Run(
                """
                INSERT INTO labels (account_id, label_id, name, kind, background_color, text_color)
                VALUES (@account, @id, @name, @kind, @bg, @fg)
                ON CONFLICT (account_id, label_id) DO UPDATE SET name = @name, kind = @kind, background_color = @bg, text_color = @fg
                """,
                transaction, ("@account", accountId), ("@id", label.Id), ("@name", label.Name), ("@kind", label.Kind.ToString()),
                ("@bg", label.BackgroundColor), ("@fg", label.TextColor));
        }

        transaction.Commit();
    }

    public IReadOnlyList<RemoteLabel> GetLabels(int accountId)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            "SELECT label_id, name, kind, background_color, text_color FROM labels WHERE account_id = @id ORDER BY kind DESC, name COLLATE NOCASE",
            null, ("@id", accountId));
        using var reader = command.ExecuteReader();
        var labels = new List<RemoteLabel>();
        while (reader.Read())
        {
            labels.Add(new RemoteLabel(reader.GetString(0), reader.GetString(1), Enum.Parse<LabelKind>(reader.GetString(2)),
                reader.GetNullableString(3), reader.GetNullableString(4)));
        }

        return labels;
    }

    public void SaveSettings(int accountId, IEnumerable<SettingsDocument> documents)
    {
        var now = time.GetUtcNow().Iso();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var document in documents)
        {
            connection.Run(
                """
                INSERT INTO account_settings (account_id, name, json, saved_at) VALUES (@account, @name, @json, @now)
                ON CONFLICT (account_id, name) DO UPDATE SET json = @json, saved_at = @now
                """,
                transaction, ("@account", accountId), ("@name", document.Name), ("@json", document.Json), ("@now", now));
        }

        transaction.Commit();
    }

    public IReadOnlyList<SettingsDocument> LoadSettings(int accountId)
    {
        using var connection = database.Open();
        using var command = connection.Command("SELECT name, json FROM account_settings WHERE account_id = @id ORDER BY name", null, ("@id", accountId));
        using var reader = command.ExecuteReader();
        var documents = new List<SettingsDocument>();
        while (reader.Read())
        {
            documents.Add(new SettingsDocument(reader.GetString(0), reader.GetString(1)));
        }

        return documents;
    }

    public void SaveVerification(int accountId, VerificationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        using var connection = database.Open();
        connection.Run(
            "UPDATE accounts SET last_verified_at = @at, last_verification_verdict = @verdict, verification_json = @json WHERE id = @id",
            null, ("@id", accountId), ("@at", report.CompletedAt.Iso()), ("@verdict", report.Verdict.ToString()), ("@json", JsonSerializer.Serialize(report, ReportJson)));
    }

    public VerificationReport? LoadVerification(int accountId)
    {
        using var connection = database.Open();
        var json = connection.Scalar<string>("SELECT verification_json FROM accounts WHERE id = @id", null, ("@id", accountId));
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<VerificationReport>(json, ReportJson);
        }
        catch (JsonException)
        {
            return null; // an unreadable old report is the same as none: verify again
        }
    }

    /// <summary>Forgets an account's database rows (messages, labels, attachments). Files are removed separately, see <see cref="ArchiveMaintenance"/>.</summary>
    internal void DeleteRows(int accountId)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        // The search index is not covered by foreign keys.
        connection.Run("DELETE FROM message_fts WHERE rowid IN (SELECT pk FROM messages WHERE account_id = @id)", transaction, ("@id", accountId));
        connection.Run("DELETE FROM accounts WHERE id = @id", transaction, ("@id", accountId));
        transaction.Commit();
    }

    private static AccountRecord? Read(SqliteConnection connection, SqliteTransaction? transaction, string where, params (string, object?)[] parameters)
    {
        using var command = connection.Command($"SELECT {Columns} FROM accounts WHERE {where}", transaction, parameters);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private static AccountRecord Map(SqliteDataReader reader) => new(
        Id: reader.GetInt32(0),
        Email: reader.GetString(1),
        DisplayName: reader.GetNullableString(2),
        Folder: reader.GetString(3),
        Connection: ParseConnection(reader.GetString(4)),
        AddedAt: SqliteExtensions.ParseIso(reader.GetString(5))!.Value,
        InitialSyncCompletedAt: SqliteExtensions.ParseIso(reader.GetNullableString(6)),
        LastSyncStartedAt: SqliteExtensions.ParseIso(reader.GetNullableString(7)),
        LastSyncCompletedAt: SqliteExtensions.ParseIso(reader.GetNullableString(8)),
        LastSyncOutcome: reader.GetNullableString(9),
        LastError: reader.GetNullableString(10),
        RemoteMessagesTotal: reader.GetNullableInt64(11),
        LastVerifiedAt: SqliteExtensions.ParseIso(reader.GetNullableString(12)),
        LastVerificationVerdict: reader.GetNullableString(13));

    internal static string ConnectionName(AccountConnection connection) => connection switch
    {
        AccountConnection.Connected => "connected",
        AccountConnection.ReconnectRequired => "reconnect_required",
        _ => "archive_only",
    };

    private static AccountConnection ParseConnection(string name) => name switch
    {
        "connected" => AccountConnection.Connected,
        "reconnect_required" => AccountConnection.ReconnectRequired,
        _ => AccountConnection.ArchiveOnly,
    };
}
