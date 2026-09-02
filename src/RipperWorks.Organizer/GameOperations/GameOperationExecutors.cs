using System.Security.Cryptography;
using System.Text;
using RipperWorks.Core;

namespace RipperWorks.Organizer.GameOperations;

/// <summary>
/// Counting wrapper for tests: tracks prepare/step/finalize invocations and writes.
/// </summary>
internal sealed class CountingGameOperationExecutor :
    IGameOperationExecutor,
    IShadowHarnessBoundExecutor
{
    private readonly IGameOperationExecutor _inner;
    private int _prepare;
    private int _steps;
    private int _finalize;
    private int _writes;
    private long _elapsedTicks;

    public CountingGameOperationExecutor(IGameOperationExecutor inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public int PrepareCount => Volatile.Read(ref _prepare);
    public int StepCount => Volatile.Read(ref _steps);
    public int FinalizeCount => Volatile.Read(ref _finalize);
    public int WriteCount => Volatile.Read(ref _writes);
    public int TotalInvocations => PrepareCount + StepCount + FinalizeCount;
    internal TimeSpan ObservedExecutorDuration => TimeSpan.FromSeconds(
        (double)Volatile.Read(ref _elapsedTicks) /
        System.Diagnostics.Stopwatch.Frequency);

    public async Task PrepareAsync(
        GameOperationPlan plan,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _prepare);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            await _inner.PrepareAsync(plan, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Add(
                ref _elapsedTicks,
                System.Diagnostics.Stopwatch.GetTimestamp() - started);
        }
    }

    public async Task ExecuteStepAsync(
        GameOperationPlan plan,
        GameOperationStepSpec step,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _steps);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            await _inner.ExecuteStepAsync(plan, step, cancellationToken)
                .ConfigureAwait(false);
            if (step.StepKind is GameOperationStepKind.WriteFile
                or GameOperationStepKind.DeleteFile)
            {
                Interlocked.Increment(ref _writes);
            }
        }
        finally
        {
            Interlocked.Add(
                ref _elapsedTicks,
                System.Diagnostics.Stopwatch.GetTimestamp() - started);
        }
    }

    public async Task VerifyStepAsync(
        GameOperationPlan plan,
        GameOperationStepSpec step,
        CancellationToken cancellationToken = default)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            await _inner.VerifyStepAsync(plan, step, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Add(
                ref _elapsedTicks,
                System.Diagnostics.Stopwatch.GetTimestamp() - started);
        }
    }

    public async Task FinalizeAsync(
        GameOperationPlan plan,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _finalize);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            await _inner.FinalizeAsync(plan, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Add(
                ref _elapsedTicks,
                System.Diagnostics.Stopwatch.GetTimestamp() - started);
        }
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _prepare, 0);
        Interlocked.Exchange(ref _steps, 0);
        Interlocked.Exchange(ref _finalize, 0);
        Interlocked.Exchange(ref _writes, 0);
        Interlocked.Exchange(ref _elapsedTicks, 0);
    }

    public ShadowHarnessPermit? BoundPermit =>
        (_inner as IShadowHarnessBoundExecutor)?.BoundPermit;

    public IGameOperationResourceStateReader ResourceStateReader =>
        (_inner as IShadowHarnessBoundExecutor)?.ResourceStateReader ??
        FileSystemGameOperationResourceStateReader.Shared;

    public IGameOperationPlanContractValidator PlanContractValidator =>
        (_inner as IShadowHarnessBoundExecutor)?.PlanContractValidator ??
        new SyntheticGameOperationPlanContractValidator();

    public PreconditionOutcome ValidateHarness(ShadowHarnessPermit permit) =>
        _inner is IShadowHarnessBoundExecutor bound
            ? bound.ValidateHarness(permit)
            : PreconditionOutcome.Block("ExecutorNotHarnessBound");
}

