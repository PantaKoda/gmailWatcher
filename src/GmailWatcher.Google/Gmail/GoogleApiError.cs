using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace GmailWatcher.Google.Gmail;

internal enum ApiErrorKind
{
    /// <summary>Not one of the cases Gmail Watcher treats specially; the caller decides.</summary>
    Other,

    /// <summary>Too many requests right now: pause and retry.</summary>
    RateLimit,

    /// <summary>The day's quota is used up: retrying today will not help.</summary>
    DailyLimit,

    /// <summary>The token does not carry the Gmail read scope.</summary>
    InsufficientScope,

    /// <summary>"insufficientPermissions" without a scope hint: for a single setting this can just mean "not allowed for this account".</summary>
    InsufficientPermission,

    /// <summary>The Gmail API is not enabled in the Google Cloud project that owns the OAuth client.</summary>
    ApiDisabled,
}

/// <summary>A Google API error body, reduced to what the classification needs.</summary>
internal sealed partial record GoogleApiError(string? Message, string? Status, IReadOnlyList<string> Reasons)
{
    public static GoogleApiError? From(ApiErrorEnvelopeDto? envelope)
    {
        if (envelope?.Error is not { } error)
        {
            return null;
        }

        // error.errors[].reason ("rateLimitExceeded") and error.details[].reason (ErrorInfo: "ACCESS_TOKEN_SCOPE_INSUFFICIENT").
        var reasons = (error.Errors ?? []).Concat(error.Details ?? [])
            .Select(item => item.Reason)
            .Where(reason => !string.IsNullOrEmpty(reason))
            .Select(reason => reason!)
            .ToList();
        return new GoogleApiError(error.Message, error.Status, reasons);
    }

    public bool HasReason(string reason) => Reasons.Contains(reason, StringComparer.OrdinalIgnoreCase);

    public static ApiErrorKind Classify(HttpStatusCode status, GoogleApiError? error)
    {
        var code = (int)status;
        var message = error?.Message ?? string.Empty;
        var has = (string reason) => error?.HasReason(reason) == true;

        if (code is 400 or 403 or 404 && (has("accessNotConfigured") || has("SERVICE_DISABLED")))
        {
            return ApiErrorKind.ApiDisabled;
        }

        if (code is not (403 or 429))
        {
            return ApiErrorKind.Other;
        }

        // "per day" must win over the rate-limit reasons: Google reuses rateLimitExceeded for some per-day quota messages.
        if (has("dailyLimitExceeded") || has("dailyLimitExceededUnreg")
            || message.Contains("per day", StringComparison.OrdinalIgnoreCase)
            || message.Contains("daily limit", StringComparison.OrdinalIgnoreCase))
        {
            return ApiErrorKind.DailyLimit;
        }

        if (has("ACCESS_TOKEN_SCOPE_INSUFFICIENT") || message.Contains("insufficient authentication scopes", StringComparison.OrdinalIgnoreCase))
        {
            return ApiErrorKind.InsufficientScope;
        }

        if (code == 429
            || has("rateLimitExceeded") || has("userRateLimitExceeded") || has("RATE_LIMIT_EXCEEDED") || has("quotaExceeded")
            || string.Equals(error?.Status, "RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase))
        {
            return ApiErrorKind.RateLimit;
        }

        return has("insufficientPermissions") ? ApiErrorKind.InsufficientPermission : ApiErrorKind.Other;
    }

    /// <summary>
    /// Gmail's per-user limit message reads "User-rate limit exceeded.  Retry after 2026-10-10T12:34:56.789Z". That time is
    /// more precise than any backoff, so it is used when there is no Retry-After header.
    /// </summary>
    public static TimeSpan? RetryAfterFromMessage(string? message, DateTimeOffset now)
    {
        if (message is null)
        {
            return null;
        }

        var match = RetryAfterPattern().Match(message);
        if (!match.Success
            || !DateTimeOffset.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var until))
        {
            return null;
        }

        var wait = until - now;
        return wait > TimeSpan.Zero ? wait : null;
    }

    [GeneratedRegex(@"Retry after (\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z)")]
    private static partial Regex RetryAfterPattern();
}
