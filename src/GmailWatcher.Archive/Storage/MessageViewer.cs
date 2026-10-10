using GmailWatcher.Archive.Ingest;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace GmailWatcher.Archive.Storage;

public sealed record AttachmentInfo(string FileName, string ContentType, long SizeBytes, bool IsInline, string? FullPath)
{
    public bool IsSaved => FullPath is not null && File.Exists(FullPath);
}

public sealed record MessageDetail(
    MessageSummary Summary,
    string From,
    string To,
    string Cc,
    string Date,
    string Body,
    bool BodyWasHtml,
    IReadOnlyList<AttachmentInfo> Attachments,
    string EmlPath,
    string? Problem);

/// <summary>
/// Reads one archived message back for display. The body is shown as plain text: HTML is converted, never rendered,
/// so nothing in an archived message can run, load a remote image or phone home.
/// </summary>
public sealed class MessageViewer(ArchiveDatabase database, ArchiveLayout layout, ILogger<MessageViewer> logger)
{
    private const int MaxBodyChars = 500_000;

    public MessageDetail? Load(MessageSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        using var connection = database.Open();
        var row = connection.Scalar<string>("SELECT folder FROM accounts WHERE id = @id", null, ("@id", summary.AccountId));
        var emlRelative = connection.Scalar<string>("SELECT eml_path FROM messages WHERE pk = @pk", null, ("@pk", summary.Pk));
        if (row is null || emlRelative is null)
        {
            return null;
        }

        var attachments = new List<AttachmentInfo>();
        using (var command = connection.Command(
            "SELECT file_name, content_type, size_bytes, is_inline, path FROM attachments WHERE message_pk = @pk ORDER BY part_index", null, ("@pk", summary.Pk)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                attachments.Add(new AttachmentInfo(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetBoolean(3),
                    reader.IsDBNull(4) ? null : layout.Resolve(row, reader.GetString(4))));
            }
        }

        var emlPath = layout.Resolve(row, emlRelative);
        string from = summary.Sender, to = "", cc = "", date = summary.Received.ToLocalTime().ToString("f", System.Globalization.CultureInfo.CurrentCulture), body = summary.Snippet;
        var wasHtml = false;
        string? problem = null;
        try
        {
            using var stream = File.OpenRead(emlPath);
            var mime = MimeMessage.Load(stream);
            from = mime.From.ToString();
            to = mime.To.ToString();
            cc = mime.Cc.ToString();
            if (mime.Date != DateTimeOffset.MinValue)
            {
                date = mime.Date.ToLocalTime().ToString("f", System.Globalization.CultureInfo.CurrentCulture);
            }

            var text = mime.TextBody;
            if (string.IsNullOrWhiteSpace(text) && mime.HtmlBody is { Length: > 0 } html)
            {
                text = HtmlText.Extract(html);
                wasHtml = true;
            }

            body = string.IsNullOrWhiteSpace(text) ? "(This message has no text.)" : Clip(text.Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"The message file could not be read ({ex.Message}). Run Verify to check the archive.";
        }
        catch (Exception ex) when (ex is FormatException or ParseException or InvalidOperationException)
        {
            logger.LogWarning(ex, "An archived message could not be parsed for display");
            problem = "This message could not be displayed as text. The original is saved as an .eml file.";
        }

        return new MessageDetail(summary, from, to, cc, date, body, wasHtml, attachments, emlPath, problem);
    }

    private static string Clip(string text) =>
        text.Length <= MaxBodyChars ? text : text[..MaxBodyChars] + "\n\n… (shortened; open the .eml for the rest)";
}
