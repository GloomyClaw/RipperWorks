using System.Windows;

namespace RipperWorks.App;

public partial class GameMaintenanceRestoreWindow : Window
{
    public GameMaintenanceRestoreWindow()
    {
        InitializeComponent();
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void RestoreButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
