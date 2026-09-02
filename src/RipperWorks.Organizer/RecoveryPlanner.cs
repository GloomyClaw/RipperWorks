using System.Security.Cryptography;
using System.Text;
using RipperWorks.Core;

namespace RipperWorks.Organizer;

internal sealed class RecoveryPlanner
{
    internal const string MissingIdentity = "<missing>";

    private readonly OrganizerRepository _repository;
    private readonly ContentStoreService _contentStore;
    private readonly string _stagingRoot;
    private readonly GameProfileService _profileService;
    private readonly VersionSwitchRecoveryPlanner _versionSwitch;

    public RecoveryPlanner(
        OrganizerRepository repository,
        ContentStoreService contentStore,
        string stagingRoot,
        GameProfileService profileService)
    {
        _repository = repository;
        _contentStore = contentStore;
        _stagingRoot = stagingRoot;
        _profileService = profileService;
        _versionSwitch = new(repository);
    }

    public async Task<RecoveryPlan> PrepareAsync(
        Guid operationId,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken)
    {
        var evidence = await _repository.LoadRecoveryEvidenceAsync(
            operationId, cancellationToken);
        if (evidence.Operation is null)
            return Blocked(operationId, "OperationMissing");
        var operation = evidence.Operation;
        if (operation.Status != InstallOperationStatus.RecoveryRequired)
        {
            return new RecoveryPlan
            {
                OperationId = operationId,
                Kind = Kind(operation.OperationType),
                PackageIds = [operation.PackageId],
                DurableStatus = operation.Status,
                DurablePhase = operation.CurrentPhase,
                DesiredState = "NoRecoveryRequired",
                AlreadyRecovered = true
            };
        }

        var commonBlocker = ValidateCommon(evidence, profile);
        if (commonBlocker is not null)
            return Blocked(operation, Kind(operation.OperationType), commonBlocker);
        var liveValidation = await _profileService.ValidateFastAsync(
            profile!.GameRoot, libraryRoot, cancellationToken);
        if (!liveValidation.IsValid)
        {
            return Blocked(
                operation,
                Kind(operation.OperationType),
                liveValidation.ErrorCode ?? "GameProfileInvalid");
        }
        var approvedLibraryRoot = NormalizeOptionalPath(libraryRoot);
        if (operation.OperationType == "Install")
        {
            return await PrepareInstallAsync(
                evidence, profile, approvedLibraryRoot, cancellationToken);
        }
        if (operation.OperationType == "Remove")
        {
            return await PrepareRemovalAsync(
                evidence, profile, approvedLibraryRoot, cancellationToken);
        }
        if (operation.OperationType.StartsWith(
                "VersionSwitch|", StringComparison.Ordinal))
        {
            return await _versionSwitch.PrepareAsync(
                evidence,
                profile,
                approvedLibraryRoot,
                PrepareAsync,
                cancellationToken);
        }
        return Blocked(operation, RecoveryOperationKind.Unknown,
            "UnknownOperationType");
    }

