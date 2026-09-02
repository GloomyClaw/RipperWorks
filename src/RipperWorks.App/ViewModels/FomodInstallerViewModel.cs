using System.Collections.ObjectModel;
using RipperWorks.Organizer;
using RipperWorks.App.Services;

namespace RipperWorks.App.ViewModels;

public sealed class FomodPluginOptionViewModel : ObservableObject
{
    private bool _isSelected;

    public FomodPlugin Plugin { get; }
    public string Name => Plugin.Name;
    public string Description => Plugin.Description;
    public string TypeDisplay => Plugin.Type.ToString();

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public FomodPluginOptionViewModel(FomodPlugin plugin, bool initialSelected)
    {
        Plugin = plugin;
        _isSelected = initialSelected;
    }
}

public sealed class FomodGroupViewModel : ObservableObject
{
    private readonly LocalizationService? _localization;

    public FomodGroup Group { get; }
    public string Name => Group.Name;

    public string GroupTypeDisplay => Group.Type switch
    {
        FomodGroupType.SelectExactlyOne => _localization?.Get("FomodSelectExactlyOne") ?? "Выберите ровно 1 вариант",
        FomodGroupType.SelectAtLeastOne => _localization?.Get("FomodSelectAtLeastOne") ?? "Выберите как минимум 1 вариант",
        FomodGroupType.SelectAtMostOne => _localization?.Get("FomodSelectAtMostOne") ?? "Выберите не более 1 варианта",
        FomodGroupType.SelectAll => _localization?.Get("FomodSelectAll") ?? "Выберите все варианты",
        _ => _localization?.Get("FomodSelectAny") ?? "Выберите любые варианты"
    };

    public bool IsSingleSelection => Group.Type == FomodGroupType.SelectExactlyOne || Group.Type == FomodGroupType.SelectAtMostOne;

    public ObservableCollection<FomodPluginOptionViewModel> Options { get; } = new();

    public FomodGroupViewModel(FomodGroup group, Action onSelectionChanged, LocalizationService? localization = null)
    {
        Group = group;
        _localization = localization;
        foreach (var plugin in group.Plugins)
        {
            var isDefault = plugin.Type == FomodPluginType.Required || plugin.Type == FomodPluginType.Recommended;
            var option = new FomodPluginOptionViewModel(plugin, isDefault);
            option.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(FomodPluginOptionViewModel.IsSelected))
                {
                    if (IsSingleSelection && option.IsSelected)
                    {
                        foreach (var other in Options)
                        {
                            if (other != option && other.IsSelected)
                            {
                                other.IsSelected = false;
                            }
                        }
                    }
                    onSelectionChanged();
                }
            };
            Options.Add(option);
        }
    }

    public string? ValidateGroupSelection()
    {
        var selectedCount = 0;
        foreach (var option in Options)
        {
            if (option.Plugin.Type == FomodPluginType.Required && !option.IsSelected)
            {
                return _localization?.Format("FomodRequiredOptionMissingFormat", option.Name) ?? $"Required option '{option.Name}' must be selected.";
            }
            if (option.Plugin.Type == FomodPluginType.NotUsable && option.IsSelected)
            {
                return _localization?.Format("FomodOptionNotUsableFormat", option.Name) ?? $"Option '{option.Name}' is not usable.";
            }
            if (option.IsSelected)
            {
                selectedCount++;
            }
        }

        return Group.Type switch
        {
            FomodGroupType.SelectExactlyOne when selectedCount != 1 =>
                _localization?.Format("FomodGroupSelectExactlyOneFormat", Name) ?? $"Group '{Name}': select exactly one option.",
            FomodGroupType.SelectAtLeastOne when selectedCount < 1 =>
                _localization?.Format("FomodGroupSelectAtLeastOneFormat", Name) ?? $"Group '{Name}': select at least one option.",
            FomodGroupType.SelectAtMostOne when selectedCount > 1 =>
                _localization?.Format("FomodGroupSelectAtMostOneFormat", Name) ?? $"Group '{Name}': select at most one option.",
            FomodGroupType.SelectAll when selectedCount != Options.Count =>
                _localization?.Format("FomodGroupSelectAllFormat", Name) ?? $"Group '{Name}': select all options.",
            _ => null
        };
    }
}

public sealed class FomodStepViewModel : ObservableObject
{
    public FomodStep Step { get; }
    public string Name => Step.Name;
    public ObservableCollection<FomodGroupViewModel> Groups { get; } = new();

    public FomodStepViewModel(FomodStep step, Action onSelectionChanged, LocalizationService? localization = null)
    {
        Step = step;
        foreach (var group in step.Groups)
        {
            Groups.Add(new FomodGroupViewModel(group, onSelectionChanged, localization));
        }
    }

    public string? ValidateStepSelection()
    {
        foreach (var group in Groups)
        {
            var err = group.ValidateGroupSelection();
            if (err is not null) return err;
        }
        return null;
    }
}

