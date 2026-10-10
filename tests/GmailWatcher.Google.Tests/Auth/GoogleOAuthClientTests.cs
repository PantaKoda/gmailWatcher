using System.Net;
using GmailWatcher.Core.Configuration;
using GmailWatcher.Core.Mail;
using GmailWatcher.Google.Auth;
using GmailWatcher.Google.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace GmailWatcher.Google.Tests.Auth;

public sealed class GoogleOAuthClientTests
{
    private const string Redirect = "http://127.0.0.1:54321/";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(Fixtures.Start);
    private readonly ScriptedHandler _handler;

    public GoogleOAuthClientTests()
    {
        _handler = new ScriptedHandler(_time);
    }

    private GoogleOAuthClient NewClient(GoogleOptions? options = null) => new(new HttpClient(_handler), options ?? Fixtures.Options(), _time);

    // ---- authorization URL ----

    [Fact]
    public void AuthorizationUrlCarriesEveryRequiredParameter()
    {
        var url = NewClient().BuildAuthorizationUri(Redirect, "state-123", "challenge-456", loginHint: "me@example.com");

        Assert.Equal("accounts.test", url.Host);
        Assert.Equal("/o/oauth2/v2/auth", url.AbsolutePath);
        var query = RecordedRequest.Parse(url.Query);
        Assert.Equal(Fixtures.ClientId, query["client_id"].Single());
        Assert.Equal(Redirect, query["redirect_uri"].Single());
        Assert.Equal("code", query["response_type"].Single());
        Assert.Equal("https://www.googleapis.com/auth/gmail.readonly", query["scope"].Single());
        Assert.Equal("offline", query["access_type"].Single());
        Assert.Equal("consent", query["prompt"].Single());
        Assert.Equal("challenge-456", query["code_challenge"].Single());
        Assert.Equal("S256", query["code_challenge_method"].Single());
        Assert.Equal("state-123", query["state"].Single());
        Assert.Equal("me@example.com", query["login_hint"].Single());
        Assert.Equal(10, query.Count);
    }

