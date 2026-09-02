using RipperWorks.Core;
using SharpCompress.Archives;

namespace RipperWorks.Organizer;

public sealed class ArchiveAnalyzer
{
    public const int AnalyzerVersion = 4;

    private readonly ArchiveResourcePolicy _resourcePolicy;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Test-only: invoked after the analysis stream is hashed and rewound,
    /// before archive enumeration begins.
    /// </summary>
    internal Action? TestOnlyAfterHashBeforeEnumerate { get; set; }

    /// <summary>
    /// Test-only: invoked once per entry after path normalization during
    /// enumeration (cancellation boundaries between entries are observable).
    /// </summary>
    internal Action? TestOnlyAfterEntryEnumerated { get; set; }

    /// <summary>
    /// Test-only: invoked after post-enumeration root detection, before the
    /// final draft is constructed.
    /// </summary>
    internal Action? TestOnlyAfterRootDetection { get; set; }

    public ArchiveAnalyzer()
        : this(ArchiveResourcePolicy.Production, TimeProvider.System)
    {
    }

    internal ArchiveAnalyzer(
        ArchiveResourcePolicy resourcePolicy,
        TimeProvider timeProvider)
    {
        _resourcePolicy = resourcePolicy ??
            throw new ArgumentNullException(nameof(resourcePolicy));
        _timeProvider = timeProvider ??
            throw new ArgumentNullException(nameof(timeProvider));
    }

    public static bool MatchesStoredIdentity(
        PackageAnalysisRecord analysis,
        PackageRecord package) =>
        ArchiveAnalysisIdentity.MatchesStoredIdentity(analysis, package);

    public static bool IsLiveContentCurrent(
        PackageAnalysisRecord analysis,
        PackageRecord package,
        CancellationToken cancellationToken = default) =>
        ArchiveAnalysisIdentity.IsLiveContentCurrent(
            analysis,
            package,
            cancellationToken);

    public static bool IsCurrent(
        PackageAnalysisRecord analysis,
        PackageRecord package) =>
        ArchiveAnalysisIdentity.IsCurrent(analysis, package);

    public static string ComputeFingerprint(
        PackageRecord package,
        string? selectedRoot) =>
        ArchiveAnalysisIdentity.ComputeFingerprint(package, selectedRoot);

    public static string BuildContentFingerprint(
        PackageRecord package,
        string archiveSha256,
        string? selectedRoot) =>
        ArchiveAnalysisIdentity.BuildContentFingerprint(
            package,
            archiveSha256,
            selectedRoot);

    public static string ComputeLegacyFingerprint(
        PackageRecord package,
        string? selectedRoot) =>
        ArchiveAnalysisIdentity.ComputeLegacyFingerprint(package, selectedRoot);

    public Task<ArchiveAnalysisDraft> AnalyzeAsync(
        PackageRecord package,
        CancellationToken cancellationToken = default)
    {
        var afterHash = TestOnlyAfterHashBeforeEnumerate;
        var afterEntry = TestOnlyAfterEntryEnumerated;
        var afterRootDetection = TestOnlyAfterRootDetection;
        return Task.Run(
            () => AnalyzeCore(
                package,
                cancellationToken,
                afterHash,
                afterEntry,
                afterRootDetection,
                _resourcePolicy,
                _timeProvider),
            cancellationToken);
    }

    public ArchiveAnalysisDraft ApplySelectedRoot(
        PackageRecord package,
        PackageAnalysisRecord analysis,
        string selectedRoot) =>
        ArchiveAnalysisDraftBuilder.ApplySelectedRoot(
            package,
            analysis,
            selectedRoot);

