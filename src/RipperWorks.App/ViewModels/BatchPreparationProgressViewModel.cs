using RipperWorks.App.Services;
using RipperWorks.Organizer;

namespace RipperWorks.App.ViewModels;

internal sealed class BatchPreparationProgressViewModel
{
    private readonly LocalizationService _localization;

    public BatchPreparationProgressViewModel(
        BatchOperationKind kind,
        LocalizationService localization)
    {
        Kind = kind;
        _localization = localization;
    }

    public BatchOperationKind Kind { get; }
    public string Title => _localization.Get(Kind == BatchOperationKind.Install
        ? "BatchInstallPreparationTitle"
        : "BatchRemovalPreparationTitle");
    public string StatusText => Title;
}
