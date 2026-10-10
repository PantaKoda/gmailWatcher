using GmailWatcher.Archive.Demo;
using GmailWatcher.Core.Accounts;
using GmailWatcher.Core.Configuration;
using GmailWatcher.Core.Mail;
using GmailWatcher.Google.Auth;
using GmailWatcher.Google.Gmail;
using Microsoft.Extensions.Logging;

namespace GmailWatcher.Desktop.Services;

/// <summary>
/// Signs a person in to a Google account and leaves its refresh token in the credential store. The two implementations are the
/// real Google sign-in and the demo stand-in, so the rest of the app doesn't know which one it is talking to.
/// </summary>
public interface IAccountConnector
{
    /// <summary>False when sign-in can't work yet (no Google OAuth client configured); <see cref="UnavailableReason"/> says why.</summary>
    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    /// <summary>
    /// Opens the sign-in, waits for it, and returns the verified address of the signed-in mailbox (its refresh token is stored
    /// under that address). <paramref name="authorizationUrl"/> receives the sign-in link so it can be shown or copied.
    /// </summary>
    /// <exception cref="ConnectException">Sign-in did not work; the message is for the user.</exception>
    Task<string> SignInAsync(string? loginHint, IProgress<Uri>? authorizationUrl, CancellationToken cancellationToken);

    /// <summary>Tells Google to forget the app's access (best effort) and removes the stored token.</summary>
    Task SignOutAsync(string email, CancellationToken cancellationToken);
}

/// <summary>A sign-in failure with a message written for the user.</summary>
public sealed class ConnectException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class GoogleAccountConnector(
    GmailWatcherOptions options,
    GoogleSignIn signIn,
    GoogleOAuthClient oauth,
    GmailMailboxFactory mailboxes,
    ICredentialStore credentials,
    ILogger<GoogleAccountConnector> logger) : IAccountConnector
{
    public bool IsAvailable => options.Google.IsConfigured;

    public string? UnavailableReason => IsAvailable
        ? null
        : "Google sign-in isn't set up yet. Gmail Watcher needs a Google OAuth client (a one-time, five-minute setup in the Google Cloud console).";

    public async Task<string> SignInAsync(string? loginHint, IProgress<Uri>? authorizationUrl, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            throw new ConnectException(UnavailableReason!);
        }

        try
        {
            var result = await signIn.SignInAsync(new SignInOptions { LoginHint = loginHint, AuthorizationUrlReady = authorizationUrl, KeepWaitingIfBrowserFails = true }, cancellationToken)
                .ConfigureAwait(false);

            // The refresh token is stored only after Gmail says whose it is: the credential is keyed by the verified address.
            var profile = await mailboxes.GetProfileAsync(result.Tokens, cancellationToken).ConfigureAwait(false);
            await credentials.WriteAsync(profile.EmailAddress, new StoredCredential(result.RefreshToken), cancellationToken).ConfigureAwait(false);
            mailboxes.ResetAccount(profile.EmailAddress);
            return profile.EmailAddress;
        }
        catch (GoogleSignInException ex)
        {
            throw new ConnectException(Describe(ex), ex);
        }
        catch (ReconnectRequiredException ex)
        {
            throw new ConnectException(ex.Message, ex);
        }
        catch (MailboxUnavailableException ex)
        {
            throw new ConnectException(ex.Message, ex);
        }
    }

    public async Task SignOutAsync(string email, CancellationToken cancellationToken)
    {
        try
        {
            if (await credentials.ReadAsync(email, cancellationToken).ConfigureAwait(false) is { } stored)
            {
                // Best effort: if Google can't be reached the local token is still removed, and the user can revoke it
                // at myaccount.google.com/permissions.
                await oauth.RevokeAsync(stored.RefreshToken, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation(ex, "Google could not be told to revoke access for an account");
        }

        await credentials.DeleteAsync(email, cancellationToken).ConfigureAwait(false);
        mailboxes.ForgetAccount(email);
    }

    internal static string Describe(GoogleSignInException ex) => ex.Kind switch
    {
        GoogleSignInFailure.Denied => "Google sign-in was cancelled, so nothing was changed.",
        GoogleSignInFailure.MissingPermission =>
            "Gmail Watcher needs permission to read your mail. On Google's screen, leave \"View your email messages and settings\" ticked, then try again.",
        GoogleSignInFailure.TimedOut => "The sign-in wasn't finished in time. Try again when you're ready.",
        GoogleSignInFailure.BrowserUnavailable => "Your browser couldn't be opened. Copy the sign-in link, open it in a browser, and finish there.",
        _ => ex.Message,
    };
}

/// <summary>Demo mode: "signs in" without a browser, to a made-up mailbox. No network, no Google.</summary>
public sealed class DemoAccountConnector(DemoMailboxFactory mailboxes, ICredentialStore credentials) : IAccountConnector
{
    private int _next = 1;

    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public async Task<string> SignInAsync(string? loginHint, IProgress<Uri>? authorizationUrl, CancellationToken cancellationToken)
    {
        await Task.Delay(400, cancellationToken).ConfigureAwait(false); // a moment, as a real sign-in would take
        var email = loginHint ?? $"demo{_next++}@example.com";
        await mailboxes.OpenAsync(email, cancellationToken).ConfigureAwait(false);
        await credentials.WriteAsync(email, new StoredCredential("demo-token"), cancellationToken).ConfigureAwait(false);
        return email;
    }

    public Task SignOutAsync(string email, CancellationToken cancellationToken) => credentials.DeleteAsync(email, cancellationToken);
}
