using Avalonia;
using GmailWatcher.Desktop.Infrastructure;
using GmailWatcher.Desktop.Infrastructure.Logging;
using GmailWatcher.Desktop.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GmailWatcher.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The staged copy of an update replaces the installed folder and starts it; nothing else runs.
        if (Updates.UpdateApplier.TryRun(args, out var applied))
        {
            return applied;
        }

        var demo = args.Contains(AppShell.DemoArgument, StringComparer.OrdinalIgnoreCase);
        var paths = AppPaths.Resolve(demo);

        // One instance per user and data folder: a second launch shows the running window and exits. The demo has its own
        // data folder, so it can run next to the real app.
        using var instance = new Platform.SingleInstance(paths.DataDirectory);
        if (!instance.IsFirst)
        {
            // Started by Windows at sign-in while it already runs: nothing to show, so do not pop the window up.
            if (args.Contains(Presentation.WindowLifetime.BackgroundArgument, StringComparer.OrdinalIgnoreCase))
            {
                return 0;
            }

            return instance.SignalFirst(TimeSpan.FromSeconds(3)) ? 0 : 1;
        }

        using var loggerFactory = CreateLoggerFactory(paths);
        var logger = loggerFactory.CreateLogger(typeof(Program));

        var configuration = ConfigurationLoader.Load(paths);
        logger.LogInformation("Gmail Watcher {Version} starting on {OS}; data directory {DataDirectory}{Demo}",
            AppInfo.Display, Environment.OSVersion, paths.DataDirectory, demo ? " (demo)" : "");
        foreach (var error in configuration.Errors)
        {
            logger.LogError("Configuration error: {Error}", error);
        }

        var install = Updates.InstallInfo.Detect(args);
        if (install.UpdatedFrom is { } from)
        {
            logger.LogInformation("Updated from {From} to {Version}", from, install.Version);
        }

        if (install.UpdateFailed is { } failure)
        {
            logger.LogWarning("Started again after a failed update: {Failure}", failure);
        }

        using var services = CompositionRoot.Build(paths, configuration, loggerFactory, demo, instance, install);
        try
        {
            return BuildAvaloniaApp(() => new App(services)).StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Unhandled exception; shutting down");
            throw;
        }
    }

    // Entry point used by the Avalonia designer/previewer.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(() => new App());

    private static AppBuilder BuildAvaloniaApp(Func<App> createApp) =>
        AppBuilder.Configure(createApp)
            .UsePlatformDetect()
            .LogToTrace();

    private static ILoggerFactory CreateLoggerFactory(AppPaths paths) =>
        LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddDebug();
            try
            {
                builder.AddProvider(new FileLoggerProvider(paths.LogDirectory));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unwritable data directory: continue with debug logging only.
            }
        });
}
