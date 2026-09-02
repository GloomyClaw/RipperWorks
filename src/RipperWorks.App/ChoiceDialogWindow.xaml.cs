using System.Windows;

namespace RipperWorks.App;

public partial class ChoiceDialogWindow : Window
{
    public ChoiceDialogWindow(
        string title,
        string message,
        string cancelLabel,
        string? middleLabel,
        string confirmLabel)
    {
        InitializeComponent();
        DataContext = new
        {
            Title = title,
            Message = message,
            CancelLabel = cancelLabel,
            MiddleLabel = middleLabel ?? string.Empty,
            HasMiddle = !string.IsNullOrWhiteSpace(middleLabel),
            ConfirmLabel = confirmLabel
        };
    }

    public int Choice { get; private set; }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        Choice = 0;
        DialogResult = false;
    }

    private void MiddleButton_OnClick(object sender, RoutedEventArgs e)
    {
        Choice = 1;
        DialogResult = true;
    }

    private void ConfirmButton_OnClick(object sender, RoutedEventArgs e)
    {
        Choice = 2;
        DialogResult = true;
    }
}
