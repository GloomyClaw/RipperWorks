using System.Windows;
using RipperWorks.App.Services;

namespace RipperWorks.App;

public partial class DownloaderReplaceConfirmationWindow : Window
{
    public DownloaderReplaceConfirmationWindow(
        int currentCount,
        int afterCount,
        LocalizationService localization)
    {
        InitializeComponent();
        DataContext = new
        {
            Title = localization.Get("DownloaderReplaceTitle"),
            Message = string.Join(
                Environment.NewLine,
                localization.Get("DownloaderReplaceWarning"),
                string.Format(
                    localization.Get("DownloaderReplaceCurrent"),
                    currentCount),
                string.Format(
                    localization.Get("DownloaderReplaceAfter"),
                    afterCount),
                string.Empty,
                localization.Get("DownloaderReplaceContinue")),
            CancelLabel = localization.Get("Cancel"),
            ReplaceLabel = localization.Get("DownloaderReplaceAction")
        };
    }

    private void ReplaceButton_OnClick(
        object sender,
        RoutedEventArgs e) =>
        DialogResult = true;
}
