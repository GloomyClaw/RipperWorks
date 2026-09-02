using RipperWorks.Core;

namespace RipperWorks.Organizer;

internal sealed class RecoveryExecutor(
    OrganizerRepository repository,
    ContentStoreService contentStore,
    string stagingRoot,
    ModInstallationService installer,
    ModRemovalService remover,
    GameProfileService profileService)
{
    internal Func<RecoveryPlan, CancellationToken, Task>?
        TestOnlyBeforeMutation { get; set; }
    internal Func<RecoveryPathAction, int, Task>?
        TestOnlyAfterPathMutation { get; set; }
    internal Func<RecoveryPlan, Task>? TestOnlyBeforeVerification { get; set; }

    public async Task<RecoveryResult> ExecuteAsync(
        RecoveryPlan plan,
        GameProfileRecord profile,
        string? libraryRoot,
        CancellationToken cancellationToken)
    {
        if (!plan.IsDeterministic)
            return Failure(plan, plan.Blockers.FirstOrDefault() ??
                "RecoveryUnsupported");
        if (TestOnlyBeforeMutation is not null)
            await TestOnlyBeforeMutation(plan, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var validation = await profileService.ValidateFastAsync(
            profile.GameRoot, libraryRoot, cancellationToken);
        if (!validation.IsValid)
        {
            return Failure(
                plan, validation.ErrorCode ?? "GameProfileInvalid");
        }

        try
        {
            return plan.Kind == RecoveryOperationKind.VersionSwitch
                ? await ExecuteVersionSwitchAsync(plan, profile, libraryRoot)
                : await ExecutePathRecoveryAsync(plan, profile);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RecoveryPathException exception)
        {
            return Failure(plan, exception.Code, exception.RelativePath);
        }
        catch (Exception)
        {
            return Failure(plan, "RecoveryExecutionFailed");
        }
    }

    private async Task<RecoveryResult> ExecutePathRecoveryAsync(
        RecoveryPlan plan,
        GameProfileRecord profile)
    {
        var index = 0;
        foreach (var action in plan.PathActions)
        {
            var path = ResolveMutationPath(
                profile.GameRoot, action.RelativeGamePath);
            var current = await IdentityAsync(path);
            if (!action.AllowedCurrentContentHashes.Contains(
                    current, StringComparer.Ordinal))
            {
                throw new RecoveryPathException(
                    "RecoveryPlanInvalidated", action.RelativeGamePath);
            }
            var target = action.TargetContentHash ??
                RecoveryPlanner.MissingIdentity;
            if (current != target)
            {
                await ApplyAsync(
                    plan.OperationId,
                    profile.GameRoot,
                    action,
                    path);
                if (TestOnlyAfterPathMutation is not null)
                    await TestOnlyAfterPathMutation(action, ++index);
            }
        }
        if (TestOnlyBeforeVerification is not null)
            await TestOnlyBeforeVerification(plan);
        await VerifyPathsAsync(plan, profile.GameRoot);
        if (plan.Kind == RecoveryOperationKind.Install)
        {
            await VerifyNoPackageLayersAsync(plan.PackageIds[0]);
            await repository.TerminalizeInstallRecoveryAsync(
                plan.OperationId, plan.PackageIds[0], CancellationToken.None);
            var installed = await repository.LoadInstalledModAsync(
                plan.PackageIds[0], CancellationToken.None);
            var files = await repository.LoadInstalledFilesAsync(
                plan.PackageIds[0], CancellationToken.None);
            if (installed is not null || files.Count != 0)
                throw new InvalidOperationException("Install metadata remained.");
        }
        else
        {
            await VerifyRemovalOwnershipAsync(plan);
            await repository.TerminalizeRemovalRecoveryAsync(
                plan.OperationId,
                plan.PackageIds[0],
                CancellationToken.None);
        }
        return Success(plan);
    }

    private async Task<RecoveryResult> ExecuteVersionSwitchAsync(
        RecoveryPlan plan,
        GameProfileRecord profile,
        string? libraryRoot)
    {
        var sourceId = plan.SourcePackageId ??
            throw new InvalidOperationException("Source package is missing.");
        var targetId = plan.TargetPackageId ??
            throw new InvalidOperationException("Target package is missing.");
        if (plan.NestedRecoveryOperationId is { } nestedOperationId)
        {
            await ApplyPathActionsAsync(
                plan, nestedOperationId, profile.GameRoot);
            await VerifyPathsAsync(plan, profile.GameRoot);
            var nestedPackageId = plan.NestedRecoveryPackageId ??
                throw new InvalidOperationException(
                    "Nested recovery package is missing.");
            if (plan.NestedRecoveryKind == RecoveryOperationKind.Install)
            {
                await VerifyNoPackageLayersAsync(nestedPackageId);
                await repository.TerminalizeInstallRecoveryAsync(
                    nestedOperationId,
                    nestedPackageId,
                    CancellationToken.None);
            }
            else if (plan.NestedRecoveryKind == RecoveryOperationKind.Remove)
            {
                await VerifyRemovalOwnershipAsync(plan, nestedPackageId);
                await repository.TerminalizeRemovalRecoveryAsync(
                    nestedOperationId,
                    nestedPackageId,
                    CancellationToken.None);
            }
            else
            {
                throw new InvalidOperationException(
                    "Nested recovery kind is unsupported.");
            }
        }
        var packages = await repository.LoadPackagesAsync(
            false, CancellationToken.None);

        if (plan.VersionSwitchAction is
            VersionSwitchRecoveryAction.VerifyOriginalSource or
            VersionSwitchRecoveryAction.RecoverSourceRemoval)
        {
            if (!await VerifyVersionSwitchResultAsync(
                    plan, sourceId, targetId, profile.GameRoot))
            {
                return Failure(plan, "RecoveryVerificationFailed");
            }
            await repository.TerminalizeRecoveryOperationAsync(
                plan.OperationId, CancellationToken.None);
            return Success(plan);
        }

        if (plan.VersionSwitchAction == VersionSwitchRecoveryAction
                .RollbackTargetAndRestoreOriginalSource)
        {
            var target = packages.Single(value =>
                value.Package.PackageId == targetId);
            var removal = await remover.RemovePrimitiveAsync(
                target,
                profile,
                libraryRoot,
                CancellationToken.None,
                plan.OperationId);
            if (!removal.Success)
            {
                return Failure(
                    plan,
                    removal.ErrorCode ?? "TargetRollbackFailed");
            }
            packages = await repository.LoadPackagesAsync(
                false, CancellationToken.None);
        }
        if (!plan.SourceAlreadyRestored)
        {
            var source = packages.Single(value =>
                value.Package.PackageId == sourceId);
            var result = await installer.InstallPrimitiveAsync(
                source, profile, libraryRoot,
                cancellationToken: CancellationToken.None,
                ignoredOperationId: plan.OperationId);
            if (!result.Success)
                return Failure(plan, result.ErrorCode ?? "SourceRestoreFailed");
        }
        await repository.RestoreVersionSwitchSourceLayersAsync(
            sourceId, plan.SourceLayers, CancellationToken.None);
        foreach (var layer in plan.SourceLayers)
        {
            var destination = ResolveMutationPath(
                profile.GameRoot, layer.RelativeGamePath);
            if (await IdentityAsync(destination) == layer.DesiredLiveContentHash)
                continue;
            await RestoreContentAsync(
                contentStore.GetObjectPath(layer.DesiredLiveContentHash),
                layer.DesiredLiveContentHash, profile.GameRoot, destination);
        }
        if (TestOnlyBeforeVerification is not null)
            await TestOnlyBeforeVerification(plan);
        if (!await VerifyVersionSwitchResultAsync(
                plan, sourceId, targetId, profile.GameRoot))
            return Failure(plan, "RecoveryVerificationFailed");
        await repository.TerminalizeRecoveryOperationAsync(
            plan.OperationId, CancellationToken.None);
        return Success(plan);
    }

    private async Task<bool> VerifySourceLayersAsync(
        RecoveryPlan plan,
        PackageId sourceId,
        string gameRoot)
    {
        var installedFiles = await repository.LoadInstalledFilesAsync(
            sourceId, CancellationToken.None);
        foreach (var expected in plan.SourceLayers)
        {
            var layers = await repository.LoadManagedPathLayersAsync(
                expected.RelativeGamePath, CancellationToken.None);
            if (expected.InsertionOrder >= layers.Count ||
                layers[expected.InsertionOrder].PackageId != sourceId ||
                layers[expected.InsertionOrder].ContentHash !=
                    expected.SourceContentHash)
                return false;
            var installed = installedFiles.SingleOrDefault(value =>
                string.Equals(value.RelativeGamePath,
                    expected.RelativeGamePath,
                    StringComparison.OrdinalIgnoreCase));
            if (installed is null ||
                installed.InstalledContentHash != expected.SourceContentHash ||
                installed.PreviousContentHash != expected.PreviousContentHash ||
                installed.PreviousFileExisted != expected.PreviousFileExisted)
                return false;
            var path = ResolveMutationPath(
                gameRoot, expected.RelativeGamePath);
            if (await IdentityAsync(path) != expected.DesiredLiveContentHash)
                return false;
        }
        return installedFiles.Count == plan.SourceLayers.Count;
    }

    private async Task ApplyPathActionsAsync(
        RecoveryPlan plan,
        Guid evidenceOperationId,
        string gameRoot)
    {
        var index = 0;
        foreach (var action in plan.PathActions)
        {
            var path = ResolveMutationPath(
                gameRoot, action.RelativeGamePath);
            var current = await IdentityAsync(path);
            if (!action.AllowedCurrentContentHashes.Contains(
                    current, StringComparer.Ordinal))
                throw new RecoveryPathException(
                    "RecoveryPlanInvalidated", action.RelativeGamePath);
            var target = action.TargetContentHash ??
                RecoveryPlanner.MissingIdentity;
            if (current == target)
                continue;
            await ApplyAsync(
                evidenceOperationId,
                gameRoot,
                action,
                path);
            if (TestOnlyAfterPathMutation is not null)
                await TestOnlyAfterPathMutation(action, ++index);
        }
    }

    private async Task ApplyAsync(
        Guid evidenceOperationId,
        string gameRoot,
        RecoveryPathAction action,
        string destination)
    {
        EnsureMutationPath(gameRoot, destination);
        switch (action.Action)
        {
            case RecoveryPathActionKind.Delete:
                File.Delete(destination);
                return;
            case RecoveryPathActionKind.RestoreContent:
                await RestoreContentAsync(
                    contentStore.GetObjectPath(action.TargetContentHash!),
                    action.TargetContentHash!, gameRoot, destination);
                return;
            case RecoveryPathActionKind.RestoreRemovalContent:
                var source = ArchivePathSafety.ResolveSafeGamePath(
                    Path.Combine(
                        stagingRoot,
                        evidenceOperationId.ToString("N")),
                    action.RelativeGamePath);
                ArchivePathSafety.EnsureNoReparsePoints(
                    Path.Combine(stagingRoot, evidenceOperationId.ToString("N")),
                    source);
                await RestoreContentAsync(
                    source, action.TargetContentHash!, gameRoot, destination);
                return;
            default:
                throw new InvalidOperationException("Unknown recovery action.");
        }
    }

    private static async Task RestoreContentAsync(
        string source,
        string expectedHash,
        string gameRoot,
        string destination)
    {
        if (await IdentityAsync(source) != expectedHash)
            throw new InvalidDataException("Recovery content changed.");
        EnsureMutationPath(gameRoot, destination);
        ArchivePathSafety.CreateDirectoriesWithoutReparse(
            gameRoot, Path.GetDirectoryName(destination)!);
        var temporary = destination + $".{Guid.NewGuid():N}.recovery";
        try
        {
            EnsureMutationPath(gameRoot, temporary);
            await using (var input = new FileStream(
                source, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var output = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await input.CopyToAsync(output, CancellationToken.None);
            if (await IdentityAsync(temporary) != expectedHash)
                throw new InvalidDataException("Recovery copy changed.");
            EnsureMutationPath(gameRoot, destination);
            EnsureMutationPath(gameRoot, temporary);
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                EnsureMutationPath(gameRoot, temporary);
                File.Delete(temporary);
            }
        }
    }

    private static async Task VerifyPathsAsync(
        RecoveryPlan plan,
        string gameRoot)
    {
        foreach (var action in plan.PathActions)
        {
            var path = ResolveMutationPath(
                gameRoot, action.RelativeGamePath);
            var expected = action.TargetContentHash ??
                RecoveryPlanner.MissingIdentity;
            if (await IdentityAsync(path) != expected)
            {
                throw new RecoveryPathException(
                    "RecoveryVerificationFailed", action.RelativeGamePath);
            }
        }
    }

    private async Task VerifyNoPackageLayersAsync(PackageId packageId)
    {
        if (await repository.LoadManagedPathLayersForPackageAsync(
                packageId, CancellationToken.None) is { Count: > 0 })
            throw new InvalidOperationException("Package layers remained.");
    }

    private async Task VerifyRemovalOwnershipAsync(
        RecoveryPlan plan,
        PackageId? packageIdOverride = null)
    {
        var packageId = packageIdOverride ?? plan.PackageIds[0];
        var installed = await repository.LoadInstalledModAsync(
            packageId, CancellationToken.None);
        var files = await repository.LoadInstalledFilesAsync(
            packageId, CancellationToken.None);
        var layers = await repository.LoadManagedPathLayersForPackageAsync(
            packageId, CancellationToken.None);
        if (installed?.InstallationState is not (
                PackageInstallationState.Installed or
                PackageInstallationState.PartiallyInstalled) ||
            files.Count == 0 || layers.Count != files.Count)
        {
            throw new InvalidOperationException("Removal ownership changed.");
        }
    }

    private static async Task<string> IdentityAsync(string path) =>
        File.Exists(path) && !Directory.Exists(path)
            ? await ContentStoreService.ComputeHashAsync(
                path, CancellationToken.None)
            : RecoveryPlanner.MissingIdentity;

    private async Task<bool> VerifyVersionSwitchResultAsync(
        RecoveryPlan plan,
        PackageId sourceId,
        PackageId targetId,
        string gameRoot)
    {
        var packages = await repository.LoadPackagesAsync(
            false, CancellationToken.None);
        var restoredSource = packages.Single(value =>
            value.Package.PackageId == sourceId);
        if (restoredSource.Package.InstallationState !=
            PackageInstallationState.Installed)
        {
            return false;
        }
        if (sourceId != targetId)
        {
            var cleanTarget = packages.Single(value =>
                value.Package.PackageId == targetId);
            if (cleanTarget.Package.InstallationState !=
                    PackageInstallationState.NotInstalled ||
                await repository.LoadManagedPathLayersForPackageAsync(
                    targetId, CancellationToken.None) is { Count: > 0 })
            {
                return false;
            }
        }
        if (plan.NestedRecoveryOperationId is { } nestedId &&
            (await repository.LoadRecoveryOperationAsync(
                nestedId, CancellationToken.None))?.Status ==
                InstallOperationStatus.RecoveryRequired)
        {
            return false;
        }
        return await VerifySourceLayersAsync(plan, sourceId, gameRoot);
    }

    private static void EnsureMutationPath(string gameRoot, string path) =>
        ArchivePathSafety.EnsureNoReparsePoints(
            gameRoot, path, allowMissing: true);

    private static string ResolveMutationPath(
        string gameRoot,
        string relativePath)
    {
        try
        {
            var path = ArchivePathSafety.ResolveSafeGamePath(
                gameRoot, relativePath);
            EnsureMutationPath(gameRoot, path);
            return path;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or
            InvalidDataException or UnauthorizedAccessException or
            NotSupportedException)
        {
            throw new RecoveryPathException(
                "UnsafeRecoveryPath", relativePath);
        }
    }

    private static RecoveryResult Success(RecoveryPlan plan) => new()
    {
        OperationId = plan.OperationId,
        Success = true,
        Plan = plan
    };

    private static RecoveryResult Failure(
        RecoveryPlan plan,
        string errorCode,
        string? path = null) => new()
        {
            OperationId = plan.OperationId,
            ErrorCode = errorCode,
            FailedPath = path,
            Plan = plan
        };

    private sealed class RecoveryPathException(
        string code,
        string relativePath) : Exception
    {
        public string Code { get; } = code;
        public string RelativePath { get; } = relativePath;
    }
}
