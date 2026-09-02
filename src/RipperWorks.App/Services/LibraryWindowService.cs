using System.Windows;
using System.Runtime.ExceptionServices;
using RipperWorks.App.ViewModels;
using RipperWorks.App.Views;
using RipperWorks.Core;
using RipperWorks.Downloader;
using RipperWorks.Organizer;

namespace RipperWorks.App.Services;

public enum DetectedOverlayChoice
{
    Cancel,
    KeepConflict,
    LinkAsAddOn
}

public enum DependentRemovalChoice
{
    Cancel,
    SelectedOnly,
    Cascade
}

public enum BatchRelatedRemovalChoice
{
    Cancel,
    SelectedOnly,
    IncludeRelated
}

public sealed record RelationSelection(
    PackageId PackageId,
    PackageRelationType RelationType);

public sealed record ArchiveSwitchConfirmation(
    bool DeleteOldArchive);

public sealed record BatchChildOverlayDecision(
    PackageId ChildPackageId,
    LibraryModId ChildLibraryModId,
    string ChildDisplayName,
    PackageId ParentPackageId,
    LibraryModId ParentLibraryModId,
    string ParentDisplayName,
    int OverlappingFileCount,
    bool HasNexusRequirementEvidence);

public sealed record BatchModifiedFilesDecision(
    PackageId PackageId,
    string ModDisplayName,
    IReadOnlyList<ModifiedFileConfirmationIdentity> Identities,
    IReadOnlyList<string> ModifiedPaths);

public interface ILibraryWindowService
{
    void ShowModDetails(
        LibraryViewModel library,
        PackageRowViewModel mod);

    void ShowInstallPlan(
        InstallPlan plan,
        LocalizationService localization)
    {
    }

    bool ShowInstallPlan(
        InstallPlan plan,
        LocalizationService localization,
        bool allowInstall)
    {
        ShowInstallPlan(plan, localization);
        return false;
    }

    bool ConfirmInstallation(
        string modName,
        InstallPlan plan,
        LocalizationService localization) => false;

    NexusDependencyWarningResult ConfirmNexusDependencies(
        IReadOnlyList<NexusDependencyWarningItem> warnings,
        bool isBatch,
        LocalizationService localization) => NexusDependencyWarningResult.Continue();

    void NavigateToDownloads(NexusModIdentity nexusIdentity)
    {
    }

    void OpenExternalUrl(Uri url)
    {
    }

    bool ConfirmRemoval(
        string modName,
        ModRemovalPlan plan,
        LocalizationService localization) => false;

    bool ConfirmModifiedFilesRemoval(
        string modName,
        IReadOnlyList<string> modifiedPaths,
        LocalizationService localization) => false;

    bool ConfirmBatchModifiedFilesRemoval(
        IReadOnlyList<BatchModifiedFilesDecision> decisions,
        LocalizationService localization) => false;

    bool ConfirmReinstallWithModifiedFiles(
        string modName,
        IReadOnlyList<string> modifiedPaths,
        LocalizationService localization) => false;

    ArchiveSwitchConfirmation? ConfirmArchiveSwitch(
        string modName,
        string installedVersion,
        string targetVersion,
        ModArchiveSwitchPlan plan,
        LocalizationService localization) => null;

    RelationSelection? ShowAddRelation(
        string currentModName,
        IReadOnlyList<RelationCandidateViewModel> candidates,
        LocalizationService localization) => null;

    bool ConfirmRelationRemoval(
        string fromModName,
        string toModName,
        LocalizationService localization) => false;

    DetectedOverlayChoice ConfirmDetectedOverlay(
        string modName,
        string ownerName,
        int fileCount,
        LocalizationService localization) =>
        DetectedOverlayChoice.Cancel;

    bool ConfirmBatchChildOverlays(
        IReadOnlyList<BatchChildOverlayDecision> decisions,
        LocalizationService localization) => false;

    DependentRemovalChoice ConfirmDependentRemoval(
        string modName,
        IReadOnlyList<PackageRelationView> dependents,
        LocalizationService localization) =>
        DependentRemovalChoice.Cancel;

