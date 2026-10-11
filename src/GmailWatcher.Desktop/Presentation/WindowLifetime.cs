using Avalonia.Controls;

namespace GmailWatcher.Desktop.Presentation;

public enum CloseAction
{
    /// <summary>Let the window close (the app then shuts down).</summary>
    Close,
    /// <summary>Keep the app running: hide the window, the tray icon stays.</summary>
    HideToTray,
}

/// <summary>
/// What closing or minimising the main window does. Kept as plain rules so they can be tested: Windows shutting down or the
/// app quitting must never be held back by "keep running in the tray".
/// </summary>
public static class WindowLifetime
{
    /// <summary>The command-line argument the Windows start-up entry passes: start hidden in the tray.</summary>
    public const string BackgroundArgument = "--background";

    public static CloseAction OnClosing(WindowCloseReason reason, bool keepRunningInTray, bool quitting) =>
        reason == WindowCloseReason.WindowClosing && keepRunningInTray && !quitting ? CloseAction.HideToTray : CloseAction.Close;

    public static bool HideOnMinimize(WindowState state, bool keepRunningInTray, bool quitting) =>
        state == WindowState.Minimized && keepRunningInTray && !quitting;

    /// <summary>Start without showing the window: launched by Windows at sign-in, and the user wants it in the tray.</summary>
    public static bool StartHidden(IEnumerable<string> arguments, bool keepRunningInTray) =>
        keepRunningInTray && arguments.Contains(BackgroundArgument, StringComparer.OrdinalIgnoreCase);
}
