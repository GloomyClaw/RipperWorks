using System.Windows;
using RipperWorks.App.Services;

namespace RipperWorks.App;

public partial class DownloaderBatchDeleteWindow : Window
{
    public DownloaderBatchDeleteWindow(
        int count,
        int activeCount,
        LocalizationService localization)
    {
        InitializeComponent();
        DataContext = new
        {
            Title = localization.Get("DownloaderBatchDeleteTitle"),
            Question = localization.Get("DownloaderBatchDeleteQuestion"),
            CountText = string.Format(
                localization.Get("DownloaderBatchDeleteCount"),
                count),
            ActiveText = string.Format(
                localization.Get("DownloaderBatchDeleteActive"),
                activeCount),
            ActiveVisibility = activeCount > 0
                ? Visibility.Visible
                : Visibility.Collapsed,
            ArchiveNotice =
                localization.Get("DownloaderBatchDeleteArchiveNotice"),
            CancelLabel = localization.Get("Cancel"),
            DeleteLabel = string.Format(
                localization.Get("DownloaderBatchDeleteButton"),
                count)
        };
    }

    private void DeleteButton_OnClick(
        object sender,
        RoutedEventArgs e) =>
        DialogResult = true;
}
