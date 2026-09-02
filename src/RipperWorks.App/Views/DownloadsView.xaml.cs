using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using RipperWorks.App.ViewModels;
using RipperWorks.Core;

namespace RipperWorks.App.Views;

public partial class DownloadsView : UserControl
{
    public DownloadsView()
    {
        InitializeComponent();
    }

    private async void DownloadsGrid_OnMouseDoubleClick(
        object sender,
        MouseButtonEventArgs e)
    {
        if (FindAncestor<CheckBox>(e.OriginalSource as DependencyObject) is not null ||
            FindAncestor<ScrollBar>(e.OriginalSource as DependencyObject) is not null ||
            FindAncestor<DataGridColumnHeader>(
                e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }
        var row = FindAncestor<DataGridRow>(
            e.OriginalSource as DependencyObject);
        if (row?.Item is not DownloaderEntry entry ||
            DataContext is not DownloadsViewModel viewModel)
        {
            return;
        }
        viewModel.SelectedEntry = entry;
        await viewModel.EditCommand.ExecuteAsync();
        e.Handled = true;
    }

    private void DownloadsGrid_OnPreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        var row = FindAncestor<DataGridRow>(
            e.OriginalSource as DependencyObject);
        if (row?.Item is DownloaderEntry entry &&
            DataContext is DownloadsViewModel viewModel)
        {
            viewModel.SelectedEntry = entry;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? value)
        where T : DependencyObject
    {
        while (value is not null)
        {
            if (value is T match)
                return match;
            value = VisualTreeHelper.GetParent(value);
        }
        return null;
    }
}