    [Fact]
    public void AuthorizationUrlAsksOnlyForTheReadOnlyScope()
    {
        var url = NewClient().BuildAuthorizationUri(Redirect, "s", "c");

        var scope = RecordedRequest.Parse(url.Query)["scope"].Single();
        Assert.Equal(GoogleOAuthClient.GmailReadOnlyScope, scope);
        Assert.DoesNotContain("modify", url.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("gmail.send", url.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("mail.google.com", url.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthorizationUrlEscapesEveryValue()
    {
        var url = NewClient().BuildAuthorizationUri("http://127.0.0.1:5555/?a=b&c=d", "a b&c=d#e", "x+y/z=", "first+last@example.com");

        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A5555%2F%3Fa%3Db%26c%3Dd", url.Query, StringComparison.Ordinal);
        Assert.Contains("scope=https%3A%2F%2Fwww.googleapis.com%2Fauth%2Fgmail.readonly", url.Query, StringComparison.Ordinal);
        Assert.Contains("state=a%20b%26c%3Dd%23e", url.Query, StringComparison.Ordinal);
        Assert.Contains("login_hint=first%2Blast%40example.com", url.Query, StringComparison.Ordinal);
        var query = RecordedRequest.Parse(url.Query);
        Assert.Equal("a b&c=d#e", query["state"].Single());
        Assert.Equal("x+y/z=", query["code_challenge"].Single());
        Assert.Equal("first+last@example.com", query["login_hint"].Single());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AuthorizationUrlLeavesOutAnEmptyLoginHint(string? hint)
    {
        var url = NewClient().BuildAuthorizationUri(Redirect, "s", "c", hint);

        Assert.DoesNotContain("login_hint", url.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthorizationUrlNeverContainsTheClientSecret()
    {
        var url = NewClient().BuildAuthorizationUri(Redirect, "s", "c");

        Assert.DoesNotContain(Fixtures.ClientSecret, url.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthorizationUrlWithoutAClientIdIsRefusedWithAnExplanation()
    {
        var options = Fixtures.Options();
        options.ClientId = null;

        var ex = Assert.Throws<GoogleSignInException>(() => NewClient(options).BuildAuthorizationUri(Redirect, "s", "c"));

        Assert.Equal(GoogleSignInFailure.Failed, ex.Kind);
        Assert.Contains("client ID", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheClientIsReadFromTheOptionsOnEveryCall()
    {
        var options = Fixtures.Options();
        var client = new GoogleOAuthClient(new HttpClient(_handler), () => options, _time);
        Assert.Contains(Uri.EscapeDataString(Fixtures.ClientId), client.BuildAuthorizationUri(Redirect, "s", "c").Query, StringComparison.Ordinal);

        options = Fixtures.Options();
        options.ClientId = "999999999999-other.apps.googleusercontent.com";

        Assert.Contains("999999999999-other", client.BuildAuthorizationUri(Redirect, "s", "c").Query, StringComparison.Ordinal);
    }

    // ---- code exchange ----

    [Fact]
    public async Task ExchangePostsTheCodeAndVerifierAndReturnsTheTokens()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token());

        var tokens = await NewClient().ExchangeCodeAsync(Fixtures.Code, "the-verifier", Redirect, Ct);

        Assert.Equal(Fixtures.AccessToken, tokens.AccessToken);
        Assert.Equal(Fixtures.RefreshToken, tokens.RefreshToken);
        Assert.Equal(Fixtures.ReadOnlyScope, tokens.Scope);
        Assert.Equal(Fixtures.Start.AddSeconds(3600), tokens.ExpiresAt);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://oauth.test/token", request.Url);
        Assert.Equal("authorization_code", request.Form["grant_type"].Single());
        Assert.Equal(Fixtures.Code, request.Form["code"].Single());
        Assert.Equal("the-verifier", request.Form["code_verifier"].Single());
        Assert.Equal(Redirect, request.Form["redirect_uri"].Single());
        Assert.Equal(Fixtures.ClientId, request.Form["client_id"].Single());
    }

    [Fact]
    public async Task ExchangeSendsTheClientSecretWhenOneIsConfigured()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token());

        await NewClient(Fixtures.Options("GOCSPX-configured")).ExchangeCodeAsync(Fixtures.Code, "v", Redirect, Ct);

        Assert.Equal("GOCSPX-configured", Assert.Single(_handler.Requests).Form["client_secret"].Single());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task ExchangeLeavesOutTheClientSecretWhenNoneIsConfigured(string? secret)
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token());

        await NewClient(Fixtures.Options(secret)).ExchangeCodeAsync(Fixtures.Code, "v", Redirect, Ct);

        Assert.DoesNotContain("client_secret", Assert.Single(_handler.Requests).Form.Select(group => group.Key));
    }

    [Fact]
    public async Task ExchangeIsRefusedWhenGmailReadAccessWasNotGranted()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(scope: "openid https://www.googleapis.com/auth/userinfo.email"));
        _handler.EnqueueJson(HttpStatusCode.OK, "{}");

        var ex = await Assert.ThrowsAsync<GoogleSignInException>(() => NewClient().ExchangeCodeAsync(Fixtures.Code, "v", Redirect, Ct));

        Assert.Equal(GoogleSignInFailure.MissingPermission, ex.Kind);
        MailboxHarness.AssertNoSecrets(ex);
    }

    [Fact]
    public async Task ExchangeRevokesTheGrantItDoesNotUse()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(scope: "openid"));
        _handler.EnqueueJson(HttpStatusCode.OK, "{}");

        await Assert.ThrowsAsync<GoogleSignInException>(() => NewClient().ExchangeCodeAsync(Fixtures.Code, "v", Redirect, Ct));

        Assert.Equal(2, _handler.Requests.Count);
        var revoke = _handler.Requests[1];
        Assert.Equal("https://oauth.test/revoke", revoke.Url);
        Assert.Equal(Fixtures.RefreshToken, revoke.Form["token"].Single());
    }

    [Fact]
    public async Task ExchangeAcceptsGmailReadAccessAmongOtherScopes()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(scope: $"openid {Fixtures.ReadOnlyScope}"));

        var tokens = await NewClient().ExchangeCodeAsync(Fixtures.Code, "v", Redirect, Ct);

        Assert.Equal(Fixtures.RefreshToken, tokens.RefreshToken);
    }

