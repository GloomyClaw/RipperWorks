using System.Windows;

namespace RipperWorks.App;

public partial class GameMaintenanceCleanupWindow : Window
{
    public GameMaintenanceCleanupWindow()
    {
        InitializeComponent();
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void CleanButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
