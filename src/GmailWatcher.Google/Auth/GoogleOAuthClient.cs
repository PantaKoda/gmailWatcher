using System.Text;
using System.Text.Json;
using GmailWatcher.Core.Configuration;
using GmailWatcher.Core.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GmailWatcher.Google.Auth;

/// <summary>What a token request returned. Secret: <see cref="ToString"/> is redacted.</summary>
/// <param name="RefreshToken">Present on the first exchange; Google only sends a new one on refresh when it rotates it.</param>
/// <param name="Scope">The space-separated scopes Google actually granted.</param>
public sealed record TokenSet(string AccessToken, DateTimeOffset ExpiresAt, string? RefreshToken, string Scope)
{
    public override string ToString() => "TokenSet { redacted }";
}

/// <summary>
/// The OAuth 2.0 calls for a Google "Desktop app" client: authorization URL, code exchange with PKCE, refresh and
/// revoke, over plain HTTPS. Endpoints and client come from <see cref="GoogleOptions"/> (read on every call, so a
/// changed client takes effect without a restart). No token, code or secret is put in an exception message or a log.
/// </summary>
public sealed class GoogleOAuthClient
{
    /// <summary>The only scope Gmail Watcher ever asks for: read access, nothing that could change or delete mail.</summary>
    public const string GmailReadOnlyScope = "https://www.googleapis.com/auth/gmail.readonly";

    private readonly HttpClient _http;
    private readonly Func<GoogleOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public GoogleOAuthClient(HttpClient http, Func<GoogleOptions> options, TimeProvider time, ILogger? logger = null)
    {
        _http = http;
        _options = options;
        _time = time;
        _logger = logger ?? NullLogger.Instance;
    }

    public GoogleOAuthClient(HttpClient http, GoogleOptions options, TimeProvider time, ILogger? logger = null)
        : this(http, () => options, time, logger)
    {
    }

    /// <summary>
    /// The URL to open in the user's browser. <c>prompt=consent</c> makes Google issue a refresh token every time, and
    /// <c>access_type=offline</c> is what allows refresh tokens at all.
    /// </summary>
    public Uri BuildAuthorizationUri(string redirectUri, string state, string codeChallenge, string? loginHint = null)
    {
        var options = _options();
        var clientId = RequireClientId(options);

        var query = new StringBuilder();
        Append(query, "client_id", clientId);
        Append(query, "redirect_uri", redirectUri);
        Append(query, "response_type", "code");
        Append(query, "scope", GmailReadOnlyScope);
        Append(query, "access_type", "offline");
        Append(query, "prompt", "consent");
        Append(query, "code_challenge", codeChallenge);
        Append(query, "code_challenge_method", "S256");
        Append(query, "state", state);
        if (!string.IsNullOrWhiteSpace(loginHint))
        {
            Append(query, "login_hint", loginHint.Trim());
        }

        var endpoint = options.AuthorizationEndpoint;
        return new Uri($"{endpoint}{(endpoint.Contains('?', StringComparison.Ordinal) ? '&' : '?')}{query}", UriKind.Absolute);

        static void Append(StringBuilder builder, string name, string value)
        {
            if (builder.Length > 0)
            {
                builder.Append('&');
            }

            builder.Append(name).Append('=').Append(Uri.EscapeDataString(value));
        }
    }

