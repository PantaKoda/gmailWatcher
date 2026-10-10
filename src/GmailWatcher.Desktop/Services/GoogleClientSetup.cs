using System.Text.Json;
using GmailWatcher.Core.Configuration;
using GmailWatcher.Desktop.Infrastructure;

namespace GmailWatcher.Desktop.Services;

public sealed record ClientCredentials(string ClientId, string? ClientSecret);

/// <summary>
/// The Google OAuth client the app signs in with. Google requires every app to have one and it is created by whoever runs
/// the app (docs/google-cloud-setup.md): either paste the two values, or import the JSON file Google lets you download.
/// Changes apply immediately, without a restart.
/// </summary>
public sealed class GoogleClientSetup(GmailWatcherOptions options, UserConfigWriter config)
{
    public bool IsConfigured => options.Google.IsConfigured;

    public string? ClientId => options.Google.ClientId;

    /// <summary>Reads the "client_secret_*.json" file Google offers for download on the credentials page.</summary>
    /// <returns>The credentials, or null with <paramref name="error"/> saying what is wrong with the file.</returns>
    public static ClientCredentials? ReadClientFile(string path, out string? error)
    {
        error = null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            // Google nests the values under "installed" (Desktop app) or "web" (Web application).
            var root = document.RootElement;
            if (root.TryGetProperty("web", out _) && !root.TryGetProperty("installed", out _))
            {
                error = "This file is for a \"Web application\" client. Create an OAuth client of type \"Desktop app\" instead.";
                return null;
            }

            if (root.TryGetProperty("installed", out var installed)
                && installed.TryGetProperty("client_id", out var id) && id.GetString() is { Length: > 0 } clientId)
            {
                return new ClientCredentials(clientId, installed.TryGetProperty("client_secret", out var secret) ? secret.GetString() : null);
            }

            error = "This doesn't look like the JSON file Google offers for a Desktop app OAuth client (it has no \"installed\" section).";
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = $"The file couldn't be read ({ex.Message}).";
        }

        return null;
    }

    /// <summary>Validates and saves the client. Returns null on success, otherwise what is wrong with the values.</summary>
    public string? Save(string clientId, string? clientSecret)
    {
        clientId = clientId.Trim();
        clientSecret = string.IsNullOrWhiteSpace(clientSecret) ? null : clientSecret.Trim();
        var candidate = new GmailWatcherOptions
        {
            Google = new GoogleOptions { ClientId = clientId, ClientSecret = clientSecret },
        };

        if (string.IsNullOrEmpty(clientId))
        {
            return "Enter the Client ID.";
        }

        if (GmailWatcherOptionsValidator.Validate(candidate).FirstOrDefault(e => e.Key.StartsWith("Google:Client", StringComparison.Ordinal)) is { } problem)
        {
            return $"{problem.Problem} {problem.Fix}";
        }

        config.Set(("Google:ClientId", clientId), ("Google:ClientSecret", clientSecret));
        options.Google.ClientId = clientId;
        options.Google.ClientSecret = clientSecret;
        return null;
    }
}
