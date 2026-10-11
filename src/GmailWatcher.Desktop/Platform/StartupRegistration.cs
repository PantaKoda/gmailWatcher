using GmailWatcher.Desktop.Presentation;

namespace GmailWatcher.Desktop.Platform;

/// <summary>Whether Windows starts Gmail Watcher when the user signs in. Only ever changed because the user switched it on or off.</summary>
public interface IStartupRegistration
{
    /// <summary>False when this copy can't register itself (running from source, the demo, not Windows); <see cref="WhyUnavailable"/> says why.</summary>
    bool IsAvailable { get; }

    string? WhyUnavailable { get; }

    /// <summary>True when Windows will start this copy at sign-in.</summary>
    bool IsEnabled { get; }

    /// <summary>Registers (or removes) the start-up entry. Registering needs <see cref="IsAvailable"/> (returns false otherwise); removing always works.</summary>
    bool Apply(bool enabled);
}

public static class StartupCommand
{
    /// <summary>The command Windows runs at sign-in: the program, started hidden in the tray.</summary>
    public static string For(string executablePath) => $"\"{executablePath}\" {WindowLifetime.BackgroundArgument}";
}

/// <summary>Used where starting with Windows makes no sense: the demo, other systems.</summary>
public sealed class NoStartupRegistration(string reason) : IStartupRegistration
{
    public bool IsAvailable => false;

    public string? WhyUnavailable => reason;

    public bool IsEnabled => false;

    public bool Apply(bool enabled) => false;
}
