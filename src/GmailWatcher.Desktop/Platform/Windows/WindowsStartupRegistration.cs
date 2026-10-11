using System.Runtime.Versioning;
using Microsoft.Win32;

namespace GmailWatcher.Desktop.Platform.Windows;

/// <summary>
/// The per-user "Run" entry (HKCU, no administrator rights, visible and removable in Settings, Apps, Startup). It starts
/// the installed GmailWatcher.exe with --background. Registered only for the real program: running from source would
/// otherwise register dotnet.exe.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsStartupRegistration(
    string subKey = WindowsStartupRegistration.RunKey,
    string valueName = WindowsStartupRegistration.DefaultValueName,
    Func<string?>? executablePath = null) : IStartupRegistration
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultValueName = "GmailWatcher";
    public const string ExecutableName = "GmailWatcher.exe";

    private readonly Func<string?> _executable = executablePath ?? (() => Environment.ProcessPath);

    public bool IsAvailable => Executable() is not null;

    public string? WhyUnavailable => IsAvailable
        ? null
        : $"Only the installed {ExecutableName} can start with Windows (this copy is running from source or under another name).";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(subKey);
            return key?.GetValue(valueName) is string;
        }
    }

    public bool Apply(bool enabled)
    {
        if (enabled)
        {
            if (Executable() is not { } exe)
            {
                return false;
            }

            using var key = Registry.CurrentUser.CreateSubKey(subKey);
            key.SetValue(valueName, StartupCommand.For(exe), RegistryValueKind.String);
            return true;
        }

        using var existing = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
        existing?.DeleteValue(valueName, throwOnMissingValue: false);
        return true;
    }

    private string? Executable() =>
        _executable() is { Length: > 0 } path && string.Equals(Path.GetFileName(path), ExecutableName, StringComparison.OrdinalIgnoreCase) ? path : null;
}