    BatchRelatedRemovalChoice ConfirmBatchRelatedRemoval(
        IReadOnlyList<BatchRelatedPackageGroup> groups,
        LocalizationService localization) =>
        BatchRelatedRemovalChoice.Cancel;

    BatchOperationResult? ShowBatchInstall(
        BatchInstallPlan plan,
        Func<IProgress<BatchOperationProgress>, CancellationToken,
            Task<BatchOperationResult>> execute,
        LocalizationService localization) => null;

    BatchOperationResult? ShowBatchRemoval(
        BatchRemovalPlan plan,
        Func<IProgress<BatchOperationProgress>, CancellationToken,
            Task<BatchOperationResult>> execute,
        LocalizationService localization) => null;

    Task ShowBatchPreparationAsync(
        BatchOperationKind kind,
        Func<Task> prepare,
        LocalizationService localization) => prepare();

    Task<ModInstallationResult> ShowInstallationProgressAsync(
        string modName,
        Func<IProgress<ModInstallationProgress>, CancellationToken,
            Task<ModInstallationResult>> install,
        LocalizationService localization) =>
        Task.FromResult(new ModInstallationResult
        {
            Status = InstallOperationStatus.Blocked,
            InstallationState = PackageInstallationState.NotInstalled,
            ErrorCode = "InstallationUnavailable"
        });

    string? PromptForGroupName(
        string title,
        string actionLabel,
        string? initialValue = null);

    ISet<string>? ShowFomodInstaller(
        string modName,
        FomodDefinition fomod,
        LocalizationService localization) => null;

    Task ShowFomodPreparationAsync(
        string modName,
        Func<IProgress<FomodPreparationPhase>, Task> prepare,
        LocalizationService localization) =>
        prepare(new Progress<FomodPreparationPhase>(_ => { }));

    Task ShowRemovalPreparationAsync(
        string modName,
        Func<Task> prepare,
        LocalizationService localization) => prepare();

    Task ShowRemovalProgressAsync(
        string modName,
        Func<Task> remove,
        LocalizationService localization) => remove();

    Task ShowFullDeleteProgressAsync(
        string modName,
        Func<Task> delete,
        LocalizationService localization) => delete();

    Task ShowBatchFullDeleteProgressAsync(
        int totalCount,
        string initialModName,
        Func<IProgress<(int Completed, int Total, string ModName)>, Task> delete,
        LocalizationService localization) =>
        delete(new Progress<(int, int, string)>());
}

