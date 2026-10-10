using System.Text.RegularExpressions;

namespace GmailWatcher.Desktop.Infrastructure;

/// <summary>
/// Removes secrets and mail addresses from text leaving the app (the diagnostics export). Gmail Watcher never logs tokens
/// or message content; this is a second line of defense for anything that slipped through, including Bearer headers in
/// exception messages and addresses in URLs.
/// </summary>
public static partial class Redactor
{
    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = AccessToken().Replace(text, "[redacted token]");
        text = RefreshToken().Replace(text, "[redacted token]");
        text = ClientSecret().Replace(text, "[redacted secret]");
        text = BearerHeader().Replace(text, "${prefix}[redacted]");
        text = SecretField().Replace(text, "${name}${separator}[redacted]");
        text = AuthorizationCodeInUrl().Replace(text, "${name}=[redacted]");
        return EmailAddress().Replace(text, "[address]");
    }

    // ya29.… OAuth access tokens.
    [GeneratedRegex(@"\bya29\.[A-Za-z0-9_\-\.]{20,}")]
    private static partial Regex AccessToken();

    // 1//… OAuth refresh tokens.
    [GeneratedRegex(@"\b1//[A-Za-z0-9_\-]{20,}")]
    private static partial Regex RefreshToken();

    // GOCSPX-… OAuth client secrets.
    [GeneratedRegex(@"\bGOCSPX-[A-Za-z0-9_\-]{10,}")]
    private static partial Regex ClientSecret();

    [GeneratedRegex(@"(?<prefix>\b(?:Bearer|token)\s+)[A-Za-z0-9._\-]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex BearerHeader();

    // "access_token": "...", refresh_token=..., client_secret: ...
    [GeneratedRegex(@"(?<name>""?\b(?:access_token|refresh_token|id_token|client_secret|code_verifier)\b""?)(?<separator>\s*[:=]\s*""?)[^""&\s,}]+", RegexOptions.IgnoreCase)]
    private static partial Regex SecretField();

    // ?code=...&state=... in a redirect URL
    [GeneratedRegex(@"(?<name>[?&](?:code|state))=[^&\s""]+", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationCodeInUrl();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b")]
    private static partial Regex EmailAddress();
}
