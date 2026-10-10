using System.Net;
using GmailWatcher.Core.Accounts;
using GmailWatcher.Core.Mail;
using GmailWatcher.Google.Auth;
using GmailWatcher.Google.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace GmailWatcher.Google.Tests.Auth;

public sealed class AccessTokenProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(Fixtures.Start);
    private readonly ScriptedHandler _handler;
    private readonly InMemoryCredentialStore _store = new InMemoryCredentialStore().With(Fixtures.Email, Fixtures.RefreshToken);
    private readonly AccessTokenProvider _provider;

    public AccessTokenProviderTests()
    {
        _handler = new ScriptedHandler(_time);
        _provider = NewProvider(_store);
    }

    private AccessTokenProvider NewProvider(ICredentialStore store) =>
        new(Fixtures.Email, store, new GoogleOAuthClient(new HttpClient(_handler), Fixtures.Options(), _time), _time);

    [Fact]
    public async Task TheFirstCallRefreshesWithTheStoredRefreshToken()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(access: "access-1", refresh: null));

        var token = await _provider.GetAccessTokenAsync(Ct);

        Assert.Equal("access-1", token);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal(Fixtures.RefreshToken, request.Form["refresh_token"].Single());
    }

    [Fact]
    public async Task ALaterCallReusesTheTokenWithoutTouchingTheNetwork()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(access: "access-1", refresh: null));

        var first = await _provider.GetAccessTokenAsync(Ct);
        _time.Advance(TimeSpan.FromMinutes(30));
        var second = await _provider.GetAccessTokenAsync(Ct);

        Assert.Equal(first, second);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task ATokenIsReplacedWhenLessThanTwoMinutesRemain()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(access: "access-1", refresh: null, expiresIn: 3600));
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(access: "access-2", refresh: null, expiresIn: 3600));

        await _provider.GetAccessTokenAsync(Ct);
        _time.Advance(TimeSpan.FromMinutes(57));
        var stillCached = await _provider.GetAccessTokenAsync(Ct);
        _time.Advance(TimeSpan.FromSeconds(61));
        var replaced = await _provider.GetAccessTokenAsync(Ct);

        Assert.Equal("access-1", stillCached);
        Assert.Equal("access-2", replaced);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task ManyCallersAtOnceShareOneRefresh()
    {
        var release = new TaskCompletionSource();
        _handler.Enqueue(async (_, _) =>
        {
            await release.Task;
            return ScriptedHandler.Json(HttpStatusCode.OK, Fixtures.Token(access: "shared", refresh: null));
        });

        var callers = Enumerable.Range(0, 50).Select(_ => Task.Run(() => _provider.GetAccessTokenAsync(Ct).AsTask(), Ct)).ToArray();
        await WaitUntilAsync(() => _handler.Requests.Count == 1);
        release.SetResult();
        var tokens = await Task.WhenAll(callers);

        Assert.All(tokens, token => Assert.Equal("shared", token));
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task ManyCallersAtOnceAfterExpiryAlsoShareOneRefresh()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(access: "first", refresh: null, expiresIn: 600));
        await _provider.GetAccessTokenAsync(Ct);
        _time.Advance(TimeSpan.FromMinutes(20));
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(access: "second", refresh: null));

        var tokens = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => _provider.GetAccessTokenAsync(Ct).AsTask(), Ct)));

        Assert.All(tokens, token => Assert.Equal("second", token));
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task ARotatedRefreshTokenIsWrittenBackAndUsedNextTime()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(access: "a1", refresh: "rotated-refresh", expiresIn: 600));
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(access: "a2", refresh: null));

        await _provider.GetAccessTokenAsync(Ct);
        Assert.Equal("rotated-refresh", _store.Peek(Fixtures.Email));
        Assert.Equal(1, _store.Writes);

        _time.Advance(TimeSpan.FromMinutes(20));
        await _provider.GetAccessTokenAsync(Ct);

        Assert.Equal("rotated-refresh", _handler.Requests[1].Form["refresh_token"].Single());
    }

    [Fact]
    public async Task AnUnchangedRefreshTokenIsNotWrittenAgain()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(refresh: Fixtures.RefreshToken));

        await _provider.GetAccessTokenAsync(Ct);

        Assert.Equal(0, _store.Writes);
    }

    [Fact]
    public async Task AFailingStoreWriteDoesNotLoseTheFreshAccessToken()
    {
        var store = new FailingWriteStore(_store);
        var provider = NewProvider(store);
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(access: "usable", refresh: "rotated"));

        var token = await provider.GetAccessTokenAsync(Ct);

        Assert.Equal("usable", token);
    }

    [Fact]
    public async Task InvalidateForcesAFreshTokenOnTheNextCall()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(access: "a1", refresh: null));
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(access: "a2", refresh: null));
        await _provider.GetAccessTokenAsync(Ct);

        _provider.Invalidate();
        var next = await _provider.GetAccessTokenAsync(Ct);

        Assert.Equal("a2", next);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task InvalidatingAnAlreadyReplacedTokenKeepsTheNewOne()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(access: "a1", refresh: null));
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(access: "a2", refresh: null));
        await _provider.GetAccessTokenAsync(Ct);
        _provider.Invalidate("a1");
        await _provider.GetAccessTokenAsync(Ct);

        // A slow request that was rejected with a1 reports it only now; a2 is fine and must survive.
        _provider.Invalidate("a1");
        var current = await _provider.GetAccessTokenAsync(Ct);

        Assert.Equal("a2", current);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task APrimedTokenIsUsedWithoutRefreshing()
    {
        _provider.Prime(new TokenSet("from-sign-in", _time.GetUtcNow().AddHours(1), Fixtures.RefreshToken, Fixtures.ReadOnlyScope));

        var token = await _provider.GetAccessTokenAsync(Ct);

        Assert.Equal("from-sign-in", token);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task ResetDropsTheCachedToken()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(access: "a1", refresh: null));
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(access: "a2", refresh: null));
        await _provider.GetAccessTokenAsync(Ct);

        _provider.Reset();

        Assert.Equal("a2", await _provider.GetAccessTokenAsync(Ct));
    }

    [Fact]
    public async Task WithoutAStoredCredentialItAsksToReconnectWithoutAnyRequest()
    {
        var provider = NewProvider(new InMemoryCredentialStore());

        var ex = await Assert.ThrowsAsync<ReconnectRequiredException>(() => provider.GetAccessTokenAsync(Ct).AsTask());

        Assert.Empty(_handler.Requests);
        MailboxHarness.AssertNoSecrets(ex);
    }

    [Fact]
    public async Task ItStaysFailedUntilReset()
    {
        var store = new InMemoryCredentialStore();
        var provider = NewProvider(store);
        await Assert.ThrowsAsync<ReconnectRequiredException>(() => provider.GetAccessTokenAsync(Ct).AsTask());
        await Assert.ThrowsAsync<ReconnectRequiredException>(() => provider.GetAccessTokenAsync(Ct).AsTask());
        Assert.Empty(_handler.Requests);

        store.With(Fixtures.Email, "new-refresh");
        provider.Reset();
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(access: "recovered", refresh: null));

        Assert.Equal("recovered", await provider.GetAccessTokenAsync(Ct));
    }

    [Fact]
    public async Task AFreshlyStoredCredentialAlsoEndsTheFailedState()
    {
        var store = new InMemoryCredentialStore();
        var provider = NewProvider(store);
        await Assert.ThrowsAsync<ReconnectRequiredException>(() => provider.GetAccessTokenAsync(Ct).AsTask());

        store.With(Fixtures.Email, "signed-in-again");
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(access: "recovered", refresh: null));

        Assert.Equal("recovered", await provider.GetAccessTokenAsync(Ct));
    }

    [Fact]
    public async Task ARevokedRefreshTokenMeansReconnectAndStaysSo()
    {
        _handler.EnqueueJson(HttpStatusCode.BadRequest, Fixtures.OAuthError("invalid_grant", "Token has been expired or revoked."));

        var ex = await Assert.ThrowsAsync<ReconnectRequiredException>(() => _provider.GetAccessTokenAsync(Ct).AsTask());
        _time.Advance(TimeSpan.FromHours(3));
        await Assert.ThrowsAsync<ReconnectRequiredException>(() => _provider.GetAccessTokenAsync(Ct).AsTask());

        Assert.Single(_handler.Requests);
        MailboxHarness.AssertNoSecrets(ex);
    }

    [Fact]
    public async Task ARejectedOAuthClientMeansReconnectButIsNotRememberedOnceFixed()
    {
        _handler.EnqueueJson(HttpStatusCode.Unauthorized, Fixtures.OAuthError("invalid_client", "The OAuth client was not found."));
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(access: "after-fix", refresh: null));

        var ex = await Assert.ThrowsAsync<ReconnectRequiredException>(() => _provider.GetAccessTokenAsync(Ct).AsTask());

        Assert.IsType<GoogleSignInException>(ex.InnerException);
        Assert.Equal("after-fix", await _provider.GetAccessTokenAsync(Ct));
    }

    [Fact]
    public async Task ResetAfterARevocationTriesTheNetworkAgain()
    {
        _handler.EnqueueJson(HttpStatusCode.BadRequest, Fixtures.OAuthError("invalid_grant"));
        await Assert.ThrowsAsync<ReconnectRequiredException>(() => _provider.GetAccessTokenAsync(Ct).AsTask());

        _provider.Reset();
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(access: "back", refresh: null));

        Assert.Equal("back", await _provider.GetAccessTokenAsync(Ct));
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task ATemporaryOutageIsNotRememberedAsAFailure()
    {
        _handler.EnqueueJson(HttpStatusCode.ServiceUnavailable, "{}");
        _handler.EnqueueException(new HttpRequestException("offline"));
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(access: "later", refresh: null));

        await Assert.ThrowsAsync<MailboxUnavailableException>(() => _provider.GetAccessTokenAsync(Ct).AsTask());
        await Assert.ThrowsAsync<MailboxUnavailableException>(() => _provider.GetAccessTokenAsync(Ct).AsTask());

        Assert.Equal("later", await _provider.GetAccessTokenAsync(Ct));
    }

    [Fact]
    public async Task AWaitingCallerCanCancelWithoutDisturbingTheRefresh()
    {
        var release = new TaskCompletionSource();
        _handler.Enqueue(async (_, _) =>
        {
            await release.Task;
            return ScriptedHandler.Json(HttpStatusCode.OK, Fixtures.Token(access: "done", refresh: null));
        });
        using var impatient = new CancellationTokenSource();

        var patient = Task.Run(() => _provider.GetAccessTokenAsync(Ct).AsTask(), Ct);
        await WaitUntilAsync(() => _handler.Requests.Count == 1);
        var waiting = Task.Run(() => _provider.GetAccessTokenAsync(impatient.Token).AsTask(), Ct);
        await impatient.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        release.SetResult();
        Assert.Equal("done", await patient);
    }

    [Fact]
    public async Task ACancelledCallerGetsACancellationNotAnOutage()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _provider.GetAccessTokenAsync(cts.Token).AsTask());

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task TheCredentialIsReadFromTheStoreForEachRefresh()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(access: "a1", refresh: null, expiresIn: 600));
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(access: "a2", refresh: null));
        await _provider.GetAccessTokenAsync(Ct);

        _store.With(Fixtures.Email, "replaced-elsewhere");
        _time.Advance(TimeSpan.FromMinutes(20));
        await _provider.GetAccessTokenAsync(Ct);

        Assert.Equal("replaced-elsewhere", _handler.Requests[1].Form["refresh_token"].Single());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not reached in time.");
            await Task.Yield();
        }
    }

    private sealed class FailingWriteStore(ICredentialStore inner) : ICredentialStore
    {
        public bool IsPersistent => inner.IsPersistent;

        public string Description => inner.Description;

        public Task<StoredCredential?> ReadAsync(string accountEmail, CancellationToken cancellationToken = default) => inner.ReadAsync(accountEmail, cancellationToken);

        public Task WriteAsync(string accountEmail, StoredCredential credential, CancellationToken cancellationToken = default) =>
            throw new IOException("The credential store is locked.");

        public Task DeleteAsync(string accountEmail, CancellationToken cancellationToken = default) => inner.DeleteAsync(accountEmail, cancellationToken);
    }
}
