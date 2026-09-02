using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace RipperWorks.App.Services;

public interface IFolderPickerService
{
    string? Pick(string title, string? initialDirectory);
}

public interface IUserDialogService
{
    bool Confirm(string message, string title);
    bool Confirm(
        string message,
        string title,
        string cancelLabel,
        string confirmLabel) =>
        Confirm(message, title);
    void ShowError(string message);
    void ShowInfo(string message) { }
}

public sealed class FolderPickerService : IFolderPickerService
{
    public string? Pick(string title, string? initialDirectory)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory) &&
            Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}

public sealed class UserDialogService(
    LocalizationService localization) : IUserDialogService
{
    public bool Confirm(string message, string title) =>
        MessageBox.Show(
            Application.Current.MainWindow,
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;

    public bool Confirm(
        string message,
        string title,
        string cancelLabel,
        string confirmLabel)
    {
        var dialog = new ChoiceDialogWindow(
            title,
            message,
            cancelLabel,
            middleLabel: null,
            confirmLabel: confirmLabel)
        {
            Owner = Application.Current?.MainWindow
        };
        return dialog.ShowDialog() == true && dialog.Choice == 2;
    }

    public void ShowError(string message)
    {
        var dialog = new ThemedMessageDialog
        {
            Owner = Application.Current?.MainWindow,
            DataContext = new
            {
                Title = localization.Get("Error"),
                Message = message,
                CloseLabel = localization.Get("Close")
            }
        };
        dialog.ShowDialog();
    }

    public void ShowInfo(string message)
    {
        var dialog = new ThemedMessageDialog
        {
            Owner = Application.Current?.MainWindow,
            DataContext = new
            {
                Title = localization.Get("Information"),
                Message = message,
                CloseLabel = localization.Get("Close")
            }
        };
        dialog.ShowDialog();
    }
}
