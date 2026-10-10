using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GmailWatcher.Core.Platform;
using GmailWatcher.Desktop.Presentation;
using GmailWatcher.Desktop.Services;

namespace GmailWatcher.Desktop.ViewModels;

/// <summary>
/// The sign-in dialog. It starts the sign-in the moment it opens: Google's page opens in the browser, this window waits for
/// the answer, and it closes itself when an account was added. The sign-in link is shown and copyable, for when the browser
/// can't be opened automatically.
/// </summary>
public sealed partial class ConnectViewModel : ObservableObject, IDisposable
{
    private readonly AccountService _accounts;
    private readonly IShell _shell;
    private readonly IUiDispatcher _dispatcher;
    private readonly CancellationTokenSource _cancel = new();
    private readonly string? _expectedEmail;
    private Task? _running;

    public ConnectViewModel(AccountService accounts, IShell shell, IUiDispatcher dispatcher, string? expectedEmail)
    {
        _accounts = accounts;
        _shell = shell;
        _dispatcher = dispatcher;
        _expectedEmail = expectedEmail;
        Heading = expectedEmail is null ? "Add a Gmail account" : $"Reconnect {expectedEmail}";
        Instruction = IsDemo
            ? "Demo: no browser is needed. Signing in to a made-up mailbox…"
            : "Your browser is opening Google's sign-in page. Choose your account and allow read-only access to your Gmail. Then come back here: this window closes by itself.";
    }

    private bool IsDemo => _accounts.Connector is DemoAccountConnector;

    /// <summary>Set when the dialog finished: the account that was added, or why not.</summary>
    public ConnectResult? Result { get; private set; }

    /// <summary>Raised when the dialog should close (success, cancel, or an error the user has read).</summary>
    public event EventHandler? CloseRequested;

    public string Heading { get; }

    [ObservableProperty]
    public partial string Instruction { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLink))]
    public partial string? AuthorizationLink { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(IsWaiting))]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaiting))]
    public partial bool IsDone { get; private set; }

    public bool HasLink => AuthorizationLink is not null && !IsDemo;

    public bool HasError => Error is not null;

    public bool IsWaiting => !IsDone && Error is null;

    /// <summary>Starts the sign-in. Called once, when the dialog opens.</summary>
    public void Start() => _running ??= RunAsync();

    private async Task RunAsync()
    {
        var link = new Progress<Uri>(url => _dispatcher.Post(() => AuthorizationLink = url.AbsoluteUri));
        ConnectResult result;
        try
        {
            result = await _accounts.ConnectAsync(_expectedEmail, link, _cancel.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = new ConnectResult(null, "Cancelled.", false);
        }

        _dispatcher.Post(() =>
        {
            Result = result;
            if (result.Succeeded || _cancel.IsCancellationRequested)
            {
                IsDone = true;
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Error = result.Error;
            }
        });
    }

    [RelayCommand]
    private async Task CopyLinkAsync()
    {
        if (AuthorizationLink is { } link)
        {
            await _shell.CopyTextAsync(link);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsDone)
        {
            return;
        }

        _cancel.Cancel();
        if (_running is null || _running.IsCompleted)
        {
            // Nothing in flight (an error is showing): just close.
            Result ??= new ConnectResult(null, null, false);
            IsDone = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private void TryAgain()
    {
        Error = null;
        AuthorizationLink = null;
        _running = RunAsync();
    }

    public void Dispose()
    {
        _cancel.Cancel();
        _cancel.Dispose();
    }
}
