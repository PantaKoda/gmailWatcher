using GmailWatcher.Archive.Demo;
using GmailWatcher.Core.Accounts;
using GmailWatcher.Core.Mail;
using GmailWatcher.Core.Platform;
using GmailWatcher.Desktop.Infrastructure;
using GmailWatcher.Desktop.Platform;
using GmailWatcher.Desktop.Presentation;
using GmailWatcher.Desktop.Services;
using GmailWatcher.Desktop.Shell;
using GmailWatcher.Desktop.Updates;
using GmailWatcher.Desktop.ViewModels;
using GmailWatcher.Google;
using GmailWatcher.Google.Auth;
using GmailWatcher.Google.Gmail;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GmailWatcher.Desktop;

internal static class CompositionRoot
{
    public static ServiceProvider Build(AppPaths paths, ConfigurationLoadResult configuration, ILoggerFactory loggerFactory, bool demo,
        SingleInstance? instance = null, InstallInfo? install = null)
    {
        var options = configuration.Options;
        var services = new ServiceCollection();
        services.AddSingleton(install ?? InstallInfo.Detect([]));
        if (instance is not null)
        {
            services.AddSingleton(instance);
        }

        services.AddSingleton(paths);
        services.AddSingleton(configuration);
        services.AddSingleton(options);
        services.AddSingleton(loggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new AppMode(demo));

        // Configuration written by the app (archive folder, Google client).
        services.AddSingleton<UserConfigWriter>();
        services.AddSingleton<SettingsService>();

        // The archive. Its folder is chosen on first run; demo mode has its own, inside the demo data folder.
        services.AddSingleton(sp => new ArchiveHost(options, sp.GetRequiredService<UserConfigWriter>(), loggerFactory, TimeProvider.System,
            demo ? Path.Combine(paths.DataDirectory, "archive") : null));

        // Starting with Windows: a per-user entry, only for the real program on Windows (the demo and other systems cannot register).
        services.AddSingleton<Platform.IStartupRegistration>(_ => !demo && OperatingSystem.IsWindows()
            ? new Platform.Windows.WindowsStartupRegistration()
            : new Platform.NoStartupRegistration(demo ? "The demo never registers itself with Windows." : "Starting with Windows is only available on Windows."));

        // Where refresh tokens go: Credential Manager on Windows, otherwise a clearly labeled memory-only store (never plaintext on disk).
        services.AddSingleton<ICredentialStore>(_ => !demo && OperatingSystem.IsWindows()
            ? new Platform.Windows.WindowsCredentialStore()
            : new SessionCredentialStore());

        if (demo)
        {
            var mailboxes = new DemoMailboxFactory(TimeSpan.FromMilliseconds(12));
            services.AddSingleton(mailboxes);
            services.AddSingleton<IMailboxFactory>(mailboxes);
            services.AddSingleton<IAccountConnector>(sp => new DemoAccountConnector(mailboxes, sp.GetRequiredService<ICredentialStore>()));
            options.Updates.Repository = null; // a demo never phones home
        }
        else
        {
            var google = GoogleHttp.CreateClient();
            services.AddSingleton(sp => new GoogleOAuthClient(google, () => options.Google, TimeProvider.System, sp.GetRequiredService<ILogger<GoogleOAuthClient>>()));
            services.AddSingleton(sp => new GoogleSignIn(sp.GetRequiredService<GoogleOAuthClient>(), sp.GetRequiredService<IExternalBrowser>(),
                TimeProvider.System, sp.GetRequiredService<ILogger<GoogleSignIn>>()));
            services.AddSingleton(sp => new GmailMailboxFactory(google, () => options.Google, options.Sync, sp.GetRequiredService<ICredentialStore>(),
                TimeProvider.System, loggerFactory));
            services.AddSingleton<IMailboxFactory>(sp => sp.GetRequiredService<GmailMailboxFactory>());
            services.AddSingleton<IAccountConnector, GoogleAccountConnector>();
        }

        services.AddSingleton<GoogleClientSetup>();
        services.AddSingleton<AccountService>();
        services.AddSingleton<SyncService>();
        services.AddSingleton<AutoSyncScheduler>();
        services.AddSingleton<DiagnosticsService>();

        // Updates from the configured repository's public GitHub releases (read without signing in).
        services.AddSingleton<IReleaseSource>(sp => new ReleaseClient(ReleaseClient.CreateClient(), options.Updates));
        services.AddSingleton<IProcessLauncher, ProcessLauncher>();
        services.AddSingleton<UpdateService>();

        // Platform adapters and the shell.
        services.AddSingleton<IExternalBrowser, BrowserLauncher>();
        services.AddSingleton<IFileLauncher, FileLauncher>();
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<AppShell>();

        services.AddTransient<ConfigurationErrorViewModel>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
