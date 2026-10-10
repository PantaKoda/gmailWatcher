using System.Net;
using System.Text;
using GmailWatcher.Core.Mail;
using GmailWatcher.Google.Tests.Support;

namespace GmailWatcher.Google.Tests.Gmail;

public sealed class GmailRestMailboxReadTests
{
    private const string Api = "https://gmail.test/gmail/v1/";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly MailboxHarness _h = new();

    // ---- profile ----

    [Fact]
    public async Task ReadsTheProfile()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ProfileJson("me@example.com", 12345, 6789, "424242"));

        var profile = await _h.Mailbox.GetProfileAsync(Ct);

        Assert.Equal(new MailboxProfile("me@example.com", 12345, 6789, "424242"), profile);
        var request = Assert.Single(_h.Handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(Api + "users/me/profile", request.Url);
    }

    [Fact]
    public async Task SignsRequestsWithTheBearerTokenAndNothingElse()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ProfileJson());

        await _h.Mailbox.GetProfileAsync(Ct);

        var authorization = Assert.Single(_h.Handler.Requests).Authorization;
        Assert.Equal("Bearer", authorization?.Scheme);
        Assert.Equal(Fixtures.AccessToken, authorization?.Parameter);
        _h.AssertNoSecretsInUrls();
    }

    [Fact]
    public async Task AcceptsAHistoryIdSentAsANumber()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """{"emailAddress":"a@b.c","messagesTotal":1,"threadsTotal":1,"historyId":777}""");

        var profile = await _h.Mailbox.GetProfileAsync(Ct);

        Assert.Equal("777", profile.HistoryId);
    }

    [Fact]
    public async Task AnApiBaseUrlWithoutATrailingSlashStillWorks()
    {
        _h.Options.ApiBaseUrl = "https://gmail.test/gmail/v1";
        var mailbox = new GmailWatcher.Google.Gmail.GmailRestMailbox(_h.Http, _h.Options, _h.Tokens, _h.Governor, _h.Time, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        _h.Handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ProfileJson());

        await mailbox.GetProfileAsync(Ct);

        Assert.Equal(Api + "users/me/profile", Assert.Single(_h.Handler.Requests).Url);
    }

    // ---- labels ----

    [Fact]
    public async Task MapsLabelsInOneCall()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """
            {"labels":[
              {"id":"INBOX","name":"INBOX","messageListVisibility":"hide","labelListVisibility":"labelShow","type":"system"},
              {"id":"CATEGORY_PROMOTIONS","name":"CATEGORY_PROMOTIONS","type":"system"},
              {"id":"Label_12","name":"Receipts/2026","type":"user","color":{"textColor":"#ffffff","backgroundColor":"#16a766"}},
              {"id":"Label_13","name":"No colour","type":"user"}
            ]}
            """);

        var labels = await _h.Mailbox.GetLabelsAsync(Ct);

        Assert.Equal(4, labels.Count);
        Assert.Equal(new RemoteLabel("INBOX", "INBOX", LabelKind.System), labels[0]);
        Assert.Equal(new RemoteLabel("CATEGORY_PROMOTIONS", "CATEGORY_PROMOTIONS", LabelKind.System), labels[1]);
        Assert.Equal(new RemoteLabel("Label_12", "Receipts/2026", LabelKind.User, "#16a766", "#ffffff"), labels[2]);
        Assert.Equal(new RemoteLabel("Label_13", "No colour", LabelKind.User), labels[3]);
        Assert.Equal(Api + "users/me/labels", Assert.Single(_h.Handler.Requests).Url);
    }

    [Fact]
    public async Task ReadsLabelTypesWithoutRegardToCaseAndGuessesWhenMissing()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """
            {"labels":[
              {"id":"SENT","name":"SENT","type":"SYSTEM"},
              {"id":"Label_1","name":"Mine","type":"USER"},
              {"id":"SPAM","name":"SPAM"},
              {"id":"Label_2","name":"Mine too"},
              {"name":"has no id"}
            ]}
            """);

        var labels = await _h.Mailbox.GetLabelsAsync(Ct);

        Assert.Equal([LabelKind.System, LabelKind.User, LabelKind.System, LabelKind.User], labels.Select(label => label.Kind));
    }

    [Fact]
    public async Task AMailboxWithoutLabelsHasAnEmptyList()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.OK, "{}");

        Assert.Empty(await _h.Mailbox.GetLabelsAsync(Ct));
    }

    // ---- listing ----

    [Fact]
    public async Task ListsMessageIdsPageByPageAsTheSequenceIsConsumed()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """{"messages":[{"id":"m1","threadId":"t1"},{"id":"m2","threadId":"t2"}],"nextPageToken":"PAGE2","resultSizeEstimate":3}""");
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """{"messages":[{"id":"m3","threadId":"t3"}],"resultSizeEstimate":3}""");

        await using var ids = _h.Mailbox.ListMessageIdsAsync(null, Ct).GetAsyncEnumerator(Ct);

        Assert.Empty(_h.Handler.Requests);
        Assert.True(await ids.MoveNextAsync());
        Assert.Equal("m1", ids.Current);
        Assert.True(await ids.MoveNextAsync());
        Assert.Equal("m2", ids.Current);
        Assert.Single(_h.Handler.Requests);
        Assert.True(await ids.MoveNextAsync());
        Assert.Equal("m3", ids.Current);
        Assert.Equal(2, _h.Handler.Requests.Count);
        Assert.False(await ids.MoveNextAsync());
    }

    [Fact]
    public async Task ListingAsksForEveryMessageIncludingSpamAndTrash()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """{"messages":[{"id":"m1","threadId":"t1"}]}""");

        await ToListAsync(_h.Mailbox.ListMessageIdsAsync(null, Ct));

        var request = Assert.Single(_h.Handler.Requests);
        Assert.Equal(Api + "users/me/messages", request.Uri.GetLeftPart(UriPartial.Path));
        Assert.Equal("500", request.Query["maxResults"].Single());
        Assert.Equal("true", request.Query["includeSpamTrash"].Single());
        Assert.DoesNotContain("labelIds", request.Query.Select(group => group.Key));
        Assert.DoesNotContain("pageToken", request.Query.Select(group => group.Key));
    }

    [Fact]
    public async Task ListingOneLabelPassesTheLabelId()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """{"messages":[{"id":"m9","threadId":"t9"}]}""");

        var ids = await ToListAsync(_h.Mailbox.ListMessageIdsAsync("Label_12", Ct));

        Assert.Equal(["m9"], ids);
        var request = Assert.Single(_h.Handler.Requests);
        Assert.Equal("Label_12", request.Query["labelIds"].Single());
        Assert.Equal("true", request.Query["includeSpamTrash"].Single());
    }

    [Fact]
    public async Task APageTokenIsEscapedAndPassedBack()
    {
        const string token = "a/b+c==&d e";
        _h.Handler.EnqueueJson(HttpStatusCode.OK, $$"""{"messages":[{"id":"m1"}],"nextPageToken":"{{token}}"}""");
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """{"messages":[{"id":"m2"}]}""");

        await ToListAsync(_h.Mailbox.ListMessageIdsAsync("INBOX", Ct));

        var second = _h.Handler.Requests[1];
        Assert.Equal(token, second.Query["pageToken"].Single());
        Assert.Equal("INBOX", second.Query["labelIds"].Single());
        Assert.Equal("500", second.Query["maxResults"].Single());
    }

    [Fact]
    public async Task AnAbsentMessagesArrayIsAnEmptyPage()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """{"resultSizeEstimate":0}""");

        Assert.Empty(await ToListAsync(_h.Mailbox.ListMessageIdsAsync(null, Ct)));
        Assert.Single(_h.Handler.Requests);
    }

    [Fact]
    public async Task AnEmptyPageInTheMiddleDoesNotEndTheListing()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """{"messages":[{"id":"m1"}],"nextPageToken":"P2"}""");
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """{"nextPageToken":"P3"}""");
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """{"messages":[{"id":"m2"}]}""");

        Assert.Equal(["m1", "m2"], await ToListAsync(_h.Mailbox.ListMessageIdsAsync(null, Ct)));
    }

    [Fact]
    public async Task AnEmptyNextPageTokenEndsTheListing()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """{"messages":[{"id":"m1"}],"nextPageToken":""}""");

        Assert.Equal(["m1"], await ToListAsync(_h.Mailbox.ListMessageIdsAsync(null, Ct)));
        Assert.Single(_h.Handler.Requests);
    }

    [Fact]
    public async Task CancellingTheListingStopsFurtherRequests()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """{"messages":[{"id":"m1"}],"nextPageToken":"P2"}""");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var id in _h.Mailbox.ListMessageIdsAsync(null, cts.Token))
            {
                Assert.Equal("m1", id);
                await cts.CancelAsync();
            }
        });

        Assert.Single(_h.Handler.Requests);
    }

    // ---- history ----

    [Fact]
    public async Task FlattensHistoryInTheOrderGmailReportedIt()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """
            {"history":[
              {"id":"101","messages":[{"id":"m1","threadId":"t1"}],
               "messagesAdded":[{"message":{"id":"m1","threadId":"t1","labelIds":["INBOX","UNREAD"]}}]},
              {"id":"102","messages":[{"id":"m1","threadId":"t1"}],
               "labelsAdded":[{"message":{"id":"m1","threadId":"t1","labelIds":["INBOX","STARRED"]},"labelIds":["STARRED"]}],
               "labelsRemoved":[{"message":{"id":"m1","threadId":"t1","labelIds":["INBOX","STARRED"]},"labelIds":["UNREAD"]}]},
              {"id":"103",
               "messagesDeleted":[{"message":{"id":"m2","threadId":"t2"}}],
               "labelsRemoved":[{"message":{"id":"m4","threadId":"t4"},"labelIds":["INBOX","UNREAD"]}],
               "labelsAdded":[{"message":{"id":"m3","threadId":"t3"},"labelIds":["TRASH"]}],
               "messagesAdded":[{"message":{"id":"m5","threadId":"t5"}},{"message":{"id":"m6","threadId":"t6"}}]}
            ],"historyId":"900"}
            """);

        var result = await _h.Mailbox.GetHistoryAsync("100", Ct);

        Assert.Equal("900", result.LatestHistoryId);
        Assert.Equal(
            [
                (HistoryChangeKind.MessageAdded, "m1", "", ""),
                (HistoryChangeKind.LabelsChanged, "m1", "STARRED", ""),
                (HistoryChangeKind.LabelsChanged, "m1", "", "UNREAD"),
                (HistoryChangeKind.MessageAdded, "m5", "", ""),
                (HistoryChangeKind.MessageAdded, "m6", "", ""),
                (HistoryChangeKind.LabelsChanged, "m3", "TRASH", ""),
                (HistoryChangeKind.LabelsChanged, "m4", "", "INBOX,UNREAD"),
                (HistoryChangeKind.MessageDeleted, "m2", "", ""),
            ],
            result.Changes.Select(change => (change.Kind, change.MessageId, string.Join(',', change.LabelsAdded), string.Join(',', change.LabelsRemoved))));
    }

    [Fact]
    public async Task HistoryRequestsOnlyTheChangeTypesTheArchiveNeeds()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """{"historyId":"55"}""");

        await _h.Mailbox.GetHistoryAsync("50", Ct);

        var request = Assert.Single(_h.Handler.Requests);
        Assert.Equal(Api + "users/me/history", request.Uri.GetLeftPart(UriPartial.Path));
        Assert.Equal("50", request.Query["startHistoryId"].Single());
        Assert.Equal("500", request.Query["maxResults"].Single());
        Assert.Equal(["messageAdded", "messageDeleted", "labelAdded", "labelRemoved"], request.Query["historyTypes"]);
        Assert.Contains("historyTypes=messageAdded&historyTypes=messageDeleted", request.Uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("pageToken", request.Query.Select(group => group.Key));
    }

    [Fact]
    public async Task HistoryFollowsEveryPageAndReportsTheLastPagesHistoryId()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """
            {"history":[{"id":"1","messagesAdded":[{"message":{"id":"a"}}]}],"nextPageToken":"H2","historyId":"200"}
            """);
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """
            {"history":[{"id":"2","messagesDeleted":[{"message":{"id":"b"}}]}],"nextPageToken":"H3","historyId":"250"}
            """);
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """
            {"history":[{"id":"3","messagesAdded":[{"message":{"id":"c"}}]}],"historyId":"300"}
            """);

        var result = await _h.Mailbox.GetHistoryAsync("100", Ct);

        Assert.Equal(["a", "b", "c"], result.Changes.Select(change => change.MessageId));
        Assert.Equal("300", result.LatestHistoryId);
        Assert.Equal(3, _h.Handler.Requests.Count);
        Assert.Equal("H2", _h.Handler.Requests[1].Query["pageToken"].Single());
        Assert.Equal("H3", _h.Handler.Requests[2].Query["pageToken"].Single());
        Assert.All(_h.Handler.Requests, request => Assert.Equal("100", request.Query["startHistoryId"].Single()));
    }

    [Fact]
    public async Task AResponseWithoutAHistoryArrayMeansNoChanges()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """{"historyId":"12345"}""");

        var result = await _h.Mailbox.GetHistoryAsync("12000", Ct);

        Assert.Empty(result.Changes);
        Assert.Equal("12345", result.LatestHistoryId);
    }

    [Fact]
    public async Task WithoutAnyHistoryIdInTheAnswerTheStartingPointIsKept()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.OK, "{}");

        var result = await _h.Mailbox.GetHistoryAsync("777", Ct);

        Assert.Equal("777", result.LatestHistoryId);
    }

    [Fact]
    public async Task ARecordWithOnlyAMessagesListYieldsNoChange()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """{"history":[{"id":"7","messages":[{"id":"m1","threadId":"t1"}]}],"historyId":"8"}""");

        var result = await _h.Mailbox.GetHistoryAsync("6", Ct);

        Assert.Empty(result.Changes);
    }

    [Fact]
    public async Task ExpiredHistoryIsReportedAsSuch()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.NotFound, Fixtures.ApiError(404, "Requested entity was not found.", "notFound", "NOT_FOUND"));

        await Assert.ThrowsAsync<HistoryExpiredException>(() => _h.Mailbox.GetHistoryAsync("1", Ct));

        Assert.Single(_h.Handler.Requests);
    }

    [Fact]
    public async Task AHistoryIdGoogleCannotMakeSenseOfCountsAsExpiredToo()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.BadRequest, Fixtures.ApiError(400, "Invalid startHistoryId.", "invalidArgument", "INVALID_ARGUMENT"));

        await Assert.ThrowsAsync<HistoryExpiredException>(() => _h.Mailbox.GetHistoryAsync("not-a-number", Ct));
    }

    [Fact]
    public async Task ExpiredHistoryOnALaterPageIsReportedAsSuchToo()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """{"history":[{"id":"1","messagesAdded":[{"message":{"id":"a"}}]}],"nextPageToken":"H2","historyId":"2"}""");
        _h.Handler.EnqueueJson(HttpStatusCode.NotFound, "{}");

        await Assert.ThrowsAsync<HistoryExpiredException>(() => _h.Mailbox.GetHistoryAsync("1", Ct));
    }

    // ---- messages ----

    [Fact]
    public async Task DownloadsAMessageInRawFormat()
    {
        var raw = Encoding.UTF8.GetBytes("From: a@example.com\r\nSubject: Hello\r\n\r\nBody text\r\n");
        _h.Handler.EnqueueJson(HttpStatusCode.OK, MessageJson("m1", raw));

        var message = await _h.Mailbox.GetMessageAsync("m1", Ct);

        Assert.NotNull(message);
        Assert.Equal("m1", message.Id);
        Assert.Equal("t-m1", message.ThreadId);
        Assert.Equal(["INBOX", "UNREAD"], message.LabelIds);
        Assert.Equal("4242", message.HistoryId);
        Assert.Equal(1_760_000_000_000L, message.InternalDateMs);
        Assert.Equal(321, message.SizeEstimate);
        Assert.Equal("Hi &#39;there&#39;", message.Snippet);
        Assert.Equal(raw, message.Raw);
        var request = Assert.Single(_h.Handler.Requests);
        Assert.Equal(Api + "users/me/messages/m1", request.Uri.GetLeftPart(UriPartial.Path));
        Assert.Equal("raw", request.Query["format"].Single());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(1000)]
    [InlineData(1001)]
    [InlineData(1002)]
    public async Task DecodesRawContentWithAndWithoutPadding(int length)
    {
        var raw = new byte[length];
        new Random(length).NextBytes(raw);
        var standard = Convert.ToBase64String(raw);
        var urlSafe = standard.Replace('+', '-').Replace('/', '_');

        _h.Handler.EnqueueJson(HttpStatusCode.OK, MessageJson("m1", urlSafe));
        _h.Handler.EnqueueJson(HttpStatusCode.OK, MessageJson("m1", urlSafe.TrimEnd('=')));
        _h.Handler.EnqueueJson(HttpStatusCode.OK, MessageJson("m1", standard));
        _h.Handler.EnqueueJson(HttpStatusCode.OK, MessageJson("m1", standard.TrimEnd('=')));

        for (var i = 0; i < 4; i++)
        {
            var message = await _h.Mailbox.GetMessageAsync("m1", Ct);
            Assert.Equal(raw, message!.Raw);
        }
    }

    [Fact]
    public async Task KeepsEveryByteValueAndNonAsciiText()
    {
        var text = Encoding.UTF8.GetBytes("Subject: =?UTF-8?B?w6TDtsO8?= Grüße 日本語 \U0001F600\r\n\r\nSmörgåsbord ✓\r\n");
        var everyByte = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        _h.Handler.EnqueueJson(HttpStatusCode.OK, MessageJson("m1", text));
        _h.Handler.EnqueueJson(HttpStatusCode.OK, MessageJson("m2", everyByte));

        var first = await _h.Mailbox.GetMessageAsync("m1", Ct);
        var second = await _h.Mailbox.GetMessageAsync("m2", Ct);

        Assert.Equal(text, first!.Raw);
        Assert.Equal(everyByte, second!.Raw);
    }

    [Fact]
    public async Task DecodesAnEscapedSlashInTheBase64()
    {
        // Some JSON writers escape "/"; the standard alphabet then arrives as "\/".
        var raw = Enumerable.Repeat((byte)0xFF, 30).ToArray();
        var standard = Convert.ToBase64String(raw);
        Assert.Contains('/', standard);
        _h.Handler.EnqueueJson(HttpStatusCode.OK, MessageJson("m1", standard.Replace("/", "\\/", StringComparison.Ordinal)));

        var message = await _h.Mailbox.GetMessageAsync("m1", Ct);

        Assert.Equal(raw, message!.Raw);
    }

    [Fact]
    public async Task DownloadsALargeMessageIntact()
    {
        var raw = new byte[12 * 1024 * 1024];
        new Random(42).NextBytes(raw);
        _h.Handler.EnqueueJson(HttpStatusCode.OK, MessageJson("big", raw));

        var message = await _h.Mailbox.GetMessageAsync("big", Ct);

        Assert.Equal(raw.Length, message!.Raw.Length);
        Assert.True(raw.AsSpan().SequenceEqual(message.Raw), "the downloaded bytes differ from the original");
    }

    [Fact]
    public async Task AMessageThatNoLongerExistsIsNull()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.NotFound, Fixtures.ApiError(404, "Requested entity was not found.", "notFound", "NOT_FOUND"));

        var message = await _h.Mailbox.GetMessageAsync("gone", Ct);

        Assert.Null(message);
        Assert.Single(_h.Handler.Requests);
    }

    [Fact]
    public async Task FallsBackToTheRequestedIdAndZeroForMissingFields()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.OK, $$"""{"raw":"{{Convert.ToBase64String("x"u8.ToArray())}}"}""");

        var message = await _h.Mailbox.GetMessageAsync("asked-for", Ct);

        Assert.Equal("asked-for", message!.Id);
        Assert.Equal(0, message.InternalDateMs);
        Assert.Empty(message.LabelIds);
        Assert.Equal(1, message.SizeEstimate);
        Assert.Equal(string.Empty, message.Snippet);
    }

    [Fact]
    public async Task TheMessageIdIsEscapedInTheUrl()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.NotFound, "{}");

        await _h.Mailbox.GetMessageAsync("a/b c?d", Ct);

        var request = Assert.Single(_h.Handler.Requests);
        Assert.Equal("/gmail/v1/users/me/messages/a%2Fb%20c%3Fd", request.Uri.AbsolutePath);
        Assert.Equal("raw", request.Query["format"].Single());
    }

    // ---- settings ----

    [Fact]
    public async Task ReadsEveryMailboxSettingUnchanged()
    {
        var expected = new (string Name, string Path, string Json)[]
        {
            ("filters", "settings/filters", """{ "filter": [ {"id":"f1"} ] }"""),
            ("sendAs", "settings/sendAs", """{"sendAs":[{"sendAsEmail":"me@example.com","isPrimary":true}]}"""),
            ("forwardingAddresses", "settings/forwardingAddresses", "{}"),
            ("autoForwarding", "settings/autoForwarding", """{"enabled":false}"""),
            ("vacation", "settings/vacation", """{"enableAutoReply":false}"""),
            ("imap", "settings/imap", """{"enabled":true}"""),
            ("pop", "settings/pop", """{"accessWindow":"disabled"}"""),
            ("language", "settings/language", """{"displayLanguage":"en-GB"}"""),
        };
        foreach (var item in expected)
        {
            _h.Handler.EnqueueJson(HttpStatusCode.OK, item.Json);
        }

        var documents = await _h.Mailbox.GetSettingsAsync(Ct);

        Assert.Equal(expected.Select(item => new SettingsDocument(item.Name, item.Json)), documents);
        Assert.Equal(expected.Select(item => Api + "users/me/" + item.Path), _h.Handler.Requests.Select(request => request.Url));
    }

    [Fact]
    public async Task LeavesOutSettingsTheAccountDoesNotAllow()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """{"filter":[]}""");
        _h.Handler.EnqueueJson(HttpStatusCode.Forbidden, "{}");
        _h.Handler.EnqueueJson(HttpStatusCode.Forbidden, Fixtures.ApiError(403, "Forbidden", "forbidden"));
        _h.Handler.EnqueueJson(HttpStatusCode.NotFound, Fixtures.ApiError(404, "Not found", "notFound", "NOT_FOUND"));
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """{"enableAutoReply":false}""");
        _h.Handler.EnqueueJson(HttpStatusCode.BadRequest, Fixtures.ApiError(400, "Mail service not enabled", "failedPrecondition", "FAILED_PRECONDITION"));
        _h.Handler.EnqueueJson(HttpStatusCode.Forbidden, Fixtures.ApiError(403, "Insufficient Permission", "insufficientPermissions"));
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """{"displayLanguage":"sv"}""");

        var documents = await _h.Mailbox.GetSettingsAsync(Ct);

        Assert.Equal(["filters", "vacation", "language"], documents.Select(document => document.Name));
        Assert.Equal(8, _h.Handler.Requests.Count);
    }

    [Fact]
    public async Task ASettingsCallStillRetriesRateLimitsAndKeepsTheDocument()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.TooManyRequests, Fixtures.ApiError(429, "Too many requests", "rateLimitExceeded", "RESOURCE_EXHAUSTED"), TimeSpan.FromSeconds(10));
        for (var i = 0; i < 8; i++)
        {
            _h.Handler.EnqueueJson(HttpStatusCode.OK, "{}");
        }

        var documents = await _h.RunAsync(_h.Mailbox.GetSettingsAsync(Ct));

        Assert.Equal(8, documents.Count);
        Assert.Equal(9, _h.Handler.Requests.Count);
    }

    [Fact]
    public async Task AMissingGmailScopeIsNotMistakenForAnUnavailableSetting()
    {
        _h.Handler.EnqueueJson(
            HttpStatusCode.Forbidden,
            Fixtures.ApiError(403, "Request had insufficient authentication scopes.", "insufficientPermissions", "PERMISSION_DENIED", "ACCESS_TOKEN_SCOPE_INSUFFICIENT"));

        await Assert.ThrowsAsync<ReconnectRequiredException>(() => _h.Mailbox.GetSettingsAsync(Ct));
    }

    // ---- everything together ----

    [Fact]
    public async Task OnlyEverSendsGetRequestsAndNeverPutsATokenInAUrl()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ProfileJson());
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """{"labels":[{"id":"INBOX","name":"INBOX","type":"system"}]}""");
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """{"messages":[{"id":"m1"}],"nextPageToken":"P2"}""");
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """{"messages":[{"id":"m2"}]}""");
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """{"historyId":"5"}""");
        _h.Handler.EnqueueJson(HttpStatusCode.OK, MessageJson("m1", "hello"u8.ToArray()));
        for (var i = 0; i < 8; i++)
        {
            _h.Handler.EnqueueJson(HttpStatusCode.OK, "{}");
        }

        await _h.Mailbox.GetProfileAsync(Ct);
        await _h.Mailbox.GetLabelsAsync(Ct);
        await ToListAsync(_h.Mailbox.ListMessageIdsAsync("INBOX", Ct));
        await _h.Mailbox.GetHistoryAsync("4", Ct);
        await _h.Mailbox.GetMessageAsync("m1", Ct);
        await _h.Mailbox.GetSettingsAsync(Ct);

        Assert.Equal(14, _h.Handler.Requests.Count);
        Assert.All(_h.Handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.All(_h.Handler.Requests, request => Assert.Null(request.Body));
        Assert.All(_h.Handler.Requests, request => Assert.Equal(Fixtures.AccessToken, request.Authorization?.Parameter));
        _h.AssertNoSecretsInUrls();
    }

    // 600 units a minute is a burst of 100 units: how many calls fit before the governor makes one wait shows what each costs.

    [Fact]
    public Task ADownloadIsChargedTwentyUnits() =>
        AssertChargedAsync(20, MessageJson("m", "x"u8.ToArray()), harness => harness.Mailbox.GetMessageAsync("m", Ct));

    [Fact]
    public Task AMessageListPageIsChargedFiveUnits() =>
        AssertChargedAsync(5, """{"messages":[{"id":"m"}]}""", async harness => await ToListAsync(harness.Mailbox.ListMessageIdsAsync(null, Ct)));

    [Fact]
    public Task AHistoryPageIsChargedTwoUnits() =>
        AssertChargedAsync(2, """{"historyId":"1"}""", harness => harness.Mailbox.GetHistoryAsync("1", Ct));

    [Fact]
    public Task TheProfileIsChargedOneUnit() =>
        AssertChargedAsync(1, Fixtures.ProfileJson(), harness => harness.Mailbox.GetProfileAsync(Ct));

    [Fact]
    public Task TheLabelListIsChargedOneUnit() =>
        AssertChargedAsync(1, """{"labels":[]}""", harness => harness.Mailbox.GetLabelsAsync(Ct));

    [Fact]
    public Task EachSettingIsChargedOneUnit() =>
        AssertChargedAsync(8, "{}", harness => harness.Mailbox.GetSettingsAsync(Ct), eightRequestsPerCall: true);

    private static async Task AssertChargedAsync(int cost, string responseJson, Func<MailboxHarness, Task> call, bool eightRequestsPerCall = false)
    {
        var harness = new MailboxHarness(unitsPerMinute: 600);
        harness.Handler.AlwaysJson(HttpStatusCode.OK, responseJson);

        for (var spent = 0; spent + cost <= 100; spent += cost)
        {
            await call(harness);
        }

        var requestsBefore = harness.Handler.Requests.Count;
        var next = call(harness);
        Assert.False(next.IsCompleted, "the call after the burst should have been made to wait");
        if (!eightRequestsPerCall)
        {
            Assert.Equal(requestsBefore, harness.Handler.Requests.Count);
        }

        harness.Time.Advance(TimeSpan.FromSeconds(cost));
        await next.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    // ---- helpers ----

    private static string MessageJson(string id, byte[] raw) => MessageJson(id, Convert.ToBase64String(raw).Replace('+', '-').Replace('/', '_').TrimEnd('='));

    private static string MessageJson(string id, string base64) =>
        $$"""{"id":"{{id}}","threadId":"t-{{id}}","labelIds":["INBOX","UNREAD"],"snippet":"Hi &#39;there&#39;","historyId":"4242","internalDate":"1760000000000","sizeEstimate":321,"raw":"{{base64}}"}""";

    private static async Task<List<string>> ToListAsync(IAsyncEnumerable<string> source)
    {
        var items = new List<string>();
        await foreach (var item in source)
        {
            items.Add(item);
        }

        return items;
    }
}
