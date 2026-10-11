using CommunityToolkit.Mvvm.ComponentModel;
using GmailWatcher.Desktop.Presentation;

namespace GmailWatcher.Desktop.ViewModels;

/// <summary>A question before something that can't be undone. For the worst ones the confirm button stays off until a phrase is typed.</summary>
public sealed partial class ConfirmViewModel(ConfirmRequest request) : ObservableObject
{
    public string Title => request.Title;

    public string Message => request.Message;

    public string ConfirmText => request.ConfirmText;

    public bool IsDangerous => request.IsDangerous;

    /// <summary>A notice, not a question: there is nothing to cancel.</summary>
    public bool InfoOnly => request.InfoOnly;

    public bool ShowCancel => !request.InfoOnly;

    /// <summary>Enter means Cancel, except for the dangerous questions (where Enter must never confirm) and notices (which have no Cancel).</summary>
    public bool CancelIsDefault => !request.IsDangerous && !request.InfoOnly;

    public bool NeedsPhrase => request.RequiredPhrase is not null;

    public string PhrasePrompt => request.RequiredPhrase is { } phrase ? $"Type {phrase} to confirm" : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    public partial string TypedPhrase { get; set; } = "";

    public bool CanConfirm => request.RequiredPhrase is null || string.Equals(TypedPhrase.Trim(), request.RequiredPhrase, StringComparison.Ordinal);
}
