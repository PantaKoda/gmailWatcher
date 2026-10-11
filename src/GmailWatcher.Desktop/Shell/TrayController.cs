using Avalonia;
using Avalonia.Controls;
using GmailWatcher.Desktop.Platform;
using GmailWatcher.Desktop.Presentation;

namespace GmailWatcher.Desktop.Shell;

/// <summary>What the tray icon's menu can ask the app to do.</summary>
public sealed record TrayActions(Action Open, Action SyncAll, Action OpenSettings, Action OpenUpdate, Action Quit);

/// <summary>
/// The notification-area icon, like Docker Desktop's or Tailscale's: always there while Gmail Watcher runs, a status dot
/// while it works or needs you, a tooltip with the state, and a menu to open the window, sync now, or quit. A left click
/// opens the window.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly TrayIcon _icon;
    private readonly NativeMenuItem _status;
    private readonly NativeMenuItem _update;
    private readonly Dictionary<TrayIconKind, WindowIcon> _icons = [];
    private TrayIconKind _shown = TrayIconKind.Normal;

    public TrayController(Application application, TrayActions actions)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(actions);
        foreach (var kind in Enum.GetValues<TrayIconKind>())
        {
            _icons[kind] = AppIconFactory.Create(kind);
        }

        var open = new NativeMenuItem("Open Gmail Watcher");
        open.Click += (_, _) => actions.Open();
        _status = new NativeMenuItem("Starting…") { IsEnabled = false };
        var sync = new NativeMenuItem("Sync all accounts now");
        sync.Click += (_, _) => actions.SyncAll();
        _update = new NativeMenuItem("Update available…") { IsVisible = false };
        _update.Click += (_, _) => actions.OpenUpdate();
        var settings = new NativeMenuItem("Settings…");
        settings.Click += (_, _) => actions.OpenSettings();
        var quit = new NativeMenuItem("Quit Gmail Watcher");
        quit.Click += (_, _) => actions.Quit();

        _icon = new TrayIcon
        {
            Icon = _icons[TrayIconKind.Normal],
            ToolTipText = "Gmail Watcher",
            IsVisible = true,
            Menu = new NativeMenu
            {
                open,
                _status,
                new NativeMenuItemSeparator(),
                sync,
                _update,
                settings,
                new NativeMenuItemSeparator(),
                quit,
            },
        };
        _icon.Clicked += (_, _) => actions.Open();
        TrayIcon.SetIcons(application, [_icon]);
    }

    /// <summary>Shows the current state. Cheap to call often: nothing is touched when nothing changed.</summary>
    public void Apply(TrayStatus status, bool updateAvailable, string? updateText = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!string.Equals(_icon.ToolTipText, status.Tooltip, StringComparison.Ordinal))
        {
            _icon.ToolTipText = status.Tooltip;
        }

        if (!string.Equals(_status.Header, status.StatusLine, StringComparison.Ordinal))
        {
            _status.Header = status.StatusLine;
        }

        if (_shown != status.Icon)
        {
            _shown = status.Icon;
            _icon.Icon = _icons[status.Icon];
        }

        _update.IsVisible = updateAvailable;
        if (updateAvailable)
        {
            _update.Header = updateText is { Length: > 0 } ? updateText : "Update available…";
        }
    }

    public void Dispose()
    {
        // A tray icon that is not removed lingers until the mouse passes over it.
        _icon.IsVisible = false;
        _icon.Dispose();
    }
}
