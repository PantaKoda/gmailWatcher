using System.Text;
using GmailWatcher.Archive.Storage;
using GmailWatcher.Core.Mail;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace GmailWatcher.Archive.Ingest;

public sealed record IngestOptions(bool ExtractAttachments = true, int MaxIndexedBodyChars = 64 * 1024);

/// <summary>
/// Turns a downloaded message into files and database-ready data.
/// <list type="number">
/// <item>The raw message is written byte for byte to its .eml first. That file is the backup; everything below is convenience.</item>
/// <item>The message is parsed. A parse failure never loses the message: it is archived with the metadata Gmail gave.</item>
/// <item>Attachments are written as separate, "from the internet"-marked files (unless turned off).</item>
/// </list>
/// </summary>
public sealed class MessageIngestor(ArchiveLayout layout, IngestOptions options, ILogger<MessageIngestor> logger)
{
    private static readonly ParserOptions Parser = new()
    {
        // Real-world mail is sloppy. Being strict would lose subjects and attachments, never improve the backup.
        AddressParserComplianceMode = RfcComplianceMode.Loose,
        ParameterComplianceMode = RfcComplianceMode.Loose,
        AllowAddressesWithoutDomain = true,
    };

    public IngestedMessage Ingest(string accountFolder, RemoteMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var emlRelative = ArchiveLayout.EmlRelativePath(message.Id, message.InternalDateMs);
        var emlPath = layout.Resolve(accountFolder, emlRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(emlPath)!);
        var sha = FileStore.WriteAtomic(emlPath, message.Raw);

        MimeMessage? mime = null;
        string? parseError = null;
        try
        {
            using var stream = new MemoryStream(message.Raw, writable: false);
            mime = MimeMessage.Load(Parser, stream, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            parseError = $"{ex.GetType().Name}: {ex.Message}";
            logger.LogWarning(ex, "Message {MessageId} could not be parsed; its raw copy is archived anyway", message.Id);
        }

        var attachments = new List<IngestedAttachment>();
        if (mime is not null)
        {
            try
            {
                attachments = SaveAttachments(accountFolder, message, mime, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !FileStore.IsDiskFull(ex))
            {
                // The .eml already contains every attachment; losing the convenience copies must not lose the message.
                parseError ??= $"attachments: {ex.GetType().Name}: {ex.Message}";
                logger.LogWarning(ex, "Attachments of message {MessageId} could not be saved separately", message.Id);
            }
        }

        var from = mime?.From.Mailboxes.FirstOrDefault();
        return new IngestedMessage(
            GmailId: message.Id,
            ThreadId: message.ThreadId,
            InternalDateMs: message.InternalDateMs,
            LabelIds: message.LabelIds,
            GmailHistoryId: message.HistoryId,
            Snippet: message.Snippet,
            Subject: mime?.Subject,
            From: from is null ? null : new MailAddress(Clean(from.Name), from.Address ?? ""),
            To: Addresses(mime?.To),
            Cc: Addresses(mime?.Cc),
            Bcc: Addresses(mime?.Bcc),
            ReplyTo: Addresses(mime?.ReplyTo),
            MessageIdHeader: mime?.MessageId,
            InReplyTo: mime?.InReplyTo,
            SizeBytes: message.Raw.LongLength,
            EmlRelativePath: emlRelative,
            EmlSha256: sha,
            Attachments: attachments,
            BodyText: mime is null ? "" : BodyText(mime),
            ParseError: parseError);
    }

    private List<IngestedAttachment> SaveAttachments(string accountFolder, RemoteMessage message, MimeMessage mime, CancellationToken cancellationToken)
    {
        var parts = new List<MimeEntity>();
        Collect(mime.Body, parts);

        var saved = new List<IngestedAttachment>(parts.Count);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryRelative = ArchiveLayout.AttachmentRelativeDirectory(message.Id, message.InternalDateMs);
        for (var index = 0; index < parts.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var part = parts[index];
            var contentType = part.ContentType.MimeType;
            var name = UniqueName(used, ArchiveLayout.SafeFileName(DisplayName(part), FallbackName(index, part)));
            var contentId = part is MimePart { ContentId: { Length: > 0 } id } ? id : null;
            var isInline = contentId is not null && part.ContentDisposition?.IsAttachment != true;

            long size;
            string hash;
            string? relative = null;
            try
            {
                if (options.ExtractAttachments)
                {
                    relative = $"{directoryRelative}/{name}";
                    var path = layout.Resolve(accountFolder, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    (size, hash) = FileStore.WriteAtomic(path, stream => WriteContent(part, stream, cancellationToken));
                    FileStore.MarkAsFromInternet(path);
                }
                else
                {
                    (size, hash) = FileStore.Measure(stream => WriteContent(part, stream, cancellationToken));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !FileStore.IsDiskFull(ex))
            {
                logger.LogWarning(ex, "Attachment {Index} of message {MessageId} could not be decoded", index, message.Id);
                (size, hash, relative) = (0, "", null);
            }

            saved.Add(new IngestedAttachment(index, name, contentType, size, hash, isInline, contentId, relative));
        }

        return saved;
    }

    /// <summary>Finds the parts that are attachments: everything that is not the text or HTML body of the message.</summary>
    private static void Collect(MimeEntity? entity, List<MimeEntity> found)
    {
        switch (entity)
        {
            case null:
                break;
            case Multipart multipart:
                foreach (var child in multipart)
                {
                    Collect(child, found);
                }

                break;
            case MessagePart:
                found.Add(entity); // an attached or forwarded message; kept whole, not unpacked
                break;
            case TextPart text when IsBody(text):
                break;
            case MimePart:
                found.Add(entity);
                break;
        }
    }

    private static bool IsBody(TextPart text) => (text.IsPlain || text.IsHtml) && !text.IsAttachment && text.FileName is null;

    private static void WriteContent(MimeEntity part, Stream destination, CancellationToken cancellationToken)
    {
        switch (part)
        {
            case MessagePart { Message: { } nested }:
                nested.WriteTo(destination, cancellationToken);
                break;
            case MimePart { Content: { } content }:
                content.DecodeTo(destination, cancellationToken);
                break;
        }
    }

    private static string? DisplayName(MimeEntity part) => part switch
    {
        MimePart { FileName: { Length: > 0 } fileName } => fileName,
        MessagePart { Message.Subject: { Length: > 0 } subject } => subject + ".eml",
        _ => null,
    };

    private static string FallbackName(int index, MimeEntity part)
    {
        var extension = part is MessagePart
            ? ".eml"
            : MimeTypes.TryGetExtension(part.ContentType.MimeType, out var known) && known.Length is > 1 and <= 16 ? known : ".bin";
        return $"attachment-{index + 1}{extension}";
    }

    private static string UniqueName(HashSet<string> used, string name)
    {
        var candidate = name;
        for (var n = 2; !used.Add(candidate); n++)
        {
            var extension = Path.GetExtension(name);
            candidate = $"{name[..^extension.Length]} ({n}){extension}";
        }

        return candidate;
    }

    private static IReadOnlyList<MailAddress> Addresses(InternetAddressList? list) =>
        list is null
            ? []
            : list.Mailboxes.Where(m => !string.IsNullOrWhiteSpace(m.Address)).Select(m => new MailAddress(Clean(m.Name), m.Address)).ToList();

    private static string? Clean(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    private string BodyText(MimeMessage mime)
    {
        string? text;
        try
        {
            text = mime.TextBody;
            if (string.IsNullOrWhiteSpace(text) && mime.HtmlBody is { Length: > 0 } html)
            {
                text = HtmlText.Extract(html);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Search loses this message's text; the message itself is unaffected. Say so instead of failing silently.
            logger.LogWarning(ex, "The text of a message could not be extracted for the search index");
            return "";
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        // One line of single spaces is enough for a search index, and it keeps the index small.
        var builder = new StringBuilder(Math.Min(text.Length, options.MaxIndexedBodyChars));
        var space = true;
        foreach (var ch in text)
        {
            if (builder.Length >= options.MaxIndexedBodyChars)
            {
                break;
            }

            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                if (!space)
                {
                    builder.Append(' ');
                    space = true;
                }
            }
            else
            {
                builder.Append(ch);
                space = false;
            }
        }

        return builder.ToString().TrimEnd();
    }
}
