using System.Windows;

namespace RipperWorks.App;

public partial class ThemedMessageDialog : Window
{
    public ThemedMessageDialog()
    {
        InitializeComponent();
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) =>
        Close();
}
