using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using GmailWatcher.Core.Mail;
using MimeKit;

namespace GmailWatcher.Archive.Demo;

/// <summary>An attachment of a synthetic message.</summary>
public sealed record SyntheticAttachment(string FileName, string ContentType, byte[] Content, bool Inline = false);

/// <summary>What a synthetic message looks like. Everything has a sensible default.</summary>
public sealed record MessageSpec
{
    public string From { get; init; } = "Alice Example <alice@example.com>";

    public string To { get; init; } = "me@example.com";

    public string? Cc { get; init; }

    public string Subject { get; init; } = "Hello";

    public string Body { get; init; } = "A short message body.";

    public string? Html { get; init; }

    public DateTimeOffset Date { get; init; } = new(2024, 5, 1, 12, 0, 0, TimeSpan.Zero);

    public IReadOnlyList<string> Labels { get; init; } = ["INBOX"];

    public string? ThreadOf { get; init; }

    public IReadOnlyList<SyntheticAttachment> Attachments { get; init; } = [];

    /// <summary>Attach a complete forwarded message (message/rfc822).</summary>
    public bool AttachForwardedMessage { get; init; }
}

/// <summary>
/// A stand-in for Gmail that lives in memory: real RFC 822 messages (built with MimeKit), labels, a history that
/// grows with every change, permanent deletion, history expiry and failure injection. Used by the tests and by the
/// app's demo mode, so both exercise the real sync engine, database and files.
/// </summary>
public sealed class InMemoryMailbox : IMailbox
{
    private static readonly string[] SystemLabels =
        ["INBOX", "SENT", "DRAFT", "SPAM", "TRASH", "STARRED", "UNREAD", "IMPORTANT", "CATEGORY_PERSONAL", "CATEGORY_UPDATES", "CATEGORY_PROMOTIONS"];

    private readonly Lock _gate = new();
    private readonly List<Stored> _messages = []; // oldest first
    private readonly List<(long Id, HistoryChange Change)> _history = [];
    private readonly Dictionary<string, RemoteLabel> _labels = new(StringComparer.Ordinal);
    private long _nextMessage = 1;
    private long _historyId = 1000;
    private long _historyFloor;

    public InMemoryMailbox(string email)
    {
        Email = email;
        foreach (var id in SystemLabels)
        {
            _labels[id] = new RemoteLabel(id, id, LabelKind.System);
        }
    }

    public string Email { get; set; }

    /// <summary>Simulated network time for each message download.</summary>
    public TimeSpan Latency { get; set; }

    /// <summary>Raised for each message download; return an exception to make that download fail.</summary>
    public Func<string, Exception?>? FailGet { get; set; }

    private readonly List<string> _ghosts = [];

    public Exception? FailProfile { get; set; }

    public Exception? FailList { get; set; }

    public int GetCalls { get; private set; }

    public int ListCalls { get; private set; }

    public int HistoryCalls { get; private set; }

    public long CurrentHistoryId
    {
        get
        {
            lock (_gate)
            {
                return _historyId;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _messages.Count;
            }
        }
    }

    public IReadOnlyList<string> Ids
    {
        get
        {
            lock (_gate)
            {
                return _messages.Select(m => m.Id).ToList();
            }
        }
    }

    public void AddLabel(string id, string name)
    {
        lock (_gate)
        {
            _labels[id] = new RemoteLabel(id, name, LabelKind.User);
        }
    }

    /// <summary>Delivers a message and returns its Gmail ID.</summary>
    public string Add(MessageSpec spec)
    {
        var raw = Build(spec);
        lock (_gate)
        {
            var id = $"{0x18c0_0000_0000_0000L + _nextMessage++:x}";
            var thread = spec.ThreadOf is { } of && _messages.FirstOrDefault(m => m.Id == of) is { } parent ? parent.ThreadId : id;
            foreach (var label in spec.Labels)
            {
                if (!_labels.ContainsKey(label))
                {
                    _labels[label] = new RemoteLabel(label, label, LabelKind.User);
                }
            }

            _historyId++;
            _messages.Add(new Stored(id, thread, [.. spec.Labels], spec.Date.ToUnixTimeMilliseconds(), raw, _historyId, SnippetOf(spec.Body)));
            _history.Add((_historyId, HistoryChange.Added(id)));
            return id;
        }
    }