public sealed class LibraryWindowService(
    Action<Exception>? exceptionLogger = null)
    : ILibraryWindowService
{
    public ISet<string>? ShowFomodInstaller(
        string modName,
        FomodDefinition fomod,
        LocalizationService localization)
    {
        var vm = new FomodInstallerViewModel(fomod, localization);
        var owner = System.Windows.Application.Current?.Windows.OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive) ?? System.Windows.Application.Current?.MainWindow;
        var win = new FomodInstallerWindow(vm)
        {
            Owner = owner
        };
        var res = win.ShowDialog();
        return res == true ? vm.GetSelectedPluginIds() : null;
    }

    public Task ShowFomodPreparationAsync(
        string modName,
        Func<IProgress<FomodPreparationPhase>, Task> prepare,
        LocalizationService localization)
    {
        var window = new FomodPreparationProgressWindow(
            modName,
            prepare,
            localization,
            exceptionLogger);
        var owner = Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(candidate =>
                candidate.IsActive &&
                !ReferenceEquals(candidate, window)) ??
            Application.Current?.MainWindow;
        if (!ReferenceEquals(owner, window))
            window.Owner = owner;
        window.ShowDialog();
        if (window.Failure is not null)
            ExceptionDispatchInfo.Capture(window.Failure).Throw();
        return Task.CompletedTask;
    }

    public Task ShowRemovalPreparationAsync(
        string modName,
        Func<Task> prepare,
        LocalizationService localization)
    {
        var window = new RemovalPreparationProgressWindow(
            modName,
            prepare,
            localization,
            exceptionLogger);
        var owner = Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(candidate =>
                candidate.IsActive &&
                !ReferenceEquals(candidate, window)) ??
            Application.Current?.MainWindow;
        if (!ReferenceEquals(owner, window))
            window.Owner = owner;
        window.ShowDialog();
        if (window.Failure is not null)
            ExceptionDispatchInfo.Capture(window.Failure).Throw();
        return Task.CompletedTask;
    }

    public Task ShowBatchPreparationAsync(
        BatchOperationKind kind,
        Func<Task> prepare,
        LocalizationService localization)
    {
        var window = new BatchPreparationProgressWindow(
            kind,
            prepare,
            localization,
            exceptionLogger)
        {
            Owner = Application.Current?.MainWindow
        };
        window.ShowDialog();
        if (window.Failure is not null)
            ExceptionDispatchInfo.Capture(window.Failure).Throw();
        return Task.CompletedTask;
    }

    public Task ShowRemovalProgressAsync(
        string modName,
        Func<Task> remove,
        LocalizationService localization)
    {
        var window = new RemovalProgressWindow(
            modName,
            remove,
            localization,
            exceptionLogger);
        var owner = Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(candidate =>
                candidate.IsActive &&
                !ReferenceEquals(candidate, window)) ??
            Application.Current?.MainWindow;
        if (!ReferenceEquals(owner, window))
            window.Owner = owner;
        window.ShowDialog();
        if (window.Failure is not null)
            ExceptionDispatchInfo.Capture(window.Failure).Throw();
        return Task.CompletedTask;
    }

    public Task ShowFullDeleteProgressAsync(
        string modName,
        Func<Task> delete,
        LocalizationService localization)
    {
        var window = new RemovalProgressWindow(
            modName,
            delete,
            localization,
            exceptionLogger,
            titleKey: "LibraryModFullDeleteTitle",
            statusKey: "LibraryModFullDeleteStatus");
        var owner = Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(candidate =>
                candidate.IsActive &&
                !ReferenceEquals(candidate, window)) ??
            Application.Current?.MainWindow;
        if (!ReferenceEquals(owner, window))
            window.Owner = owner;
        window.ShowDialog();
        if (window.Failure is not null)
            ExceptionDispatchInfo.Capture(window.Failure).Throw();
        return Task.CompletedTask;
    }

    public Task ShowBatchFullDeleteProgressAsync(
        int totalCount,
        string initialModName,
        Func<IProgress<(int Completed, int Total, string ModName)>, Task> delete,
        LocalizationService localization)
    {
        var vm = new RemovalProgressViewModel(
            initialModName,
            localization,
            titleKey: "BatchFullDeleteTitle",
            statusKey: null);
        vm.ReportProgress(initialModName, 0, totalCount);

        var progress = new Progress<(int Completed, int Total, string ModName)>(p =>
        {
            vm.ReportProgress(p.ModName, p.Completed, p.Total);
        });

        var window = new RemovalProgressWindow(
            vm,
            () => delete(progress),
            exceptionLogger);
        var owner = Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(candidate =>
                candidate.IsActive &&
                !ReferenceEquals(candidate, window)) ??
            Application.Current?.MainWindow;
        if (!ReferenceEquals(owner, window))
            window.Owner = owner;
        window.ShowDialog();
        if (window.Failure is not null)
            ExceptionDispatchInfo.Capture(window.Failure).Throw();
        return Task.CompletedTask;
    }

    public ArchiveSwitchConfirmation? ConfirmArchiveSwitch(
        string modName,
        string installedVersion,
        string targetVersion,
        ModArchiveSwitchPlan plan,
        LocalizationService localization)
    {
        var window = new ArchiveSwitchConfirmationWindow(
            modName,
            installedVersion,
            targetVersion,
            plan,
            localization)
        {
            Owner = Application.Current?.MainWindow
        };
        return window.ShowDialog() == true
            ? new ArchiveSwitchConfirmation(window.DeleteOldArchive)
            : null;
    }

    public void ShowModDetails(
        LibraryViewModel library,
        PackageRowViewModel mod)
    {
        var window = new ModDetailsWindow
        {
            Owner = Application.Current?.MainWindow,
            DataContext = new ModDetailsDialogViewModel(library, mod)
        };
        var libraryModId = mod.LibraryModId.Value;
        window.Loaded += (_, _) =>
            LibrarySelectionDiagnostics.Record(
                "CardOpened",
                libraryModId,
                verifyInvariants: true);
        window.Closing += (_, _) =>
            LibrarySelectionDiagnostics.Record(
                "CardClosing",
                libraryModId,
                verifyInvariants: true);
        window.Closed += (_, _) =>
            LibrarySelectionDiagnostics.Record(
                "CardClosed",
                libraryModId,
                verifyInvariants: true);
        window.ShowDialog();
    }

    public string? PromptForGroupName(
        string title,
        string actionLabel,
        string? initialValue = null)
    {
        var window = new GroupNameDialog(
            title,
            actionLabel,
            initialValue)
        {
            Owner = Application.Current?.MainWindow
        };
        return window.ShowDialog() == true
            ? window.GroupName
            : null;
    }

    public void ShowInstallPlan(
        InstallPlan plan,
        LocalizationService localization)
    {
        var window = new InstallPlanWindow
        {
            Owner = Application.Current?.MainWindow,
            DataContext = new InstallPlanDialogViewModel(
                plan,
                localization)
        };
        window.ShowDialog();
    }

    public bool ShowInstallPlan(
        InstallPlan plan,
        LocalizationService localization,
        bool allowInstall)
    {
        var window = new InstallPlanWindow
        {
            Owner = Application.Current?.MainWindow,
            DataContext = new InstallPlanDialogViewModel(
                plan,
                localization,
                allowInstall)
        };
        return window.ShowDialog() == true;
    }

    public bool ConfirmInstallation(
        string modName,
        InstallPlan plan,
        LocalizationService localization)
    {
        var window = new InstallConfirmationWindow(
            modName,
            plan,
            localization)
        {
            Owner = Application.Current?.MainWindow
        };
        return window.ShowDialog() == true;
    }

    public NexusDependencyWarningResult ConfirmNexusDependencies(
        IReadOnlyList<NexusDependencyWarningItem> warnings,
        bool isBatch,
        LocalizationService localization)
    {
        var window = new NexusDependencyWarningWindow(
            warnings,
            isBatch,
            localization)
        {
            Owner = Application.Current?.MainWindow
        };
        window.ShowDialog();
        return window.Result;
    }

    public void NavigateToDownloads(NexusModIdentity nexusIdentity)
    {
        if (Application.Current?.MainWindow?.DataContext is MainWindowViewModel mainVm)
        {
            mainVm.SelectDownloads();
            mainVm.Downloads.NavigateToNexusMod(nexusIdentity);
        }
    }

    public void OpenExternalUrl(Uri url)
    {
        if (url is not null && url.IsAbsoluteUri &&
            (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url.AbsoluteUri,
                    UseShellExecute = true
                });
            }
            catch
            {
                // Non-destructive platform/browser launch failure; must not crash or resume install.
            }
        }
    }

    public bool ConfirmRemoval(
        string modName,
        ModRemovalPlan plan,
        LocalizationService localization)
    {
        var window = new RemovalConfirmationWindow(
            modName,
            plan,
            localization)
        {
            Owner = Application.Current?.MainWindow
        };
        return window.ShowDialog() == true;
    }

    public bool ConfirmModifiedFilesRemoval(
        string modName,
        IReadOnlyList<string> modifiedPaths,
        LocalizationService localization)
    {
        var window = new ModifiedFilesConfirmationWindow(
            localization.Get("RemovalModifiedFilesTitle"),
            string.Format(
                localization.Get("RemovalModifiedFilesHeading"),
                modName),
            string.Format(
                localization.Get("RemovalModifiedFilesExplanation"),
                modifiedPaths.Count),
            modifiedPaths,
            localization.Get("Cancel"),
            localization.Get("RemovalModifiedFilesConfirm"))
        {
            Owner = Application.Current?.MainWindow
        };
        return window.ShowDialog() == true;
    }

    public bool ConfirmBatchModifiedFilesRemoval(
        IReadOnlyList<BatchModifiedFilesDecision> decisions,
        LocalizationService localization)
    {
        if (decisions.Count == 0)
            return true;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(localization.Get("BatchRemovalModifiedFilesHeading"));

        foreach (var decision in decisions)
        {
            sb.AppendLine();
            sb.AppendLine($"• {decision.ModDisplayName}");
            sb.AppendLine($"  {string.Format(localization.Get("BatchRemovalModifiedFilesCount"), decision.ModifiedPaths.Count)}");
            foreach (var path in decision.ModifiedPaths)
            {
                sb.AppendLine($"    - {path}");
            }
        }

        var window = new ChoiceDialogWindow(
            localization.Get("BatchRemovalModifiedFilesTitle"),
            sb.ToString().TrimEnd(),
            localization.Get("Cancel"),
            null,
            localization.Get("BatchRemovalModifiedFilesConfirm"))
        {
            Owner = Application.Current?.MainWindow
        };
        window.ShowDialog();
        return window.Choice == 2;
    }

    public bool ConfirmReinstallWithModifiedFiles(
        string modName,
        IReadOnlyList<string> modifiedPaths,
        LocalizationService localization)
    {
        var window = new ModifiedFilesConfirmationWindow(
            localization.Get("ArchiveSwitchReinstallModifiedTitle"),
            string.Format(
                localization.Get("ArchiveSwitchReinstallModifiedHeading"),
                modName),
            string.Format(
                localization.Get("ArchiveSwitchReinstallModifiedExplanation"),
                modifiedPaths.Count),
            modifiedPaths,
            localization.Get("Cancel"),
            localization.Get("ArchiveSwitchReinstallModifiedConfirm"))
        {
            Owner = Application.Current?.MainWindow
        };
        return window.ShowDialog() == true;
    }

    public RelationSelection? ShowAddRelation(
        string currentModName,
        IReadOnlyList<RelationCandidateViewModel> candidates,
        LocalizationService localization)
    {
        var window = new AddRelationWindow(
            currentModName,
            candidates,
            localization)
        {
            Owner = Application.Current?.MainWindow
        };
        return window.ShowDialog() == true
            ? window.Selection
            : null;
    }

    public bool ConfirmRelationRemoval(
        string fromModName,
        string toModName,
        LocalizationService localization)
    {
        var window = new ChoiceDialogWindow(
            localization.Get("RelationRemoveTitle"),
            string.Format(
                localization.Get("RelationRemoveQuestion"),
                fromModName,
                toModName),
            localization.Get("Cancel"),
            null,
            localization.Get("RelationRemove"))
        {
            Owner = Application.Current?.MainWindow
        };
        return window.ShowDialog() == true && window.Choice == 2;
    }

    public DetectedOverlayChoice ConfirmDetectedOverlay(
        string modName,
        string ownerName,
        int fileCount,
        LocalizationService localization)
    {
        var window = new ChoiceDialogWindow(
            localization.Get("OverlayRelationTitle"),
            string.Format(
                localization.Get("OverlayRelationQuestion"),
                modName,
                fileCount,
                ownerName),
            localization.Get("Cancel"),
            localization.Get("OverlayKeepConflict"),
            localization.Get("OverlayLinkAndContinue"))
        {
            Owner = Application.Current?.MainWindow
        };
        window.ShowDialog();
        return window.Choice switch
        {
            1 => DetectedOverlayChoice.KeepConflict,
            2 => DetectedOverlayChoice.LinkAsAddOn,
            _ => DetectedOverlayChoice.Cancel
        };
    }

    public bool ConfirmBatchChildOverlays(
        IReadOnlyList<BatchChildOverlayDecision> decisions,
        LocalizationService localization)
    {
        if (decisions.Count == 0)
            return true;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(localization.Get("BatchChildOverlayIntro"));

        foreach (var decision in decisions)
        {
            sb.AppendLine();
            sb.AppendLine($"• {decision.ChildDisplayName} → {decision.ParentDisplayName}");
            sb.AppendLine($"  {string.Format(localization.Get("BatchChildOverlayFilesCount"), decision.OverlappingFileCount)}");
            if (decision.HasNexusRequirementEvidence)
            {
                sb.AppendLine($"  {localization.Get("BatchChildOverlayNexusEvidence")}");
            }
        }

        var window = new ChoiceDialogWindow(
            localization.Get("BatchChildOverlayTitle"),
            sb.ToString().TrimEnd(),
            localization.Get("Cancel"),
            null,
            localization.Get("BatchChildOverlayConfirm"))
        {
            Owner = Application.Current?.MainWindow
        };
        window.ShowDialog();
        return window.Choice == 2;
    }

    public DependentRemovalChoice ConfirmDependentRemoval(
        string modName,
        IReadOnlyList<PackageRelationView> dependents,
        LocalizationService localization)
    {
        var details = string.Join(
            Environment.NewLine,
            dependents.Select(relation =>
                $"• {relation.FromDisplayName} — " +
                localization.Get(
                    relation.Relation.RelationType ==
                    PackageRelationType.AddOnOf
                        ? "RelationTypeAddOnOf"
                        : "RelationTypeRequires")));
        var window = new ChoiceDialogWindow(
            localization.Get("DependentRemovalTitle"),
            string.Format(
                localization.Get("DependentRemovalQuestion"),
                modName,
                details),
            localization.Get("Cancel"),
            localization.Get("DependentRemovalSelectedOnly"),
            localization.Get("DependentRemovalCascade"))
        {
            Owner = Application.Current?.MainWindow
        };
        window.ShowDialog();
        return window.Choice switch
        {
            1 => DependentRemovalChoice.SelectedOnly,
            2 => DependentRemovalChoice.Cascade,
            _ => DependentRemovalChoice.Cancel
        };
    }

    public BatchRelatedRemovalChoice ConfirmBatchRelatedRemoval(
        IReadOnlyList<BatchRelatedPackageGroup> groups,
        LocalizationService localization)
    {
        var details = string.Join(
            Environment.NewLine + Environment.NewLine,
            groups.Select(group =>
                group.Owner.DisplayName + Environment.NewLine +
                string.Join(
                    Environment.NewLine,
                    group.Dependents.Select(item =>
                        $"  • {item.DisplayName}"))));
        var window = new ChoiceDialogWindow(
            localization.Get("BatchRemovalRelationsTitle"),
            localization.Get("BatchRemovalRelationsMessage") +
            Environment.NewLine + Environment.NewLine + details,
            localization.Get("Cancel"),
            localization.Get("BatchRemoveSelectedOnly"),
            localization.Get("BatchRemoveWithRelated"))
        {
            Owner = Application.Current?.MainWindow
        };
        window.ShowDialog();
        return window.Choice switch
        {
            1 => BatchRelatedRemovalChoice.SelectedOnly,
            2 => BatchRelatedRemovalChoice.IncludeRelated,
            _ => BatchRelatedRemovalChoice.Cancel
        };
    }

    public BatchOperationResult? ShowBatchInstall(
        BatchInstallPlan plan,
        Func<IProgress<BatchOperationProgress>, CancellationToken,
            Task<BatchOperationResult>> execute,
        LocalizationService localization)
    {
        var window = new BatchOperationWindow(
            BatchOperationDialogViewModel.ForInstall(plan, localization),
            execute)
        {
            Owner = Application.Current?.MainWindow
        };
        window.ShowDialog();
        return window.Result;
    }

    public BatchOperationResult? ShowBatchRemoval(
        BatchRemovalPlan plan,
        Func<IProgress<BatchOperationProgress>, CancellationToken,
            Task<BatchOperationResult>> execute,
        LocalizationService localization)
    {
        var window = new BatchOperationWindow(
            BatchOperationDialogViewModel.ForRemoval(plan, localization),
            execute)
        {
            Owner = Application.Current?.MainWindow
        };
        window.ShowDialog();
        return window.Result;
    }

    public Task<ModInstallationResult> ShowInstallationProgressAsync(
        string modName,
        Func<IProgress<ModInstallationProgress>, CancellationToken,
            Task<ModInstallationResult>> install,
        LocalizationService localization)
    {
        var window = new InstallProgressWindow(
            modName,
            install,
            localization,
            exceptionLogger)
        {
            Owner = Application.Current?.MainWindow
        };
        window.ShowDialog();
        return Task.FromResult(
            window.Result ??
            new ModInstallationResult
            {
                Status = InstallOperationStatus.Blocked,
                InstallationState =
                    PackageInstallationState.NotInstalled,
                ErrorCode = "InstallationCanceled"
            });
    }

}
