using GmailWatcher.Core.Mail;

namespace GmailWatcher.Archive.Demo;

/// <summary>
/// Fills an <see cref="InMemoryMailbox"/> with plausible mail: people, newsletters, receipts, attachments of several
/// types (some repeated, so duplicate detection has something to find), forwarded messages, and a little Spam and Trash.
/// Deterministic for a given seed. Everything is invented; none of it is real mail.
/// </summary>
public static class SyntheticMail
{
    private static readonly string[] People =
    [
        "Ada Lovelace <ada@analytical.example>", "Grace Hopper <grace@navy.example>", "Alan Turing <alan@bletchley.example>",
        "Katherine Johnson <katherine@nasa.example>", "Margaret Hamilton <margaret@apollo.example>", "Linus Torvalds <linus@kernel.example>",
        "Barbara Liskov <barbara@mit.example>", "Dennis Ritchie <dennis@bell.example>", "Hedy Lamarr <hedy@spread.example>",
    ];

    private static readonly string[] Services =
    [
        "Orbit Weekly <news@orbit.example>", "Stellar Bank <alerts@stellarbank.example>", "ShipIt <orders@shipit.example>",
        "Cloud Console <noreply@cloud.example>", "City Library <holds@library.example>", "Fly Right <tickets@flyright.example>",
    ];

    private static readonly string[] Subjects =
    [
        "Lunch on Thursday?", "Re: draft for review", "Meeting notes", "Your invoice is ready", "Weekend plans", "Quick question",
        "Photos from the trip", "Fwd: itinerary", "Your order has shipped", "Statement available", "Welcome aboard", "Reminder: appointment",
        "Re: Re: budget", "Thoughts on the proposal", "Happy birthday!", "Receipt for your purchase",
    ];

    private static readonly string[] Sentences =
    [
        "Thanks for sending this over, I will take a look this afternoon.", "Can we move the meeting to Thursday at ten?",
        "Attached you will find the latest version of the document.", "I have not forgotten about it, just buried in other things.",
        "Let me know if anything is unclear and I will explain it in more detail.", "The numbers look right to me, but please double check the totals.",
        "We should probably talk about this in person rather than over email.", "Sorry for the late reply; the week got away from me.",
    ];

    private static readonly (string Name, string Type)[] AttachmentKinds =
    [
        ("invoice-{0}.pdf", "application/pdf"), ("photo-{0}.jpg", "image/jpeg"), ("report-{0}.xlsx", "application/octet-stream"),
        ("notes-{0}.txt", "text/plain"), ("itinerary-{0}.pdf", "application/pdf"), ("slides-{0}.pptx", "application/octet-stream"),
    ];

    public static void Populate(InMemoryMailbox mailbox, int count, int seed = 42, DateTimeOffset? newest = null)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        var random = new Random(seed);
        var end = newest ?? DateTimeOffset.UtcNow;
        mailbox.AddLabel("Label_Receipts", "Receipts");
        mailbox.AddLabel("Label_Travel", "Travel");
        mailbox.AddLabel("Label_Work", "Work");
        var logo = Bytes(random, 4_000); // the same file in many messages

        // Oldest first, so Gmail's ids and history grow with time like a real mailbox.
        var years = 9.0;
        for (var i = 0; i < count; i++)
        {
            var age = Math.Pow(1 - ((double)i / Math.Max(1, count - 1)), 1.6) * years; // more mail recently
            var date = end - TimeSpan.FromDays(age * 365.25) - TimeSpan.FromMinutes(random.Next(0, 24 * 60));
            var isService = random.NextDouble() < 0.35;
            var isSent = !isService && random.NextDouble() < 0.18;
            var from = isSent ? "Me <me@example.com>" : Pick(random, isService ? Services : People);
            var to = isSent ? Pick(random, People) : "me@example.com";

            var labels = new List<string>();
            var roll = random.NextDouble();
            if (isSent)
            {
                labels.Add("SENT");
            }
            else if (roll < 0.02)
            {
                labels.Add("SPAM");
            }
            else if (roll < 0.05)
            {
                labels.Add("TRASH");
            }
            else
            {
                labels.Add("INBOX");
                if (isService && random.NextDouble() < 0.6)
                {
                    labels.Add("CATEGORY_PROMOTIONS");
                }

                if (random.NextDouble() < 0.12)
                {
                    labels.Add("UNREAD");
                }

                if (random.NextDouble() < 0.05)
                {
                    labels.Add("IMPORTANT");
                }

                if (random.NextDouble() < 0.03)
                {
                    labels.Add("STARRED");
                }
            }

            if (random.NextDouble() < 0.10)
            {
                labels.Add(Pick(random, ["Label_Receipts", "Label_Travel", "Label_Work"]));
            }

            var attachments = new List<SyntheticAttachment>();
            if (random.NextDouble() < 0.22)
            {
                for (var n = random.Next(1, 3); n > 0; n--)
                {
                    var (name, type) = AttachmentKinds[random.Next(AttachmentKinds.Length)];
                    attachments.Add(new SyntheticAttachment(string.Format(System.Globalization.CultureInfo.InvariantCulture, name, random.Next(1, 999)), type, Bytes(random, random.Next(3_000, 140_000))));
                }
            }

            var html = isService && random.NextDouble() < 0.7;
            if (html)
            {
                attachments.Add(new SyntheticAttachment("logo.png", "image/png", logo, Inline: true));
            }

            var subject = Pick(random, Subjects);
            var body = string.Join(' ', Enumerable.Range(0, random.Next(1, 5)).Select(_ => Pick(random, Sentences)));
            mailbox.Add(new MessageSpec
            {
                From = from,
                To = to,
                Subject = isService ? $"{subject} #{random.Next(1000, 9999)}" : subject,
                Body = body,
                Html = html ? $"<html><body><img src=\"cid:logo\"><p>{body}</p><p><a href=\"https://example.com\">View online</a></p></body></html>" : null,
                Date = date,
                Labels = labels,
                Attachments = attachments,
                AttachForwardedMessage = random.NextDouble() < 0.01,
            });
        }
    }

    private static T Pick<T>(Random random, IReadOnlyList<T> items) => items[random.Next(items.Count)];

    private static byte[] Bytes(Random random, int length)
    {
        var bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }
}

/// <summary>Demo mode's stand-in for Google: a mailbox per demo account, created on first use, with simulated network time.</summary>
public sealed class DemoMailboxFactory(TimeSpan latency, int messagesPerAccount = 1500) : IMailboxFactory
{
    private readonly Dictionary<string, InMemoryMailbox> _mailboxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public Task<IMailbox> OpenAsync(string accountEmail, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_mailboxes.TryGetValue(accountEmail, out var mailbox))
            {
                mailbox = Create(accountEmail, messagesPerAccount);
                _mailboxes[accountEmail] = mailbox;
            }

            return Task.FromResult<IMailbox>(mailbox);
        }
    }

    /// <summary>A new demo account with its own mail.</summary>
    public InMemoryMailbox AddAccount(string email, int messages)
    {
        lock (_gate)
        {
            var mailbox = Create(email, messages);
            _mailboxes[email] = mailbox;
            return mailbox;
        }
    }

    /// <summary>The same address always gets the same mail, in every run. (string.GetHashCode is randomized per process.)</summary>
    internal static int StableSeed(string email) =>
        BitConverter.ToInt32(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant()))) & 0x7FFFFFFF;

    private InMemoryMailbox Create(string email, int messages)
    {
        var mailbox = new InMemoryMailbox(email) { Latency = latency };
        SyntheticMail.Populate(mailbox, messages, seed: StableSeed(email));
        return mailbox;
    }
}
