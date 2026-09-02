using System.Windows;
using RipperWorks.App.ViewModels;

namespace RipperWorks.App.Views;

public partial class FomodInstallerWindow : Window
{
    public FomodInstallerWindow(FomodInstallerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseAction = () =>
        {
            DialogResult = viewModel.DialogResult;
            Close();
        };
    }
}
