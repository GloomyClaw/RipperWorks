using RipperWorks.Core;

namespace RipperWorks.Organizer.GameOperations;

/// <summary>
/// Production-backed managed-path authority for RF-06 shadow characterization.
/// </summary>
internal sealed class OrganizerManagedPathAuthority : IShadowManagedPathGuard
{
    private readonly OrganizerRepository _repository;
    private readonly bool _requireManagedForWrite;
    private readonly bool _requireManagedForDelete;

    public OrganizerManagedPathAuthority(
        OrganizerRepository repository,
        bool requireManagedForWrite = false,
        bool requireManagedForDelete = true)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _requireManagedForWrite = requireManagedForWrite;
        _requireManagedForDelete = requireManagedForDelete;
    }

    public bool CanWrite(string relativePath) =>
        !_requireManagedForWrite || IsManagedSync(relativePath);

    public bool CanDelete(string relativePath) =>
        !_requireManagedForDelete || IsManagedSync(relativePath);

    public async Task<bool> IsManagedAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var managed = await _repository
            .LoadManagedPathAsync(Normalize(relativePath), cancellationToken)
            .ConfigureAwait(false);
        return managed is not null;
    }

    public async Task<ManagedPathLayerRecord?> GetTopLayerAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var layers = await _repository
            .LoadManagedPathLayersAsync(Normalize(relativePath), cancellationToken)
            .ConfigureAwait(false);
        return layers.Count == 0 ? null : layers[^1];
    }

    public async Task<string?> GetPlannedRestoreIdentityAsync(
        string relativePath,
        ManagedPathLayerRecord topLayer,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(relativePath);
        var managed = await _repository
            .LoadManagedPathAsync(normalized, cancellationToken)
            .ConfigureAwait(false);
        var layers = await _repository
            .LoadManagedPathLayersAsync(normalized, cancellationToken)
            .ConfigureAwait(false);
        var next = layers
            .Where(layer => layer.LayerOrder < topLayer.LayerOrder)
            .OrderByDescending(layer => layer.LayerOrder)
            .FirstOrDefault();
        if (next is not null)
            return next.ContentHash;
        return managed?.BaseFileExisted == true
            ? managed.BaseContentHash
            : null;
    }

    private bool IsManagedSync(string relativePath) =>
        IsManagedAsync(relativePath).GetAwaiter().GetResult();

    private static string Normalize(string relativePath) =>
        (relativePath ?? string.Empty).Replace('/', '\\').Trim();
}

