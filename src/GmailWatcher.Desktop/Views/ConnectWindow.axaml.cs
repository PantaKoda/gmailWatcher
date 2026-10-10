using Avalonia.Controls;
using GmailWatcher.Desktop.ViewModels;

namespace GmailWatcher.Desktop.Views;

public partial class ConnectWindow : Window
{
    public ConnectWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (DataContext is ConnectViewModel viewModel)
            {
                viewModel.CloseRequested += (_, _) => Close();
                viewModel.Start();
            }
        };
        Closing += (_, _) =>
        {
            // Closing the window with the X button is a cancel: the sign-in must not keep waiting for a browser nobody watches.
            if (DataContext is ConnectViewModel viewModel)
            {
                viewModel.CancelCommand.Execute(null);
            }
        };
    }
}
