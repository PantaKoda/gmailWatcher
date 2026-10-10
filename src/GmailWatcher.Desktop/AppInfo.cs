using System.Reflection;

namespace GmailWatcher.Desktop;

internal static class AppInfo
{
    // The SDK appends "+<commit>" to the informational version when the build runs in a git checkout.
    private static readonly string Informational =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static string Version { get; } = Informational.Split('+')[0];

    /// <summary>Short commit the build came from, or null when it was built outside git.</summary>
    public static string? Commit { get; } = Informational.Split('+') is [_, var sha, ..] && sha.Length > 0 ? sha[..Math.Min(7, sha.Length)] : null;

    /// <summary>"0.1.0 (9cdd779)" for About and diagnostics.</summary>
    public static string Display { get; } = Commit is null ? Version : $"{Version} ({Commit})";
}
