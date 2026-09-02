using System.ComponentModel;
using System.Windows;

namespace RipperWorks.App;

public partial class GroupNameDialog : Window, INotifyPropertyChanged
{
    private string _groupName;

    public GroupNameDialog(
        string title,
        string actionLabel,
        string? initialValue)
    {
        DialogTitle = title;
        ActionLabel = actionLabel;
        CancelLabel = (Application.Current?.MainWindow?.DataContext
            as ViewModels.MainWindowViewModel)?.Library.CancelLabel
            ?? "Cancel";
        _groupName = initialValue ?? string.Empty;
        DataContext = this;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            GroupNameBox.Focus();
            GroupNameBox.SelectAll();
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string DialogTitle { get; }
    public string ActionLabel { get; }
    public string CancelLabel { get; }

    public string GroupName
    {
        get => _groupName;
        set
        {
            if (_groupName == value)
                return;
            _groupName = value;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(GroupName)));
        }
    }

    private void AcceptButton_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(GroupName))
        {
            GroupNameBox.Focus();
            return;
        }
        GroupName = GroupName.Trim();
        DialogResult = true;
    }
}
