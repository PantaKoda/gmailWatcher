using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using GmailWatcher.Core.Configuration;
using GmailWatcher.Core.Mail;
using GmailWatcher.Google.Auth;
using Microsoft.Extensions.Logging;

namespace GmailWatcher.Google.Gmail;

/// <summary>
/// Read-only access to one Gmail mailbox over the REST API (<c>users/me/...</c>). Every call goes through one pipeline that
/// paces itself with the account's <see cref="QuotaGovernor"/>, signs requests with the account's token, retries what is
/// worth retrying and turns everything else into the exceptions <see cref="IMailbox"/> documents. Only GET requests are
/// ever sent.
/// </summary>
public sealed class GmailRestMailbox : IMailbox
{
    /// <summary>Tries per request, counting the first.</summary>
    internal const int MaxAttempts = 8;

    private const int PageSize = 500;

    /// <summary>
    /// One try, headers and body together. HttpClient's own timeout only covers the wait for headers, and a stalled
    /// download of a large message would otherwise block a worker forever.
    /// </summary>
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromMinutes(5);

    private static readonly (string Name, string Path)[] SettingsEndpoints =
    [
        ("filters", "settings/filters"),
        ("sendAs", "settings/sendAs"),
        ("forwardingAddresses", "settings/forwardingAddresses"),
        ("autoForwarding", "settings/autoForwarding"),
        ("vacation", "settings/vacation"),
        ("imap", "settings/imap"),
        ("pop", "settings/pop"),
        ("language", "settings/language"),
    ];

    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly AccessTokenProvider _tokens;
    private readonly QuotaGovernor _governor;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public GmailRestMailbox(HttpClient http, GoogleOptions options, AccessTokenProvider tokens, QuotaGovernor governor, TimeProvider time, ILogger logger)
    {
        _http = http;
        _tokens = tokens;
        _governor = governor;
        _time = time;
        _logger = logger;

        var baseUrl = options.ApiBaseUrl.EndsWith('/') ? options.ApiBaseUrl : options.ApiBaseUrl + "/";
        _baseUri = new Uri(baseUrl, UriKind.Absolute);
    }

    public async Task<MailboxProfile> GetProfileAsync(CancellationToken cancellationToken)
    {
        return await SendAsync("users/me/profile", GmailQuotaCosts.GetProfile, isSetting: false, Handle, cancellationToken).ConfigureAwait(false);

        static async Task<MailboxProfile> Handle(ApiResponse response, CancellationToken ct)
        {
            if (!response.IsSuccess)
            {
                throw Unexpected(response, "the account profile");
            }

            var dto = await ReadAsync(response, GoogleJsonContext.Default.ProfileDto, ct).ConfigureAwait(false);
            return new MailboxProfile(
                dto.EmailAddress ?? throw new InvalidDataException("Gmail's profile has no email address."),
                dto.MessagesTotal ?? 0,
                dto.ThreadsTotal ?? 0,
                dto.HistoryId ?? throw new InvalidDataException("Gmail's profile has no history ID."));
        }
    }

    public async Task<IReadOnlyList<RemoteLabel>> GetLabelsAsync(CancellationToken cancellationToken)
    {
        // One call for all labels. labels.get per label would also return counts, which the archive does not need.
        return await SendAsync("users/me/labels", GmailQuotaCosts.ListLabels, isSetting: false, Handle, cancellationToken).ConfigureAwait(false);

        static async Task<IReadOnlyList<RemoteLabel>> Handle(ApiResponse response, CancellationToken ct)
        {
            if (!response.IsSuccess)
            {
                throw Unexpected(response, "the label list");
            }

            var dto = await ReadAsync(response, GoogleJsonContext.Default.LabelListDto, ct).ConfigureAwait(false);
            var labels = new List<RemoteLabel>();
            foreach (var label in dto.Labels ?? [])
            {
                if (string.IsNullOrEmpty(label.Id))
                {
                    continue;
                }

                labels.Add(new RemoteLabel(label.Id, label.Name ?? label.Id, KindOf(label), label.Color?.BackgroundColor, label.Color?.TextColor));
            }

            return labels;
        }

        static LabelKind KindOf(LabelDto label) => label.Type switch
        {
            null => label.Id!.StartsWith("Label_", StringComparison.Ordinal) ? LabelKind.User : LabelKind.System,
            var type when type.Equals("system", StringComparison.OrdinalIgnoreCase) => LabelKind.System,
            _ => LabelKind.User,
        };
    }