    private static ArchiveAnalysisDraft AnalyzeCore(
        PackageRecord package,
        CancellationToken cancellationToken,
        Action? afterHashBeforeEnumerate,
        Action? afterEntryEnumerated,
        Action? afterRootDetection,
        ArchiveResourcePolicy resourcePolicy,
        TimeProvider timeProvider)
    {
        var budget = ArchiveResourceBudgetScope.ForAnalysis(
            resourcePolicy,
            timeProvider);
        var rawEntries = new List<ArchiveRawEntry>();
        string? contentSha = null;
        void CheckProcessingBoundary()
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.CheckDeadline();
        }
        try
        {
            CheckProcessingBoundary();
            using var stream = new FileStream(
                package.ArchivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                ArchiveContentHasher.BufferSize,
                FileOptions.SequentialScan);
            cancellationToken.ThrowIfCancellationRequested();
            contentSha = ArchiveContentHasher.ComputeStreamSha256(
                stream,
                cancellationToken,
                budget.CheckDeadline);
            stream.Position = 0;
            afterHashBeforeEnumerate?.Invoke();
            CheckProcessingBoundary();

            using var archive = ArchiveOpen.OpenStream(stream);
            CheckProcessingBoundary();
            var isMultiVolume = archive.Volumes.Skip(1).Any();
            CheckProcessingBoundary();
            byte[]? moduleConfigBytes = null;
            byte[]? infoXmlBytes = null;
            var hasDuplicateModuleConfig = false;
            var hasDuplicateInfoXml = false;

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                budget.ObserveDeclaredEntry(entry.Size, entry.CompressedSize, entry.IsDirectory);

                var isSafe = ArchivePathSafety.TryNormalizeArchivePath(entry.Key, out var normalized, out var pathWarning);
                var warning = string.IsNullOrWhiteSpace(entry.LinkTarget) ? pathWarning : "LinkEntry";

                rawEntries.Add(new ArchiveRawEntry(
                    entry.Key ?? string.Empty,
                    normalized,
                    entry.Size,
                    entry.IsDirectory,
                    entry.IsEncrypted,
                    entry.IsSplitAfter || !entry.IsComplete,
                    isSafe,
                    warning));
                if (isSafe && !entry.IsDirectory && !entry.IsEncrypted)
                {
                    if (FomodParser.IsFomodEntry(normalized))
                    {
                        if (moduleConfigBytes is not null)
                        {
                            hasDuplicateModuleConfig = true;
                        }
                        moduleConfigBytes = FomodService.ReadEntryBytes(entry, budget, cancellationToken);
                    }
                    else if (string.Equals(normalized.Replace('\\', '/').TrimStart('/'), FomodParser.InfoXmlPath, StringComparison.OrdinalIgnoreCase))
                    {
                        if (infoXmlBytes is not null)
                        {
                            hasDuplicateInfoXml = true;
                        }
                        infoXmlBytes = FomodService.ReadEntryBytes(entry, budget, cancellationToken);
                    }
                }
                afterEntryEnumerated?.Invoke();
                CheckProcessingBoundary();
            }

            if (hasDuplicateModuleConfig || hasDuplicateInfoXml)
            {
                return CreateBlocked(
                    package,
                    rawEntries,
                    ArchiveAnalyzerResultCodes.UnsupportedFomod,
                    contentSha: contentSha,
                    checkProcessingBoundary: CheckProcessingBoundary);
            }

            if (rawEntries.Count == 0)
            {
                return CreateBlocked(
                    package,
                    rawEntries,
                    ArchiveAnalyzerResultCodes.EmptyArchive,
                    contentSha: contentSha,
                    checkProcessingBoundary: CheckProcessingBoundary);
            }
            if (ContainsEntry(
                    rawEntries,
                    static entry => entry.IsEncrypted,
                    CheckProcessingBoundary))
            {
                return CreateBlocked(
                    package,
                    rawEntries,
                    ArchiveAnalyzerResultCodes.PasswordProtected,
                    contentSha: contentSha,
                    checkProcessingBoundary: CheckProcessingBoundary);
            }
            if (isMultiVolume ||
                !archive.IsComplete ||
                ContainsEntry(
                    rawEntries,
                    static entry => entry.IsSplit,
                    CheckProcessingBoundary))
            {
                return CreateBlocked(
                    package,
                    rawEntries,
                    ArchiveAnalyzerResultCodes.MultiVolumeOrIncomplete,
                    contentSha: contentSha,
                    checkProcessingBoundary: CheckProcessingBoundary);
            }
            if (ContainsEntry(
                    rawEntries,
                    static entry =>
                        !entry.IsSafe ||
                        string.Equals(
                            entry.WarningCode,
                            "LinkEntry",
                            StringComparison.Ordinal),
                    CheckProcessingBoundary))
            {
                return CreateBlocked(
                    package,
                    rawEntries,
                    ArchiveAnalyzerResultCodes.UnsafeEntry,
                    contentSha: contentSha,
                    checkProcessingBoundary: CheckProcessingBoundary);
            }

