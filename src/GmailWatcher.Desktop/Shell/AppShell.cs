using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using GmailWatcher.Archive.Demo;
using GmailWatcher.Core.Settings;
using GmailWatcher.Desktop.Infrastructure;
using GmailWatcher.Desktop.Platform;
using GmailWatcher.Desktop.Platform.Windowing;
using GmailWatcher.Desktop.Presentation;
using GmailWatcher.Desktop.Services;
using GmailWatcher.Desktop.Updates;
using GmailWatcher.Desktop.ViewModels;
using GmailWatcher.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GmailWatcher.Desktop.Shell;

/// <summary>
/// Owns the windows and the app lifetime: the main window, the dialogs, appearance (theme, accent, density, motion), and a
/// clean shutdown that lets running syncs commit what they already downloaded.
/// </summary>
public sealed class AppShell(
    IServiceProvider services,
    SettingsService settings,
    ArchiveHost archive,
    SyncService sync,
    AccountService accounts,
    UpdateService updates,
    AutoSyncScheduler autoSync,
    AppMode mode,
    ILogger<AppShell> logger) : IShell, IDisposable
{
    public const string DemoArgument = "--demo";

    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private Application? _application;
    private MainWindow? _main;
    private MainViewModel? _mainViewModel;
    private SettingsWindow? _settingsWindow;
    private SettingsViewModel? _settingsViewModel;
    private UpdateWindow? _updateWindow;
    private DispatcherTimer? _clock;

    public void Start(IClassicDesktopStyleApplicationLifetime desktop, Application application)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        _desktop = desktop;
        _application = application;

        settings.Load();
        ApplyAppearance();
        settings.AppChanged += (_, e) =>
        {
            if (e.Previous.Appearance != e.Current.Appearance)
            {
                Dispatcher.UIThread.Post(ApplyAppearance);
            }
        };

        archive.TryOpen();
        if (mode.IsDemo)
        {
            _ = SeedDemoAsync();
        }

        var icon = AppIconFactory.Create();
        _mainViewModel = ActivatorUtilities.CreateInstance<MainViewModel>(services, this);
        _main = new MainWindow { DataContext = _mainViewModel, Icon = icon };
        RestorePlacement(_main);
        ApplyAppearance();
        _main.Closing += OnMainClosing;
        desktop.MainWindow = _main;
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        desktop.ShutdownRequested += (_, _) => Shutdown();

        // A second launch asks this one to show its window.
        if (services.GetService<SingleInstance>() is { } instance)
        {
            instance.ActivationRequested += (_, _) => Dispatcher.UIThread.Post(ShowMain);
            instance.Listen();
        }

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clock.Tick += (_, _) => _mainViewModel.Tick();
        _clock.Start();

        // Updates: a daily check; installing is always the user's choice. The new version takes over after we quit.
        updates.ExitRequested += (_, _) => Dispatcher.UIThread.Post(Quit);
        updates.Start();
        autoSync.Start();
        logger.LogInformation("Shell started; demo {Demo}, archive {Archive}", mode.IsDemo, archive.IsOpen ? "open" : "not open");
    }

    // --- IShell -------------------------------------------------------------------------------------------------------

    public void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsViewModel = ActivatorUtilities.CreateInstance<SettingsViewModel>(services, this);
            _settingsWindow = new SettingsWindow { DataContext = _settingsViewModel, Icon = _main?.Icon };
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsViewModel?.Dispose();
                _settingsViewModel = null;
                _settingsWindow = null;
            };
        }

        BringToFront(_settingsWindow);
    }

    public void OpenUpdate()
    {
        if (_updateWindow is null)
        {
            var viewModel = ActivatorUtilities.CreateInstance<UpdateViewModel>(services);
            _updateWindow = new UpdateWindow { DataContext = viewModel, Icon = _main?.Icon };
            _updateWindow.Closed += (_, _) =>
            {
                viewModel.Dispose(); // an install in progress keeps running in UpdateService
                _updateWindow = null;
            };
        }

        BringToFront(_updateWindow);
    }

    public async Task<bool> OpenGoogleSetupAsync()
    {
        var viewModel = ActivatorUtilities.CreateInstance<GoogleSetupViewModel>(services, this);
        var window = new GoogleSetupWindow { DataContext = viewModel, Icon = _main?.Icon };
        Prepare(window);
        var saved = _main is null ? false : await window.ShowDialog<bool>(_main);
        return saved || services.GetRequiredService<GoogleClientSetup>().IsConfigured;
    }

    public async Task<ConnectResult?> AddAccountAsync(string? expectedEmail)
    {
        if (_main is null)
        {
            return null;
        }

        var viewModel = new ConnectViewModel(accounts, this, new AvaloniaUiDispatcher(), expectedEmail);
        var window = new ConnectWindow { DataContext = viewModel, Icon = _main.Icon };
        Prepare(window);
        await window.ShowDialog(_main);
        viewModel.Dispose();
        return viewModel.Result;
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        if (_main is null)
        {
            return null;
        }

        var folders = await _main.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickClientFileAsync()
    {
        var owner = (Window?)_settingsWindow ?? _main;
        if (owner is null)
        {
            return null;
        }

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the JSON file you downloaded from Google",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Google OAuth client (JSON)") { Patterns = ["*.json"] }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        if (_main is null)
        {
            return false;
        }

        var window = new ConfirmWindow { DataContext = new ConfirmViewModel(request), Icon = _main.Icon };
        Prepare(window);
        return await window.ShowDialog<bool>(_main);
    }

    public async Task CopyTextAsync(string text)
    {
        try
        {
            if (_main is not null && TopLevel.GetTopLevel(_main)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Copying to the clipboard failed");
        }
    }

    public void LaunchDemo()
    {
        try
        {
            if (Environment.ProcessPath is { } exe)
            {
                using var process = Process.Start(new ProcessStartInfo(exe, DemoArgument) { UseShellExecute = false });
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "The demo could not be started");
        }
    }

    public void Quit()
    {
        _settingsWindow?.Close();
        _updateWindow?.Close();
        _main?.Close();
        _desktop?.Shutdown();
    }

    // --- Windows ------------------------------------------------------------------------------------------------------

    private void ShowMain()
    {
        if (_main is null)
        {
            return;
        }

        _main.Show();
        if (_main.WindowState == WindowState.Minimized)
        {
            _main.WindowState = WindowState.Normal;
        }

        _main.Activate();
    }

    private void BringToFront(Window window)
    {
        Prepare(window);
        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    /// <summary>Gives a window the current density and motion classes the theme styles depend on.</summary>
    private void Prepare(Window window)
    {
        var appearance = settings.App.Appearance;
        window.Classes.Set("reduce-motion", !ResolveMotion(appearance.Motion));
        window.Classes.Set("compact", appearance.Density == Density.Compact);
    }

    private void ApplyAppearance()
    {
        if (_application is null)
        {
            return;
        }

        var appearance = settings.App.Appearance;
        AccentPalette.Apply(_application, appearance.AccentColor);
        _application.RequestedThemeVariant = appearance.Theme switch
        {
            ThemePreference.Light => ThemeVariant.Light,
            ThemePreference.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        foreach (var window in new Window?[] { _main, _settingsWindow, _updateWindow })
        {
            if (window is not null)
            {
                Prepare(window);
            }
        }
    }

    public static bool ResolveMotion(MotionPreference preference) => preference switch
    {
        MotionPreference.On => true,
        MotionPreference.Off => false,
        _ => SystemVisuals.AnimationsEnabled,
    };

    private void RestorePlacement(Window window)
    {
        var saved = settings.App.Window;
        if (saved is { Width: { } width, Height: { } height })
        {
            window.Width = Math.Max(window.MinWidth, width);
            window.Height = Math.Max(window.MinHeight, height);
        }

        if (saved is { X: { } x, Y: { } y })
        {
            var position = new PixelPoint((int)x, (int)y);
            // Only where a screen still is: a window saved on a monitor that is gone must not open out of reach.
            if (window.Screens.All.Any(screen => screen.WorkingArea.Contains(position)))
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Position = position;
            }
        }

        if (saved.Maximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }

    private void OnMainClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_main is null)
        {
            return;
        }

        var maximized = _main.WindowState == WindowState.Maximized;
        settings.Update(s => s with
        {
            Window = maximized
                ? s.Window with { Maximized = true }
                : s.Window with
                {
                    Maximized = false,
                    X = _main.Position.X,
                    Y = _main.Position.Y,
                    Width = _main.Width,
                    Height = _main.Height,
                },
        });
    }

    private void Shutdown()
    {
        // Running syncs stop cleanly and commit what they already downloaded. A few seconds is plenty: a batch is 50 messages.
        sync.StopAllAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
        settings.Flush();
    }

    private async Task SeedDemoAsync()
    {
        try
        {
            if (!archive.IsOpen || accounts.List().Count > 0 || services.GetService<DemoMailboxFactory>() is not { } factory)
            {
                return;
            }

            // Building made-up mailboxes takes a moment: keep it off the UI thread.
            await Task.Run(() =>
            {
                factory.AddAccount("ada@example.com", 1500);
                factory.AddAccount("work@example.com", 600);
            });
            foreach (var email in new[] { "ada@example.com", "work@example.com" })
            {
                await accounts.ConnectAsync(email, null, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The demo accounts could not be created");
        }
    }

    public void Dispose()
    {
        _clock?.Stop();
        _mainViewModel?.Dispose();
    }
}
