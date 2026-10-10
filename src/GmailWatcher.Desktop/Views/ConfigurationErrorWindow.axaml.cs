using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using GmailWatcher.Desktop.ViewModels;

namespace GmailWatcher.Desktop.Views;

public partial class ConfigurationErrorWindow : Window
{
    public ConfigurationErrorWindow()
    {
        InitializeComponent();
    }

    private async void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConfigurationErrorViewModel vm)
        {
            var directory = Directory.CreateDirectory(vm.UserConfigDirectory);
            await Launcher.LaunchDirectoryInfoAsync(directory);
        }
    }

    private void OnQuitClick(object? sender, RoutedEventArgs e) => Close();
}
