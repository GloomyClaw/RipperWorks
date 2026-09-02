using RipperWorks.App.Services;

namespace RipperWorks.App.ViewModels;

internal sealed class RemovalPreparationProgressViewModel
{
    private readonly LocalizationService _localization;

    public RemovalPreparationProgressViewModel(
        string modName,
        LocalizationService localization)
    {
        ModName = modName;
        _localization = localization;
    }

    public string Title => _localization.Get("RemovalPreparationTitle");
    public string ModName { get; }
    public string StatusText =>
        _localization.Get("RemovalPreparationStatus");
}
