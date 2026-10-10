using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GmailWatcher.Core.Accounts;
using GmailWatcher.Desktop.Presentation;
using GmailWatcher.Desktop.Services;

namespace GmailWatcher.Desktop.ViewModels;

/// <summary>What an account card's buttons do. Implemented by the main view model, which owns the services.</summary>
public interface IAccountCommands
{
    void Sync(AccountRecord account);

    void Stop(int accountId);

    void Verify(AccountRecord account);

    Task ReconnectAsync(AccountRecord account);

    void OpenFolder(AccountRecord account);

    /// <summary>Forgets the sync position so the next sync lists the whole mailbox again (slower, but checks everything).</summary>
    void RecheckEverything(AccountRecord account);

    Task DisconnectAsync(AccountRecord account);

    Task DeleteArchiveAsync(AccountRecord account);

    void ShowDetails(AccountRowViewModel row);

    void CloseDetails();
}

/// <summary>One account card in the main list. Long-lived: refreshed in place so the list never flickers or loses focus.</summary>
public sealed partial class AccountRowViewModel : ObservableObject
{
    private readonly IAccountCommands _commands;
    private readonly TimeProvider _time;

    public AccountRowViewModel(AccountRecord record, IAccountCommands commands, TimeProvider time)
    {
        _commands = commands;
        _time = time;
        Record = record;
        Display = AccountPresenter.Present(record, null, AccountActivity.Idle, time.GetUtcNow());
    }

    [ObservableProperty]
    public partial AccountRecord Record { get; private set; }

    [ObservableProperty]
    public partial AccountStats? Stats { get; private set; }

    [ObservableProperty]
    public partial AccountActivity Activity { get; private set; } = AccountActivity.Idle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConnectionTag))]
    [NotifyPropertyChangedFor(nameof(VerifyIsGood))]
    [NotifyPropertyChangedFor(nameof(VerifyIsWarning))]
    [NotifyPropertyChangedFor(nameof(VerifyIsBad))]
    [NotifyPropertyChangedFor(nameof(ShowVerifyLine))]
    public partial AccountDisplay Display { get; private set; }

    public int Id => Record.Id;

    public string Title => Record.Title;

    public string Email => Record.Email;

    public bool HasConnectionTag => Display.ConnectionTag is not null;

    public bool VerifyIsGood => Display.Verify == VerifyState.Verified;

    public bool VerifyIsWarning => Display.Verify is VerifyState.Stale or VerifyState.FilesOnly;

    public bool VerifyIsBad => Display.Verify == VerifyState.Problems;

    public bool ShowVerifyLine => Display.VerifyLine.Length > 0 && !Display.IsBusy;

    /// <summary>Recomputes the card from fresh data. Any argument that is null keeps its current value.</summary>
    public void Refresh(AccountRecord? record = null, AccountStats? stats = null, AccountActivity? activity = null)
    {
        if (record is not null)
        {
            Record = record;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Email));
        }

        if (stats is not null)
        {
            Stats = stats;
        }

        if (activity is not null)
        {
            Activity = activity;
        }

        Display = AccountPresenter.Present(Record, Stats, Activity, _time.GetUtcNow());
    }

    [RelayCommand]
    private void Sync() => _commands.Sync(Record);

    [RelayCommand]
    private void Stop() => _commands.Stop(Id);

    [RelayCommand]
    private void Verify() => _commands.Verify(Record);

    [RelayCommand]
    private Task ReconnectAsync() => _commands.ReconnectAsync(Record);

    [RelayCommand]
    private void OpenFolder() => _commands.OpenFolder(Record);

    [RelayCommand]
    private void RecheckEverything() => _commands.RecheckEverything(Record);

    [RelayCommand]
    private Task DisconnectAsync() => _commands.DisconnectAsync(Record);

    [RelayCommand]
    private Task DeleteArchiveAsync() => _commands.DeleteArchiveAsync(Record);

    [RelayCommand]
    private void ShowDetails() => _commands.ShowDetails(this);
}