public sealed class FomodInstallerViewModel : ObservableObject
{
    private readonly FomodDefinition _fomod;
    private readonly LocalizationService? _localization;
    private int _currentStepIndex;
    private string? _validationError;
    private bool _canContinue;
    private bool _canGoNext;
    private bool _isLastStep;

    public string ModuleName => string.IsNullOrWhiteSpace(_fomod.ModuleName) ? (_localization?.Get("FomodInstaller") ?? "FOMOD Installer") : _fomod.ModuleName;
    public string WindowTitle => _localization?.Format("FomodWindowTitleFormat", ModuleName) ?? $"FOMOD Installer — {ModuleName}";
    public string BackButtonText => _localization?.Get("FomodBack") ?? "Назад";
    public string NextButtonText => _localization?.Get("FomodNext") ?? "Далее";
    public string InstallButtonText => _localization?.Get("FomodInstall") ?? "Установить";
    public string StepLabel => $"{(_localization?.Get("FomodStep") ?? "Шаг")}: {CurrentStep?.Name}";

    public ObservableCollection<FomodStepViewModel> Steps { get; } = new();

    public int CurrentStepIndex
    {
        get => _currentStepIndex;
        set
        {
            if (SetProperty(ref _currentStepIndex, value))
            {
                OnPropertyChanged(nameof(CurrentStep));
                OnPropertyChanged(nameof(StepLabel));
            }
        }
    }

    public FomodStepViewModel? CurrentStep =>
        CurrentStepIndex >= 0 && CurrentStepIndex < Steps.Count ? Steps[CurrentStepIndex] : null;

    public string? ValidationError
    {
        get => _validationError;
        private set => SetProperty(ref _validationError, value);
    }

    public bool CanContinue
    {
        get => _canContinue;
        private set => SetProperty(ref _canContinue, value);
    }

    public bool CanGoNext
    {
        get => _canGoNext;
        private set => SetProperty(ref _canGoNext, value);
    }

    public bool IsLastStep
    {
        get => _isLastStep;
        private set => SetProperty(ref _isLastStep, value);
    }

    public RelayCommand NextStepCommand { get; }
    public RelayCommand PreviousStepCommand { get; }
    public RelayCommand ContinueCommand { get; }

    public bool? DialogResult { get; private set; }
    public Action? CloseAction { get; set; }

    public FomodInstallerViewModel(FomodDefinition fomod, LocalizationService? localization = null)
    {
        _fomod = fomod;
        _localization = localization;
        foreach (var step in fomod.Steps)
        {
            Steps.Add(new FomodStepViewModel(step, ValidateSelections, localization));
        }

        NextStepCommand = new RelayCommand(GoToNextStep, () => CurrentStepIndex < Steps.Count - 1 && CanGoNext);
        PreviousStepCommand = new RelayCommand(GoToPreviousStep, () => CurrentStepIndex > 0);
        ContinueCommand = new RelayCommand(ExecuteContinue, () => CanContinue);

        ValidateSelections();
    }

    private void ValidateSelections()
    {
        IsLastStep = Steps.Count == 0 || CurrentStepIndex == Steps.Count - 1;

        var currentStepErr = CurrentStep?.ValidateStepSelection();
        CanGoNext = currentStepErr is null;

        var selectedIds = GetSelectedPluginIds();
        var fullErr = FomodPlanner.ValidateSelection(_fomod, selectedIds);
        var localizedFullErr = fullErr is not null ? (_localization?.Get("FomodSelectionInvalid") ?? _localization?.Get("FomodPreparationFailed") ?? "Selection is invalid") : null;
        ValidationError = currentStepErr ?? (IsLastStep ? localizedFullErr : null);
        CanContinue = fullErr is null;

        NextStepCommand.NotifyCanExecuteChanged();
        PreviousStepCommand.NotifyCanExecuteChanged();
        ContinueCommand.NotifyCanExecuteChanged();
    }

    public HashSet<string> GetSelectedPluginIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in Steps)
        {
            foreach (var group in step.Groups)
            {
                foreach (var option in group.Options)
                {
                    if (option.IsSelected)
                    {
                        ids.Add(option.Plugin.Id);
                    }
                }
            }
        }
        return ids;
    }

    private void GoToNextStep()
    {
        if (CurrentStepIndex < Steps.Count - 1)
        {
            CurrentStepIndex++;
            ValidateSelections();
        }
    }

    private void GoToPreviousStep()
    {
        if (CurrentStepIndex > 0)
        {
            CurrentStepIndex--;
            ValidateSelections();
        }
    }

    private void ExecuteContinue()
    {
        var selectedIds = GetSelectedPluginIds();
        var err = FomodPlanner.ValidateSelection(_fomod, selectedIds);
        if (err is not null)
        {
            ValidationError = _localization?.Get("FomodSelectionInvalid") ?? _localization?.Get("FomodPreparationFailed") ?? "Selection is invalid";
            CanContinue = false;
            return;
        }

        DialogResult = true;
        CloseAction?.Invoke();
    }
}
