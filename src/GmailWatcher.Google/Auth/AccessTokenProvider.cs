using GmailWatcher.Core.Accounts;
using GmailWatcher.Core.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GmailWatcher.Google.Auth;

/// <summary>
/// Hands out a valid access token for one account. The refresh token lives in the <see cref="ICredentialStore"/>;
/// the access token only in memory. Many workers can ask at once: one refresh runs and the others share its result.
/// </summary>
public sealed class AccessTokenProvider
{
    /// <summary>A token this close to expiry is replaced, so a request never starts with one that dies in flight.</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(2);

    private readonly string _accountEmail;
    private readonly ICredentialStore _store;
    private readonly GoogleOAuthClient _oauth;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _stateLock = new();
    private Cached? _cached;
    private ReconnectRequiredException? _failure;
    private string? _failedRefreshToken;

    public AccessTokenProvider(string accountEmail, ICredentialStore store, GoogleOAuthClient oauth, TimeProvider time, ILogger? logger = null)
    {
        _accountEmail = accountEmail;
        _store = store;
        _oauth = oauth;
        _time = time;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <exception cref="ReconnectRequiredException">No saved sign-in, or Google no longer accepts it. Stays failed until <see cref="Reset"/> (or until a different refresh token is stored).</exception>
    /// <exception cref="MailboxUnavailableException">Google could not be reached; the next call tries again.</exception>
    public async ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (Fresh(Volatile.Read(ref _cached)) is { } current)
        {
            return current;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have refreshed while this one waited.
            if (Fresh(Volatile.Read(ref _cached)) is { } refreshed)
            {
                return refreshed;
            }

            var stored = await _store.ReadAsync(_accountEmail, cancellationToken).ConfigureAwait(false);
            var storedToken = string.IsNullOrEmpty(stored?.RefreshToken) ? null : stored.RefreshToken;

            lock (_stateLock)
            {
                if (_failure is not null)
                {
                    // Signing in again stores a different token: that is as good as a Reset.
                    if (storedToken is null || storedToken == _failedRefreshToken)
                    {
                        throw new ReconnectRequiredException(_failure.Message, _failure);
                    }

                    _failure = null;
                    _failedRefreshToken = null;
                }
            }

            if (storedToken is null)
            {
                throw Fail(null, new ReconnectRequiredException("No saved sign-in for this account. Connect the account again."));
            }

            TokenSet tokens;
            try
            {
                tokens = await _oauth.RefreshAsync(storedToken, cancellationToken).ConfigureAwait(false);
            }
            catch (ReconnectRequiredException ex)
            {
                throw Fail(storedToken, ex);
            }
            catch (GoogleSignInException ex)
            {
                // Google refused the OAuth client itself (wrong or deleted client ID or secret). Signing in again is the way
                // out, and IMailbox users only know ReconnectRequiredException. Not remembered: once the settings are fixed
                // the next call simply works.
                throw new ReconnectRequiredException(ex.Message, ex);
            }

            Volatile.Write(ref _cached, new Cached(tokens.AccessToken, tokens.ExpiresAt));
            await SaveRotatedTokenAsync(storedToken, tokens).ConfigureAwait(false);
            return tokens.AccessToken;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Forces the next call to fetch a new access token (for example after Gmail answered 401).</summary>
    public void Invalidate() => Volatile.Write(ref _cached, null);

    /// <summary>
    /// Like <see cref="Invalidate()"/>, but only when <paramref name="rejectedToken"/> is still the current one. Several
    /// requests can be rejected with the same token at once; only the first should discard it, not a newer replacement.
    /// </summary>
    public void Invalidate(string rejectedToken)
    {
        var current = Volatile.Read(ref _cached);
        if (current is not null && string.Equals(current.Token, rejectedToken, StringComparison.Ordinal))
        {
            Interlocked.CompareExchange(ref _cached, null, current);
        }
    }

    /// <summary>Clears a failed state and the cached token, for example after the user signed in again.</summary>
    public void Reset()
    {
        lock (_stateLock)
        {
            _failure = null;
            _failedRefreshToken = null;
        }

        Volatile.Write(ref _cached, null);
    }

    /// <summary>
    /// Uses a token that was just obtained by a sign-in, so the first request does not refresh again. The refresh
    /// token is not touched: it is stored by whoever completed the sign-in.
    /// </summary>
    public void Prime(TokenSet tokens) => Volatile.Write(ref _cached, new Cached(tokens.AccessToken, tokens.ExpiresAt));

    private string? Fresh(Cached? cached) =>
        cached is not null && cached.ExpiresAt - _time.GetUtcNow() > RefreshMargin ? cached.Token : null;

    private ReconnectRequiredException Fail(string? refreshToken, ReconnectRequiredException failure)
    {
        lock (_stateLock)
        {
            _failure = failure;
            _failedRefreshToken = refreshToken;
        }

        Volatile.Write(ref _cached, null);
        return failure;
    }

    private async Task SaveRotatedTokenAsync(string current, TokenSet tokens)
    {
        if (string.IsNullOrEmpty(tokens.RefreshToken) || tokens.RefreshToken == current)
        {
            return;
        }

        try
        {
            // Not cancellable: the old token may already be dead, so losing the new one would force a reconnect.
            await _store.WriteAsync(_accountEmail, new StoredCredential(tokens.RefreshToken), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Google issued a new refresh token but it could not be saved.");
        }
    }

    private sealed record Cached(string Token, DateTimeOffset ExpiresAt)
    {
        public override string ToString() => "Cached { redacted }";
    }
}
