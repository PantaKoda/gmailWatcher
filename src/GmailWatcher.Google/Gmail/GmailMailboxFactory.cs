using System.Collections.Concurrent;
using GmailWatcher.Core.Accounts;
using GmailWatcher.Core.Configuration;
using GmailWatcher.Core.Mail;
using GmailWatcher.Google.Auth;
using Microsoft.Extensions.Logging;

namespace GmailWatcher.Google.Gmail;

/// <summary>
/// Opens the mailboxes of connected accounts. One <see cref="AccessTokenProvider"/> and one <see cref="QuotaGovernor"/>
/// are kept per account for the life of the factory, so every sync, verification and background check of an account
/// shares its token and its rate memory instead of each learning Gmail's limits the hard way.
/// </summary>
public sealed class GmailMailboxFactory : IMailboxFactory
{
    private readonly HttpClient _http;
    private readonly Func<GoogleOptions> _google;
    private readonly SyncOptions _sync;
    private readonly ICredentialStore _store;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggers;
    private readonly GoogleOAuthClient _oauth;
    private readonly ConcurrentDictionary<string, AccountState> _accounts = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="google">Read on every call, so the OAuth client and endpoints can be changed while the app runs.</param>
    public GmailMailboxFactory(HttpClient http, Func<GoogleOptions> google, SyncOptions sync, ICredentialStore store, TimeProvider time, ILoggerFactory loggers)
    {
        _http = http;
        _google = google;
        _sync = sync;
        _store = store;
        _time = time;
        _loggers = loggers;
        _oauth = new GoogleOAuthClient(http, google, time, loggers.CreateLogger<GoogleOAuthClient>());
    }

    public GmailMailboxFactory(HttpClient http, GoogleOptions google, SyncOptions sync, ICredentialStore store, TimeProvider time, ILoggerFactory loggers)
        : this(http, () => google, sync, store, time, loggers)
    {
    }

    /// <exception cref="ReconnectRequiredException">There is no saved sign-in for the account.</exception>
    public async Task<IMailbox> OpenAsync(string accountEmail, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountEmail);
        var email = accountEmail.Trim();

        var credential = await _store.ReadAsync(email, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(credential?.RefreshToken))
        {
            throw new ReconnectRequiredException("No saved sign-in for this account. Connect the account again.");
        }

        var account = _accounts.GetOrAdd(email, CreateAccount);
        return new GmailRestMailbox(_http, _google(), account.Tokens, account.Governor, _time, _loggers.CreateLogger<GmailRestMailbox>());
    }

    /// <summary>
    /// Call after a (re)connect has stored a new refresh token: clears a failed sign-in state and the cached access token
    /// of that account. The rate memory is kept.
    /// </summary>
    public void ResetAccount(string accountEmail)
    {
        if (_accounts.TryGetValue(accountEmail.Trim(), out var account))
        {
            account.Tokens.Reset();
        }
    }

    /// <summary>Drops everything remembered about an account (after it is disconnected or removed).</summary>
    public void ForgetAccount(string accountEmail) => _accounts.TryRemove(accountEmail.Trim(), out _);

    /// <summary>
    /// Asks Gmail whose mailbox a fresh sign-in is for, before anything is stored: the account's email address is not part of
    /// the tokens, and a reconnect must not silently attach a different account's tokens.
    /// </summary>
    public async Task<MailboxProfile> GetProfileAsync(TokenSet signedIn, CancellationToken cancellationToken)
    {
        var tokens = new AccessTokenProvider("(sign-in)", new SingleCredentialStore(signedIn.RefreshToken), _oauth, _time, _loggers.CreateLogger<AccessTokenProvider>());
        tokens.Prime(signedIn);
        var governor = new QuotaGovernor(Math.Max(60, _sync.QuotaUnitsPerMinute), _time);
        var mailbox = new GmailRestMailbox(_http, _google(), tokens, governor, _time, _loggers.CreateLogger<GmailRestMailbox>());
        return await mailbox.GetProfileAsync(cancellationToken).ConfigureAwait(false);
    }

    private AccountState CreateAccount(string email) => new(
        new AccessTokenProvider(email, _store, _oauth, _time, _loggers.CreateLogger<AccessTokenProvider>()),
        new QuotaGovernor(Math.Max(60, _sync.QuotaUnitsPerMinute), _time));

    private sealed record AccountState(AccessTokenProvider Tokens, QuotaGovernor Governor);

    /// <summary>Holds one refresh token in memory for the identity check; nothing is persisted.</summary>
    private sealed class SingleCredentialStore(string? refreshToken) : ICredentialStore
    {
        private StoredCredential? _credential = string.IsNullOrEmpty(refreshToken) ? null : new StoredCredential(refreshToken);

        public bool IsPersistent => false;

        public string Description => "memory";

        public Task<StoredCredential?> ReadAsync(string accountEmail, CancellationToken cancellationToken = default) => Task.FromResult(_credential);

        public Task WriteAsync(string accountEmail, StoredCredential credential, CancellationToken cancellationToken = default)
        {
            _credential = credential;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string accountEmail, CancellationToken cancellationToken = default)
        {
            _credential = null;
            return Task.CompletedTask;
        }
    }
}
