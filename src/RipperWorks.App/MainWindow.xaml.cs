using System.Windows;
using System.Windows.Controls;
using RipperWorks.App.ViewModels;
using RipperWorks.App.Views;

namespace RipperWorks.App;

public partial class MainWindow : Window
{
    private bool _isNexusInitialized;
    private bool _isNexusInitInProgress;
    private bool _nexusActivationAttemptedForCurrentVisibility;

    internal Func<NexusBrowserView, Task>? NexusBrowserInitializer { get; set; }
    internal Action<NexusBrowserView>? NexusBrowserHomeNavigator { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += MainWindow_OnDataContextChanged;
        NexusBrowser.IsVisibleChanged += NexusBrowser_OnIsVisibleChanged;
        Closed += MainWindow_OnClosed;
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (DataContext is MainWindowViewModel viewModel)
            viewModel.Settings.RefreshLaunchAvailability();
    }

    private void MainWindow_OnDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainWindowViewModel oldViewModel)
            oldViewModel.Settings.SettingsSaved -= Settings_OnSettingsSaved;
        if (e.NewValue is MainWindowViewModel newViewModel)
            newViewModel.Settings.SettingsSaved += Settings_OnSettingsSaved;
        NexusApiKeyBox.Password = string.Empty;
    }

    private async void NexusBrowser_OnIsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || !viewModel.IsNexusVisible)
        {
            _nexusActivationAttemptedForCurrentVisibility = false;
            return;
        }

        if (_nexusActivationAttemptedForCurrentVisibility ||
            _isNexusInitialized ||
            _isNexusInitInProgress)
        {
            return;
        }

        _nexusActivationAttemptedForCurrentVisibility = true;
        _isNexusInitInProgress = true;
        try
        {
            if (NexusBrowserInitializer is not null)
            {
                await NexusBrowserInitializer(NexusBrowser);
            }
            else
            {
                await NexusBrowser.InitializeAsync();
            }

            _isNexusInitialized = true;

            if (NexusBrowser.HasExplicitTarget)
            {
                // Explicit navigation target was requested (e.g. from Library/Downloads) and is handled by NexusBrowserView.
            }
            else if (NexusBrowserHomeNavigator is not null)
            {
                NexusBrowserHomeNavigator(NexusBrowser);
            }
            else if (NexusBrowser.ViewModel?.CurrentUri is null)
            {
                NexusBrowser.NavigateHome();
            }
        }
        catch (Exception ex)
        {
            if (NexusBrowser.ViewModel is { } vm)
            {
                vm.StatusText = ex.Message;
            }
        }
        finally
        {
            _isNexusInitInProgress = false;
        }
    }

    private void NavigationList_OnPreviewMouseLeftButtonDown(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is not MainWindowViewModel)
            return;

        if (e.OriginalSource is not DependencyObject depObj)
            return;

        var container = (depObj as ListBoxItem)
            ?? (ItemsControl.ContainerFromElement(NavigationList, depObj) as ListBoxItem);

        if (container?.DataContext is NavigationItem navItem && navItem.Key == "Nexus")
        {
            // Reselection decision is based strictly on the PRE-CLICK selection state.
            // If the item is already selected before ListBox selection mutation occurs,
            // this is an active reselection. If IsSelected is false, it's normal entry.
            if (container.IsSelected && _isNexusInitialized && !_isNexusInitInProgress)
            {
                if (NexusBrowserHomeNavigator is not null)
                {
                    NexusBrowserHomeNavigator(NexusBrowser);
                }
                else
                {
                    NexusBrowser.NavigateHome();
                }
            }
        }
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        NexusBrowser.IsVisibleChanged -= NexusBrowser_OnIsVisibleChanged;
        Closed -= MainWindow_OnClosed;
        NexusBrowser.Dispose();
    }

    private void NexusApiKeyBox_OnPasswordChanged(
        object sender,
        RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
            viewModel.Settings.NexusApiKey = NexusApiKeyBox.Password;
    }

    private void Settings_OnSettingsSaved(
        object? sender,
        RipperWorks.Core.RipperWorksSettings e) =>
        NexusApiKeyBox.Password = string.Empty;

    private void SettingsCardsGrid_OnSizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= 1040;
        SettingsCardsGrid.ColumnDefinitions[0].Width =
            new GridLength(1, GridUnitType.Star);
        SettingsCardsGrid.ColumnDefinitions[1].Width =
            wide ? new GridLength(24) : new GridLength(0);
        SettingsCardsGrid.ColumnDefinitions[2].Width =
            wide
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(0);
        Grid.SetColumn(NexusCard, wide ? 2 : 0);
        Grid.SetRow(NexusCard, wide ? 0 : 1);
        NexusCard.Margin = wide
            ? new Thickness(0)
            : new Thickness(0, 16, 0, 0);
    }
}