    /// <exception cref="GoogleSignInException">
    /// Google refused the code, did not return a refresh token, or the user did not grant Gmail read access
    /// (<see cref="GoogleSignInFailure.MissingPermission"/>; the grant is revoked again).
    /// </exception>
    /// <exception cref="MailboxUnavailableException">Google could not be reached or had a server error.</exception>
    public async Task<TokenSet> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken)
    {
        var options = _options();
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = RequireClientId(options),
        };
        AddClientSecret(form, options);

        var tokens = await PostTokenRequestAsync(options, form, isRefresh: false, [code, codeVerifier], cancellationToken).ConfigureAwait(false);

        if (!HasReadOnlyScope(tokens.Scope))
        {
            // Google's granular consent lets people untick Gmail access and still continue. Without it every call would fail later.
            await RevokeAsync(tokens.RefreshToken ?? tokens.AccessToken, cancellationToken).ConfigureAwait(false);
            throw new GoogleSignInException(
                GoogleSignInFailure.MissingPermission,
                "Gmail read access was not granted. On Google's permission screen, leave \"View your email messages and settings\" ticked, then sign in again.");
        }

        if (string.IsNullOrEmpty(tokens.RefreshToken))
        {
            await RevokeAsync(tokens.AccessToken, cancellationToken).ConfigureAwait(false);
            throw new GoogleSignInException(
                GoogleSignInFailure.Failed,
                "Google did not give Gmail Watcher a long-lived sign-in, so it could not keep syncing. Remove Gmail Watcher under Google Account > Security > Third-party access, then sign in again.");
        }

        return tokens;
    }

    /// <exception cref="ReconnectRequiredException">Google no longer accepts the refresh token (revoked, expired, password changed).</exception>
    /// <exception cref="MailboxUnavailableException">Google could not be reached or had a server error.</exception>
    /// <exception cref="GoogleSignInException">Google rejected the OAuth client itself or the request.</exception>
    public async Task<TokenSet> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var options = _options();
        if (!options.IsConfigured)
        {
            throw new ReconnectRequiredException("Google sign-in is not set up: no OAuth client ID is configured. Add one in Settings, then sign in again.");
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = options.ClientId!.Trim(),
        };
        AddClientSecret(form, options);

        return await PostTokenRequestAsync(options, form, isRefresh: true, [refreshToken], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Asks Google to forget a token. Best effort: it never throws, and says whether Google confirmed.</summary>
    public async Task<bool> RevokeAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            var options = _options();
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token });
            using var request = new HttpRequestMessage(HttpMethod.Post, options.RevocationEndpoint) { Content = content };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Google did not confirm revoking a token (HTTP {Status}).", (int)response.StatusCode);
            }

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not revoke a token at Google.");
            return false;
        }
    }

    private async Task<TokenSet> PostTokenRequestAsync(GoogleOptions options, Dictionary<string, string> form, bool isRefresh, string[] secrets, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var request = new HttpRequestMessage(HttpMethod.Post, options.TokenEndpoint) { Content = content };
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new MailboxUnavailableException("Google did not answer in time. Check your internet connection and try again.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new MailboxUnavailableException("Could not reach Google. Check your internet connection and try again.", ex);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (status >= 500 || status is 408 or 429)
            {
                throw new MailboxUnavailableException($"Google is not available right now (HTTP {status}). Try again in a moment.");
            }

            if (response.IsSuccessStatusCode)
            {
                return await ReadTokenSetAsync(response, ct).ConfigureAwait(false);
            }

            var error = await ReadOAuthErrorAsync(response, ct).ConfigureAwait(false);
            var errorCode = SafeErrorCode(error?.Error);
            if (errorCode == "invalid_grant")
            {
                if (isRefresh)
                {
                    throw new ReconnectRequiredException("Google no longer accepts this account's saved sign-in (it was revoked, expired or the password changed). Sign in again.");
                }

                throw new GoogleSignInException(
                    GoogleSignInFailure.Failed,
                    "Google rejected the sign-in code. It may have expired or already been used; try signing in again.");
            }

            var description = Scrub(error?.ErrorDescription, secrets);
            var detail = string.IsNullOrEmpty(description) ? string.Empty : $" ({description})";
            throw new GoogleSignInException(
                GoogleSignInFailure.Failed,
                errorCode switch
                {
                    "invalid_client" => $"Google does not accept this OAuth client. Check the client ID and secret in Settings.{detail}",
                    "redirect_uri_mismatch" => $"Google does not accept the local sign-in address. Make sure the OAuth client is of type \"Desktop app\".{detail}",
                    _ => $"Google refused the sign-in request ({errorCode}).{detail}",
                });
        }
    }

    private async Task<TokenSet> ReadTokenSetAsync(HttpResponseMessage response, CancellationToken ct)
    {
        TokenResponseDto? dto;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            dto = await JsonSerializer.DeserializeAsync(stream, GoogleJsonContext.Default.TokenResponseDto, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new GoogleSignInException(GoogleSignInFailure.Failed, "Google's answer could not be understood. Try again in a moment.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new MailboxUnavailableException("The connection to Google was interrupted. Try again.", ex);
        }

        if (dto is null || string.IsNullOrEmpty(dto.AccessToken))
        {
            throw new GoogleSignInException(GoogleSignInFailure.Failed, "Google's answer did not contain an access token. Try again in a moment.");
        }

        var lifetime = dto.ExpiresIn is > 0 ? dto.ExpiresIn.Value : 3600;
        return new TokenSet(
            dto.AccessToken,
            _time.GetUtcNow().AddSeconds(lifetime),
            string.IsNullOrEmpty(dto.RefreshToken) ? null : dto.RefreshToken,
            dto.Scope ?? string.Empty);
    }

    private static async Task<OAuthErrorDto?> ReadOAuthErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync(stream, GoogleJsonContext.Default.OAuthErrorDto, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException)
        {
            return null;
        }
    }

    private static void AddClientSecret(Dictionary<string, string> form, GoogleOptions options)
    {
        // Google documents the secret of a Desktop client as not confidential, but its token endpoint still asks for it.
        if (!string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            form["client_secret"] = options.ClientSecret.Trim();
        }
    }

    private static string RequireClientId(GoogleOptions options)
    {
        if (!options.IsConfigured)
        {
            throw new GoogleSignInException(
                GoogleSignInFailure.Failed,
                "Google sign-in is not set up: no OAuth client ID is configured. Create a \"Desktop app\" client in the Google Cloud console and add its ID in Settings.");
        }

        return options.ClientId!.Trim();
    }

    private static bool HasReadOnlyScope(string scope) =>
        scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(GmailReadOnlyScope, StringComparer.Ordinal);

    /// <summary>OAuth error codes are short snake_case words; anything else (it came from the network) is not shown.</summary>
    private static string SafeErrorCode(string? code)
    {
        if (string.IsNullOrEmpty(code) || code.Length > 64 || !code.All(c => c is (>= 'a' and <= 'z') or '_'))
        {
            return "unknown_error";
        }

        return code;
    }

    private static string Scrub(string? text, string[] secrets)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var cleaned = new string(text.Where(c => c >= ' ' && c < 127).ToArray());
        foreach (var secret in secrets.Where(s => !string.IsNullOrEmpty(s)))
        {
            cleaned = cleaned.Replace(secret, "[removed]", StringComparison.Ordinal);
        }

        return cleaned.Length > 200 ? cleaned[..200] : cleaned;
    }
}
