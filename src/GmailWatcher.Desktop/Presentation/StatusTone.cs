namespace GmailWatcher.Desktop.Presentation;

/// <summary>Visual tone of a status indicator. Always paired with a text label, never color alone.</summary>
public enum StatusTone
{
    None,
    Unknown,
    Neutral,
    Running,
    Success,
    Warning,
    Failure,
}