    /// <summary>Delivers arbitrary bytes as a message (for malformed mail).</summary>
    public string AddRaw(byte[] raw, DateTimeOffset date, params string[] labels)
    {
        lock (_gate)
        {
            var id = $"{0x18c0_0000_0000_0000L + _nextMessage++:x}";
            _historyId++;
            _messages.Add(new Stored(id, id, [.. labels], date.ToUnixTimeMilliseconds(), raw, _historyId, Snippet(raw)));
            _history.Add((_historyId, HistoryChange.Added(id)));
            return id;
        }
    }

    /// <summary>
    /// Makes the message list name an ID that no longer exists: Gmail's list can lag behind after a large deletion, while asking for the
    /// message itself answers "no such message".
    /// </summary>
    public void AddGhost(string id)
    {
        lock (_gate)
        {
            _ghosts.Add(id);
        }
    }

    /// <summary>Permanently deletes a message (what emptying Trash or "delete forever" does).</summary>
    public void Delete(string id)
    {
        lock (_gate)
        {
            if (_messages.RemoveAll(m => m.Id == id) > 0)
            {
                _historyId++;
                _history.Add((_historyId, HistoryChange.Deleted(id)));
            }
        }
    }

    public void DeleteAll()
    {
        foreach (var id in Ids)
        {
            Delete(id);
        }
    }

    public void ChangeLabels(string id, IEnumerable<string>? add = null, IEnumerable<string>? remove = null)
    {
        lock (_gate)
        {
            var message = _messages.Single(m => m.Id == id);
            var added = (add ?? []).Where(message.Labels.Add).ToList();
            var removed = (remove ?? []).Where(message.Labels.Remove).ToList();
            if (added.Count + removed.Count > 0)
            {
                _historyId++;
                _history.Add((_historyId, HistoryChange.Labels(id, added, removed)));
            }
        }
    }

    /// <summary>Makes Gmail forget its history: the next incremental request fails like a stale history ID does.</summary>
    public void ExpireHistory()
    {
        lock (_gate)
        {
            _historyFloor = _historyId;
        }
    }

    public Task<MailboxProfile> GetProfileAsync(CancellationToken cancellationToken)
    {
        if (FailProfile is { } failure)
        {
            throw failure;
        }

        lock (_gate)
        {
            return Task.FromResult(new MailboxProfile(Email, _messages.Count, _messages.Select(m => m.ThreadId).Distinct().Count(), _historyId.ToString(CultureInfo.InvariantCulture)));
        }
    }

