using System.Text.Json.Nodes;
using GmailWatcher.Core.Configuration;
using GmailWatcher.Google.Auth;
using Microsoft.Extensions.Time.Testing;

namespace GmailWatcher.Google.Tests.Support;

internal static class Fixtures
{
    public const string ClientId = "123456789012-testclient.apps.googleusercontent.com";
    public const string ClientSecret = "GOCSPX-test-client-secret";
    public const string AccessToken = "ya29.a0-test-access-token";
    public const string RefreshToken = "1//0g-test-refresh-token";
    public const string Code = "4/0A-test-authorization-code";
    public const string Email = "user@example.com";
    public const string ReadOnlyScope = GoogleOAuthClient.GmailReadOnlyScope;

    public static readonly DateTimeOffset Start = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Every token and code a test uses; none may ever show up in a URL or an exception message.</summary>
    public static readonly string[] Secrets = [AccessToken, RefreshToken, Code, ClientSecret];

    public static GoogleOptions Options(string? clientSecret = ClientSecret) => new()
    {
        ClientId = ClientId,
        ClientSecret = clientSecret,
        AuthorizationEndpoint = "https://accounts.test/o/oauth2/v2/auth",
        TokenEndpoint = "https://oauth.test/token",
        RevocationEndpoint = "https://oauth.test/revoke",
        ApiBaseUrl = "https://gmail.test/gmail/v1/",
    };

    public static string Token(string access = AccessToken, int expiresIn = 3600, string? refresh = RefreshToken, string? scope = ReadOnlyScope)
    {
        var json = new JsonObject { ["access_token"] = access, ["expires_in"] = expiresIn, ["token_type"] = "Bearer" };
        if (refresh is not null)
        {
            json["refresh_token"] = refresh;
        }

        if (scope is not null)
        {
            json["scope"] = scope;
        }

        return json.ToJsonString();
    }

    public static string OAuthError(string error, string? description = null)
    {
        var json = new JsonObject { ["error"] = error };
        if (description is not null)
        {
            json["error_description"] = description;
        }

        return json.ToJsonString();
    }

    /// <summary>A Google API error body in the documented shape.</summary>
    public static string ApiError(int code, string message, string? reason = null, string status = "PERMISSION_DENIED", string? detailReason = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message, ["status"] = status };
        if (reason is not null)
        {
            error["errors"] = new JsonArray(new JsonObject { ["message"] = message, ["domain"] = "global", ["reason"] = reason });
        }

        if (detailReason is not null)
        {
            error["details"] = new JsonArray(new JsonObject
            {
                ["@type"] = "type.googleapis.com/google.rpc.ErrorInfo",
                ["reason"] = detailReason,
                ["domain"] = "googleapis.com",
            });
        }

        return new JsonObject { ["error"] = error }.ToJsonString();
    }

    public static string ProfileJson(string email = Email, long messages = 1234, long threads = 567, string historyId = "98765") =>
        new JsonObject { ["emailAddress"] = email, ["messagesTotal"] = messages, ["threadsTotal"] = threads, ["historyId"] = historyId }.ToJsonString();
}

