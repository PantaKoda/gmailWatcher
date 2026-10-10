using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using GmailWatcher.Core.Mail;
using GmailWatcher.Core.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GmailWatcher.Google.Auth;

/// <summary>Options of one sign-in attempt.</summary>
public sealed record SignInOptions
{
    /// <summary>Pre-selects this Google account on the sign-in page (used when reconnecting a known account).</summary>
    public string? LoginHint { get; init; }

    /// <summary>
    /// Receives the authorization URL as soon as the local listener is ready, before the browser is opened. Show it as a
    /// "copy link" fallback. With <see cref="KeepWaitingIfBrowserFails"/> the link keeps working even when no browser opened.
    /// </summary>
    public IProgress<Uri>? AuthorizationUrlReady { get; init; }

    /// <summary>How long to wait for the user to finish in the browser.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// When the system browser cannot be opened: false throws <see cref="GoogleSignInFailure.BrowserUnavailable"/> at once; true
    /// keeps waiting so the user can open the link elsewhere (the URL is available through <see cref="AuthorizationUrlReady"/>).
    /// </summary>
    public bool KeepWaitingIfBrowserFails { get; init; }
}

/// <summary>A completed sign-in. <see cref="RefreshToken"/> is the secret to store; the access token is short-lived.</summary>
public sealed record SignInResult(TokenSet Tokens)
{
    public string RefreshToken => Tokens.RefreshToken ?? throw new InvalidOperationException("The sign-in did not return a refresh token.");
}

/// <summary>
/// One interactive Google sign-in: PKCE, a loopback listener, the system browser, state check and code exchange.
/// Always requests the read-only Gmail scope.
/// </summary>
public sealed class GoogleSignIn
{
    private readonly GoogleOAuthClient _oauth;
    private readonly IExternalBrowser _browser;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public GoogleSignIn(GoogleOAuthClient oauth, IExternalBrowser browser, TimeProvider time, ILogger? logger = null)
    {
        _oauth = oauth;
        _browser = browser;
        _time = time;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <exception cref="GoogleSignInException">The user declined, did not allow Gmail access, took too long, or the sign-in failed (see <see cref="GoogleSignInException.Kind"/>).</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public async Task<SignInResult> SignInAsync(SignInOptions? options, CancellationToken cancellationToken)
    {
        options ??= new SignInOptions();
        var verifier = Pkce.CreateVerifier();
        var state = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

        using var listener = new LoopbackListener(_time);
        var url = _oauth.BuildAuthorizationUri(listener.RedirectUri, state, Pkce.CreateChallenge(verifier), options.LoginHint);

        options.AuthorizationUrlReady?.Report(url);
        var opened = await _browser.OpenAsync(url).ConfigureAwait(false);
        if (opened != LinkOpenResult.Opened)
        {
            _logger.LogWarning("The system browser could not be opened for sign-in ({Result}).", opened);
            if (!options.KeepWaitingIfBrowserFails)
            {
                throw new GoogleSignInException(
                    GoogleSignInFailure.BrowserUnavailable,
                    "Gmail Watcher could not open your browser. Copy the sign-in link and open it in a browser yourself.");
            }
        }

        LoopbackResult callback;
        try
        {
            callback = await listener.WaitForCallbackAsync(options.Timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new GoogleSignInException(
                GoogleSignInFailure.TimedOut,
                "The sign-in was not finished in time. Start again and complete the steps in your browser.",
                ex);
        }

        // Checked first, so a forged redirect (a page that merely knows the port) cannot pass for a denial either.
        if (!StateMatches(state, callback.State))
        {
            throw new GoogleSignInException(
                GoogleSignInFailure.Failed,
                "The sign-in reply did not match this sign-in attempt, so it was ignored. Start again.");
        }

        if (callback.Error is not null)
        {
            throw callback.Error == "access_denied"
                ? new GoogleSignInException(GoogleSignInFailure.Denied, "Google sign-in was cancelled. Nothing was connected.")
                : new GoogleSignInException(GoogleSignInFailure.Failed, $"Google ended the sign-in with an error ({DescribeError(callback.Error)}).");
        }

        if (string.IsNullOrEmpty(callback.Code))
        {
            throw new GoogleSignInException(GoogleSignInFailure.Failed, "Google's reply did not contain a sign-in code. Start again.");
        }

        try
        {
            var tokens = await _oauth.ExchangeCodeAsync(callback.Code, verifier, listener.RedirectUri, cancellationToken).ConfigureAwait(false);
            return new SignInResult(tokens);
        }
        catch (MailboxUnavailableException ex)
        {
            throw new GoogleSignInException(GoogleSignInFailure.Failed, ex.Message, ex);
        }
    }

    private static bool StateMatches(string expected, string? actual) =>
        actual is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));

    /// <summary>The error code came from the query string of a local request, so only plain OAuth-style words are repeated.</summary>
    private static string DescribeError(string error) =>
        error.Length is > 0 and <= 64 && error.All(c => c is (>= 'a' and <= 'z') or '_') ? error : "unknown_error";
}
