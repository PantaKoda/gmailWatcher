using System.Text;
using GmailWatcher.Archive.Demo;
using GmailWatcher.Archive.Ingest;
using GmailWatcher.Archive.Storage;
using GmailWatcher.Core.Mail;
using Microsoft.Extensions.Logging.Abstractions;

namespace GmailWatcher.Archive.Tests;

public sealed class MessageIngestorTests : IDisposable
{
    private readonly ArchiveFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static RemoteMessage Message(MessageSpec spec, string id = "18c000000000001") =>
        Wrap(InMemoryMailbox.Build(spec), spec.Date, id);

    private static RemoteMessage Wrap(byte[] raw, DateTimeOffset date, string id = "18c000000000001") =>
        new(id, id, ["INBOX"], "1001", date.ToUnixTimeMilliseconds(), raw.Length, "snippet", raw);

    private IngestedMessage Ingest(RemoteMessage message, IngestOptions? options = null)
    {
        var account = _fixture.AddAccount();
        var ingestor = options is null ? _fixture.Ingestor : new MessageIngestor(_fixture.Layout, options, NullLogger<MessageIngestor>.Instance);
        return ingestor.Ingest(account.Folder, message);
    }

    private string Path(string relative) => _fixture.Layout.Resolve(_fixture.Accounts.Find("me@example.com")!.Folder, relative);

