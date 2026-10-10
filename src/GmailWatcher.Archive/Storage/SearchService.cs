using System.Text;
using Microsoft.Data.Sqlite;

namespace GmailWatcher.Archive.Storage;

public sealed record MessageSummary(
    long Pk,
    int AccountId,
    string AccountEmail,
    string GmailId,
    string ThreadId,
    DateTimeOffset Received,
    string Subject,
    string? FromName,
    string? FromAddress,
    string Snippet,
    int AttachmentCount,
    long SizeBytes,
    bool RemoteDeleted,
    IReadOnlyList<string> Labels)
{
    public string Sender => string.IsNullOrWhiteSpace(FromName) ? FromAddress ?? "(unknown sender)" : FromName;
}

/// <param name="Text">What the user typed. Words match as prefixes; "quotes" match a phrase; from:, to:, subject: and file: narrow to one part.</param>
public sealed record SearchQuery(string Text = "", int? AccountId = null, string? LabelId = null, bool OnlyWithAttachments = false, int Offset = 0, int Limit = 50);

public sealed record SearchPage(IReadOnlyList<MessageSummary> Items, long Total)
{
    public bool HasMore(SearchQuery query) => query.Offset + Items.Count < Total;
}

/// <summary>Finds archived messages: full-text search (SQLite FTS5) with account, label and attachment filters, newest first.</summary>
public sealed class SearchService(ArchiveDatabase database)
{
    public SearchPage Search(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var match = ToMatch(query.Text);
        var where = new StringBuilder("1 = 1");
        var parameters = new List<(string, object?)>();
        if (query.AccountId is { } account)
        {
            where.Append(" AND m.account_id = @account");
            parameters.Add(("@account", account));
        }

        if (query.OnlyWithAttachments)
        {
            where.Append(" AND m.attachment_count > 0");
        }

        if (!string.IsNullOrEmpty(query.LabelId))
        {
            where.Append(" AND EXISTS (SELECT 1 FROM message_labels ml WHERE ml.message_pk = m.pk AND ml.label_id = @label)");
            parameters.Add(("@label", query.LabelId));
        }

        var from = "messages m";
        if (match is not null)
        {
            from = "message_fts f JOIN messages m ON m.pk = f.rowid";
            where.Append(" AND message_fts MATCH @match");
            parameters.Add(("@match", match));
        }

        using var connection = database.Open();
        var total = connection.Scalar<long>($"SELECT COUNT(*) FROM {from} WHERE {where}", null, [.. parameters]);

        var rows = new List<MessageSummary>();
        using (var command = connection.Command(
            $"""
            SELECT m.pk, m.account_id, a.email, m.gmail_id, m.thread_id, m.internal_date, m.subject, m.from_name, m.from_address, m.snippet,
                   m.attachment_count, m.size_bytes, m.remote_deleted_at IS NOT NULL
            FROM {from} JOIN accounts a ON a.id = m.account_id
            WHERE {where}
            ORDER BY m.internal_date DESC, m.pk DESC
            LIMIT @limit OFFSET @offset
            """,
            null, [.. parameters, ("@limit", Math.Clamp(query.Limit, 1, 500)), ("@offset", Math.Max(0, query.Offset))]))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add(new MessageSummary(
                    reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)), reader.GetNullableString(6) ?? "(no subject)",
                    reader.GetNullableString(7), reader.GetNullableString(8), reader.GetNullableString(9) ?? "",
                    reader.GetInt32(10), reader.GetInt64(11), reader.GetBoolean(12), []));
            }
        }

        return new SearchPage([.. rows.Select(r => r with { Labels = LabelsOf(connection, r.Pk, r.AccountId) })], total);
    }

    private static List<string> LabelsOf(SqliteConnection connection, long pk, int accountId)
    {
        using var command = connection.Command(
            "SELECT COALESCE(lb.name, ml.label_id) FROM message_labels ml LEFT JOIN labels lb ON lb.account_id = @a AND lb.label_id = ml.label_id WHERE ml.message_pk = @pk ORDER BY 1",
            null, ("@a", accountId), ("@pk", pk));
        using var reader = command.ExecuteReader();
        var labels = new List<string>();
        while (reader.Read())
        {
            labels.Add(reader.GetString(0));
        }

        return labels;
    }

    /// <summary>
    /// Turns what a person typed into a valid FTS5 query. Raw FTS5 syntax (AND, NEAR, unbalanced quotes, a bare '*')
    /// raises errors, so every term is quoted; no input can produce a malformed query. Null when nothing searchable remains.
    /// </summary>
    internal static string? ToMatch(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var terms = new List<string>();
        foreach (var (column, value, isPhrase) in Tokenize(text))
        {
            var cleaned = value.Replace("\"", "", StringComparison.Ordinal).Trim();
            if (cleaned.Length == 0 || !cleaned.Any(char.IsLetterOrDigit))
            {
                continue;
            }

            var term = $"\"{cleaned}\"" + (!isPhrase && cleaned.Length >= 2 ? "*" : "");
            terms.Add(column is null ? term : $"{column} : {term}");
        }

        return terms.Count == 0 ? null : string.Join(' ', terms);
    }

    private static IEnumerable<(string? Column, string Value, bool IsPhrase)> Tokenize(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            if (text[i] == '"')
            {
                yield return (null, ReadPhrase(text, ref i), true);
                continue;
            }

            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            var word = text[start..i];
            var colon = word.IndexOf(':', StringComparison.Ordinal);
            var column = colon > 0 ? ColumnOf(word[..colon]) : null;
            if (column is null)
            {
                yield return (null, word, false);
                continue;
            }

            var rest = word[(colon + 1)..];
            if (rest.StartsWith('"'))
            {
                // from:"ann smith": the phrase runs to the closing quote, across spaces.
                i = start + colon + 1;
                yield return (column, ReadPhrase(text, ref i), true);
            }
            else
            {
                yield return (column, rest, false);
            }
        }
    }

    /// <summary>Reads "a phrase" starting at the opening quote; ends at the closing quote or the end of the text.</summary>
    private static string ReadPhrase(string text, ref int i)
    {
        var end = text.IndexOf('"', i + 1);
        var phrase = end < 0 ? text[(i + 1)..] : text[(i + 1)..end];
        i = end < 0 ? text.Length : end + 1;
        return phrase;
    }

    private static string? ColumnOf(string name) => name.ToLowerInvariant() switch
    {
        "from" or "sender" => "sender",
        "to" or "cc" or "recipient" => "recipients",
        "subject" => "subject",
        "file" or "filename" or "attachment" => "attachments",
        _ => null,
    };
}