    public async IAsyncEnumerable<string> ListMessageIdsAsync(string? labelId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? pageToken = null;
        do
        {
            var query = new QueryBuilder().Add("maxResults", PageSize).Add("includeSpamTrash", "true");
            if (!string.IsNullOrEmpty(labelId))
            {
                query.Add("labelIds", labelId);
            }

            query.Add("pageToken", pageToken);

            var page = await SendAsync($"users/me/messages{query}", GmailQuotaCosts.ListMessages, isSetting: false, Handle, cancellationToken).ConfigureAwait(false);
            foreach (var message in page.Messages ?? [])
            {
                if (!string.IsNullOrEmpty(message.Id))
                {
                    yield return message.Id;
                }
            }

            pageToken = string.IsNullOrEmpty(page.NextPageToken) ? null : page.NextPageToken;
        }
        while (pageToken is not null);

        static async Task<MessageListDto> Handle(ApiResponse response, CancellationToken ct) =>
            response.IsSuccess
                ? await ReadAsync(response, GoogleJsonContext.Default.MessageListDto, ct).ConfigureAwait(false)
                : throw Unexpected(response, "the message list");
    }

    public async Task<HistoryResult> GetHistoryAsync(string startHistoryId, CancellationToken cancellationToken)
    {
        var changes = new List<HistoryChange>();
        string? pageToken = null;
        string? latest = null;
        do
        {
            var query = new QueryBuilder()
                .Add("startHistoryId", startHistoryId)
                .Add("maxResults", PageSize)
                .Add("historyTypes", "messageAdded")
                .Add("historyTypes", "messageDeleted")
                .Add("historyTypes", "labelAdded")
                .Add("historyTypes", "labelRemoved")
                .Add("pageToken", pageToken);

            var page = await SendAsync($"users/me/history{query}", GmailQuotaCosts.ListHistory, isSetting: false, Handle, cancellationToken).ConfigureAwait(false);
            foreach (var record in page.History ?? [])
            {
                Flatten(record, changes);
            }

            latest = string.IsNullOrEmpty(page.HistoryId) ? latest : page.HistoryId;
            pageToken = string.IsNullOrEmpty(page.NextPageToken) ? null : page.NextPageToken;
        }
        while (pageToken is not null);

        return new HistoryResult(changes, latest ?? startHistoryId);

        static async Task<HistoryListDto> Handle(ApiResponse response, CancellationToken ct)
        {
            // Google documents 404 for a start ID it no longer has. A 400 (an ID it cannot make sense of) means the same to us:
            // the position can't be continued from, and a full pass is the safe answer. It costs listing time, never data.
            if (response.Status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                throw new HistoryExpiredException();
            }

            return response.IsSuccess
                ? await ReadAsync(response, GoogleJsonContext.Default.HistoryListDto, ct).ConfigureAwait(false)
                : throw Unexpected(response, "the change history");
        }

        // Order within one record: added, labels added, labels removed, deleted.
        static void Flatten(HistoryRecordDto record, List<HistoryChange> changes)
        {
            foreach (var item in record.MessagesAdded ?? [])
            {
                if (item.Message?.Id is { Length: > 0 } id)
                {
                    changes.Add(HistoryChange.Added(id));
                }
            }

            foreach (var item in record.LabelsAdded ?? [])
            {
                if (item.Message?.Id is { Length: > 0 } id)
                {
                    changes.Add(HistoryChange.Labels(id, item.LabelIds ?? [], []));
                }
            }

            foreach (var item in record.LabelsRemoved ?? [])
            {
                if (item.Message?.Id is { Length: > 0 } id)
                {
                    changes.Add(HistoryChange.Labels(id, [], item.LabelIds ?? []));
                }
            }

            foreach (var item in record.MessagesDeleted ?? [])
            {
                if (item.Message?.Id is { Length: > 0 } id)
                {
                    changes.Add(HistoryChange.Deleted(id));
                }
            }
        }
    }

