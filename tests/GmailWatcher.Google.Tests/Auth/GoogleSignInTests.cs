using System.Net;
using System.Net.Sockets;
using GmailWatcher.Core.Configuration;
using GmailWatcher.Core.Mail;
using GmailWatcher.Core.Platform;
using GmailWatcher.Google.Auth;
using GmailWatcher.Google.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace GmailWatcher.Google.Tests.Auth;

public sealed class GoogleSignInTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestClock _time = new();
    private readonly ScriptedHandler _tokenEndpoint;
    private readonly List<string> _events = [];

    public GoogleSignInTests()
    {
        _tokenEndpoint = new ScriptedHandler(_time);
    }

    private GoogleSignIn NewSignIn(IExternalBrowser browser, GoogleOptions? options = null) =>
        new(new GoogleOAuthClient(new HttpClient(_tokenEndpoint), options ?? Fixtures.Options(), _time), browser, _time);

    private FakeBrowser NewBrowser(Func<Uri, Task>? userAction, LinkOpenResult result = LinkOpenResult.Opened) => new(_events, userAction, result);

    // ---- what the user's browser does ----

    private static Func<Uri, Task> UserApproves(string code = Fixtures.Code, string? state = null) => url =>
        CallBackAsync(url, $"code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state ?? StateOf(url))}");

    private static Func<Uri, Task> UserDenies() => url => CallBackAsync(url, $"error=access_denied&state={Uri.EscapeDataString(StateOf(url))}");

    private static Func<Uri, Task> UserSees(string error) => url => CallBackAsync(url, $"error={Uri.EscapeDataString(error)}&state={Uri.EscapeDataString(StateOf(url))}");

    private static string StateOf(Uri url) => RecordedRequest.Parse(url.Query)["state"].Single();

    private static string RedirectOf(Uri url) => RecordedRequest.Parse(url.Query)["redirect_uri"].Single();

    private static async Task CallBackAsync(Uri authorizationUrl, string query)
    {
        using var browser = new HttpClient();
        using var response = await browser.GetAsync($"{RedirectOf(authorizationUrl)}?{query}");
        await response.Content.ReadAsStringAsync();
    }

    // ---- tests ----

    [Fact]
    public async Task SignsInAndReturnsTheTokens()
    {
        _tokenEndpoint.EnqueueJson(HttpStatusCode.OK, Fixtures.Token());
        var browser = NewBrowser(UserApproves());

        var result = await NewSignIn(browser).SignInAsync(null, Ct);

        Assert.Equal(Fixtures.AccessToken, result.Tokens.AccessToken);
        Assert.Equal(Fixtures.RefreshToken, result.RefreshToken);
        Assert.Equal(Fixtures.ReadOnlyScope, result.Tokens.Scope);
    }

    [Fact]
    public async Task TheCodeIsExchangedWithTheVerifierThatMatchesTheChallengeInTheUrl()
    {
        _tokenEndpoint.EnqueueJson(HttpStatusCode.OK, Fixtures.Token());
        var browser = NewBrowser(UserApproves());

        await NewSignIn(browser).SignInAsync(null, Ct);

        var url = Assert.Single(browser.Opened);
        var query = RecordedRequest.Parse(url.Query);
        var exchange = Assert.Single(_tokenEndpoint.Requests);
        Assert.Equal(Fixtures.Code, exchange.Form["code"].Single());
        Assert.Equal(RedirectOf(url), exchange.Form["redirect_uri"].Single());
        Assert.Equal(query["code_challenge"].Single(), Pkce.CreateChallenge(exchange.Form["code_verifier"].Single()));
        Assert.Equal("S256", query["code_challenge_method"].Single());
        Assert.Equal(GoogleOAuthClient.GmailReadOnlyScope, query["scope"].Single());
        Assert.Matches(@"^http://127\.0\.0\.1:\d+/$", RedirectOf(url));
    }

    [Fact]
    public async Task TheUrlIsReportedBeforeTheBrowserIsOpened()
    {
        _tokenEndpoint.EnqueueJson(HttpStatusCode.OK, Fixtures.Token());
        var browser = NewBrowser(UserApproves());
        var progress = new RecordingProgress(_events);

        await NewSignIn(browser).SignInAsync(new SignInOptions { AuthorizationUrlReady = progress }, Ct);

        Assert.Equal(["url ready", "browser open"], _events);
        Assert.Equal(browser.Opened.Single(), Assert.Single(progress.Urls));
    }

    [Fact]
    public async Task TheLoginHintIsPassedOn()
    {
        _tokenEndpoint.EnqueueJson(HttpStatusCode.OK, Fixtures.Token());
        var browser = NewBrowser(UserApproves());

        await NewSignIn(browser).SignInAsync(new SignInOptions { LoginHint = "me@example.com" }, Ct);

        Assert.Equal("me@example.com", RecordedRequest.Parse(browser.Opened.Single().Query)["login_hint"].Single());
    }

    [Fact]
    public async Task EachSignInUsesFreshStateAndAFreshVerifier()
    {
        _tokenEndpoint.EnqueueJson(HttpStatusCode.OK, Fixtures.Token());
        _tokenEndpoint.EnqueueJson(HttpStatusCode.OK, Fixtures.Token());
        var browser = NewBrowser(UserApproves());
        var signIn = NewSignIn(browser);

        await signIn.SignInAsync(null, Ct);
        await signIn.SignInAsync(null, Ct);

        Assert.NotEqual(StateOf(browser.Opened[0]), StateOf(browser.Opened[1]));
        Assert.NotEqual(
            _tokenEndpoint.Requests[0].Form["code_verifier"].Single(),
            _tokenEndpoint.Requests[1].Form["code_verifier"].Single());
        Assert.NotEqual(RedirectOf(browser.Opened[0]), RedirectOf(browser.Opened[1]));
    }

    [Fact]
    public async Task ARedirectWithTheWrongStateIsRejectedWithoutAnyTokenRequest()
    {
        var browser = NewBrowser(UserApproves(state: "forged-state"));

        var ex = await Assert.ThrowsAsync<GoogleSignInException>(() => NewSignIn(browser).SignInAsync(null, Ct));

        Assert.Equal(GoogleSignInFailure.Failed, ex.Kind);
        Assert.Empty(_tokenEndpoint.Requests);
        MailboxHarness.AssertNoSecrets(ex);
    }

    [Fact]
    public async Task ARedirectWithoutStateIsRejected()
    {
        var browser = NewBrowser(url => CallBackAsync(url, "code=abc"));

        var ex = await Assert.ThrowsAsync<GoogleSignInException>(() => NewSignIn(browser).SignInAsync(null, Ct));

        Assert.Equal(GoogleSignInFailure.Failed, ex.Kind);
        Assert.Empty(_tokenEndpoint.Requests);
    }

    [Fact]
    public async Task ADeniedConsentScreenIsReportedAsDenied()
    {
        var browser = NewBrowser(UserDenies());

        var ex = await Assert.ThrowsAsync<GoogleSignInException>(() => NewSignIn(browser).SignInAsync(null, Ct));

        Assert.Equal(GoogleSignInFailure.Denied, ex.Kind);
        Assert.Empty(_tokenEndpoint.Requests);
    }

    [Fact]
    public async Task AForgedDenialWithTheWrongStateIsNotTakenForADenial()
    {
        var browser = NewBrowser(url => CallBackAsync(url, "error=access_denied&state=forged"));

        var ex = await Assert.ThrowsAsync<GoogleSignInException>(() => NewSignIn(browser).SignInAsync(null, Ct));

        Assert.Equal(GoogleSignInFailure.Failed, ex.Kind);
    }

    [Fact]
    public async Task OtherGoogleErrorsAreReportedWithTheirCode()
    {
        var browser = NewBrowser(UserSees("admin_policy_enforced"));

        var ex = await Assert.ThrowsAsync<GoogleSignInException>(() => NewSignIn(browser).SignInAsync(null, Ct));

        Assert.Equal(GoogleSignInFailure.Failed, ex.Kind);
        Assert.Contains("admin_policy_enforced", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostileErrorCodeIsNotRepeatedInTheMessage()
    {
        var browser = NewBrowser(UserSees("<script>alert(1)</script>"));

        var ex = await Assert.ThrowsAsync<GoogleSignInException>(() => NewSignIn(browser).SignInAsync(null, Ct));

        Assert.DoesNotContain("<script>", ex.Message, StringComparison.Ordinal);
        Assert.Contains("unknown_error", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABrowserThatCannotBeOpenedFailsAtOnceButTheUrlWasAlreadyReported()
    {
        var browser = NewBrowser(null, LinkOpenResult.Failed);
        var progress = new RecordingProgress(_events);

        var ex = await Assert.ThrowsAsync<GoogleSignInException>(() =>
            NewSignIn(browser).SignInAsync(new SignInOptions { AuthorizationUrlReady = progress }, Ct));

        Assert.Equal(GoogleSignInFailure.BrowserUnavailable, ex.Kind);
        Assert.Equal(browser.Opened.Single(), Assert.Single(progress.Urls));
        Assert.Empty(_tokenEndpoint.Requests);
    }

    [Fact]
    public async Task ARefusedLinkIsAlsoReportedAsBrowserUnavailable()
    {
        var browser = NewBrowser(null, LinkOpenResult.Refused);

        var ex = await Assert.ThrowsAsync<GoogleSignInException>(() => NewSignIn(browser).SignInAsync(null, Ct));

        Assert.Equal(GoogleSignInFailure.BrowserUnavailable, ex.Kind);
    }

    [Fact]
    public async Task TheUrlStillWorksWhenTheBrowserFailedAndTheCallerChoseToKeepWaiting()
    {
        _tokenEndpoint.EnqueueJson(HttpStatusCode.OK, Fixtures.Token());
        var browser = NewBrowser(null, LinkOpenResult.Failed);
        Task? userPastesTheLink = null;
        var progress = new RecordingProgress(_events, url => userPastesTheLink = Task.Run(() => UserApproves()(url)));

        var result = await NewSignIn(browser).SignInAsync(new SignInOptions { AuthorizationUrlReady = progress, KeepWaitingIfBrowserFails = true }, Ct);

        Assert.Equal(Fixtures.RefreshToken, result.RefreshToken);
        await userPastesTheLink!;
    }

    [Fact]
    public async Task GivesUpWhenNobodyFinishesInTime()
    {
        var browser = NewBrowser(null);

        var ex = await Assert.ThrowsAsync<GoogleSignInException>(() =>
            _time.PumpAsync(NewSignIn(browser).SignInAsync(null, Ct)));

        Assert.Equal(GoogleSignInFailure.TimedOut, ex.Kind);
        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    [Fact]
    public async Task TheTimeLimitIsConfigurable()
    {
        var browser = NewBrowser(null);
        var task = NewSignIn(browser).SignInAsync(new SignInOptions { Timeout = TimeSpan.FromSeconds(30) }, Ct);

        _time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(task.IsCompleted);
        var ex = await Assert.ThrowsAsync<GoogleSignInException>(() => _time.PumpAsync(task));

        Assert.Equal(GoogleSignInFailure.TimedOut, ex.Kind);
    }

    [Fact]
    public async Task CancellationIsReportedAsCancellationNotAsAFailure()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var browser = NewBrowser(null);
        var task = NewSignIn(browser).SignInAsync(null, cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task ASignInWithoutGmailAccessIsRefusedAndNothingIsKept()
    {
        _tokenEndpoint.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(scope: "openid"));
        _tokenEndpoint.EnqueueJson(HttpStatusCode.OK, "{}");
        var browser = NewBrowser(UserApproves());

        var ex = await Assert.ThrowsAsync<GoogleSignInException>(() => NewSignIn(browser).SignInAsync(null, Ct));

        Assert.Equal(GoogleSignInFailure.MissingPermission, ex.Kind);
        Assert.Equal("https://oauth.test/revoke", _tokenEndpoint.Requests[1].Url);
    }

    [Fact]
    public async Task ARejectedCodeIsAFailure()
    {
        _tokenEndpoint.EnqueueJson(HttpStatusCode.BadRequest, Fixtures.OAuthError("invalid_grant"));
        var browser = NewBrowser(UserApproves());

        var ex = await Assert.ThrowsAsync<GoogleSignInException>(() => NewSignIn(browser).SignInAsync(null, Ct));

        Assert.Equal(GoogleSignInFailure.Failed, ex.Kind);
        MailboxHarness.AssertNoSecrets(ex);
    }

    [Fact]
    public async Task GoogleBeingUnreachableDuringTheExchangeIsAFailureWithTheCauseAttached()
    {
        _tokenEndpoint.EnqueueException(new HttpRequestException("offline"));
        var browser = NewBrowser(UserApproves());

        var ex = await Assert.ThrowsAsync<GoogleSignInException>(() => NewSignIn(browser).SignInAsync(null, Ct));

        Assert.Equal(GoogleSignInFailure.Failed, ex.Kind);
        Assert.IsType<MailboxUnavailableException>(ex.InnerException);
        MailboxHarness.AssertNoSecrets(ex);
    }

    [Fact]
    public async Task WithoutAnOAuthClientTheBrowserIsNeverOpened()
    {
        var options = Fixtures.Options();
        options.ClientId = null;
        var browser = NewBrowser(UserApproves());

        var ex = await Assert.ThrowsAsync<GoogleSignInException>(() => NewSignIn(browser, options).SignInAsync(null, Ct));

        Assert.Equal(GoogleSignInFailure.Failed, ex.Kind);
        Assert.Empty(browser.Opened);
    }

    [Fact]
    public async Task ThePortIsReleasedWhenTheSignInEnds()
    {
        _tokenEndpoint.EnqueueJson(HttpStatusCode.OK, Fixtures.Token());
        var browser = NewBrowser(UserApproves());

        await NewSignIn(browser).SignInAsync(null, Ct);

        var port = new Uri(RedirectOf(browser.Opened.Single())).Port;
        await Assert.ThrowsAnyAsync<SocketException>(async () =>
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, Ct);
        });
    }

    [Fact]
    public async Task ThePortIsReleasedWhenTheSignInFails()
    {
        var browser = NewBrowser(UserDenies());

        await Assert.ThrowsAsync<GoogleSignInException>(() => NewSignIn(browser).SignInAsync(null, Ct));

        var port = new Uri(RedirectOf(browser.Opened.Single())).Port;
        await Assert.ThrowsAnyAsync<SocketException>(async () =>
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, Ct);
        });
    }

    [Fact]
    public async Task NoRequestEverCarriesATokenOrCodeInItsUrl()
    {
        _tokenEndpoint.EnqueueJson(HttpStatusCode.OK, Fixtures.Token());
        var browser = NewBrowser(UserApproves());

        await NewSignIn(browser).SignInAsync(null, Ct);

        foreach (var request in _tokenEndpoint.Requests)
        {
            Assert.DoesNotContain(Fixtures.Code, request.Url, StringComparison.Ordinal);
            Assert.DoesNotContain(Fixtures.ClientSecret, request.Url, StringComparison.Ordinal);
        }

        foreach (var url in browser.Opened)
        {
            Assert.DoesNotContain(Fixtures.ClientSecret, url.AbsoluteUri, StringComparison.Ordinal);
        }
    }

    // ---- fakes ----

    private sealed class FakeBrowser(List<string> events, Func<Uri, Task>? userAction, LinkOpenResult result) : IExternalBrowser
    {
        public List<Uri> Opened { get; } = [];

        public Task<LinkOpenResult> OpenAsync(Uri url)
        {
            events.Add("browser open");
            Opened.Add(url);
            if (userAction is not null)
            {
                // The user clicks through in the background; the app is already waiting for the redirect.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await userAction(url);
                    }
                    catch (Exception)
                    {
                        // A failing "user" shows up as a failed sign-in in the test itself.
                    }
                });
            }

            return Task.FromResult(result);
        }
    }

    private sealed class RecordingProgress(List<string> events, Action<Uri>? onReport = null) : IProgress<Uri>
    {
        public List<Uri> Urls { get; } = [];

        public void Report(Uri value)
        {
            events.Add("url ready");
            Urls.Add(value);
            onReport?.Invoke(value);
        }
    }
}
