using System.Net;
using GmailWatcher.Core.Configuration;
using GmailWatcher.Core.Mail;
using GmailWatcher.Google.Auth;
using GmailWatcher.Google.Gmail;
using GmailWatcher.Google.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GmailWatcher.Google.Tests.Gmail;

public sealed class GmailMailboxFactoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestClock _time = new();
    private readonly ScriptedHandler _handler;
    private readonly InMemoryCredentialStore _store = new InMemoryCredentialStore().With(Fixtures.Email, Fixtures.RefreshToken);
    private readonly GoogleOptions _options = Fixtures.Options();
    private readonly GmailMailboxFactory _factory;

    public GmailMailboxFactoryTests()
    {
        _handler = new ScriptedHandler(_time);
        _factory = new GmailMailboxFactory(new HttpClient(_handler), () => _options, new SyncOptions { QuotaUnitsPerMinute = 6000 }, _store, _time, NullLoggerFactory.Instance);
    }

    private IReadOnlyList<RecordedRequest> TokenRequests => _handler.Requests.Where(request => request.Uri.Host == "oauth.test").ToList();

    private IReadOnlyList<RecordedRequest> GmailRequests => _handler.Requests.Where(request => request.Uri.Host == "gmail.test").ToList();

    [Fact]
    public async Task WithoutASavedSignInItAsksToReconnectBeforeAnyRequest()
    {
        var ex = await Assert.ThrowsAsync<ReconnectRequiredException>(() => _factory.OpenAsync("stranger@example.com", Ct));

        Assert.Empty(_handler.Requests);
        MailboxHarness.AssertNoSecrets(ex);
    }

    [Fact]
    public async Task OpensAMailboxThatSignsInWithTheStoredRefreshToken()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(access: "access-1", refresh: null));
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ProfileJson("user@example.com"));

        var mailbox = await _factory.OpenAsync(Fixtures.Email, Ct);
        var profile = await mailbox.GetProfileAsync(Ct);

        Assert.Equal("user@example.com", profile.EmailAddress);
        Assert.Equal(Fixtures.RefreshToken, Assert.Single(TokenRequests).Form["refresh_token"].Single());
        Assert.Equal("access-1", Assert.Single(GmailRequests).Authorization?.Parameter);
    }

    [Fact]
    public async Task IsUsableThroughTheCoreInterface()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(refresh: null));
        _handler.EnqueueJson(HttpStatusCode.OK, """{"labels":[]}""");
        IMailboxFactory factory = _factory;

        var mailbox = await factory.OpenAsync(Fixtures.Email, Ct);

        Assert.Empty(await mailbox.GetLabelsAsync(Ct));
    }

    [Fact]
    public async Task AnAccountKeepsOneAccessTokenAcrossMailboxesAndSpellingsOfItsAddress()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(access: "access-1", refresh: null));
        _handler.Always(request => ScriptedHandler.Json(HttpStatusCode.OK, Fixtures.ProfileJson()));

        var first = await _factory.OpenAsync(Fixtures.Email, Ct);
        var second = await _factory.OpenAsync("User@Example.COM", Ct);
        var third = await _factory.OpenAsync(" user@example.com ", Ct);
        await first.GetProfileAsync(Ct);
        await second.GetProfileAsync(Ct);
        await third.GetProfileAsync(Ct);

        Assert.Single(TokenRequests);
        Assert.Equal(3, GmailRequests.Count);
        Assert.All(GmailRequests, request => Assert.Equal("access-1", request.Authorization?.Parameter));
    }

    [Fact]
    public async Task EachAccountHasItsOwnTokens()
    {
        _store.With("other@example.com", "other-refresh-token");
        _handler.Always(request => request.Uri.Host == "oauth.test"
            ? ScriptedHandler.Json(HttpStatusCode.OK, Fixtures.Token(access: "access-for-" + request.Form["refresh_token"].Single(), refresh: null))
            : ScriptedHandler.Json(HttpStatusCode.OK, Fixtures.ProfileJson()));

        await (await _factory.OpenAsync(Fixtures.Email, Ct)).GetProfileAsync(Ct);
        await (await _factory.OpenAsync("other@example.com", Ct)).GetProfileAsync(Ct);

        Assert.Equal(2, TokenRequests.Count);
        Assert.Equal(["access-for-" + Fixtures.RefreshToken, "access-for-other-refresh-token"], GmailRequests.Select(request => request.Authorization?.Parameter));
    }

    [Fact]
    public async Task MailboxesOfOneAccountShareTheirRateLimitMemory()
    {
        _store.With("other@example.com", "other-refresh-token");
        var limited = true;
        _handler.Always(request =>
        {
            if (request.Uri.Host == "oauth.test")
            {
                return ScriptedHandler.Json(HttpStatusCode.OK, Fixtures.Token(refresh: null));
            }

            if (limited && request.Authorization is not null)
            {
                limited = false;
                return ScriptedHandler.Json(HttpStatusCode.TooManyRequests, Fixtures.ApiError(429, "Too many requests", "rateLimitExceeded", "RESOURCE_EXHAUSTED"), TimeSpan.FromSeconds(30));
            }

            return ScriptedHandler.Json(HttpStatusCode.OK, Fixtures.ProfileJson());
        });
        var one = await _factory.OpenAsync(Fixtures.Email, Ct);
        var another = await _factory.OpenAsync(Fixtures.Email, Ct);
        var otherAccount = await _factory.OpenAsync("other@example.com", Ct);

        var limitedCall = one.GetProfileAsync(Ct);
        var sameAccount = another.GetProfileAsync(Ct);
        var differentAccount = otherAccount.GetProfileAsync(Ct);
        await _time.PumpAsync(differentAccount);
        var otherDone = _time.GetUtcNow();
        await _time.PumpAsync(Task.WhenAll(limitedCall, sameAccount));

        var firstGmail = GmailRequests[0].At;
        Assert.True(otherDone - firstGmail < TimeSpan.FromSeconds(30), "another account was held up by this account's limit");
        var afterLimit = GmailRequests.Where(request => request.At - firstGmail >= TimeSpan.FromSeconds(30)).ToList();
        Assert.True(afterLimit.Count >= 2, "both mailboxes of the limited account should have waited out the pause");
    }

    [Fact]
    public async Task PicksUpAChangedOAuthClientWithoutBeingRebuilt()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(refresh: null));
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ProfileJson());
        await (await _factory.OpenAsync(Fixtures.Email, Ct)).GetProfileAsync(Ct);

        _options.ClientId = "999999999999-changed.apps.googleusercontent.com";
        _options.ApiBaseUrl = "https://gmail.other.test/v1/";
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(refresh: null));
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ProfileJson());
        _time.Advance(TimeSpan.FromHours(2));
        await (await _factory.OpenAsync(Fixtures.Email, Ct)).GetProfileAsync(Ct);

        Assert.Equal(Fixtures.ClientId, TokenRequests[0].Form["client_id"].Single());
        Assert.Equal("999999999999-changed.apps.googleusercontent.com", TokenRequests[1].Form["client_id"].Single());
        Assert.Equal("gmail.other.test", _handler.Requests[^1].Uri.Host);
    }

    [Fact]
    public async Task ARevokedSignInStaysFailedUntilTheAccountIsReset()
    {
        _handler.EnqueueJson(HttpStatusCode.BadRequest, Fixtures.OAuthError("invalid_grant"));
        var mailbox = await _factory.OpenAsync(Fixtures.Email, Ct);
        await Assert.ThrowsAsync<ReconnectRequiredException>(() => mailbox.GetProfileAsync(Ct));
        await Assert.ThrowsAsync<ReconnectRequiredException>(() => mailbox.GetProfileAsync(Ct));
        Assert.Single(_handler.Requests);

        _factory.ResetAccount(Fixtures.Email);
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(refresh: null));
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ProfileJson());

        Assert.Equal(Fixtures.Email, (await mailbox.GetProfileAsync(Ct)).EmailAddress);
    }

    [Fact]
    public async Task SigningInAgainEndsAFailedStateEvenIfTheCallerOnlyStoresTheNewToken()
    {
        _handler.EnqueueJson(HttpStatusCode.BadRequest, Fixtures.OAuthError("invalid_grant"));
        var mailbox = await _factory.OpenAsync(Fixtures.Email, Ct);
        await Assert.ThrowsAsync<ReconnectRequiredException>(() => mailbox.GetProfileAsync(Ct));

        _store.With(Fixtures.Email, "brand-new-refresh-token");
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(refresh: null));
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ProfileJson());

        await mailbox.GetProfileAsync(Ct);

        Assert.Equal("brand-new-refresh-token", TokenRequests[1].Form["refresh_token"].Single());
    }

    [Fact]
    public async Task ForgettingAnAccountStartsItFromScratch()
    {
        _handler.Always(request => request.Uri.Host == "oauth.test"
            ? ScriptedHandler.Json(HttpStatusCode.OK, Fixtures.Token(refresh: null))
            : ScriptedHandler.Json(HttpStatusCode.OK, Fixtures.ProfileJson()));
        await (await _factory.OpenAsync(Fixtures.Email, Ct)).GetProfileAsync(Ct);

        _factory.ForgetAccount(Fixtures.Email);
        await (await _factory.OpenAsync(Fixtures.Email, Ct)).GetProfileAsync(Ct);

        Assert.Equal(2, TokenRequests.Count);
    }

    [Fact]
    public void ResettingOrForgettingAnUnknownAccountIsHarmless()
    {
        _factory.ResetAccount("nobody@example.com");
        _factory.ForgetAccount("nobody@example.com");
    }

    [Fact]
    public async Task OpeningAnEmptyAddressIsAProgrammingError()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _factory.OpenAsync(null!, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _factory.OpenAsync("  ", Ct));
    }

    // ---- who just signed in ----

    [Fact]
    public async Task TellsWhoseMailboxANewSignInBelongsToWithoutStoringAnything()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ProfileJson("new.person@example.com"));
        var signedIn = new TokenSet("fresh-access-token", Fixtures.Start.AddHours(1), "fresh-refresh-token", Fixtures.ReadOnlyScope);

        var profile = await _factory.GetProfileAsync(signedIn, Ct);

        Assert.Equal("new.person@example.com", profile.EmailAddress);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal("fresh-access-token", request.Authorization?.Parameter);
        Assert.Equal(0, _store.Writes);
        Assert.Equal(0, _store.Reads);
    }

    [Fact]
    public async Task TheSignInIdentityCheckRefreshesWithTheNewRefreshTokenIfTheAccessTokenIsRejected()
    {
        _handler.EnqueueJson(HttpStatusCode.Unauthorized, "{}");
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.Token(access: "second-access", refresh: null));
        _handler.EnqueueJson(HttpStatusCode.OK, Fixtures.ProfileJson("new.person@example.com"));
        var signedIn = new TokenSet("fresh-access-token", Fixtures.Start.AddHours(1), "fresh-refresh-token", Fixtures.ReadOnlyScope);

        var profile = await _factory.GetProfileAsync(signedIn, Ct);

        Assert.Equal("new.person@example.com", profile.EmailAddress);
        Assert.Equal("fresh-refresh-token", TokenRequests.Single().Form["refresh_token"].Single());
        Assert.Equal(0, _store.Writes);
    }
}
