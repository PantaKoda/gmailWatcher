using System.Net;
using System.Net.Http.Headers;
using System.Text;
using GmailWatcher.Core.Mail;
using GmailWatcher.Google.Tests.Support;

namespace GmailWatcher.Google.Tests.Gmail;

/// <summary>How the request pipeline reacts when Gmail or the network does not simply answer.</summary>
public sealed class GmailRestMailboxErrorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly MailboxHarness _h = new();

    private static string RateLimit(int code = 429, string reason = "rateLimitExceeded", string message = "Too many requests") =>
        Fixtures.ApiError(code, message, reason, code == 429 ? "RESOURCE_EXHAUSTED" : "PERMISSION_DENIED");

    private TimeSpan Gap(int from, int to) => _h.GmailRequest(to).At - _h.GmailRequest(from).At;

    // ---- 401 ----

    [Fact]
    public async Task ARejectedTokenIsReplacedOnceAndTheRequestRepeated()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.Unauthorized, Fixtures.ApiError(401, "Invalid Credentials", "authError", "UNAUTHENTICATED"));
        _h.Handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(access: "access-2", refresh: null));
        _h.Handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ProfileJson("me@example.com"));

        var profile = await _h.Mailbox.GetProfileAsync(Ct);

        Assert.Equal("me@example.com", profile.EmailAddress);
        Assert.Equal(["gmail.test", "oauth.test", "gmail.test"], _h.Handler.Requests.Select(request => request.Uri.Host));
        Assert.Equal(Fixtures.AccessToken, _h.GmailRequest(0).Authorization?.Parameter);
        Assert.Equal("access-2", _h.GmailRequest(1).Authorization?.Parameter);
        _h.AssertNoSecretsInUrls();
    }

    [Fact]
    public async Task ASecondRejectionMeansTheUserMustReconnect()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.Unauthorized, "{}");
        _h.Handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(access: "access-2", refresh: null));
        _h.Handler.EnqueueJson(HttpStatusCode.Unauthorized, "{}");

        var ex = await Assert.ThrowsAsync<ReconnectRequiredException>(() => _h.Mailbox.GetProfileAsync(Ct));

        Assert.Equal(3, _h.Handler.Requests.Count);
        MailboxHarness.AssertNoSecrets(ex);
    }

    [Fact]
    public async Task ARevokedRefreshTokenAfterARejectionMeansReconnect()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.Unauthorized, "{}");
        _h.Handler.EnqueueJson(HttpStatusCode.BadRequest, Fixtures.OAuthError("invalid_grant", "Token has been expired or revoked."));

        var ex = await Assert.ThrowsAsync<ReconnectRequiredException>(() => _h.Mailbox.GetMessageAsync("m1", Ct));

        Assert.Equal(2, _h.Handler.Requests.Count);
        MailboxHarness.AssertNoSecrets(ex);
    }

    [Fact]
    public async Task AFirstCallWithoutATokenRefreshesItBeforeAskingGmail()
    {
        var harness = new MailboxHarness(tokenReady: false);
        harness.Handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(access: "fresh", refresh: null));
        harness.Handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ProfileJson());

        await harness.Mailbox.GetProfileAsync(Ct);

        Assert.Equal("oauth.test", harness.Handler.Requests[0].Uri.Host);
        Assert.Equal("fresh", harness.GmailRequest(0).Authorization?.Parameter);
    }

    [Fact]
    public async Task TheStoredCredentialBeingGoneMeansReconnect()
    {
        var harness = new MailboxHarness(tokenReady: false);
        await harness.Store.DeleteAsync(Fixtures.Email, Ct);

        await Assert.ThrowsAsync<ReconnectRequiredException>(() => harness.Mailbox.GetProfileAsync(Ct));

        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task ATemporaryOutageWhileRefreshingTheTokenIsRetried()
    {
        var harness = new MailboxHarness(tokenReady: false);
        harness.Handler.EnqueueJson(HttpStatusCode.ServiceUnavailable, "{}");
        harness.Handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(access: "later", refresh: null));
        harness.Handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ProfileJson());

        await harness.RunAsync(harness.Mailbox.GetProfileAsync(Ct));

        Assert.Equal(2, harness.TokenRequests.Count);
        Assert.Equal("later", harness.GmailRequest(0).Authorization?.Parameter);
        Assert.True(harness.TokenRequests[1].At - harness.TokenRequests[0].At >= TimeSpan.FromMilliseconds(500));
    }

    // ---- rate limits ----

    [Theory]
    [InlineData(429, "rateLimitExceeded")]
    [InlineData(429, "userRateLimitExceeded")]
    [InlineData(403, "rateLimitExceeded")]
    [InlineData(403, "userRateLimitExceeded")]
    public async Task RateLimitsAreRetriedAfterAPauseAndSlowTheAccountDown(int code, string reason)
    {
        _h.Handler.EnqueueJson((HttpStatusCode)code, RateLimit(code, reason));
        _h.Handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ProfileJson());

        var profile = await _h.RunAsync(_h.Mailbox.GetProfileAsync(Ct));

        Assert.Equal(Fixtures.Email, profile.EmailAddress);
        Assert.Equal(2, _h.GmailRequests.Count);
        Assert.True(Gap(0, 1) >= TimeSpan.FromSeconds(5), $"retried after only {Gap(0, 1)}");
        Assert.True(_h.Governor.CurrentRateMultiplier < 1.0);
    }

    [Fact]
    public async Task ATooManyRequestsAnswerWithoutAnyJsonIsStillARateLimit()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.TooManyRequests, "<html>Too Many Requests</html>");
        _h.Handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ProfileJson());

        await _h.RunAsync(_h.Mailbox.GetProfileAsync(Ct));

        Assert.True(Gap(0, 1) >= TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ARetryAfterHeaderIsHonoured()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.TooManyRequests, RateLimit(), retryAfter: TimeSpan.FromSeconds(30));
        _h.Handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ProfileJson());

        await _h.RunAsync(_h.Mailbox.GetProfileAsync(Ct));

        Assert.True(Gap(0, 1) >= TimeSpan.FromSeconds(30), $"retried after only {Gap(0, 1)}");
        Assert.True(Gap(0, 1) < TimeSpan.FromSeconds(40), $"waited far longer than asked: {Gap(0, 1)}");
    }

    [Fact]
    public async Task ARetryAfterGivenAsADateIsHonouredToo()
    {
        var response = ScriptedHandler.Json(HttpStatusCode.TooManyRequests, RateLimit());
        response.Headers.RetryAfter = new RetryConditionHeaderValue(Fixtures.Start.AddSeconds(45));
        _h.Handler.Enqueue(response);
        _h.Handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ProfileJson());

        await _h.RunAsync(_h.Mailbox.GetProfileAsync(Ct));

        Assert.True(Gap(0, 1) >= TimeSpan.FromSeconds(45), $"retried after only {Gap(0, 1)}");
    }

    [Fact]
    public async Task TheRetryTimeInGmailsOwnMessageIsUsedWhenThereIsNoHeader()
    {
        _h.Handler.EnqueueJson(
            HttpStatusCode.Forbidden,
            RateLimit(403, "userRateLimitExceeded", "User-rate limit exceeded.  Retry after 2026-10-10T12:01:00.000Z"));
        _h.Handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ProfileJson());

        await _h.RunAsync(_h.Mailbox.GetProfileAsync(Ct));

        Assert.True(Gap(0, 1) >= TimeSpan.FromSeconds(60), $"retried after only {Gap(0, 1)}");
    }

    [Fact]
    public async Task EveryTryWaitsForTheGovernorsPauseNotOnlyItsOwnBackoff()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.TooManyRequests, RateLimit());
        _h.Handler.EnqueueJson(HttpStatusCode.TooManyRequests, RateLimit());
        _h.Handler.EnqueueJson(HttpStatusCode.TooManyRequests, RateLimit());
        _h.Handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ProfileJson());

        await _h.RunAsync(_h.Mailbox.GetProfileAsync(Ct));

        // The governor pauses 5 s, 10 s and 20 s for the three limits.
        Assert.True(Gap(0, 1) >= TimeSpan.FromSeconds(5));
        Assert.True(Gap(1, 2) >= TimeSpan.FromSeconds(10));
        Assert.True(Gap(2, 3) >= TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task AllRequestsOfTheAccountPauseWhenOneIsRateLimited()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.TooManyRequests, RateLimit(), retryAfter: TimeSpan.FromSeconds(30));
        _h.Handler.Always(request => ScriptedHandler.Json(
            HttpStatusCode.OK,
            request.PathAndQuery.EndsWith("/profile", StringComparison.Ordinal) ? Fixtures.ProfileJson() : """{"labels":[]}"""));

        var limited = _h.Mailbox.GetProfileAsync(Ct);
        var bystander = _h.Mailbox.GetLabelsAsync(Ct);
        await _h.RunAsync(Task.WhenAll(limited, bystander));

        Assert.Equal(3, _h.GmailRequests.Count);
        var first = _h.GmailRequest(0).At;
        Assert.True(_h.GmailRequest(1).At - first >= TimeSpan.FromSeconds(30));
        Assert.True(_h.GmailRequest(2).At - first >= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task KeepsGettingRateLimitedMeansQuotaExhaustedAfterEightTries()
    {
        _h.Handler.AlwaysJson(HttpStatusCode.TooManyRequests, RateLimit());

        var ex = await Assert.ThrowsAsync<QuotaExhaustedException>(() => _h.RunAsync(_h.Mailbox.GetProfileAsync(Ct)));

        // Eight attempts. Under heavy load the clock can cancel an attempt before the handler has recorded it, hence the range.
        Assert.InRange(_h.GmailRequests.Count, 7, 8);
        Assert.Equal(0.2, _h.Governor.CurrentRateMultiplier, 6);
        MailboxHarness.AssertNoSecrets(ex);
    }

    [Fact]
    public async Task ASuccessfulCallAfterTheLimitStartsWinningBackSpeed()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.TooManyRequests, RateLimit());
        _h.Handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ProfileJson());
        _h.Handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ProfileJson());
        await _h.RunAsync(_h.Mailbox.GetProfileAsync(Ct));
        var slowed = _h.Governor.CurrentRateMultiplier;

        _h.Time.Advance(TimeSpan.FromMinutes(3));
        await _h.Mailbox.GetProfileAsync(Ct);

        Assert.True(slowed < 0.71);
        Assert.True(_h.Governor.CurrentRateMultiplier >= slowed + 0.049, $"{slowed} -> {_h.Governor.CurrentRateMultiplier}");
    }

    [Fact]
    public async Task ServerTroubleDoesNotSlowTheAccountDown()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.ServiceUnavailable, "{}");
        _h.Handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ProfileJson());

        await _h.RunAsync(_h.Mailbox.GetProfileAsync(Ct));

        Assert.Equal(1.0, _h.Governor.CurrentRateMultiplier);
    }

    // ---- daily limit ----

    [Theory]
    [InlineData("dailyLimitExceeded", "Daily Limit Exceeded")]
    [InlineData("dailyLimitExceededUnreg", "Daily Limit for Unauthenticated Use Exceeded.")]
    [InlineData("rateLimitExceeded", "Quota exceeded for quota metric 'Queries' and limit 'Queries per day' of service 'gmail.googleapis.com'")]
    public async Task TheDailyLimitIsNotRetried(string reason, string message)
    {
        _h.Handler.EnqueueJson(HttpStatusCode.Forbidden, Fixtures.ApiError(403, message, reason, "PERMISSION_DENIED"));

        var ex = await Assert.ThrowsAsync<QuotaExhaustedException>(() => _h.Mailbox.GetMessageAsync("m1", Ct));

        Assert.Single(_h.Handler.Requests);
        Assert.Equal(1.0, _h.Governor.CurrentRateMultiplier);
        MailboxHarness.AssertNoSecrets(ex);
    }

    // ---- permissions and setup ----

    [Theory]
    [InlineData("insufficientPermissions", null)]
    [InlineData("insufficientPermissions", "ACCESS_TOKEN_SCOPE_INSUFFICIENT")]
    [InlineData(null, "ACCESS_TOKEN_SCOPE_INSUFFICIENT")]
    public async Task MissingGmailReadAccessMeansReconnect(string? reason, string? detail)
    {
        _h.Handler.EnqueueJson(
            HttpStatusCode.Forbidden,
            Fixtures.ApiError(403, "Request had insufficient authentication scopes.", reason, "PERMISSION_DENIED", detail));

        var ex = await Assert.ThrowsAsync<ReconnectRequiredException>(() => _h.Mailbox.GetProfileAsync(Ct));

        Assert.Single(_h.Handler.Requests);
        Assert.Contains("read access", ex.Message, StringComparison.OrdinalIgnoreCase);
        MailboxHarness.AssertNoSecrets(ex);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "accessNotConfigured", null)]
    [InlineData(HttpStatusCode.Forbidden, null, "SERVICE_DISABLED")]
    [InlineData(HttpStatusCode.NotFound, "accessNotConfigured", null)]
    [InlineData(HttpStatusCode.Forbidden, "accessNotConfigured", "SERVICE_DISABLED")]
    public async Task ADisabledGmailApiAsksTheUserToEnableItInTheirProject(HttpStatusCode status, string? reason, string? detail)
    {
        _h.Handler.EnqueueJson(
            status,
            Fixtures.ApiError((int)status, "Gmail API has not been used in project 123456789012 before or it is disabled.", reason, "PERMISSION_DENIED", detail));

        var ex = await Assert.ThrowsAsync<MailboxUnavailableException>(() => _h.Mailbox.GetProfileAsync(Ct));

        Assert.Single(_h.Handler.Requests);
        Assert.Contains("Gmail API", ex.Message, StringComparison.Ordinal);
        Assert.Contains("enable", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Google Cloud", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADisabledGmailApiOnTheHistoryEndpointIsNotMistakenForExpiredHistory()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.NotFound, Fixtures.ApiError(404, "Gmail API has not been used.", "accessNotConfigured"));

        await Assert.ThrowsAsync<MailboxUnavailableException>(() => _h.Mailbox.GetHistoryAsync("1", Ct));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AnUnexpectedRefusalIsReportedWithItsStatusAndNotRetried(HttpStatusCode status)
    {
        _h.Handler.EnqueueJson(status, Fixtures.ApiError((int)status, "Something Gmail Watcher does not know about.", "weirdReason", "FAILED_PRECONDITION"));

        var ex = await Assert.ThrowsAsync<MailboxUnavailableException>(() => _h.Mailbox.GetProfileAsync(Ct));

        Assert.Single(_h.Handler.Requests);
        Assert.Contains($"HTTP {(int)status}", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Something Gmail Watcher does not know about.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusalWithAHugeNonJsonBodyIsStillReportedCleanly()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.Forbidden, new string('x', 2 * 1024 * 1024));

        var ex = await Assert.ThrowsAsync<MailboxUnavailableException>(() => _h.Mailbox.GetLabelsAsync(Ct));

        Assert.Contains("HTTP 403", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.Message.Length < 400);
    }

    // ---- server and network trouble ----

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task ServerErrorsAreRetriedWithABackoff(HttpStatusCode status)
    {
        _h.Handler.EnqueueJson(status, "<html>oops</html>");
        _h.Handler.EnqueueJson(status, "<html>oops</html>");
        _h.Handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ProfileJson());

        var profile = await _h.RunAsync(_h.Mailbox.GetProfileAsync(Ct));

        Assert.Equal(Fixtures.Email, profile.EmailAddress);
        Assert.Equal(3, _h.GmailRequests.Count);
        Assert.True(Gap(0, 1) >= TimeSpan.FromMilliseconds(500), $"first retry after {Gap(0, 1)}");
        Assert.True(Gap(1, 2) >= TimeSpan.FromSeconds(1), $"second retry after {Gap(1, 2)}");
    }

    [Fact]
    public async Task ServerErrorsThatNeverEndBecomeUnavailableAfterEightTries()
    {
        _h.Handler.AlwaysJson(HttpStatusCode.ServiceUnavailable, "{}");

        var ex = await Assert.ThrowsAsync<MailboxUnavailableException>(() => _h.RunAsync(_h.Mailbox.GetMessageAsync("m1", Ct)));

        // Eight attempts. Under heavy load the clock can cancel an attempt before the handler has recorded it, hence the range.
        Assert.InRange(_h.GmailRequests.Count, 7, 8);
        Assert.IsType<HttpRequestException>(ex.InnerException);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ((HttpRequestException)ex.InnerException).StatusCode);
        var waited = _h.GmailRequest(7).At - _h.GmailRequest(0).At;
        Assert.True(waited >= TimeSpan.FromSeconds(45), $"gave up after only {waited}");
        MailboxHarness.AssertNoSecrets(ex);
    }

    [Fact]
    public async Task ANetworkErrorIsRetried()
    {
        _h.Handler.EnqueueException(new HttpRequestException("An error occurred while sending the request."));
        _h.Handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ProfileJson());

        await _h.RunAsync(_h.Mailbox.GetProfileAsync(Ct));

        Assert.Equal(2, _h.GmailRequests.Count);
    }

    [Fact]
    public async Task AnHttpClientTimeoutIsRetried()
    {
        _h.Handler.EnqueueException(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException()));
        _h.Handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ProfileJson());

        await _h.RunAsync(_h.Mailbox.GetProfileAsync(Ct));

        Assert.Equal(2, _h.GmailRequests.Count);
    }

    [Fact]
    public async Task ANetworkThatStaysDownBecomesUnavailable()
    {
        _h.Handler.Always(_ => throw new HttpRequestException("No such host is known."));

        var ex = await Assert.ThrowsAsync<MailboxUnavailableException>(() => _h.RunAsync(_h.Mailbox.GetLabelsAsync(Ct)));

        // Eight attempts. Under heavy load the clock can cancel an attempt before the handler has recorded it, hence the range.
        Assert.InRange(_h.GmailRequests.Count, 7, 8);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task ADownloadThatBreaksHalfwayIsRetriedFromTheStart()
    {
        _h.Handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) });
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """{"id":"m1","threadId":"t1","raw":"aGVsbG8"}""");

        var message = await _h.RunAsync(_h.Mailbox.GetMessageAsync("m1", Ct));

        Assert.Equal("hello"u8.ToArray(), message!.Raw);
        Assert.Equal(2, _h.GmailRequests.Count);
    }

    [Fact]
    public async Task AnAnswerThatIsCutOffIsRetried()
    {
        _h.Handler.EnqueueJson(HttpStatusCode.OK, """{"emailAddress":"a@b.c","messagesTotal":1,"thre""");
        _h.Handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ProfileJson());

        var profile = await _h.RunAsync(_h.Mailbox.GetProfileAsync(Ct));

        Assert.Equal(Fixtures.Email, profile.EmailAddress);
        Assert.Equal(2, _h.GmailRequests.Count);
    }

    [Fact]
    public async Task AMessageWithoutContentIsRetriedThenReportedAsUnavailable()
    {
        _h.Handler.AlwaysJson(HttpStatusCode.OK, """{"id":"m1","threadId":"t1"}""");

        var ex = await Assert.ThrowsAsync<MailboxUnavailableException>(() => _h.RunAsync(_h.Mailbox.GetMessageAsync("m1", Ct)));

        // Eight attempts. Under heavy load the clock can cancel an attempt before the handler has recorded it, hence the range.
        Assert.InRange(_h.GmailRequests.Count, 7, 8);
        Assert.IsType<InvalidDataException>(ex.InnerException);
    }

    [Fact]
    public async Task ARequestThatNeverAnswersIsGivenUpOnAndRetried()
    {
        var hang = new ScriptStep(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        _h.Handler.Enqueue(hang);
        _h.Handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ProfileJson());

        await _h.RunAsync(_h.Mailbox.GetProfileAsync(Ct));

        Assert.Equal(2, _h.GmailRequests.Count);
        Assert.True(Gap(0, 1) >= TimeSpan.FromMinutes(5), $"gave up on the first try after only {Gap(0, 1)}");
    }

    [Fact]
    public async Task ARequestThatNeverAnswersEventuallyBecomesUnavailable()
    {
        _h.Handler.Always(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await Assert.ThrowsAsync<MailboxUnavailableException>(() => _h.RunAsync(_h.Mailbox.GetProfileAsync(Ct)));

        // Eight attempts. Under heavy load the clock can cancel an attempt before the handler has recorded it, hence the range.
        Assert.InRange(_h.GmailRequests.Count, 7, 8);
    }

    // ---- cancellation ----

    [Fact]
    public async Task AnAlreadyCancelledCallSendsNothing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _h.Mailbox.GetProfileAsync(cts.Token));

        Assert.Empty(_h.Handler.Requests);
    }

    [Fact]
    public async Task CancellingWhileARequestIsInFlightThrowsCancellationNotAnOutage()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _h.Handler.Enqueue(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var call = _h.Mailbox.GetProfileAsync(cts.Token);
        await WaitUntilAsync(() => _h.Handler.Requests.Count == 1);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Single(_h.Handler.Requests);
    }

    [Fact]
    public async Task CancellingDuringABackoffStopsRetrying()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _h.Handler.EnqueueJson(HttpStatusCode.ServiceUnavailable, "{}");

        var call = _h.Mailbox.GetProfileAsync(cts.Token);
        await WaitUntilAsync(() => _h.Handler.Requests.Count == 1);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Single(_h.Handler.Requests);
    }

    [Fact]
    public async Task CancellingWhileWaitingForQuotaStopsTheCall()
    {
        var harness = new MailboxHarness(unitsPerMinute: 60);
        harness.Handler.AlwaysJson(HttpStatusCode.OK, """{"id":"m","threadId":"t","raw":"eA"}""");
        await harness.Mailbox.GetMessageAsync("m", Ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var waiting = harness.Mailbox.GetMessageAsync("m", cts.Token);
        Assert.False(waiting.IsCompleted);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Single(harness.Handler.Requests);
    }

    // ---- helpers ----

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not reached in time.");
            await Task.Yield();
        }
    }

    /// <summary>A response body whose connection dies while it is being read.</summary>
    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("The response ended prematurely.");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("The response ended prematurely."));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