            if (moduleConfigBytes is not null)
            {
                var fomod = FomodParser.Parse(moduleConfigBytes, infoXmlBytes);
                if (!fomod.IsSupported)
                {
                    return CreateBlocked(
                        package,
                        rawEntries,
                        ArchiveAnalyzerResultCodes.UnsupportedFomod,
                        resultMessage: fomod.UnsupportedReason,
                        contentSha: contentSha,
                        checkProcessingBoundary: CheckProcessingBoundary);
                }
                if (fomod.Steps.Count == 0 && fomod.RequiredFiles.Count > 0)
                {
                    return FomodPlanner.BuildDraft(
                        package,
                        fomod,
                        new HashSet<string>(),
                        rawEntries,
                        contentSha!);
                }
                return ArchiveAnalysisDraftBuilder.CreateFomodSelectionRequiredDraft(
                    package,
                    rawEntries,
                    fomod,
                    contentSha!,
                    CheckProcessingBoundary);
            }

            return ArchiveAnalysisDraftBuilder.MapDetectedRoot(
                package,
                rawEntries,
                contentSha,
                CheckProcessingBoundary,
                afterRootDetection);
        }
        catch (ArchiveResourceBudgetException exception)
        {
            return CreateBlocked(
                package,
                rawEntries,
                exception.Code,
                exception.Message,
                contentSha);
        }
        catch (System.Security.Cryptography.CryptographicException exception)
        {
            return CreateFailureDraftRespectingBudget(
                package,
                rawEntries,
                PackageAnalysisState.Blocked,
                ArchiveAnalyzerResultCodes.PasswordProtected,
                exception.Message,
                contentSha,
                CheckProcessingBoundary);
        }
        catch (SharpCompress.Common.CryptographicException exception)
        {
            return CreateFailureDraftRespectingBudget(
                package,
                rawEntries,
                PackageAnalysisState.Blocked,
                ArchiveAnalyzerResultCodes.PasswordProtected,
                exception.Message,
                contentSha,
                CheckProcessingBoundary);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return CreateFailureDraftRespectingBudget(
                package,
                rawEntries,
                PackageAnalysisState.Error,
                ArchiveAnalyzerResultCodes.CorruptArchive,
                exception.Message,
                contentSha,
                CheckProcessingBoundary);
        }
    }

    private static ArchiveAnalysisDraft CreateBlocked(
        PackageRecord package,
        IReadOnlyList<ArchiveRawEntry> entries,
        string resultCode,
        string? resultMessage = null,
        string? contentSha = null,
        Action? checkProcessingBoundary = null) =>
        ArchiveAnalysisDraftBuilder.CreateDraft(
            package,
            PackageAnalysisState.Blocked,
            entries,
            [],
            resultCode,
            resultMessage,
            contentSha,
            checkProcessingBoundary);

    private static bool ContainsEntry(
        IEnumerable<ArchiveRawEntry> entries,
        Func<ArchiveRawEntry, bool> predicate,
        Action checkProcessingBoundary)
    {
        foreach (var entry in entries)
        {
            checkProcessingBoundary();
            if (predicate(entry))
            {
                checkProcessingBoundary();
                return true;
            }
        }
        checkProcessingBoundary();
        return false;
    }

    private static ArchiveAnalysisDraft CreateFailureDraftRespectingBudget(
        PackageRecord package,
        IReadOnlyList<ArchiveRawEntry> entries,
        PackageAnalysisState state,
        string resultCode,
        string? resultMessage,
        string? contentSha,
        Action checkProcessingBoundary)
    {
        try
        {
            return ArchiveAnalysisDraftBuilder.CreateDraft(
                package,
                state,
                entries,
                [],
                resultCode,
                resultMessage,
                contentSha,
                checkProcessingBoundary);
        }
        catch (ArchiveResourceBudgetException deadline)
        {
            return CreateBlocked(
                package,
                entries,
                deadline.Code,
                deadline.Message,
                contentSha);
        }
    }
}
