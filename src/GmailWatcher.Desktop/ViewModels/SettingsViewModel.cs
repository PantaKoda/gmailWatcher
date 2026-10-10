using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GmailWatcher.Core.Accounts;
using GmailWatcher.Core.Settings;
using DensityKind = GmailWatcher.Core.Settings.Density;
using GmailWatcher.Desktop.Infrastructure;
using GmailWatcher.Desktop.Platform;
using GmailWatcher.Desktop.Presentation;
using GmailWatcher.Desktop.Services;
using GmailWatcher.Desktop.Updates;

namespace GmailWatcher.Desktop.ViewModels;

/// <summary>A value with a name people can read.</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly SettingsService _settings;
    private readonly ArchiveHost _archive;
    private readonly GoogleClientSetup _google;
    private readonly ICredentialStore _credentials;
    private readonly UpdateService _updates;
    private readonly DiagnosticsService _diagnostics;
    private readonly IShell _shell;
    private readonly IFileLauncher _files;
    private readonly AppPaths _paths;
    private bool _loading;

    public SettingsViewModel(SettingsService settings, ArchiveHost archive, GoogleClientSetup google, ICredentialStore credentials, UpdateService updates,
        DiagnosticsService diagnostics, IShell shell, IFileLauncher files, AppPaths paths, AppMode mode)
    {
        _settings = settings;
        _archive = archive;
        _google = google;
        _credentials = credentials;
        _updates = updates;
        _diagnostics = diagnostics;
        _shell = shell;
        _files = files;
        _paths = paths;
        IsDemo = mode.IsDemo;
        Load();
        _settings.ProblemChanged += OnProblemChanged;
        _updates.Changed += OnUpdatesChanged;
    }

    public bool IsDemo { get; }

    public IReadOnlyList<Choice<ThemePreference>> Themes { get; } =
    [
        new(ThemePreference.System, "Follow Windows"),
        new(ThemePreference.Light, "Light"),
        new(ThemePreference.Dark, "Dark"),
    ];

    public IReadOnlyList<AccentPreset> Accents => AccentPalette.Presets;

    public IReadOnlyList<Choice<DensityKind>> Densities { get; } =
    [
        new(DensityKind.Comfortable, "Comfortable"),
        new(DensityKind.Compact, "Compact"),
    ];

    public IReadOnlyList<Choice<MotionPreference>> MotionChoices { get; } =
    [
        new(MotionPreference.System, "Follow Windows"),
        new(MotionPreference.On, "Animate activity"),
        new(MotionPreference.Off, "Reduce motion"),
    ];

    public IReadOnlyList<Choice<AutoSyncInterval>> AutoSyncChoices { get; } =
    [
        new(AutoSyncInterval.Off, "Only when I press Sync"),
        new(AutoSyncInterval.EveryHour, "Every hour"),
        new(AutoSyncInterval.EverySixHours, "Every 6 hours"),
        new(AutoSyncInterval.Daily, "Once a day"),
    ];

    [ObservableProperty]
    public partial Choice<ThemePreference> Theme { get; set; } = null!;

    [ObservableProperty]
    public partial AccentPreset Accent { get; set; } = null!;

    [ObservableProperty]
    public partial Choice<DensityKind> Density { get; set; } = null!;

    [ObservableProperty]
    public partial Choice<MotionPreference> Motion { get; set; } = null!;

    [ObservableProperty]
    public partial Choice<AutoSyncInterval> AutoSync { get; set; } = null!;

    [ObservableProperty]
    public partial bool SyncOnStartup { get; set; }

    public string Version => AppInfo.Display;

    public string DataDirectory => _paths.DataDirectory;

    public string ArchiveDirectory => _archive.Directory ?? "(not chosen yet)";

    public string GoogleStatus => _google.IsConfigured
        ? $"Signing in with the OAuth client {_google.ClientId}."
        : "Not set up yet: Gmail Watcher can't add or reconnect accounts until you create a Google OAuth client.";

    public string CredentialText => _credentials.IsPersistent
        ? $"Google sign-ins (refresh tokens) are kept in {_credentials.Description}, never in files or in the archive."
        : $"Google sign-ins are kept in {_credentials.Description}.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStorageProblem))]
    public partial string? StorageProblem { get; private set; }

    public bool HasStorageProblem => StorageProblem is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionMessage))]
    public partial string? ActionMessage { get; private set; }

    public bool HasActionMessage => ActionMessage is not null;

    [ObservableProperty]
    public partial bool IsCheckingForUpdates { get; private set; }

    public bool CanCheckForUpdates => _updates.IsEnabled;

    public void Dispose()
    {
        _settings.ProblemChanged -= OnProblemChanged;
        _updates.Changed -= OnUpdatesChanged;
    }

    private void Load()
    {
        _loading = true;
        var app = _settings.App;
        Theme = Themes.First(c => c.Value == app.Appearance.Theme);
        Accent = AccentPalette.Find(app.Appearance.AccentColor);
        Density = Densities.First(c => c.Value == app.Appearance.Density);
        Motion = MotionChoices.First(c => c.Value == app.Appearance.Motion);
        AutoSync = AutoSyncChoices.First(c => c.Value == app.Sync.AutoSync);
        SyncOnStartup = app.Sync.SyncOnStartup;
        StorageProblem = _settings.Problem;
        _loading = false;
    }

    partial void OnThemeChanged(Choice<ThemePreference> value) => Save(s => s with { Appearance = s.Appearance with { Theme = value.Value } });

    partial void OnAccentChanged(AccentPreset value) => Save(s => s with { Appearance = s.Appearance with { AccentColor = value.Hex } });

    partial void OnDensityChanged(Choice<DensityKind> value) => Save(s => s with { Appearance = s.Appearance with { Density = value.Value } });

    partial void OnMotionChanged(Choice<MotionPreference> value) => Save(s => s with { Appearance = s.Appearance with { Motion = value.Value } });

    partial void OnAutoSyncChanged(Choice<AutoSyncInterval> value) => Save(s => s with { Sync = s.Sync with { AutoSync = value.Value } });

    partial void OnSyncOnStartupChanged(bool value) => Save(s => s with { Sync = s.Sync with { SyncOnStartup = value } });

    private void Save(Func<AppSettings, AppSettings> change)
    {
        if (!_loading)
        {
            _settings.Update(change);
        }
    }

    [RelayCommand]
    private async Task ChangeGoogleAsync()
    {
        await _shell.OpenGoogleSetupAsync();
        OnPropertyChanged(nameof(GoogleStatus));
    }

    [RelayCommand]
    private void OpenArchiveFolder()
    {
        if (_archive.Directory is { } directory && !_files.OpenFolder(directory))
        {
            ActionMessage = "The archive folder couldn't be opened.";
        }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        Directory.CreateDirectory(_paths.DataDirectory);
        if (!_files.OpenFolder(_paths.DataDirectory))
        {
            ActionMessage = "The data folder couldn't be opened.";
        }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        IsCheckingForUpdates = true;
        ActionMessage = "Checking GitHub for a newer version…";
        try
        {
            var result = await _updates.CheckNowAsync();
            switch (result.Outcome)
            {
                case UpdateCheckOutcome.Available:
                    ActionMessage = $"Version {result.Latest?.Version} is available.";
                    _shell.OpenUpdate();
                    break;
                case UpdateCheckOutcome.UpToDate:
                    ActionMessage = $"You're up to date (version {_updates.Current}).";
                    break;
                case UpdateCheckOutcome.Busy:
                    ActionMessage = "An update is already being checked or installed.";
                    break;
                case UpdateCheckOutcome.Disabled:
                    ActionMessage = "Updates are turned off in this copy's configuration.";
                    break;
                default:
                    ActionMessage = result.Error ?? "Couldn't check for updates.";
                    break;
            }
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    [RelayCommand]
    private void ExportDiagnostics()
    {
        try
        {
            var file = _diagnostics.Export();
            ActionMessage = $"Saved {file}. It contains versions, counts and redacted logs: no mail, no tokens, no addresses.";
            _files.Reveal(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActionMessage = $"The diagnostics couldn't be saved ({ex.Message}).";
        }
    }

    [RelayCommand]
    private void Quit() => _shell.Quit();

    private void OnProblemChanged(object? sender, EventArgs e) => StorageProblem = _settings.Problem;

    private void OnUpdatesChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(CanCheckForUpdates));
}
