namespace GmailWatcher.Desktop.Infrastructure;

/// <summary>
/// Per-user locations for the app's own files: settings, configuration overrides, logs, update staging. The mail archive
/// is elsewhere (its folder is chosen on first run, see <c>Archive:Directory</c>).
/// </summary>
public sealed record AppPaths(string DataDirectory, string DefaultsFile, string UserConfigFile, string LogDirectory)
{
    public const string DataDirectoryVariable = "GMAILWATCHER_DATA_DIR";

    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>The normal data folder, used when GMAILWATCHER_DATA_DIR is not set.</summary>
    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GmailWatcher");

    /// <summary>The archive folder suggested on first run: visible, outside AppData, and not inside OneDrive's synced Documents.</summary>
    public static string SuggestedArchiveDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "GmailWatcher Archive");

    /// <summary>
    /// Uses %LOCALAPPDATA%\GmailWatcher on Windows (the platform equivalent elsewhere).
    /// GMAILWATCHER_DATA_DIR overrides it for portable use and isolated testing.
    /// </summary>
    public static AppPaths Resolve(bool demo = false)
    {
        var root = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = demo ? Path.Combine(DefaultDataDirectory, "demo") : DefaultDataDirectory;
        }

        root = Path.GetFullPath(root);
        return new AppPaths(
            DataDirectory: root,
            DefaultsFile: Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            UserConfigFile: Path.Combine(root, "gmailwatcher.config.json"),
            LogDirectory: Path.Combine(root, "logs"));
    }
}
