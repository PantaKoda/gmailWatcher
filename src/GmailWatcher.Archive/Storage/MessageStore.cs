using System.Globalization;
using GmailWatcher.Archive.Ingest;
using GmailWatcher.Core.Accounts;
using GmailWatcher.Core.Mail;
using Microsoft.Data.Sqlite;

namespace GmailWatcher.Archive.Storage;

/// <summary>One archived message as the verifier needs it.</summary>
public sealed record StoredMessageFile(long Pk, string GmailId, string EmlPath, long SizeBytes, string Sha256);

/// <summary>One saved attachment file as the verifier needs it.</summary>
public sealed record StoredAttachmentFile(long MessagePk, string GmailId, string FileName, string Path, long SizeBytes, string Sha256);

/// <summary>
/// Reads and writes archived messages. Mail is append-only: nothing here removes a message because Gmail no longer
/// has it. Such messages are only marked (<c>remote_deleted_at</c>), so the archive stays the complete record.
/// </summary>
public sealed class MessageStore(ArchiveDatabase database, TimeProvider time)
{
    /// <summary>The Gmail IDs of every archived message of an account.</summary>
    public HashSet<string> GetIds(int accountId)
    {
        using var connection = database.Open();
        using var command = connection.Command("SELECT gmail_id FROM messages WHERE account_id = @id", null, ("@id", accountId));
        using var reader = command.ExecuteReader();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    public long Count(int accountId)
    {
        using var connection = database.Open();
        return connection.Scalar<long>("SELECT COUNT(*) FROM messages WHERE account_id = @id", null, ("@id", accountId));
    }

    public AccountStats GetStats(int accountId)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            """
            SELECT COUNT(*), COALESCE(SUM(size_bytes), 0), COUNT(DISTINCT thread_id), COALESCE(SUM(attachment_count), 0),
                   COALESCE(SUM(attachment_bytes), 0), COALESCE(SUM(remote_deleted_at IS NOT NULL), 0), MIN(internal_date), MAX(internal_date)
            FROM messages WHERE account_id = @id
            """,
            null, ("@id", accountId));
        using var reader = command.ExecuteReader();
        reader.Read();
        var labels = connection.Scalar<long>("SELECT COUNT(*) FROM labels WHERE account_id = @id", null, ("@id", accountId));
        return new AccountStats(
            Messages: reader.GetInt64(0),
            MessageBytes: reader.GetInt64(1),
            Threads: reader.GetInt64(2),
            Attachments: reader.GetInt64(3),
            AttachmentBytes: reader.GetInt64(4),
            RemoteDeleted: reader.GetInt64(5),
            Labels: labels,
            Oldest: reader.IsDBNull(6) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
            Newest: reader.IsDBNull(7) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)));
    }

    /// <summary>
    /// Writes a batch of downloaded messages in one transaction. The files are already on disk (and flushed): a row is
    /// committed only after what it points at exists. A message that is already stored is replaced (a repair).
    /// </summary>
    public void InsertBatch(int accountId, IReadOnlyList<IngestedMessage> messages)
    {
        if (messages.Count == 0)
        {
            return;
        }

        var now = time.GetUtcNow().Iso();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var revision = ArchiveDatabase.NextRevision(connection, transaction);
        foreach (var message in messages)
        {
            Insert(connection, transaction, accountId, message, revision, now);
        }

        transaction.Commit();
    }

    private static void Insert(SqliteConnection connection, SqliteTransaction transaction, int accountId, IngestedMessage message, long revision, string now)
    {
        var existing = connection.Scalar<long?>("SELECT pk FROM messages WHERE account_id = @a AND gmail_id = @id", transaction, ("@a", accountId), ("@id", message.GmailId));
        if (existing is { } oldPk)
        {
            connection.Run("DELETE FROM message_fts WHERE rowid = @pk", transaction, ("@pk", oldPk));
            connection.Run("DELETE FROM messages WHERE pk = @pk", transaction, ("@pk", oldPk)); // cascades to labels, addresses, attachments
        }

        var pk = connection.Scalar<long>(
            """
            INSERT INTO messages (account_id, gmail_id, thread_id, internal_date, subject, from_name, from_address, message_id_header, in_reply_to,
                                  snippet, size_bytes, eml_path, eml_sha256, attachment_count, attachment_bytes, gmail_history_id, parse_error, archived_at, rev)
            VALUES (@a, @id, @thread, @date, @subject, @fromName, @fromAddress, @mid, @reply,
                    @snippet, @size, @eml, @sha, @acount, @abytes, @history, @error, @now, @rev)
            RETURNING pk
            """,
            transaction,
            ("@a", accountId), ("@id", message.GmailId), ("@thread", message.ThreadId), ("@date", message.InternalDateMs),
            ("@subject", message.Subject), ("@fromName", message.From?.Name), ("@fromAddress", message.From?.Address),
            ("@mid", message.MessageIdHeader), ("@reply", message.InReplyTo), ("@snippet", message.Snippet), ("@size", message.SizeBytes),
            ("@eml", message.EmlRelativePath), ("@sha", message.EmlSha256), ("@acount", message.AttachmentCount), ("@abytes", message.AttachmentBytes),
            ("@history", message.GmailHistoryId), ("@error", message.ParseError), ("@now", now), ("@rev", revision));

        foreach (var label in message.LabelIds.Distinct(StringComparer.Ordinal))
        {
            connection.Run("INSERT INTO message_labels (message_pk, label_id) VALUES (@pk, @label)", transaction, ("@pk", pk), ("@label", label));
        }

        InsertAddresses(connection, transaction, pk, "from", message.From is null ? [] : [message.From]);
        InsertAddresses(connection, transaction, pk, "to", message.To);
        InsertAddresses(connection, transaction, pk, "cc", message.Cc);
        InsertAddresses(connection, transaction, pk, "bcc", message.Bcc);
        InsertAddresses(connection, transaction, pk, "reply-to", message.ReplyTo);

        foreach (var attachment in message.Attachments)
        {
            connection.Run(
                """
                INSERT INTO attachments (message_pk, part_index, file_name, content_type, size_bytes, sha256, is_inline, content_id, path)
                VALUES (@pk, @index, @name, @type, @size, @sha, @inline, @cid, @path)
                """,
                transaction, ("@pk", pk), ("@index", attachment.Index), ("@name", attachment.FileName), ("@type", attachment.ContentType),
                ("@size", attachment.SizeBytes), ("@sha", attachment.Sha256), ("@inline", attachment.IsInline ? 1 : 0), ("@cid", attachment.ContentId), ("@path", attachment.RelativePath));
        }

        connection.Run(
            "INSERT INTO message_fts (rowid, subject, sender, recipients, attachments, body) VALUES (@pk, @subject, @sender, @recipients, @attachments, @body)",
            transaction,
            ("@pk", pk), ("@subject", message.Subject ?? ""), ("@sender", Describe(message.From is null ? [] : [message.From])),
            ("@recipients", Describe(message.To.Concat(message.Cc))), ("@attachments", string.Join(' ', message.Attachments.Select(a => a.FileName))),
            ("@body", message.BodyText.Length == 0 ? message.Snippet : message.BodyText));
    }

    /// <summary>
    /// Replaces what was derived from decoding the text of already archived messages (subject, sender, recipients, search
    /// index) with <paramref name="batch"/>. Files, labels and attachments are untouched. A message whose visible fields
    /// changed gets a new revision, so the analytics copy picks it up. Returns how many such messages there were.
    /// </summary>
    public int ApplyText(IReadOnlyList<(long Pk, MessageText Text)> batch)
    {
        if (batch.Count == 0)
        {
            return 0;
        }

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        long? revision = null;
        var changed = 0;
        foreach (var (pk, text) in batch)
        {
            string? subject = null, fromName = null, fromAddress = null, snippet = null;
            using (var command = connection.Command("SELECT subject, from_name, from_address, snippet FROM messages WHERE pk = @pk", transaction, ("@pk", pk)))
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read())
                {
                    continue;
                }

                subject = reader.IsDBNull(0) ? null : reader.GetString(0);
                fromName = reader.IsDBNull(1) ? null : reader.GetString(1);
                fromAddress = reader.IsDBNull(2) ? null : reader.GetString(2);
                snippet = reader.IsDBNull(3) ? "" : reader.GetString(3);
            }

            var wanted = AddressRows(text);
            var stored = new List<(string Role, int Position, string? Name, string Address)>();
            using (var command = connection.Command("SELECT role, position, name, address FROM message_addresses WHERE message_pk = @pk ORDER BY role, position", transaction, ("@pk", pk)))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    stored.Add((reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3)));
                }
            }

            var differs = !string.Equals(subject, text.Subject, StringComparison.Ordinal)
                || !string.Equals(fromName, text.From?.Name, StringComparison.Ordinal)
                || !string.Equals(fromAddress, text.From?.Address, StringComparison.Ordinal)
                || !stored.OrderBy(r => r.Role, StringComparer.Ordinal).ThenBy(r => r.Position).SequenceEqual(wanted.OrderBy(r => r.Role, StringComparer.Ordinal).ThenBy(r => r.Position));
            if (differs)
            {
                changed++;
                revision ??= ArchiveDatabase.NextRevision(connection, transaction);
                connection.Run(
                    "UPDATE messages SET subject = @subject, from_name = @fromName, from_address = @fromAddress, rev = @rev WHERE pk = @pk",
                    transaction, ("@subject", text.Subject), ("@fromName", text.From?.Name), ("@fromAddress", text.From?.Address), ("@rev", revision), ("@pk", pk));
                connection.Run("DELETE FROM message_addresses WHERE message_pk = @pk", transaction, ("@pk", pk));
                InsertAddresses(connection, transaction, pk, "from", text.From is null ? [] : [text.From]);
                InsertAddresses(connection, transaction, pk, "to", text.To);
                InsertAddresses(connection, transaction, pk, "cc", text.Cc);
                InsertAddresses(connection, transaction, pk, "bcc", text.Bcc);
                InsertAddresses(connection, transaction, pk, "reply-to", text.ReplyTo);
            }

            // The body is not stored anywhere but the index, so the index entry is always rewritten.
            var attachmentNames = new List<string>();
            using (var command = connection.Command("SELECT file_name FROM attachments WHERE message_pk = @pk ORDER BY part_index", transaction, ("@pk", pk)))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    attachmentNames.Add(reader.GetString(0));
                }
            }

            connection.Run("DELETE FROM message_fts WHERE rowid = @pk", transaction, ("@pk", pk));
            connection.Run(
                "INSERT INTO message_fts (rowid, subject, sender, recipients, attachments, body) VALUES (@pk, @subject, @sender, @recipients, @attachments, @body)",
                transaction,
                ("@pk", pk), ("@subject", text.Subject ?? ""), ("@sender", Describe(text.From is null ? [] : [text.From])),
                ("@recipients", Describe(text.To.Concat(text.Cc))), ("@attachments", string.Join(' ', attachmentNames)),
                ("@body", text.BodyText.Length == 0 ? snippet : text.BodyText));
        }

        transaction.Commit();
        return changed;
    }

    private static List<(string Role, int Position, string? Name, string Address)> AddressRows(MessageText text)
    {
        var rows = new List<(string, int, string?, string)>();
        void Add(string role, IReadOnlyList<MailAddress> list)
        {
            // INSERT OR IGNORE on (message, role, position) means every listed address is stored.
            for (var i = 0; i < list.Count; i++)
            {
                rows.Add((role, i, list[i].Name, list[i].Address));
            }
        }

        Add("from", text.From is null ? [] : [text.From]);
        Add("to", text.To);
        Add("cc", text.Cc);
        Add("bcc", text.Bcc);
        Add("reply-to", text.ReplyTo);
        return rows;
    }

    /// <summary>How far the text of an account's archived messages has been brought up to date (see <see cref="TextRefresher"/>).</summary>
    public int GetTextVersion(int accountId)
    {
        using var connection = database.Open();
        return (int)(connection.Scalar<long?>("SELECT value FROM meta WHERE key = @key", null, ("@key", $"text_version:{accountId}")) ?? 1);
    }

    public void SetTextVersion(int accountId, int version)
    {
        using var connection = database.Open();
        connection.Run(
            "INSERT INTO meta (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
            null, ("@key", $"text_version:{accountId}"), ("@value", version));
    }

    private static void InsertAddresses(SqliteConnection connection, SqliteTransaction transaction, long pk, string role, IReadOnlyList<MailAddress> addresses)
    {
        for (var position = 0; position < addresses.Count; position++)
        {
            connection.Run(
                "INSERT OR IGNORE INTO message_addresses (message_pk, role, position, name, address) VALUES (@pk, @role, @pos, @name, @address)",
                transaction, ("@pk", pk), ("@role", role), ("@pos", position), ("@name", addresses[position].Name), ("@address", addresses[position].Address));
        }
    }

    private static string Describe(IEnumerable<MailAddress> addresses) =>
        string.Join(' ', addresses.Select(a => string.IsNullOrEmpty(a.Name) ? a.Address : $"{a.Name} {a.Address}"));

    /// <summary>Marks messages Gmail no longer has. They stay in the archive. Returns how many were newly marked.</summary>
    public int MarkRemoteDeleted(int accountId, IEnumerable<string> gmailIds)
    {
        var now = time.GetUtcNow().Iso();
        var changed = 0;
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        long? revision = null;
        foreach (var id in gmailIds)
        {
            var pk = connection.Scalar<long?>(
                "SELECT pk FROM messages WHERE account_id = @a AND gmail_id = @id AND remote_deleted_at IS NULL", transaction, ("@a", accountId), ("@id", id));
            if (pk is null)
            {
                continue;
            }

            revision ??= ArchiveDatabase.NextRevision(connection, transaction);
            changed += connection.Run("UPDATE messages SET remote_deleted_at = @now, rev = @rev WHERE pk = @pk", transaction, ("@now", now), ("@rev", revision), ("@pk", pk));
        }

        transaction.Commit();
        return changed;
    }

    /// <summary>Clears the mark on messages that are in Gmail after all (a listing is the truth). Returns how many.</summary>
    public int UnmarkRemoteDeleted(int accountId, ISet<string> presentRemotely)
    {
        using var connection = database.Open();
        var marked = new List<string>();
        using (var command = connection.Command("SELECT gmail_id FROM messages WHERE account_id = @a AND remote_deleted_at IS NOT NULL", null, ("@a", accountId)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (presentRemotely.Contains(reader.GetString(0)))
                {
                    marked.Add(reader.GetString(0));
                }
            }
        }

        if (marked.Count == 0)
        {
            return 0;
        }

        using var transaction = connection.BeginTransaction(deferred: false);
        var revision = ArchiveDatabase.NextRevision(connection, transaction);
        foreach (var id in marked)
        {
            connection.Run("UPDATE messages SET remote_deleted_at = NULL, rev = @rev WHERE account_id = @a AND gmail_id = @id", transaction, ("@rev", revision), ("@a", accountId), ("@id", id));
        }

        transaction.Commit();
        return marked.Count;
    }

    /// <summary>Gmail IDs of archived messages that carry <paramref name="labelId"/>.</summary>
    public HashSet<string> GetIdsWithLabel(int accountId, string labelId)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            "SELECT m.gmail_id FROM message_labels ml JOIN messages m ON m.pk = ml.message_pk WHERE m.account_id = @a AND ml.label_id = @label",
            null, ("@a", accountId), ("@label", labelId));
        using var reader = command.ExecuteReader();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    /// <summary>
    /// Applies label changes from Gmail's history in the order Gmail reported them. Changes for messages that are not
    /// archived (yet) are skipped: a message downloaded afterwards already carries its current labels.
    /// Returns how many archived messages ended up with different labels.
    /// </summary>
    public int ApplyLabelChanges(int accountId, IEnumerable<HistoryChange> changes)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var pks = new Dictionary<string, long?>(StringComparer.Ordinal);
        var touched = new HashSet<long>();
        foreach (var change in changes.Where(c => c.Kind == HistoryChangeKind.LabelsChanged))
        {
            if (!pks.TryGetValue(change.MessageId, out var pk))
            {
                pk = connection.Scalar<long?>("SELECT pk FROM messages WHERE account_id = @a AND gmail_id = @id", transaction, ("@a", accountId), ("@id", change.MessageId));
                pks[change.MessageId] = pk;
            }

            if (pk is not { } messagePk)
            {
                continue;
            }

            foreach (var label in change.LabelsAdded)
            {
                if (connection.Run("INSERT OR IGNORE INTO message_labels (message_pk, label_id) VALUES (@pk, @label)", transaction, ("@pk", messagePk), ("@label", label)) > 0)
                {
                    touched.Add(messagePk);
                }
            }

            foreach (var label in change.LabelsRemoved)
            {
                if (connection.Run("DELETE FROM message_labels WHERE message_pk = @pk AND label_id = @label", transaction, ("@pk", messagePk), ("@label", label)) > 0)
                {
                    touched.Add(messagePk);
                }
            }
        }

        if (touched.Count > 0)
        {
            var revision = ArchiveDatabase.NextRevision(connection, transaction);
            foreach (var pk in touched)
            {
                connection.Run("UPDATE messages SET rev = @rev WHERE pk = @pk", transaction, ("@rev", revision), ("@pk", pk));
            }
        }

        transaction.Commit();
        return touched.Count;
    }

    /// <summary>
    /// Makes the archived label membership of one label match Gmail's, for messages that exist in Gmail. Messages Gmail no
    /// longer has keep the labels they had when last seen. Returns how many messages changed.
    /// </summary>
    public int ReconcileLabel(int accountId, string labelId, ISet<string> remoteWithLabel, ISet<string> archivedIds, ISet<string> allRemote)
    {
        var archivedWithLabel = GetIdsWithLabel(accountId, labelId);
        var toAdd = remoteWithLabel.Where(id => archivedIds.Contains(id) && !archivedWithLabel.Contains(id)).ToList();
        var toRemove = archivedWithLabel.Where(id => allRemote.Contains(id) && !remoteWithLabel.Contains(id)).ToList();
        if (toAdd.Count == 0 && toRemove.Count == 0)
        {
            return 0;
        }

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var revision = ArchiveDatabase.NextRevision(connection, transaction);
        foreach (var id in toAdd)
        {
            Change(connection, transaction, accountId, id, labelId, add: true, revision);
        }

        foreach (var id in toRemove)
        {
            Change(connection, transaction, accountId, id, labelId, add: false, revision);
        }

        transaction.Commit();
        return toAdd.Count + toRemove.Count;
    }

    private static void Change(SqliteConnection connection, SqliteTransaction transaction, int accountId, string gmailId, string labelId, bool add, long revision)
    {
        var pk = connection.Scalar<long?>("SELECT pk FROM messages WHERE account_id = @a AND gmail_id = @id", transaction, ("@a", accountId), ("@id", gmailId));
        if (pk is null)
        {
            return;
        }

        connection.Run(
            add ? "INSERT OR IGNORE INTO message_labels (message_pk, label_id) VALUES (@pk, @label)" : "DELETE FROM message_labels WHERE message_pk = @pk AND label_id = @label",
            transaction, ("@pk", pk), ("@label", labelId));
        connection.Run("UPDATE messages SET rev = @rev WHERE pk = @pk", transaction, ("@rev", revision), ("@pk", pk));
    }

    /// <summary>The archived files of an account in pages (keyset pagination: no long-lived read transaction).</summary>
    public IEnumerable<IReadOnlyList<StoredMessageFile>> EnumerateFiles(int accountId, int pageSize = 500)
    {
        long last = 0;
        while (true)
        {
            using var connection = database.Open();
            using var command = connection.Command(
                "SELECT pk, gmail_id, eml_path, size_bytes, eml_sha256 FROM messages WHERE account_id = @a AND pk > @last ORDER BY pk LIMIT @limit",
                null, ("@a", accountId), ("@last", last), ("@limit", pageSize));
            using var reader = command.ExecuteReader();
            var page = new List<StoredMessageFile>(pageSize);
            while (reader.Read())
            {
                page.Add(new StoredMessageFile(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4)));
            }

            if (page.Count == 0)
            {
                yield break;
            }

            last = page[^1].Pk;
            yield return page;
        }
    }

    /// <summary>Saved attachment files of the given messages (those written as separate files).</summary>
    public IReadOnlyList<StoredAttachmentFile> GetAttachmentFiles(IReadOnlyCollection<StoredMessageFile> messages)
    {
        var byPk = messages.ToDictionary(m => m.Pk);
        var files = new List<StoredAttachmentFile>();
        using var connection = database.Open();
        foreach (var chunk in messages.Chunk(500))
        {
            var parameters = chunk.Select((m, i) => ($"@p{i}", (object?)m.Pk)).ToArray();
            using var command = connection.Command(
                $"SELECT message_pk, file_name, path, size_bytes, sha256 FROM attachments WHERE path IS NOT NULL AND message_pk IN ({string.Join(',', parameters.Select(p => p.Item1))})",
                null, parameters);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var pk = reader.GetInt64(0);
                files.Add(new StoredAttachmentFile(pk, byPk[pk].GmailId, reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4)));
            }
        }

        return files;
    }

    /// <summary>
    /// Removes the database rows of messages whose files are damaged, so the next sync downloads them again. Only
    /// worthwhile while Gmail still has them. Returns how many rows were removed.
    /// </summary>
    public int Forget(int accountId, IEnumerable<string> gmailIds)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var removed = 0;
        foreach (var id in gmailIds)
        {
            var pk = connection.Scalar<long?>("SELECT pk FROM messages WHERE account_id = @a AND gmail_id = @id", transaction, ("@a", accountId), ("@id", id));
            if (pk is null)
            {
                continue;
            }

            connection.Run("DELETE FROM message_fts WHERE rowid = @pk", transaction, ("@pk", pk));
            removed += connection.Run("DELETE FROM messages WHERE pk = @pk", transaction, ("@pk", pk));
        }

        transaction.Commit();
        return removed;
    }

    /// <summary>Which of these messages Gmail has said it no longer has (their archived copy is the only one left).</summary>
    public HashSet<string> GetRemoteDeleted(int accountId, IEnumerable<string> gmailIds)
    {
        var wanted = gmailIds.ToHashSet(StringComparer.Ordinal);
        var deleted = new HashSet<string>(StringComparer.Ordinal);
        using var connection = database.Open();
        using var command = connection.Command("SELECT gmail_id FROM messages WHERE account_id = @a AND remote_deleted_at IS NOT NULL", null, ("@a", accountId));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (wanted.Contains(reader.GetString(0)))
            {
                deleted.Add(reader.GetString(0));
            }
        }

        return deleted;
    }

    /// <summary>SQLite's own quick structural check of the database file; null when it is fine, otherwise the first problem.</summary>
    public string? CheckIntegrity()
    {
        using var connection = database.Open();
        using var command = connection.Command("PRAGMA quick_check(1)");
        var result = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase) ? null : result ?? "no answer";
    }
}
