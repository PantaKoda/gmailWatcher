using Avalonia.Controls;
using Avalonia.Interactivity;
using GmailWatcher.Desktop.ViewModels;

namespace GmailWatcher.Desktop.Views;

public partial class GoogleSetupWindow : Window
{
    public GoogleSetupWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is GoogleSetupViewModel viewModel)
            {
                viewModel.Saved += (_, _) => Close(true);
            }
        };
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