    private async Task<RecoveryPlan> PrepareInstallAsync(
        RecoveryRepositoryEvidence evidence,
        GameProfileRecord profile,
        string? libraryRoot,
        CancellationToken cancellationToken)
    {
        var operation = evidence.Operation!;
        if (evidence.PackageLayers.Count != 0)
            return Blocked(operation, RecoveryOperationKind.Install,
                "ConflictingManagedLayers");
        if (RequiresManifest(operation) && evidence.OperationFiles.Count == 0)
            return Blocked(operation, RecoveryOperationKind.Install,
                "OperationFileEvidenceMissing");
        if (HasDuplicatePaths(evidence.OperationFiles))
            return Blocked(operation, RecoveryOperationKind.Install,
                "AmbiguousOperationFileEvidence");
        var actions = new List<RecoveryPathAction>();
        var dependencies = new List<string>();
        var identities = new List<string>();
        foreach (var file in OrderedUnique(evidence.OperationFiles))
        {
            var destination = Resolve(profile.GameRoot, file.RelativeGamePath);
            if (destination is null)
                return Blocked(operation, RecoveryOperationKind.Install,
                    "UnsafeRecoveryPath");
            var current = await IdentityAsync(destination, cancellationToken);
            var target = file.PreviousFileExisted
                ? file.PreviousContentHash
                : null;
            if (file.PreviousFileExisted &&
                !await ContentAvailableAsync(target, cancellationToken))
            {
                return Blocked(operation, RecoveryOperationKind.Install,
                    "ContentStoreEvidenceMissing");
            }
            var allowed = new[] { file.NewContentHash, target ?? MissingIdentity }
                .Distinct(StringComparer.Ordinal).ToArray();
            if (!allowed.Contains(current, StringComparer.Ordinal))
                return Blocked(operation, RecoveryOperationKind.Install,
                    "LiveContentContradictsEvidence");
            actions.Add(new(
                file.RelativeGamePath,
                target is null
                    ? RecoveryPathActionKind.Delete
                    : RecoveryPathActionKind.RestoreContent,
                target,
                allowed));
            identities.Add($"{file.RelativeGamePath}:{current}:{file.Applied}");
            identities.Add(await OwnershipIdentityAsync(
                file.RelativeGamePath, cancellationToken));
            if (target is not null)
                dependencies.Add(_contentStore.GetObjectPath(target));
        }
        return Ready(operation, RecoveryOperationKind.Install,
            [operation.PackageId], "PreInstallState", actions, dependencies,
            evidence, identities, libraryRoot);
    }

    private async Task<RecoveryPlan> PrepareRemovalAsync(
        RecoveryRepositoryEvidence evidence,
        GameProfileRecord profile,
        string? libraryRoot,
        CancellationToken cancellationToken)
    {
        var operation = evidence.Operation!;
        if (evidence.InstalledMod is null || evidence.InstalledFiles.Count == 0)
            return Blocked(operation, RecoveryOperationKind.Remove,
                "InstalledOwnershipEvidenceMissing");
        if (evidence.PackageLayers.Count != evidence.InstalledFiles.Count)
            return Blocked(operation, RecoveryOperationKind.Remove,
                "ManagedLayerEvidenceMismatch");
        if (HasDuplicatePaths(evidence.OperationFiles))
            return Blocked(operation, RecoveryOperationKind.Remove,
                "AmbiguousOperationFileEvidence");
        var actions = new List<RecoveryPathAction>();
        var dependencies = new List<string>();
        var identities = new List<string>();
        var operationRoot = Path.Combine(
            _stagingRoot, operation.OperationId.ToString("N"));
        foreach (var file in OrderedUnique(evidence.OperationFiles))
        {
            var destination = Resolve(profile.GameRoot, file.RelativeGamePath);
            var staged = Resolve(operationRoot, file.RelativeGamePath);
            if (destination is null || staged is null)
                return Blocked(operation, RecoveryOperationKind.Remove,
                    "UnsafeRecoveryPath");
            var stagedIdentity = await IdentityAsync(staged, cancellationToken);
            if (stagedIdentity != file.NewContentHash)
                return Blocked(operation, RecoveryOperationKind.Remove,
                    "RemovalRestoreContentMissing");
            var current = await IdentityAsync(destination, cancellationToken);
            var removedIdentity = file.PreviousFileExisted
                ? file.PreviousContentHash
                : MissingIdentity;
            if (removedIdentity is null ||
                current != file.NewContentHash && current != removedIdentity)
            {
                return Blocked(operation, RecoveryOperationKind.Remove,
                    "LiveContentContradictsEvidence");
            }
            actions.Add(new(
                file.RelativeGamePath,
                RecoveryPathActionKind.RestoreRemovalContent,
                file.NewContentHash,
                [file.NewContentHash, removedIdentity]));
            dependencies.Add(staged);
            identities.Add($"{file.RelativeGamePath}:{current}:{stagedIdentity}");
            var matchingInstalledFiles = evidence.InstalledFiles
                .Where(item => string.Equals(
                    item.RelativeGamePath,
                    file.RelativeGamePath,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matchingInstalledFiles.Count != 1)
            {
                return Blocked(operation, RecoveryOperationKind.Remove,
                    "InstalledOwnershipEvidenceMissing");
            }
            var installedFile = matchingInstalledFiles[0];

            var pathLayers = await _repository.LoadManagedPathLayersAsync(
                file.RelativeGamePath, cancellationToken);
            if (pathLayers.LastOrDefault() is not { } topLayer ||
                topLayer.PackageId != operation.PackageId ||
                topLayer.ContentHash != installedFile.InstalledContentHash)
            {
                return Blocked(operation, RecoveryOperationKind.Remove,
                    "ManagedLayerEvidenceMismatch");
            }
            identities.Add(await OwnershipIdentityAsync(
                file.RelativeGamePath, cancellationToken));
        }
        return Ready(operation, RecoveryOperationKind.Remove,
            [operation.PackageId], "PreRemovalInstalledState", actions,
            dependencies, evidence, identities, libraryRoot);
    }

    internal async Task<string> OwnershipIdentityAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        var managed = await _repository.LoadManagedPathAsync(
            relativePath, cancellationToken);
        var layers = await _repository.LoadManagedPathLayersAsync(
            relativePath, cancellationToken);
        return $"managed:{relativePath}:{managed?.BaseFileExisted}:" +
            $"{managed?.BaseContentHash}:" + string.Join(",", layers.Select(
                layer => $"{layer.LayerOrder}/{layer.PackageId.Value}/" +
                    $"{layer.ContentHash}/{layer.OperationId}"));
    }

