using System.Windows;
using RipperWorks.App.Services;

namespace RipperWorks.App;

public sealed record NexusDependencyWarningDisplayItem(
    string TargetName,
    string AvailabilityText,
    string? SourceContext,
    string? ActionText,
    bool CanAssist,
    NexusDependencyWarningItem Item);

public partial class NexusDependencyWarningWindow : Window
{
    public NexusDependencyWarningResult Result { get; private set; } =
        NexusDependencyWarningResult.Cancel();

    public NexusDependencyWarningWindow(
        IReadOnlyList<NexusDependencyWarningItem> items,
        bool isBatch,
        LocalizationService localization)
    {
        InitializeComponent();

        var displayItems = items.Select(item => new NexusDependencyWarningDisplayItem(
            TargetName: item.TargetName,
            AvailabilityText: item.AvailabilityText,
            SourceContext: isBatch && !string.IsNullOrWhiteSpace(item.SourceModDisplayName)
                ? string.Format(localization.Get("NexusDependencyWarningRequiredBy"), item.SourceModDisplayName)
                : null,
            ActionText: item.ActionText,
            CanAssist: item.CanAssist,
            Item: item)).ToArray();

        DataContext = new
        {
            Title = localization.Get("NexusInstallWarningTitle"),
            Message = localization.Get(isBatch
                ? "NexusInstallWarningBatchMessage"
                : "NexusInstallWarningMessage"),
            Items = displayItems,
            CancelLabel = localization.Get("Cancel"),
            ContinueLabel = localization.Get("NexusInstallWarningContinue")
        };
    }

    private void AssistButton_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is FrameworkElement element &&
            element.DataContext is NexusDependencyWarningDisplayItem displayItem)
        {
            var item = displayItem.Item;
            if (item.Availability == NexusRequirementLocalAvailability.InLibrary)
            {
                Result = NexusDependencyWarningResult.OpenLibrary(
                    item.TargetLibraryModId,
                    item.TargetNexusIdentity);
                DialogResult = true;
            }
            else if (item.Availability == NexusRequirementLocalAvailability.InDownloads &&
                     item.TargetNexusIdentity is { } identity)
            {
                Result = NexusDependencyWarningResult.OpenDownloads(identity);
                DialogResult = true;
            }
            else if ((item.Availability == NexusRequirementLocalAvailability.Missing ||
                      item.Availability == NexusRequirementLocalAvailability.External) &&
                     item.TargetUrl is { } url)
            {
                Result = NexusDependencyWarningResult.OpenUrl(url);
                DialogResult = true;
            }
        }
    }

    private void CancelButton_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        Result = NexusDependencyWarningResult.Cancel();
        DialogResult = false;
    }

    private void ContinueButton_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        Result = NexusDependencyWarningResult.Continue();
        DialogResult = true;
    }
}
