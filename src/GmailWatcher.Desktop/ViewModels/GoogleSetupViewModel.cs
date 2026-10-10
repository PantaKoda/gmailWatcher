using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GmailWatcher.Core.Platform;
using GmailWatcher.Desktop.Presentation;
using GmailWatcher.Desktop.Services;

namespace GmailWatcher.Desktop.ViewModels;

/// <summary>
/// Guides the one-time creation of the Google OAuth client and takes its two values (pasted, or imported from the JSON file
/// Google offers). Everything stays on this PC.
/// </summary>
public sealed partial class GoogleSetupViewModel(GoogleClientSetup setup, IShell shell, IExternalBrowser browser) : ObservableObject
{
    [ObservableProperty]
    public partial string ClientId { get; set; } = setup.ClientId ?? "";

    [ObservableProperty]
    public partial string ClientSecret { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; private set; }

    public bool HasProblem => Problem is not null;

    /// <summary>Raised after the client was saved; the window closes.</summary>
    public event EventHandler? Saved;

    [RelayCommand]
    private async Task ImportFileAsync()
    {
        if (await shell.PickClientFileAsync() is not { } path)
        {
            return;
        }

        if (GoogleClientSetup.ReadClientFile(path, out var error) is { } credentials)
        {
            ClientId = credentials.ClientId;
            ClientSecret = credentials.ClientSecret ?? "";
            Problem = null;
        }
        else
        {
            Problem = error;
        }
    }

    [RelayCommand]
    private void Save()
    {
        Problem = setup.Save(ClientId, ClientSecret);
        if (Problem is null)
        {
            Saved?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private Task OpenProjectAsync() => Open("https://console.cloud.google.com/projectcreate");

    [RelayCommand]
    private Task OpenGmailApiAsync() => Open("https://console.cloud.google.com/apis/library/gmail.googleapis.com");

    [RelayCommand]
    private Task OpenConsentAsync() => Open("https://console.cloud.google.com/apis/credentials/consent");

    [RelayCommand]
    private Task OpenCredentialsAsync() => Open("https://console.cloud.google.com/apis/credentials");

    private async Task Open(string url)
    {
        if (await browser.OpenAsync(new Uri(url)) != LinkOpenResult.Opened)
        {
            Problem = "Your browser couldn't be opened. Copy this address into it instead: " + url;
        }
    }
}