/// <summary>
/// Deterministic TEMP-root executor for RF-06 synthetic operations.
/// Prepare is read-only (no Directory.CreateDirectory / file mutation).
/// Parent directories for writes are created only in ExecuteStep.
/// </summary>
/// <summary>Internal synthetic TEMP-harness executor (shadow/test only).</summary>
internal sealed class SyntheticGameOperationExecutor :
    IGameOperationExecutor,
    IShadowHarnessBoundExecutor
{
    private readonly Func<GameOperationStepSpec, bool>? _failOnStep;
    private readonly bool _failAfterSideEffect;
    private readonly ShadowHarnessPermit? _permit;
    private readonly IGameOperationResourceStateReader _resourceReader;
    private readonly IGameOperationPlanContractValidator _planContract =
        new SyntheticGameOperationPlanContractValidator();

    public SyntheticGameOperationExecutor(
        ShadowHarnessPermit? permit = null,
        Func<GameOperationStepSpec, bool>? failOnStep = null,
        bool failAfterSideEffect = false,
        IGameOperationResourceStateReader? resourceReader = null)
    {
        _permit = permit;
        _failOnStep = failOnStep;
        _failAfterSideEffect = failAfterSideEffect;
        _resourceReader = resourceReader ??
            FileSystemGameOperationResourceStateReader.Shared;
    }

    public int WriteCount { get; private set; }

    public Task PrepareAsync(
        GameOperationPlan plan,
        CancellationToken cancellationToken = default)
    {
        // Read-only: root must already exist (validator enforces).
        cancellationToken.ThrowIfCancellationRequested();
        EnsurePermit(plan);
        if (!Directory.Exists(plan.ProfileKey.Value))
            throw new DirectoryNotFoundException("ProfileRootMissing");
        return Task.CompletedTask;
    }

    public async Task ExecuteStepAsync(
        GameOperationPlan plan,
        GameOperationStepSpec step,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsurePermit(plan);
        var path = ResolveUnderRoot(plan.ProfileKey.Value, step.RelativePath);
        if (_failOnStep?.Invoke(step) == true && !_failAfterSideEffect)
            throw new IOException("SyntheticExecutorPreEffectFault");

        await AssertExpectedBeforeAsync(path, step, cancellationToken)
            .ConfigureAwait(false);

        switch (step.StepKind)
        {
            case GameOperationStepKind.WriteFile:
            {
                var parent = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(parent);
                var content = step.ContentIdentity ?? step.StepKey;
                await File.WriteAllTextAsync(path, content, cancellationToken)
                    .ConfigureAwait(false);
                WriteCount++;
                break;
            }
            case GameOperationStepKind.DeleteFile:
            {
                if (await GameOperationResourceIdentityValidator
                        .RequireRegularFileOrMissingAsync(
                            _resourceReader,
                            path,
                            cancellationToken)
                        .ConfigureAwait(false))
                {
                    File.Delete(path);
                    WriteCount++;
                }

                break;
            }
            case GameOperationStepKind.VerifyFile:
                break;
            default:
                throw new InvalidOperationException("UnknownStepKind");
        }

        if (_failOnStep?.Invoke(step) == true && _failAfterSideEffect)
            throw new IOException("SyntheticExecutorPostEffectFault");
    }

    public async Task VerifyStepAsync(
        GameOperationPlan plan,
        GameOperationStepSpec step,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsurePermit(plan);
        var path = ResolveUnderRoot(plan.ProfileKey.Value, step.RelativePath);
        if (step.StepKind != GameOperationStepKind.WriteFile)
        {
            var deleteOutcome = await GameOperationResourceIdentityValidator
                .ValidateAsync(
                    _resourceReader,
                    path,
                    step.ExpectedAfterIdentity,
                    allowSyntheticText: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!deleteOutcome.IsAllowed)
            {
                throw new InvalidOperationException(
                    GameOperationResourceIdentityValidator.ToAfterError(
                        deleteOutcome.ErrorCode ?? "ExpectedAfterMismatch"));
            }

            return;
        }
        var expected = step.ExpectedAfterIdentity
            ?? step.ContentIdentity
            ?? step.StepKey;
        var outcome = await GameOperationResourceIdentityValidator.ValidateAsync(
                _resourceReader,
                path,
                expected,
                allowSyntheticText: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (!outcome.IsAllowed)
            throw new InvalidOperationException(
                GameOperationResourceIdentityValidator.ToAfterError(
                    outcome.ErrorCode ?? "ExpectedAfterMismatch"));
    }

    private async Task AssertExpectedBeforeAsync(
        string path,
        GameOperationStepSpec step,
        CancellationToken cancellationToken)
    {
        var outcome = await GameOperationResourceIdentityValidator.ValidateAsync(
                _resourceReader,
                path,
                step.ExpectedBeforeIdentity,
                allowSyntheticText: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (!outcome.IsAllowed)
            throw new InvalidOperationException(outcome.ErrorCode);
    }

    public Task FinalizeAsync(
        GameOperationPlan plan,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public ShadowHarnessPermit? BoundPermit => _permit;
    public IGameOperationResourceStateReader ResourceStateReader => _resourceReader;
    public IGameOperationPlanContractValidator PlanContractValidator => _planContract;

    public PreconditionOutcome ValidateHarness(ShadowHarnessPermit permit) =>
        _permit is not null &&
        ReferenceEquals(_permit, permit)
            ? PreconditionOutcome.Allow()
            : PreconditionOutcome.Block("ShadowRootNotAllowed");

    private void EnsurePermit(GameOperationPlan plan)
    {
        if (_permit is null)
            throw new InvalidOperationException("ShadowRootNotAllowed");
        _permit.EnsureAllowed(plan.ProfileKey.Value, "profile");
        if (_permit.HasReparseAncestorUnderHarness(plan.ProfileKey.Value))
            throw new InvalidOperationException("ShadowRootNotAllowed");
    }

    /// <summary>
    /// Mirrors install/remove containment: resolved path must stay under root.
    /// </summary>
    public static string ResolveUnderRoot(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        if (relative.Contains("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException("UnsafeRelativePath");
        }

        var combined = Path.GetFullPath(Path.Combine(fullRoot, relative));
        var prefix = fullRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(combined, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("PathEscape");
        }

        RejectReparseOnPath(fullRoot, relative);
        return combined;
    }

    internal static void RejectReparseOnPath(string fullRoot, string relative)
    {
        var probe = fullRoot;
        foreach (var segment in relative.Split(
                     new[] { '\\', '/' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            probe = Path.Combine(probe, segment);
            if (Directory.Exists(probe))
            {
                var info = new DirectoryInfo(probe);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidOperationException("ReparsePointRejected");
            }
        }
    }
}

/// <summary>
/// Characterization adapter around existing ArchivePathSafety + file replace
/// patterns used by install/remove. Shadow-only: tests pass TEMP roots.
/// Prepare is read-only. Install write: temp + File.Move overwrite.
/// Delete: only when file missing or content matches Expected ContentIdentity
/// (64-hex → SHA compare; otherwise text compare). Optional managed-path guard.
/// </summary>
/// <summary>
/// Internal synthetic fixture only — not production characterization.
/// Prefer <see cref="ProductionSafePrimitiveShadowExecutor"/> for evidence.
/// </summary>
internal sealed class SyntheticSafePrimitiveFixture :
    IGameOperationExecutor,
    IShadowHarnessBoundExecutor
{
    private readonly IShadowManagedPathGuard? _pathGuard;
    private readonly ShadowHarnessPermit? _permit;
    private readonly IGameOperationResourceStateReader _resourceReader;
    private readonly IGameOperationPlanContractValidator _planContract =
        new SyntheticGameOperationPlanContractValidator();

    public SyntheticSafePrimitiveFixture(
        ShadowHarnessPermit? permit = null,
        IShadowManagedPathGuard? pathGuard = null,
        IGameOperationResourceStateReader? resourceReader = null)
    {
        _permit = permit;
        _pathGuard = pathGuard;
        _resourceReader = resourceReader ??
            FileSystemGameOperationResourceStateReader.Shared;
    }

    public int CharacterizationHits { get; private set; }

    public Task PrepareAsync(
        GameOperationPlan plan,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CharacterizationHits++;
        EnsureFixturePermit(plan);
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
        EnsureFixturePermit(plan);
        SyntheticGameOperationExecutor.RejectReparseOnPath(
            Path.GetFullPath(plan.ProfileKey.Value),
            step.RelativePath);

        var destination = ArchivePathSafety.ResolveSafeGamePath(
            plan.ProfileKey.Value,
            step.RelativePath);

        if (step.StepKind == GameOperationStepKind.WriteFile)
        {
            if (_pathGuard is not null && !_pathGuard.CanWrite(step.RelativePath))
                throw new InvalidOperationException("UnmanagedPathRefused");

            var parent = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(parent);
            var temporary = destination + ".rf06tmp";
            try
            {
                await File.WriteAllTextAsync(
                        temporary,
                        step.ContentIdentity ?? step.StepKey,
                        cancellationToken)
                    .ConfigureAwait(false);
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    try { File.Delete(temporary); }
                    catch { /* best-effort */ }
                }
            }
        }
        else if (step.StepKind == GameOperationStepKind.DeleteFile)
        {
            if (_pathGuard is not null && !_pathGuard.CanDelete(step.RelativePath))
                throw new InvalidOperationException("UnmanagedPathRefused");

            if (!await GameOperationResourceIdentityValidator
                    .RequireRegularFileOrMissingAsync(
                        _resourceReader,
                        destination,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                return;
            }

            // User-modified protection: delete only when content matches expected
            // identity, or when no identity was specified (synthetic remove path).
            if (!string.IsNullOrWhiteSpace(step.ContentIdentity))
            {
                var expected = step.ContentIdentity.Trim();
                if (IsSha256Hex(expected))
                {
                    var actual = await HashFileAsync(destination, cancellationToken)
                        .ConfigureAwait(false);
                    if (!string.Equals(
                            actual,
                            expected,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "UnexpectedContentRefuseDelete");
                    }
                }
                else
                {
                    var text = await File.ReadAllTextAsync(
                            destination,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!string.Equals(text, expected, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "UnexpectedContentRefuseDelete");
                    }
                }
            }

            File.Delete(destination);
        }
    }

    public Task VerifyStepAsync(
        GameOperationPlan plan,
        GameOperationStepSpec step,
        CancellationToken cancellationToken = default) =>
        VerifyStepCoreAsync(plan, step, cancellationToken);

    private async Task VerifyStepCoreAsync(
        GameOperationPlan plan,
        GameOperationStepSpec step,
        CancellationToken cancellationToken)
    {
        CharacterizationHits++;
        EnsureFixturePermit(plan);
        var destination = ArchivePathSafety.ResolveSafeGamePath(
            plan.ProfileKey.Value,
            step.RelativePath);
        var outcome = await GameOperationResourceIdentityValidator.ValidateAsync(
                _resourceReader,
                destination,
                step.ExpectedAfterIdentity,
                allowSyntheticText: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (!outcome.IsAllowed)
        {
            throw new InvalidOperationException(
                GameOperationResourceIdentityValidator.ToAfterError(
                    outcome.ErrorCode ?? "ExpectedAfterMismatch"));
        }
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

    public PreconditionOutcome ValidateHarness(ShadowHarnessPermit permit) =>
        _permit is not null &&
        ReferenceEquals(_permit, permit)
            ? PreconditionOutcome.Allow()
            : PreconditionOutcome.Block("ShadowRootNotAllowed");

    private void EnsureFixturePermit(GameOperationPlan plan)
    {
        if (_permit is null)
            throw new InvalidOperationException("ShadowRootNotAllowed");
        _permit.EnsureAllowed(plan.ProfileKey.Value, "profile");
    }

    private static bool IsSha256Hex(string value)
    {
        if (value.Length != 64)
            return false;
        foreach (var c in value)
        {
            var isHex =
                (c >= '0' && c <= '9') ||
                (c >= 'a' && c <= 'f') ||
                (c >= 'A' && c <= 'F');
            if (!isHex)
                return false;
        }

        return true;
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var algorithm = SHA256.Create();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                   .ConfigureAwait(false)) > 0)
        {
            algorithm.TransformBlock(buffer, 0, read, null, 0);
        }

        algorithm.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(algorithm.Hash!).ToLowerInvariant();
    }
}
