using GmailWatcher.Core.Configuration;
using GmailWatcher.Google.Auth;
using GmailWatcher.Google.Gmail;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GmailWatcher.Google.Tests.Support;

/// <summary>A mailbox wired to a fake network and a fake clock, with a ready access token.</summary>
internal sealed class MailboxHarness
{
    public MailboxHarness(int unitsPerMinute = 6000, bool tokenReady = true)
    {
        Handler = new ScriptedHandler(Time);
        Http = new HttpClient(Handler);
        OAuth = new GoogleOAuthClient(Http, Options, Time);
        Tokens = new AccessTokenProvider(Fixtures.Email, Store, OAuth, Time);
        if (tokenReady)
        {
            Tokens.Prime(new TokenSet(Fixtures.AccessToken, Time.GetUtcNow().AddHours(24), null, Fixtures.ReadOnlyScope));
        }

        Governor = new QuotaGovernor(unitsPerMinute, Time);
        Mailbox = new GmailRestMailbox(Http, Options, Tokens, Governor, Time, NullLogger.Instance);
    }

    public TestClock Time { get; } = new();

    public ScriptedHandler Handler { get; }

    public HttpClient Http { get; }

    public GoogleOptions Options { get; } = Fixtures.Options();

    public InMemoryCredentialStore Store { get; } = new InMemoryCredentialStore().With(Fixtures.Email, Fixtures.RefreshToken);

    public GoogleOAuthClient OAuth { get; }

    public AccessTokenProvider Tokens { get; }

    public QuotaGovernor Governor { get; }

    public GmailRestMailbox Mailbox { get; }

    public IReadOnlyList<RecordedRequest> GmailRequests => Handler.Requests.Where(request => request.Uri.Host == "gmail.test").ToList();

    public IReadOnlyList<RecordedRequest> TokenRequests => Handler.Requests.Where(request => request.Uri.Host == "oauth.test").ToList();

    public Task<T> RunAsync<T>(Task<T> task) => Time.PumpAsync(task);

    public Task RunAsync(Task task) => Time.PumpAsync(task);

    /// <summary>The Gmail request that was sent at position <paramref name="index"/>.</summary>
    public RecordedRequest GmailRequest(int index) => GmailRequests[index];

    /// <summary>No token, code or secret may ever travel in a URL.</summary>
    public void AssertNoSecretsInUrls()
    {
        foreach (var request in Handler.Requests)
        {
            foreach (var secret in Fixtures.Secrets)
            {
                Assert.DoesNotContain(secret, request.Url);
                Assert.DoesNotContain(Uri.EscapeDataString(secret), request.Url);
            }
        }
    }

    public static void AssertNoSecrets(Exception exception)
    {
        var text = exception.ToString();
        foreach (var secret in Fixtures.Secrets)
        {
            Assert.DoesNotContain(secret, text);
        }
    }
}