    private async Task<bool> ContentAvailableAsync(
        string? hash,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hash))
            return false;
        return await IdentityAsync(
            _contentStore.GetObjectPath(hash), cancellationToken) == hash;
    }

    internal static RecoveryPlan Ready(
        InstallOperationRecord operation,
        RecoveryOperationKind kind,
        IReadOnlyList<PackageId> packages,
        string desiredState,
        IReadOnlyList<RecoveryPathAction> actions,
        IReadOnlyList<string> dependencies,
        RecoveryRepositoryEvidence evidence,
        IReadOnlyList<string> identities,
        string? libraryRoot) => new()
        {
            OperationId = operation.OperationId,
            Kind = kind,
            PackageIds = packages,
            DurableStatus = operation.Status,
            DurablePhase = operation.CurrentPhase,
            DesiredState = desiredState,
            PathActions = actions,
            EvidenceDependencies = dependencies,
            ApprovedLibraryRoot = libraryRoot,
            IsDeterministic = true,
            ValidationToken = Token(
                operation, evidence, identities, libraryRoot)
        };

    internal static RecoveryPlan Blocked(
        InstallOperationRecord operation,
        RecoveryOperationKind kind,
        string blocker) => new()
        {
            OperationId = operation.OperationId,
            Kind = kind,
            PackageIds = [operation.PackageId],
            DurableStatus = operation.Status,
            DurablePhase = operation.CurrentPhase,
            DesiredState = "RecoveryRequired",
            Blockers = [blocker]
        };

    private static RecoveryPlan Blocked(Guid id, string blocker) => new()
        {
            OperationId = id,
            Kind = RecoveryOperationKind.Unknown,
            DesiredState = "RecoveryRequired",
            Blockers = [blocker]
        };

    internal static bool HasDuplicatePaths(
        IReadOnlyList<InstallOperationFileRecord> files) =>
        files.Select(value => value.RelativeGamePath)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count;

    internal static async Task<string> IdentityAsync(
        string path,
        CancellationToken cancellationToken) => File.Exists(path) &&
        !Directory.Exists(path)
            ? await ContentStoreService.ComputeHashAsync(path, cancellationToken)
            : MissingIdentity;

    internal static string? Resolve(string root, string relativePath)
    {
        try
        {
            var path = ArchivePathSafety.ResolveSafeGamePath(root, relativePath);
            ArchivePathSafety.EnsureNoReparsePoints(
                root, path, allowMissing: true);
            return path;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or InvalidDataException or
            UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    internal static string RelationIdentity(PackageRelationView view) =>
        $"{view.Relation.RelationId}:{view.Relation.FromPackageId.Value}:" +
        $"{view.Relation.ToPackageId.Value}:{view.Relation.RelationType}:" +
        $"{view.Relation.Source}:{view.Relation.IsConfirmed}";

    private static string? ValidateCommon(
        RecoveryRepositoryEvidence evidence,
        GameProfileRecord? profile)
    {
        if (!evidence.PackageExists)
            return "PackageIdentityMissing";
        var storedProfile = evidence.Profile;
        if (profile is null || storedProfile is null ||
            profile.ValidationState != GameProfileValidationState.Valid)
            return "GameProfileMissing";
        if (!PathEquals(profile.GameRoot, storedProfile.GameRoot) ||
            !PathEquals(profile.ExecutablePath, storedProfile.ExecutablePath) ||
            storedProfile.InitializedAtUtc > evidence.Operation!.StartedAtUtc)
        {
            return "RecoveryProfileChanged";
        }
        return null;
    }

    private static RecoveryOperationKind Kind(string value) => value switch
    {
        "Install" => RecoveryOperationKind.Install,
        "Remove" => RecoveryOperationKind.Remove,
        _ when value.StartsWith("VersionSwitch|", StringComparison.Ordinal) =>
            RecoveryOperationKind.VersionSwitch,
        _ => RecoveryOperationKind.Unknown
    };

    private static bool RequiresManifest(InstallOperationRecord operation) =>
        operation.CurrentPhase >= InstallOperationPhase.ManifestPlanned;

    private static IReadOnlyList<InstallOperationFileRecord> OrderedUnique(
        IReadOnlyList<InstallOperationFileRecord> files) =>
        files.OrderByDescending(value => value.Sequence).ToArray();

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    private static string Token(
        InstallOperationRecord operation,
        RecoveryRepositoryEvidence evidence,
        IReadOnlyList<string> identities,
        string? libraryRoot)
    {
        var value = string.Join("\n", new[]
        {
            operation.OperationId.ToString("N"), operation.OperationType,
            operation.Status.ToString(), operation.CurrentPhase.ToString(),
            operation.PackageId.Value,
            evidence.Profile?.GameRoot ?? string.Empty,
            evidence.Profile?.ExecutablePath ?? string.Empty,
            evidence.Profile?.InitializedAtUtc.ToString("O") ?? string.Empty,
            libraryRoot ?? MissingIdentity,
            evidence.InstalledMod?.InstallationState.ToString() ?? "none",
            string.Join("|", evidence.OperationFiles.Select(file =>
                $"{file.Id}:{file.RelativeGamePath}:{file.NewContentHash}:" +
                $"{file.PreviousContentHash}:{file.PreviousFileExisted}:" +
                $"{file.Sequence}:{file.Applied}")),
            string.Join("|", evidence.InstalledFiles.Select(file =>
                $"{file.RelativeGamePath}:{file.InstalledContentHash}:" +
                $"{file.PreviousContentHash}:{file.PreviousFileExisted}:" +
                $"{file.Sequence}")),
            string.Join("|", evidence.PackageLayers.Select(layer =>
                $"{layer.RelativePath}:{layer.LayerOrder}:" +
                $"{layer.PackageId.Value}:{layer.ContentHash}:" +
                $"{layer.OperationId}")),
            string.Join("|", evidence.Relations.Select(RelationIdentity)),
            string.Join("|", identities)
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
    }
}
