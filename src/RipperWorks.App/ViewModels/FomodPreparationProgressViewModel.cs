using RipperWorks.App.Services;

namespace RipperWorks.App.ViewModels;

public enum FomodPreparationPhase
{
    ValidatingSelection,
    PreparingFiles,
    UpdatingModData,
    PreparingInstallPlan
}

internal sealed class FomodPreparationProgressViewModel
    : ObservableObject
{
    private readonly LocalizationService _localization;
    private FomodPreparationPhase _phase;

    public FomodPreparationProgressViewModel(
        string modName,
        LocalizationService localization)
    {
        ModName = modName;
        _localization = localization;
        _phase = FomodPreparationPhase.ValidatingSelection;
    }

    public string Title => _localization.Get("FomodPreparationTitle");
    public string ModName { get; }
    public FomodPreparationPhase Phase
    {
        get => _phase;
        private set
        {
            if (SetProperty(ref _phase, value))
                OnPropertyChanged(nameof(StatusText));
        }
    }
    public string StatusText => _localization.Get(
        $"FomodPreparation{Phase}");

    public void Update(FomodPreparationPhase phase) => Phase = phase;
}
