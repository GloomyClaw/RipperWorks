using RipperWorks.App.Services;

namespace RipperWorks.App.ViewModels;

internal sealed class RemovalProgressViewModel : ObservableObject
{
    private readonly LocalizationService _localization;
    private readonly string? _titleKey;
    private readonly string? _statusKey;
    private string _modName;
    private string? _customStatusText;

    public RemovalProgressViewModel(
        string modName,
        LocalizationService localization,
        string? titleKey = null,
        string? statusKey = null)
    {
        _modName = modName;
        _localization = localization;
        _titleKey = titleKey;
        _statusKey = statusKey;
    }

    public string Title => _localization.Get(_titleKey ?? "RemovalProgressTitle");
    public string ModName
    {
        get => _modName;
        set => SetProperty(ref _modName, value);
    }
    public string StatusText =>
        _customStatusText ?? _localization.Get(_statusKey ?? "RemovalProgressStatus");

    public void ReportProgress(string currentMod, int completed, int total)
    {
        ModName = currentMod;
        _customStatusText = string.Format(
            _localization.Get("BatchFullDeleteProgressStatus"),
            completed,
            total);
        OnPropertyChanged(nameof(StatusText));
    }
}
