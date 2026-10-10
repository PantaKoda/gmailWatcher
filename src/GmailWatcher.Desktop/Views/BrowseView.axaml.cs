using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace GmailWatcher.Desktop.Views;

public partial class BrowseView : UserControl
{
    public BrowseView()
    {
        InitializeComponent();
    }

    /// <summary>The text the right-click menu was opened on: the menu item sits in a popup that hangs off that text (PlacementTarget is not filled in).</summary>
    private static SelectableTextBlock? TextOf(object? sender) =>
        (sender as Control)?.FindLogicalAncestorOfType<SelectableTextBlock>();

    /// <summary>Copies the selected text, or all of it when nothing is selected (a click on Copy means "copy this").</summary>
    private void OnCopySelection(object? sender, RoutedEventArgs e)
    {
        if (TextOf(sender) is { } block)
        {
            if (string.IsNullOrEmpty(block.SelectedText))
            {
                block.SelectAll();
            }

            block.Copy();
        }
    }

    private void OnSelectAll(object? sender, RoutedEventArgs e) => TextOf(sender)?.SelectAll();
}
