using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GmailWatcher.Desktop.Infrastructure;
using GmailWatcher.Desktop.Shell;
using GmailWatcher.Desktop.ViewModels;
using GmailWatcher.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace GmailWatcher.Desktop;

public partial class App : Application
{
    private readonly IServiceProvider? _services;

    // Used by the designer/previewer and headless tests, which have no composition root.
    public App()
    {
    }

    internal App(IServiceProvider services)
    {
        _services = services;
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (_services is not null && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var configuration = _services.GetRequiredService<ConfigurationLoadResult>();
            if (configuration.IsValid)
            {
                _services.GetRequiredService<AppShell>().Start(desktop, this);
            }
            else
            {
                desktop.MainWindow = new ConfigurationErrorWindow { DataContext = _services.GetRequiredService<ConfigurationErrorViewModel>() };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