    public Task<IReadOnlyList<RemoteLabel>> GetLabelsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<RemoteLabel>>(_labels.Values.ToList());
        }
    }

    public async IAsyncEnumerable<string> ListMessageIdsAsync(string? labelId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ListCalls++;
        if (FailList is { } failure)
        {
            throw failure;
        }

        List<string> ids;
        lock (_gate)
        {
            ids = _messages.Where(m => labelId is null || m.Labels.Contains(labelId)).Reverse().Select(m => m.Id).ToList(); // newest first
            if (labelId is null)
            {
                ids.InsertRange(0, _ghosts);
            }
        }

        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return id;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Task<HistoryResult> GetHistoryAsync(string startHistoryId, CancellationToken cancellationToken)
    {
        HistoryCalls++;
        lock (_gate)
        {
            var start = long.Parse(startHistoryId, CultureInfo.InvariantCulture);
            if (start < _historyFloor)
            {
                throw new HistoryExpiredException();
            }

            var changes = _history.Where(h => h.Id > start).Select(h => h.Change).ToList();
            return Task.FromResult(new HistoryResult(changes, _historyId.ToString(CultureInfo.InvariantCulture)));
        }
    }

    public async Task<RemoteMessage?> GetMessageAsync(string messageId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            GetCalls++;
        }

        if (Latency > TimeSpan.Zero)
        {
            await Task.Delay(Latency, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (FailGet?.Invoke(messageId) is { } failure)
        {
            throw failure;
        }

        lock (_gate)
        {
            var message = _messages.FirstOrDefault(m => m.Id == messageId);
            return message is null
                ? null
                : new RemoteMessage(message.Id, message.ThreadId, [.. message.Labels], message.HistoryId.ToString(CultureInfo.InvariantCulture),
                    message.InternalDateMs, message.Raw.Length, message.Snippet, message.Raw);
        }
    }

    public Task<IReadOnlyList<SettingsDocument>> GetSettingsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SettingsDocument>>(
        [
            new SettingsDocument("filters", """{"filter":[{"id":"f1","criteria":{"from":"newsletter@example.com"},"action":{"addLabelIds":["CATEGORY_PROMOTIONS"]}}]}"""),
            new SettingsDocument("sendAs", $$"""{"sendAs":[{"sendAsEmail":"{{Email}}","isPrimary":true,"signature":"Sent from the demo"}]}"""),
        ]);

    /// <summary>The RFC 822 text of a spec, as Gmail would return it.</summary>
    public static byte[] Build(MessageSpec spec)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(spec.From));
        message.To.AddRange(InternetAddressList.Parse(spec.To));
        if (spec.Cc is not null)
        {
            message.Cc.AddRange(InternetAddressList.Parse(spec.Cc));
        }

        message.Subject = spec.Subject;
        message.Date = spec.Date;
        message.MessageId = $"{Guid.NewGuid():N}@example.com";

        var body = new BodyBuilder { TextBody = spec.Body, HtmlBody = spec.Html };
        foreach (var attachment in spec.Attachments)
        {
            var contentType = ContentType.Parse(attachment.ContentType);
            if (attachment.Inline)
            {
                var resource = body.LinkedResources.Add(attachment.FileName, attachment.Content, contentType);
                resource.ContentId = $"{Guid.NewGuid():N}@example.com";
            }
            else if (string.IsNullOrEmpty(attachment.FileName))
            {
                // MimeKit refuses to add an attachment without a name; real mail has them.
                body.Attachments.Add(new MimePart(contentType)
                {
                    Content = new MimeContent(new MemoryStream(attachment.Content)),
                    ContentTransferEncoding = ContentEncoding.Base64,
                    ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                });
            }
            else
            {
                body.Attachments.Add(attachment.FileName, attachment.Content, contentType);
            }
        }

        var root = (MimeEntity)body.ToMessageBody();
        if (spec.AttachForwardedMessage)
        {
            var inner = new MimeMessage();
            inner.From.Add(new MailboxAddress("Bob", "bob@example.com"));
            inner.To.Add(new MailboxAddress("Me", "me@example.com"));
            inner.Subject = "Forwarded: original";
            inner.Body = new TextPart("plain") { Text = "The original message." };
            var mixed = new Multipart("mixed") { root, new MessagePart { Message = inner } };
            root = mixed;
        }

        message.Body = root;
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return stream.ToArray();
    }

    private static string Snippet(byte[] raw)
    {
        var text = Encoding.UTF8.GetString(raw);
        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var body = split < 0 ? text : text[(split + 4)..];
        return body.Length <= 100 ? body.Trim() : body[..100].Trim();
    }

    private static string SnippetOf(string body) => body.Length <= 100 ? body.Trim() : body[..100].Trim();

    private sealed class Stored(string id, string threadId, HashSet<string> labels, long internalDateMs, byte[] raw, long historyId, string snippet)
    {
        public string Id { get; } = id;

        public string ThreadId { get; } = threadId;

        public HashSet<string> Labels { get; } = labels;

        public long InternalDateMs { get; } = internalDateMs;

        public byte[] Raw { get; } = raw;

        public long HistoryId { get; } = historyId;

        public string Snippet { get; } = snippet;
    }
}