    [Fact]
    public async Task ExchangeWithoutAnyScopeInTheAnswerIsRefused()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(scope: null));
        _handler.EnqueueJson(HttpStatusCode.OK, "{}");

        var ex = await Assert.ThrowsAsync<GoogleSignInException>(() => NewClient().ExchangeCodeAsync(Fixtures.Code, "v", Redirect, Ct));

        Assert.Equal(GoogleSignInFailure.MissingPermission, ex.Kind);
    }

    [Fact]
    public async Task ExchangeWithoutARefreshTokenIsRefusedAndTheAccessTokenRevoked()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(refresh: null));
        _handler.EnqueueJson(HttpStatusCode.OK, "{}");

        var ex = await Assert.ThrowsAsync<GoogleSignInException>(() => NewClient().ExchangeCodeAsync(Fixtures.Code, "v", Redirect, Ct));

        Assert.Equal(GoogleSignInFailure.Failed, ex.Kind);
        Assert.Equal(Fixtures.AccessToken, _handler.Requests[1].Form["token"].Single());
        MailboxHarness.AssertNoSecrets(ex);
    }

    [Fact]
    public async Task ExchangeMapsInvalidGrantToASignInFailure()
    {
        _handler.EnqueueJson(HttpStatusCode.BadRequest, Fixtures.OAuthError("invalid_grant", "Bad Request"));

        var ex = await Assert.ThrowsAsync<GoogleSignInException>(() => NewClient().ExchangeCodeAsync(Fixtures.Code, "v", Redirect, Ct));

        Assert.Equal(GoogleSignInFailure.Failed, ex.Kind);
        MailboxHarness.AssertNoSecrets(ex);
    }

    [Theory]
    [InlineData("invalid_client", "Check the client ID and secret")]
    [InlineData("redirect_uri_mismatch", "Desktop app")]
    [InlineData("invalid_request", "invalid_request")]
    public async Task ExchangeMapsOtherOAuthErrorsToASignInFailureWithAHint(string error, string expectedHint)
    {
        _handler.EnqueueJson(HttpStatusCode.BadRequest, Fixtures.OAuthError(error, "Details from Google."));

        var ex = await Assert.ThrowsAsync<GoogleSignInException>(() => NewClient().ExchangeCodeAsync(Fixtures.Code, "v", Redirect, Ct));

        Assert.Equal(GoogleSignInFailure.Failed, ex.Kind);
        Assert.Contains(expectedHint, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErrorDescriptionsThatEchoASecretAreScrubbed()
    {
        _handler.EnqueueJson(HttpStatusCode.BadRequest, Fixtures.OAuthError("invalid_request", $"Malformed code {Fixtures.Code} and verifier the-verifier"));

        var ex = await Assert.ThrowsAsync<GoogleSignInException>(() => NewClient().ExchangeCodeAsync(Fixtures.Code, "the-verifier", Redirect, Ct));

        Assert.DoesNotContain(Fixtures.Code, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("the-verifier", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostileErrorCodesAreNotRepeated()
    {
        _handler.EnqueueJson(HttpStatusCode.BadRequest, Fixtures.OAuthError("<b>boom</b>"));

        var ex = await Assert.ThrowsAsync<GoogleSignInException>(() => NewClient().ExchangeCodeAsync(Fixtures.Code, "v", Redirect, Ct));

        Assert.DoesNotContain("<b>", ex.Message, StringComparison.Ordinal);
        Assert.Contains("unknown_error", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExchangeMapsServerErrorsToUnavailable()
    {
        _handler.EnqueueJson(HttpStatusCode.ServiceUnavailable, "<html>try later</html>");

        var ex = await Assert.ThrowsAsync<MailboxUnavailableException>(() => NewClient().ExchangeCodeAsync(Fixtures.Code, "v", Redirect, Ct));

        Assert.Contains("503", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExchangeMapsAnUnreadableAnswerToASignInFailure()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, "this is not json");

        var ex = await Assert.ThrowsAsync<GoogleSignInException>(() => NewClient().ExchangeCodeAsync(Fixtures.Code, "v", Redirect, Ct));

        Assert.Equal(GoogleSignInFailure.Failed, ex.Kind);
    }

    [Fact]
    public async Task ExchangeMapsAnAnswerWithoutAnAccessTokenToASignInFailure()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, """{"expires_in":3600}""");

        await Assert.ThrowsAsync<GoogleSignInException>(() => NewClient().ExchangeCodeAsync(Fixtures.Code, "v", Redirect, Ct));
    }

    [Fact]
    public async Task ExchangeWithoutAClientIdFailsBeforeAnyRequest()
    {
        var options = Fixtures.Options();
        options.ClientId = " ";

        var ex = await Assert.ThrowsAsync<GoogleSignInException>(() => NewClient(options).ExchangeCodeAsync(Fixtures.Code, "v", Redirect, Ct));

        Assert.Equal(GoogleSignInFailure.Failed, ex.Kind);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task ADefaultLifetimeIsUsedWhenGoogleSendsNone()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, """{"access_token":"a","refresh_token":"r","scope":"https://www.googleapis.com/auth/gmail.readonly"}""");

        var tokens = await NewClient().ExchangeCodeAsync(Fixtures.Code, "v", Redirect, Ct);

        Assert.Equal(Fixtures.Start.AddHours(1), tokens.ExpiresAt);
    }

    [Fact]
    public void TokenSetsNeverPrintTheirTokens()
    {
        var tokens = new TokenSet(Fixtures.AccessToken, Fixtures.Start, Fixtures.RefreshToken, Fixtures.ReadOnlyScope);

        Assert.DoesNotContain(Fixtures.AccessToken, tokens.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Fixtures.RefreshToken, tokens.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Fixtures.AccessToken, new SignInResult(tokens).ToString(), StringComparison.Ordinal);
    }

    // ---- refresh ----

    [Fact]
    public async Task RefreshPostsTheRefreshTokenAndReturnsANewAccessToken()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(access: "new-access", refresh: null, expiresIn: 1800));

        var tokens = await NewClient().RefreshAsync(Fixtures.RefreshToken, Ct);

        Assert.Equal("new-access", tokens.AccessToken);
        Assert.Null(tokens.RefreshToken);
        Assert.Equal(Fixtures.Start.AddSeconds(1800), tokens.ExpiresAt);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal("refresh_token", request.Form["grant_type"].Single());
        Assert.Equal(Fixtures.RefreshToken, request.Form["refresh_token"].Single());
        Assert.Equal(Fixtures.ClientId, request.Form["client_id"].Single());
        Assert.Equal(Fixtures.ClientSecret, request.Form["client_secret"].Single());
        Assert.DoesNotContain("code_verifier", request.Form.Select(group => group.Key));
    }

    [Fact]
    public async Task RefreshReturnsAnIssuedNewRefreshToken()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(refresh: "rotated"));

        var tokens = await NewClient().RefreshAsync(Fixtures.RefreshToken, Ct);

        Assert.Equal("rotated", tokens.RefreshToken);
    }

    [Fact]
    public async Task RefreshOmitsTheClientSecretWhenNoneIsConfigured()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token());

        await NewClient(Fixtures.Options(null)).RefreshAsync(Fixtures.RefreshToken, Ct);

        Assert.DoesNotContain("client_secret", Assert.Single(_handler.Requests).Form.Select(group => group.Key));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task RefreshMapsInvalidGrantToReconnectRequired(HttpStatusCode status)
    {
        _handler.EnqueueJson(status, Fixtures.OAuthError("invalid_grant", "Token has been expired or revoked."));

        var ex = await Assert.ThrowsAsync<ReconnectRequiredException>(() => NewClient().RefreshAsync(Fixtures.RefreshToken, Ct));

        MailboxHarness.AssertNoSecrets(ex);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task RefreshMapsServerTroubleToUnavailable(HttpStatusCode status)
    {
        _handler.EnqueueJson(status, "{}");

        await Assert.ThrowsAsync<MailboxUnavailableException>(() => NewClient().RefreshAsync(Fixtures.RefreshToken, Ct));
    }

    [Fact]
    public async Task RefreshMapsNetworkFailureToUnavailable()
    {
        _handler.EnqueueException(new HttpRequestException("No such host is known."));

        var ex = await Assert.ThrowsAsync<MailboxUnavailableException>(() => NewClient().RefreshAsync(Fixtures.RefreshToken, Ct));

        Assert.IsType<HttpRequestException>(ex.InnerException);
        MailboxHarness.AssertNoSecrets(ex);
    }

    [Fact]
    public async Task RefreshMapsATimeoutToUnavailable()
    {
        _handler.EnqueueException(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException()));

        await Assert.ThrowsAsync<MailboxUnavailableException>(() => NewClient().RefreshAsync(Fixtures.RefreshToken, Ct));
    }

    [Fact]
    public async Task RefreshHonoursTheCallersCancellation()
    {
        using var cts = new CancellationTokenSource();
        _handler.Enqueue(async (_, token) =>
        {
            await cts.CancelAsync();
            token.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewClient().RefreshAsync(Fixtures.RefreshToken, cts.Token));
    }

    [Fact]
    public async Task RefreshMapsAnOtherwiseRejectedClientToASignInFailure()
    {
        _handler.EnqueueJson(HttpStatusCode.Unauthorized, Fixtures.OAuthError("invalid_client", "The OAuth client was not found."));

        var ex = await Assert.ThrowsAsync<GoogleSignInException>(() => NewClient().RefreshAsync(Fixtures.RefreshToken, Ct));

        Assert.Equal(GoogleSignInFailure.Failed, ex.Kind);
    }

    [Fact]
    public async Task RefreshWithoutAClientIdAsksToReconnect()
    {
        var options = Fixtures.Options();
        options.ClientId = null;

        await Assert.ThrowsAsync<ReconnectRequiredException>(() => NewClient(options).RefreshAsync(Fixtures.RefreshToken, Ct));

        Assert.Empty(_handler.Requests);
    }

    // ---- revoke ----

    [Fact]
    public async Task RevokePostsTheToken()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, "{}");

        var revoked = await NewClient().RevokeAsync(Fixtures.RefreshToken, Ct);

        Assert.True(revoked);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://oauth.test/revoke", request.Url);
        Assert.Equal(Fixtures.RefreshToken, request.Form["token"].Single());
        Assert.DoesNotContain(Fixtures.RefreshToken, request.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevokeSaysFalseWhenGoogleDoesNotConfirm()
    {
        _handler.EnqueueJson(HttpStatusCode.BadRequest, Fixtures.OAuthError("invalid_token"));

        Assert.False(await NewClient().RevokeAsync(Fixtures.RefreshToken, Ct));
    }

    [Fact]
    public async Task RevokeNeverThrows()
    {
        _handler.EnqueueException(new HttpRequestException("offline"));
        _handler.EnqueueException(new InvalidOperationException("anything"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Assert.False(await NewClient().RevokeAsync(Fixtures.RefreshToken, Ct));
        Assert.False(await NewClient().RevokeAsync(Fixtures.RefreshToken, Ct));
        Assert.False(await NewClient().RevokeAsync(Fixtures.RefreshToken, cancelled.Token));
    }
}
