using GmailWatcher.Desktop.Services;

namespace GmailWatcher.Desktop.Presentation;

/// <summary>A question the user must answer before something that can't be undone.</summary>
/// <param name="RequiredPhrase">When set, the confirm button stays disabled until the user types it exactly (for deleting an archive).</param>
public sealed record ConfirmRequest(string Title, string Message, string ConfirmText, bool IsDangerous = false, string? RequiredPhrase = null);

/// <summary>Window-level actions view models can request without referencing windows.</summary>
public interface IShell
{
    void OpenSettings();

    /// <summary>Opens the update window: what changed since this version, and Install update.</summary>
    void OpenUpdate();

    /// <summary>Shows the Google OAuth client setup. True when a client is configured afterwards.</summary>
    Task<bool> OpenGoogleSetupAsync();

    /// <summary>Shows the sign-in dialog, which signs in and adds (or reconnects) an account. Null if the dialog was dismissed.</summary>
    Task<ConnectResult?> AddAccountAsync(string? expectedEmail);

    /// <summary>The folder the user picked, or null.</summary>
    Task<string?> PickFolderAsync(string title);

    /// <summary>The Google "client_secret" JSON file the user picked, or null.</summary>
    Task<string?> PickClientFileAsync();

    Task<bool> ConfirmAsync(ConfirmRequest request);

    /// <summary>Copies non-secret text (a sign-in link, a path) to the clipboard.</summary>
    Task CopyTextAsync(string text);

    /// <summary>Starts a second copy of the app on demo data, in its own data folder.</summary>
    void LaunchDemo();

    void Quit();
}