/// <summary>
/// Production-characterized shadow executor. Requires explicit
/// <see cref="ShadowHarnessPermit"/>. Internal only.
/// </summary>
internal sealed class ProductionSafePrimitiveShadowExecutor :
    IGameOperationExecutor,
    IShadowHarnessBoundExecutor
{
    private readonly OrganizerRepository _repository;
    private readonly ContentStoreService _contentStore;
    private readonly PackageId? _owningPackageId;
    private readonly ShadowHarnessPermit _permit;
    private readonly OrganizerManagedPathAuthority _pathAuthority;
    private readonly ShadowAtomicFileRestore _atomicRestore;
    private readonly IGameOperationResourceStateReader _resourceReader;
    private readonly IGameOperationPlanContractValidator _planContract;

    internal ShadowRestoreFaultPhase TestOnlyRestoreFault
    {
        get => _atomicRestore.TestOnlyFault;
        set => _atomicRestore.TestOnlyFault = value;
    }

    public ProductionSafePrimitiveShadowExecutor(
        ShadowHarnessPermit permit,
        OrganizerRepository repository,
        ContentStoreService contentStore,
        PackageId? owningPackageId = null,
        IGameOperationResourceStateReader? resourceReader = null)
    {
        _permit = permit ?? throw new ArgumentNullException(nameof(permit));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _contentStore = contentStore ?? throw new ArgumentNullException(nameof(contentStore));
        _owningPackageId = owningPackageId;
        _resourceReader = resourceReader ??
            FileSystemGameOperationResourceStateReader.Shared;
        _atomicRestore = new ShadowAtomicFileRestore(permit);
        _pathAuthority = new OrganizerManagedPathAuthority(
            repository,
            requireManagedForWrite: false,
            requireManagedForDelete: true);
        _planContract = new ProductionGameOperationPlanContractValidator(
            _pathAuthority,
            _contentStore,
            _owningPackageId,
            _permit,
            _resourceReader);
    }

    public int CharacterizationHits { get; private set; }

    public Task PrepareAsync(
        GameOperationPlan plan,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CharacterizationHits++;
        EnsurePlan(plan);
        if (!Directory.Exists(plan.ProfileKey.Value))
            throw new DirectoryNotFoundException("ProfileRootMissing");
        return Task.CompletedTask;
    }

    public async Task ExecuteStepAsync(
        GameOperationPlan plan,
        GameOperationStepSpec step,
        CancellationToken cancellationToken = default)
    {
        CharacterizationHits++;
        EnsurePlan(plan);
        SyntheticGameOperationExecutor.RejectReparseOnPath(
            Path.GetFullPath(plan.ProfileKey.Value),
            step.RelativePath);

        var destination = ArchivePathSafety.ResolveSafeGamePath(
            plan.ProfileKey.Value,
            step.RelativePath);
        _permit.EnsureAllowed(destination, "destination");
        if (_permit.HasReparseAncestorUnderHarness(destination))
            throw new InvalidOperationException("ShadowRootNotAllowed");

        if (step.StepKind == GameOperationStepKind.WriteFile)
            await WriteAsync(destination, step, cancellationToken).ConfigureAwait(false);
        else if (step.StepKind == GameOperationStepKind.DeleteFile)
            await DeleteWithRestoreAsync(destination, step, cancellationToken)
                .ConfigureAwait(false);
    }

    public async Task VerifyStepAsync(
        GameOperationPlan plan,
        GameOperationStepSpec step,
        CancellationToken cancellationToken = default)
    {
        CharacterizationHits++;
        EnsurePlan(plan);
        var destination = ArchivePathSafety.ResolveSafeGamePath(
            plan.ProfileKey.Value,
            step.RelativePath);
        await AssertAfterAsync(destination, step.ExpectedAfterIdentity, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task FinalizeAsync(
        GameOperationPlan plan,
        CancellationToken cancellationToken = default)
    {
        CharacterizationHits++;
        return Task.CompletedTask;
    }

    public ShadowHarnessPermit? BoundPermit => _permit;
    public IGameOperationResourceStateReader ResourceStateReader => _resourceReader;
    public IGameOperationPlanContractValidator PlanContractValidator => _planContract;

    public PreconditionOutcome ValidateHarness(ShadowHarnessPermit permit)
    {
        if (!ReferenceEquals(permit, _permit))
        {
            return PreconditionOutcome.Block("ShadowRootNotAllowed");
        }

        return permit.ValidateBoundResources(
            _repository.DatabasePath,
            _contentStore.Root);
    }

    private void EnsurePlan(GameOperationPlan plan)
    {
        _permit.EnsureAllowed(plan.ProfileKey.Value, "profile");
        if (_permit.HasReparseAncestorUnderHarness(plan.ProfileKey.Value))
            throw new InvalidOperationException("ShadowRootNotAllowed");
    }

    private async Task WriteAsync(
        string destination,
        GameOperationStepSpec step,
        CancellationToken cancellationToken)
    {
        if (!_pathAuthority.CanWrite(step.RelativePath))
            throw new InvalidOperationException("UnmanagedPathRefused");

        await AssertBeforeAsync(destination, step.ExpectedBeforeIdentity, cancellationToken)
            .ConfigureAwait(false);

        if (!GameOperationStepIdentity.TryGetSha256(
                step.ExpectedAfterIdentity,
                out var expectedAfterSha))
        {
            throw new InvalidOperationException("ExpectedAfterShaRequired");
        }

        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var temporary = destination + ".rf06tmp";
        try
        {
            if (!GameOperationStepIdentity.TryGetSha256(
                    step.ContentIdentity,
                    out var contentSha))
            {
                throw new InvalidOperationException("ContentShaRequired");
            }

            var objectPath = _contentStore.GetObjectPath(contentSha);
            _permit.EnsureAllowed(objectPath, "writeContentObject");
            File.Copy(objectPath, temporary, overwrite: true);
            var stagedHash = await ContentStoreService.ComputeHashAsync(
                    temporary,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    stagedHash,
                    expectedAfterSha,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("StagedContentHashMismatch");
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch { /* best-effort cleanup */ }
            }
        }
    }

    private async Task DeleteWithRestoreAsync(
        string destination,
        GameOperationStepSpec step,
        CancellationToken cancellationToken)
    {
        if (!_pathAuthority.CanDelete(step.RelativePath))
            throw new InvalidOperationException("UnmanagedPathRefused");

        await AssertBeforeAsync(destination, step.ExpectedBeforeIdentity, cancellationToken)
            .ConfigureAwait(false);

        var topLayer = await _pathAuthority
            .GetTopLayerAsync(step.RelativePath, cancellationToken)
            .ConfigureAwait(false);
        if (topLayer is null)
            throw new InvalidOperationException("ManagedLayerMissing");

        if (_owningPackageId is not null &&
            topLayer.PackageId != _owningPackageId.Value)
        {
            throw new InvalidOperationException("LayerOwnershipRefused");
        }

        var liveHash = await ContentStoreService.ComputeHashAsync(
                destination,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                liveHash,
                topLayer.ContentHash,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("InstalledFilesModified");
        }

        var relative = step.RelativePath.Replace('/', '\\');
        var managed = await _repository
            .LoadManagedPathAsync(relative, cancellationToken)
            .ConfigureAwait(false);
        var layers = await _repository
            .LoadManagedPathLayersAsync(relative, cancellationToken)
            .ConfigureAwait(false);
        var nextLayer = layers
            .Where(layer => layer.LayerOrder < topLayer.LayerOrder)
            .OrderByDescending(layer => layer.LayerOrder)
            .FirstOrDefault();

        string? restoreHash = nextLayer?.ContentHash ?? managed?.BaseContentHash;
        var shouldRestore =
            nextLayer is not null ||
            (managed?.BaseFileExisted == true &&
             !string.IsNullOrWhiteSpace(managed.BaseContentHash));

        if (!shouldRestore)
        {
            if (!GameOperationStepIdentity.IsMissing(step.ExpectedAfterIdentity) &&
                !string.IsNullOrWhiteSpace(step.ExpectedAfterIdentity))
            {
                throw new InvalidOperationException("ExpectedAfterNotMissing");
            }

            File.Delete(destination);
            return;
        }

        if (string.IsNullOrWhiteSpace(restoreHash))
            throw new InvalidOperationException("RestoreIdentityMissing");
        var objectPath = _contentStore.GetObjectPath(restoreHash);
        if (!File.Exists(objectPath))
            throw new InvalidOperationException("ContentStoreObjectMissing");
        var objectHash = await ContentStoreService.ComputeHashAsync(
                objectPath,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                objectHash,
                restoreHash,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("ContentStoreObjectCorrupt");
        }

        if (GameOperationStepIdentity.TryGetSha256(
                step.ExpectedAfterIdentity,
                out var expectedRestore) &&
            !string.Equals(
                expectedRestore,
                restoreHash,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("ExpectedAfterRestoreMismatch");
        }

        await _atomicRestore.ReplaceAsync(
                objectPath,
                destination,
                restoreHash,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task AssertBeforeAsync(
        string path,
        string? expected,
        CancellationToken cancellationToken)
    {
        var outcome = await GameOperationResourceIdentityValidator.ValidateAsync(
                _resourceReader,
                path,
                expected,
                allowSyntheticText: false,
                cancellationToken)
            .ConfigureAwait(false);
        if (!outcome.IsAllowed)
            throw new InvalidOperationException(outcome.ErrorCode);
    }

    private async Task AssertAfterAsync(
        string path,
        string? expected,
        CancellationToken cancellationToken)
    {
        var outcome = await GameOperationResourceIdentityValidator.ValidateAsync(
                _resourceReader,
                path,
                expected,
                allowSyntheticText: false,
                cancellationToken)
            .ConfigureAwait(false);
        if (!outcome.IsAllowed)
        {
            throw new InvalidOperationException(
                GameOperationResourceIdentityValidator.ToAfterError(
                    outcome.ErrorCode ?? "ExpectedAfterMismatch"));
        }
    }
}
