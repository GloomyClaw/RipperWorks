using System.Windows;

namespace RipperWorks.App;

public partial class InstallPlanWindow : Window
{
    public InstallPlanWindow()
    {
        InitializeComponent();
    }

    private void CloseButton_OnClick(
        object sender,
        RoutedEventArgs e) =>
        Close();

    private void InstallButton_OnClick(
        object sender,
        RoutedEventArgs e) =>
        DialogResult = true;
}
