using System.Collections.Generic;
using System.Windows;

namespace RipperWorks.App;

public partial class ModifiedFilesConfirmationWindow : Window
{
    public ModifiedFilesConfirmationWindow(
        string title,
        string heading,
        string explanation,
        IReadOnlyList<string> modifiedPaths,
        string cancelLabel,
        string confirmLabel)
    {
        InitializeComponent();
        Title = title;
        DataContext = new
        {
            Title = title,
            Heading = heading,
            Explanation = explanation,
            ModifiedPaths = modifiedPaths,
            CancelLabel = cancelLabel,
            ConfirmLabel = confirmLabel
        };
    }

    private void CancelButton_OnClick(
        object sender,
        RoutedEventArgs e) =>
        DialogResult = false;

    private void ConfirmButton_OnClick(
        object sender,
        RoutedEventArgs e) =>
        DialogResult = true;
}
