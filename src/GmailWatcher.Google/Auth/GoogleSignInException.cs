namespace GmailWatcher.Google.Auth;

public enum GoogleSignInFailure
{
    /// <summary>The user declined on Google's consent screen.</summary>
    Denied,

    /// <summary>The user signed in but did not allow Gmail read access (Google lets people untick it).</summary>
    MissingPermission,

    /// <summary>Nobody completed the sign-in in the browser before the time limit.</summary>
    TimedOut,

    /// <summary>The system browser could not be opened. The sign-in link is still available for copying.</summary>
    BrowserUnavailable,

    /// <summary>Anything else: a misconfigured OAuth client, a mismatching reply, a refused code.</summary>
    Failed,
}

/// <summary>
/// A sign-in did not produce usable access. The message is written for the user and never contains tokens, codes
/// or secrets.
/// </summary>
public sealed class GoogleSignInException(GoogleSignInFailure kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public GoogleSignInFailure Kind { get; } = kind;
}
