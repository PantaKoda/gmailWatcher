using System.Globalization;
using DuckDB.NET.Data;
using GmailWatcher.Archive.Storage;

namespace GmailWatcher.Archive.Analytics;

public sealed record MonthCount(DateOnly Month, long Messages, long Bytes);

/// <summary>One line of a ranking: a name, an optional second line, how many, and how many bytes.</summary>
public sealed record RankedItem(string Name, string? Detail, long Count, long Bytes);

/// <summary>What the archive says about one account, or about all of them. Computed by DuckDB from the archive.</summary>
public sealed record MailboxInsights(
    long Messages,
    long MessageBytes,
    long Attachments,
    long AttachmentBytes,
    long Unread,
    long Sent,
    long TrashAndSpam,
    DateTimeOffset? Oldest,
    DateTimeOffset? Newest,
    IReadOnlyList<MonthCount> ByMonth,
    IReadOnlyList<RankedItem> TopSenders,
    IReadOnlyList<RankedItem> SendersBySize,
    IReadOnlyList<RankedItem> TopRecipients,
    IReadOnlyList<RankedItem> Labels,
    IReadOnlyList<RankedItem> AttachmentTypes,
    IReadOnlyList<RankedItem> LargestAttachments,
    IReadOnlyList<RankedItem> LargestMessages,
    long DuplicateAttachmentFiles,
    long DuplicateAttachmentBytes)
{
    public static MailboxInsights Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, null, null, [], [], [], [], [], [], [], [], 0, 0);
}

/// <summary>The canned analytics shown in the app. For anything else, open analytics.duckdb in any DuckDB tool.</summary>
public sealed class InsightsService(AnalyticsMirror mirror, AccountStore accounts)
{
    private const int Top = 12;

    /// <summary>Brings the analytics up to date, then answers. <paramref name="accountId"/> null means all accounts.</summary>
    public MailboxInsights Load(int? accountId, CancellationToken cancellationToken = default)
    {
        foreach (var account in accounts.List().Where(a => accountId is null || a.Id == accountId))
        {
            mirror.Update(account.Id, cancellationToken);
        }

        var scope = accountId is { } id ? $"account_id = {id}" : "TRUE";
        return mirror.Read(connection => Query(connection, scope)) ?? MailboxInsights.Empty;
    }

    private static MailboxInsights Query(DuckDBConnection c, string scope)
    {
        long messages = 0, bytes = 0, attachments = 0, attachmentBytes = 0, unread = 0, sent = 0, trash = 0;
        DateTimeOffset? oldest = null, newest = null;
        Read(c, $"""
            SELECT count(*), CAST(coalesce(sum(size_bytes), 0) AS BIGINT), CAST(coalesce(sum(attachment_count), 0) AS BIGINT), CAST(coalesce(sum(attachment_bytes), 0) AS BIGINT),
                   count(*) FILTER (WHERE is_unread), count(*) FILTER (WHERE is_sent), count(*) FILTER (WHERE is_trash OR is_spam), min(received), max(received)
            FROM messages WHERE {scope}
            """, r =>
        {
            (messages, bytes, attachments, attachmentBytes) = (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3));
            (unread, sent, trash) = (r.GetInt64(4), r.GetInt64(5), r.GetInt64(6));
            if (!r.IsDBNull(7))
            {
                oldest = new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(7), DateTimeKind.Utc));
                newest = new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(8), DateTimeKind.Utc));
            }
        });

        var months = new List<MonthCount>();
        Read(c, $"SELECT strftime(received, '%Y-%m'), count(*), CAST(sum(size_bytes) AS BIGINT) FROM messages WHERE {scope} GROUP BY 1 ORDER BY 1", r =>
        {
            if (DateOnly.TryParseExact(r.GetString(0) + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
            {
                months.Add(new MonthCount(month, r.GetInt64(1), r.GetInt64(2)));
            }
        });

        // Senders: mail from other people (not what the account itself sent).
        var senders = Ranked(c, $"""
            SELECT coalesce(nullif(any_value(from_name), ''), from_address), from_address, count(*), CAST(sum(size_bytes) AS BIGINT)
            FROM messages WHERE {scope} AND NOT is_sent AND from_address <> '' GROUP BY from_address ORDER BY 3 DESC, 4 DESC LIMIT {Top}
            """);
        var bySize = Ranked(c, $"""
            SELECT from_domain, NULL, count(*), CAST(sum(size_bytes) AS BIGINT)
            FROM messages WHERE {scope} AND NOT is_sent AND from_domain <> '' GROUP BY from_domain ORDER BY 4 DESC LIMIT {Top}
            """);
        var recipients = Ranked(c, $"""
            SELECT address, NULL, count(*), CAST(0 AS BIGINT)
            FROM recipients WHERE {scope} AND role = 'to' AND gmail_id IN (SELECT gmail_id FROM messages WHERE {scope} AND is_sent) GROUP BY address ORDER BY 3 DESC LIMIT {Top}
            """);
        var labels = Ranked(c, $"""
            SELECT label_name, NULL, count(*), CAST(0 AS BIGINT)
            FROM message_labels WHERE {scope} GROUP BY label_name ORDER BY 3 DESC LIMIT {Top}
            """);
        var types = Ranked(c, $"""
            SELECT extension, NULL, count(*), CAST(sum(size_bytes) AS BIGINT)
            FROM attachments WHERE {scope} AND NOT is_inline GROUP BY extension ORDER BY 4 DESC LIMIT {Top}
            """);
        var largestAttachments = Ranked(c, $"""
            SELECT a.file_name, strftime(a.received, '%Y-%m-%d') || ' · ' || coalesce(nullif(m.from_address, ''), '?'), 1, a.size_bytes
            FROM attachments a JOIN messages m ON m.account_id = a.account_id AND m.gmail_id = a.gmail_id
            WHERE a.{scope} AND NOT a.is_inline ORDER BY a.size_bytes DESC LIMIT {Top}
            """);
        var largestMessages = Ranked(c, $"""
            SELECT coalesce(nullif(subject, ''), '(no subject)'), strftime(received, '%Y-%m-%d') || ' · ' || coalesce(nullif(from_address, ''), '?'), 1, size_bytes
            FROM messages WHERE {scope} ORDER BY size_bytes DESC LIMIT {Top}
            """);

        long duplicateFiles = 0, duplicateBytes = 0;
        Read(c, $"""
            WITH repeated AS (
                SELECT count(*) AS copies, any_value(size_bytes) AS size
                FROM attachments WHERE {scope} AND NOT is_inline AND sha256 <> '' GROUP BY sha256 HAVING count(*) > 1)
            SELECT CAST(coalesce(sum(copies - 1), 0) AS BIGINT), CAST(coalesce(sum((copies - 1) * size), 0) AS BIGINT) FROM repeated
            """, r => (duplicateFiles, duplicateBytes) = (r.GetInt64(0), r.GetInt64(1)));

        return new MailboxInsights(messages, bytes, attachments, attachmentBytes, unread, sent, trash, oldest, newest, months,
            senders, bySize, recipients, labels, types, largestAttachments, largestMessages, duplicateFiles, duplicateBytes);
    }

    private static List<RankedItem> Ranked(DuckDBConnection connection, string sql)
    {
        var items = new List<RankedItem>();
        Read(connection, sql, r => items.Add(new RankedItem(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt64(2), r.GetInt64(3))));
        return items;
    }

    private static void Read(DuckDBConnection connection, string sql, Action<DuckDBDataReader> row)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            row(reader);
        }
    }
}