    [Fact]
    public void The_eml_is_byte_identical_to_what_gmail_returned()
    {
        var message = Message(new MessageSpec { Subject = "Quarterly numbers", Body = "See attached." });

        var ingested = Ingest(message);

        Assert.Equal(message.Raw, File.ReadAllBytes(Path(ingested.EmlRelativePath)));
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(message.Raw)), ingested.EmlSha256);
        Assert.Equal(message.Raw.LongLength, ingested.SizeBytes);
        Assert.Equal("mail/2024/05/18c000000000001.eml", ingested.EmlRelativePath);
    }

    [Fact]
    public void Headers_and_addresses_are_extracted()
    {
        var ingested = Ingest(Message(new MessageSpec
        {
            From = "Renée Müller <renee@example.org>",
            To = "me@example.com, \"Bob B\" <bob@example.net>",
            Cc = "carol@example.com",
            Subject = "Café — résumé",
        }));

        Assert.Equal("Café — résumé", ingested.Subject);
        Assert.Equal(new MailAddress("Renée Müller", "renee@example.org"), ingested.From);
        Assert.Equal(["me@example.com", "bob@example.net"], ingested.To.Select(a => a.Address));
        Assert.Equal("Bob B", ingested.To[1].Name);
        Assert.Equal("carol@example.com", Assert.Single(ingested.Cc).Address);
        Assert.NotNull(ingested.MessageIdHeader);
        Assert.Null(ingested.ParseError);
    }

    [Fact]
    public void Attachments_are_saved_as_separate_files_with_checksums()
    {
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4 fake pdf content");
        var csv = Encoding.UTF8.GetBytes("a,b;1,2"); // no line breaks: MIME canonicalizes "\n" in text parts to CRLF
        var ingested = Ingest(Message(new MessageSpec
        {
            Attachments = [new("report.pdf", "application/pdf", pdf), new("data.csv", "text/csv", csv)],
        }));

        Assert.Equal(2, ingested.Attachments.Count);
        Assert.Equal(2, ingested.AttachmentCount);
        Assert.Equal(pdf.Length + csv.Length, ingested.AttachmentBytes);
        var report = ingested.Attachments.Single(a => a.FileName == "report.pdf");
        Assert.Equal("application/pdf", report.ContentType);
        Assert.Equal(pdf.Length, report.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(pdf)), report.Sha256);
        Assert.Equal(pdf, File.ReadAllBytes(Path(report.RelativePath!)));
        Assert.StartsWith("attachments/2024/05/18c000000000001/", report.RelativePath);
        Assert.Contains("data.csv", ingested.Attachments.Select(a => a.FileName));
    }

    [Fact]
    public void Embedded_images_are_kept_but_not_counted_as_attachments()
    {
        var ingested = Ingest(Message(new MessageSpec
        {
            Html = "<p>Hi <img src='cid:logo'></p>",
            Attachments = [new("logo.png", "image/png", [1, 2, 3, 4], Inline: true), new("contract.pdf", "application/pdf", [9, 9, 9])],
        }));

        Assert.Equal(2, ingested.Attachments.Count);
        Assert.True(ingested.Attachments.Single(a => a.FileName == "logo.png").IsInline);
        Assert.Equal(1, ingested.AttachmentCount);
        Assert.Equal(3, ingested.AttachmentBytes);
    }

    [Fact]
    public void A_forwarded_message_is_kept_whole_as_an_eml_attachment()
    {
        var ingested = Ingest(Message(new MessageSpec { Body = "FYI", AttachForwardedMessage = true }));

        var forwarded = Assert.Single(ingested.Attachments);
        Assert.Equal("message/rfc822", forwarded.ContentType);
        Assert.EndsWith(".eml", forwarded.FileName);
        Assert.Contains("The original message.", File.ReadAllText(Path(forwarded.RelativePath!)));
    }

    [Theory]
    [InlineData("..\\..\\evil.exe")]
    [InlineData("../../evil.exe")]
    [InlineData("C:\\Windows\\system32\\drivers\\etc\\hosts")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("a:b*c?d\"e<f>g|h.txt")]
    [InlineData("...")]
    [InlineData("  trailing dots...  ")]
    public void Hostile_attachment_names_never_leave_the_message_folder(string name)
    {
        var ingested = Ingest(Message(new MessageSpec { Attachments = [new(name, "application/octet-stream", [1, 2, 3])] }));

        var attachment = Assert.Single(ingested.Attachments);
        var full = Path(attachment.RelativePath!);
        var messageFolder = Path(ArchiveLayout.AttachmentRelativeDirectory("18c000000000001", new DateTimeOffset(2024, 5, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()));
        Assert.True(ArchiveLayout.IsInside(messageFolder, full), $"{full} escaped {messageFolder}");
        Assert.True(File.Exists(full));
        Assert.DoesNotContain("..", attachment.FileName);
        Assert.Equal(attachment.FileName, System.IO.Path.GetFileName(full));
    }

    [Fact]
    public void Duplicate_names_in_one_message_get_numbered()
    {
        var ingested = Ingest(Message(new MessageSpec
        {
            Attachments = [new("scan.jpg", "image/jpeg", [1]), new("scan.jpg", "image/jpeg", [2]), new("SCAN.JPG", "image/jpeg", [3])],
        }));

        Assert.Equal(["scan.jpg", "scan (2).jpg", "SCAN (3).JPG"], ingested.Attachments.OrderBy(a => a.Index).Select(a => a.FileName));
        Assert.Equal(3, ingested.Attachments.Select(a => a.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void A_nameless_attachment_gets_a_name_from_its_type()
    {
        var ingested = Ingest(Message(new MessageSpec { Attachments = [new("", "application/pdf", [1, 2])] }));

        Assert.Equal("attachment-1.pdf", Assert.Single(ingested.Attachments).FileName);
    }

    [Fact]
    public void Html_only_messages_are_indexed_as_plain_text_without_scripts_or_styles()
    {
        var ingested = Ingest(Message(new MessageSpec
        {
            Body = "",
            Html = "<html><head><style>p{color:red}</style><title>Tab</title></head><body><script>alert(1)</script><p>Hello&nbsp;<b>wonderful</b> world</p><!-- hidden --><div>Second line</div></body></html>",
        }));

        Assert.Contains("wonderful", ingested.BodyText);
        Assert.Contains("Second line", ingested.BodyText);
        Assert.DoesNotContain("alert", ingested.BodyText);
        Assert.DoesNotContain("color:red", ingested.BodyText);
        Assert.DoesNotContain("hidden", ingested.BodyText);
        Assert.DoesNotContain("<", ingested.BodyText);
    }

    [Fact]
    public void The_indexed_body_is_capped()
    {
        var ingested = Ingest(
            Message(new MessageSpec { Body = string.Join(' ', Enumerable.Repeat("word", 50_000)) }),
            new IngestOptions(MaxIndexedBodyChars: 1000));

        Assert.True(ingested.BodyText.Length <= 1000);
        Assert.StartsWith("word word", ingested.BodyText);
    }

    [Fact]
    public void With_extraction_off_attachments_are_indexed_but_no_files_are_written()
    {
        var content = new byte[] { 5, 6, 7, 8, 9 };
        var ingested = Ingest(
            Message(new MessageSpec { Attachments = [new("a.bin", "application/octet-stream", content)] }),
            new IngestOptions(ExtractAttachments: false));

        var attachment = Assert.Single(ingested.Attachments);
        Assert.Null(attachment.RelativePath);
        Assert.Equal(content.Length, attachment.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)), attachment.Sha256);
        Assert.False(Directory.Exists(Path("attachments")));
    }

    [Fact]
    public void A_message_that_cannot_be_parsed_is_still_archived_with_gmails_metadata()
    {
        var garbage = new byte[2048];
        new Random(1).NextBytes(garbage);
        var message = Wrap(garbage, new DateTimeOffset(2023, 11, 3, 8, 0, 0, TimeSpan.Zero));

        var ingested = Ingest(message);

        Assert.Equal(garbage, File.ReadAllBytes(Path(ingested.EmlRelativePath)));
        Assert.Equal("snippet", ingested.Snippet);
        Assert.Equal("18c000000000001", ingested.GmailId);
    }

    [Fact]
    public void Re_ingesting_replaces_the_file_left_by_an_interrupted_attempt()
    {
        var message = Message(new MessageSpec { Subject = "again" });
        var first = Ingest(message);
        File.WriteAllText(Path(first.EmlRelativePath), "damaged");

        var second = Ingest(message);

        Assert.Equal(message.Raw, File.ReadAllBytes(Path(second.EmlRelativePath)));
        Assert.Empty(Directory.GetFiles(System.IO.Path.GetDirectoryName(Path(second.EmlRelativePath))!, "*.part-*"));
    }

    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("", "fallback.bin")]
    [InlineData("   ", "fallback.bin")]
    [InlineData("a/b\\c.txt", "c.txt")]
    [InlineData("..\\..\\evil.exe", "evil.exe")]
    [InlineData("C:\\Windows\\hosts", "hosts")]
    [InlineData("COM1.txt", "_COM1.txt")]
    public void Safe_file_names(string input, string expected) =>
        Assert.Equal(expected, ArchiveLayout.SafeFileName(input, "fallback.bin"));

    [Fact]
    public void Long_names_are_shortened_but_keep_their_extension()
    {
        var name = ArchiveLayout.SafeFileName(new string('x', 400) + ".docx", "f");

        Assert.True(name.Length <= 120);
        Assert.EndsWith(".docx", name);
    }
}