    public async Task<RemoteMessage?> GetMessageAsync(string messageId, CancellationToken cancellationToken)
    {
        var path = $"users/me/messages/{Uri.EscapeDataString(messageId)}?format=raw";
        return await SendAsync(path, GmailQuotaCosts.GetMessage, isSetting: false, Handle, cancellationToken).ConfigureAwait(false);

        async Task<RemoteMessage?> Handle(ApiResponse response, CancellationToken ct)
        {
            if (response.Status == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccess)
            {
                throw Unexpected(response, "a message");
            }

            var dto = await ReadAsync(response, GoogleJsonContext.Default.MessageDto, ct).ConfigureAwait(false);
            var raw = dto.Raw ?? throw new InvalidDataException("Gmail returned a message without its content.");
            return new RemoteMessage(
                dto.Id ?? messageId,
                dto.ThreadId ?? string.Empty,
                dto.LabelIds ?? [],
                dto.HistoryId ?? string.Empty,
                long.TryParse(dto.InternalDate, NumberStyles.None, CultureInfo.InvariantCulture, out var internalDate) ? internalDate : 0,
                dto.SizeEstimate ?? raw.Length,
                dto.Snippet ?? string.Empty,
                raw);
        }
    }

    public async Task<IReadOnlyList<SettingsDocument>> GetSettingsAsync(CancellationToken cancellationToken)
    {
        var documents = new List<SettingsDocument>();
        foreach (var (name, path) in SettingsEndpoints)
        {
            var json = await SendAsync($"users/me/{path}", GmailQuotaCosts.GetSetting, isSetting: true, Handle, cancellationToken).ConfigureAwait(false);
            if (json is not null)
            {
                documents.Add(new SettingsDocument(name, json));
            }
            else
            {
                _logger.LogInformation("Gmail does not provide the {Setting} setting for this account; it is left out of the archive.", name);
            }
        }

        return documents;

        static async Task<string?> Handle(ApiResponse response, CancellationToken ct)
        {
            if (response.IsSuccess)
            {
                var text = await response.Message.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }

            // Not every account type has every setting (Workspace policies, missing delegation, ...).
            return response.Status is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
                ? null
                : throw Unexpected(response, "a mailbox setting");
        }
    }

    // ---- request pipeline ----

    private async Task<T> SendAsync<T>(string relativeUrl, int cost, bool isSetting, Func<ApiResponse, CancellationToken, Task<T>> handle, CancellationToken ct)
    {
        var uri = new Uri(_baseUri, relativeUrl);
        var authRejected = false;

        for (var attempt = 1; ; attempt++)
        {
            var outcome = await TryOnceAsync(uri, cost, isSetting, handle, ct).ConfigureAwait(false);
            switch (outcome.Status)
            {
                case AttemptStatus.Success:
                    return outcome.Value!;

                case AttemptStatus.Unauthorized when !authRejected && attempt < MaxAttempts:
                    // The token was revoked or expired early. A fresh one is fetched on the next try; a second 401 is final.
                    authRejected = true;
                    _logger.LogDebug("Gmail rejected the access token; trying again with a new one.");
                    continue;

                case AttemptStatus.Unauthorized:
                    throw new ReconnectRequiredException("Google no longer accepts this account's sign-in for Gmail. Sign in again to give Gmail Watcher read access.");
            }

            if (attempt >= MaxAttempts)
            {
                throw outcome.Status == AttemptStatus.RateLimited
                    ? new QuotaExhaustedException(
                        $"Gmail kept asking Gmail Watcher to slow down ({MaxAttempts} tries). The sync stops here and continues later.",
                        outcome.RetryAfter)
                    : new MailboxUnavailableException(
                        $"Gmail did not answer properly after {MaxAttempts} tries. A later sync continues where this one stopped.",
                        outcome.Error);
            }

            var delay = Backoff(attempt);
            _logger.LogDebug("Gmail request attempt {Attempt} of {Max} failed ({Reason}); retrying in {Delay}.", attempt, MaxAttempts, outcome.Status, delay);
            await Task.Delay(delay, _time, ct).ConfigureAwait(false);
        }
    }

    private async Task<Attempt<T>> TryOnceAsync<T>(Uri uri, int cost, bool isSetting, Func<ApiResponse, CancellationToken, Task<T>> handle, CancellationToken ct)
    {
        string token;
        try
        {
            await _governor.AcquireAsync(cost, ct).ConfigureAwait(false);
            token = await _tokens.GetAccessTokenAsync(ct).ConfigureAwait(false);
        }
        catch (MailboxUnavailableException ex)
        {
            // Refreshing the token needed Google and could not reach it. Same as any other network trouble.
            return Attempt<T>.Of(AttemptStatus.Transient, error: ex);
        }

        using var timeout = new CancellationTokenSource(AttemptTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);

            var status = response.StatusCode;
            if (status == HttpStatusCode.Unauthorized)
            {
                _tokens.Invalidate(token);
                return Attempt<T>.Of(AttemptStatus.Unauthorized);
            }

            if ((int)status >= 500 || status == HttpStatusCode.RequestTimeout)
            {
                return Attempt<T>.Of(AttemptStatus.Transient, error: new HttpRequestException($"Gmail answered HTTP {(int)status}.", null, status));
            }

            GoogleApiError? error = null;
            if (!response.IsSuccessStatusCode)
            {
                error = await ReadErrorAsync(response, linked.Token).ConfigureAwait(false);
                switch (GoogleApiError.Classify(status, error))
                {
                    case ApiErrorKind.ApiDisabled:
                        throw new MailboxUnavailableException(
                            "The Gmail API is not turned on for your Google Cloud project. In the Google Cloud console open APIs & Services > Library, "
                            + "enable \"Gmail API\" for the project that owns your OAuth client, wait a minute, then sync again.");

                    case ApiErrorKind.DailyLimit:
                        throw new QuotaExhaustedException("Gmail's daily usage limit for this account is used up. Syncing can continue once Google resets it.");

                    case ApiErrorKind.RateLimit:
                        var retryAfter = RetryAfterOf(response) ?? GoogleApiError.RetryAfterFromMessage(error?.Message, _time.GetUtcNow());
                        _governor.ReportRateLimited(retryAfter);
                        return Attempt<T>.Of(AttemptStatus.RateLimited, retryAfter);

                    case ApiErrorKind.InsufficientScope:
                    case ApiErrorKind.InsufficientPermission when !isSetting:
                        throw new ReconnectRequiredException("Gmail Watcher is not allowed to read this mailbox. Sign in again and allow Gmail read access.");
                }
            }

            _governor.ReportSuccess();
            return Attempt<T>.Of(AttemptStatus.Success, value: await handle(new ApiResponse(response, error), linked.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or JsonException or InvalidDataException)
        {
            // The attempt timed out, the connection broke (also halfway through a download) or the body was cut off.
            return Attempt<T>.Of(AttemptStatus.Transient, error: ex);
        }
    }

    private TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - _time.GetUtcNow();
            return wait > TimeSpan.Zero ? wait : null;
        }

        return null;
    }

    /// <summary>1 s, 2 s, 4 s ... up to 30 s, each scaled by a random 50-100% so parallel workers do not retry in lockstep.</summary>
    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt - 1), 30) * (0.5 + 0.5 * Random.Shared.NextDouble()));

    private static async Task<GoogleApiError?> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            return GoogleApiError.From(JsonSerializer.Deserialize(buffer.AsSpan(0, total), GoogleJsonContext.Default.ApiErrorEnvelopeDto));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<T> ReadAsync<T>(ApiResponse response, JsonTypeInfo<T> typeInfo, CancellationToken ct)
        where T : class
    {
        await using var stream = await response.Message.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, typeInfo, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("Gmail sent an empty answer.");
    }

    private static MailboxUnavailableException Unexpected(ApiResponse response, string what)
    {
        var detail = response.Error?.Message is { Length: > 0 } message ? ": " + (message.Length > 200 ? message[..200] : message) : string.Empty;
        return new MailboxUnavailableException($"Gmail refused to provide {what} (HTTP {(int)response.Status}{detail}).");
    }

    private sealed record ApiResponse(HttpResponseMessage Message, GoogleApiError? Error)
    {
        public HttpStatusCode Status => Message.StatusCode;

        public bool IsSuccess => Message.IsSuccessStatusCode;
    }

    private enum AttemptStatus
    {
        Success,
        Unauthorized,
        RateLimited,
        Transient,
    }

    private readonly record struct Attempt<T>(AttemptStatus Status, T? Value, Exception? Error, TimeSpan? RetryAfter)
    {
        public static Attempt<T> Of(AttemptStatus status, TimeSpan? retryAfter = null, T? value = default, Exception? error = null) =>
            new(status, value, error, retryAfter);
    }

    /// <summary>Builds <c>?a=1&amp;b=2</c> with every value escaped; null values are skipped.</summary>
    private sealed class QueryBuilder
    {
        private readonly StringBuilder _text = new();

        public QueryBuilder Add(string name, int value) => Add(name, value.ToString(CultureInfo.InvariantCulture));

        public QueryBuilder Add(string name, string? value)
        {
            if (value is not null)
            {
                _text.Append(_text.Length == 0 ? '?' : '&').Append(name).Append('=').Append(Uri.EscapeDataString(value));
            }

            return this;
        }

        public override string ToString() => _text.ToString();
    }
}
