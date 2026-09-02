using RipperWorks.Core;

namespace RipperWorks.Organizer;

internal sealed record ArchiveRawEntry(
    string OriginalPath,
    string NormalizedPath,
    long Size,
    bool IsDirectory,
    bool IsEncrypted,
    bool IsSplit,
    bool IsSafe,
    string? WarningCode);

internal static class ArchiveAnalysisDraftBuilder
{
    private static readonly HashSet<string> GameRoots =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "archive",
            "bin",
            "engine",
            "r6",
            "red4ext",
            "tools",
            "mods"
        };

    private static readonly HashSet<string> ArchiveExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".zip",
            ".7z",
            ".rar"
        };

    public static ArchiveAnalysisDraft ApplySelectedRoot(
        PackageRecord package,
        PackageAnalysisRecord analysis,
        string selectedRoot)
    {
        var identity = RequireCurrentIdentity(package, analysis, selectedRoot);
        var entries = analysis.Entries.Select(entry =>
        {
            var candidate = DetectCandidate(entry.NormalizedPath);
            var belongsToRoot = candidate is not null &&
                string.Equals(
                    candidate.Root,
                    selectedRoot,
                    StringComparison.OrdinalIgnoreCase);
            return entry with
            {
                Id = 0,
                RelativeInstallPath = belongsToRoot
                    ? candidate!.RelativePath
                    : null,
                IsInstallable = belongsToRoot,
                WarningCode = belongsToRoot
                    ? null
                    : "OutsideSelectedRoot"
            };
        }).ToArray();

        var resultCode = default(string);
        var state = PackageAnalysisState.Ready;
        var duplicate = FindDuplicate(entries);
        if (duplicate is not null)
        {
            state = PackageAnalysisState.Blocked;
            resultCode = ArchiveAnalyzerResultCodes.DuplicateInstallPath;
            entries = DisableDuplicate(entries, duplicate);
        }
        else if (!entries.Any(entry =>
                     entry.IsInstallable && !entry.IsDirectory))
        {
            state = PackageAnalysisState.Blocked;
            resultCode = ArchiveAnalyzerResultCodes.NoInstallableFiles;
        }

        var fingerprint = ArchiveAnalyzer.BuildContentFingerprint(
            package,
            identity.ArchiveSha256,
            selectedRoot);
        return new ArchiveAnalysisDraft
        {
            Package = package with { Sha256 = identity.ArchiveSha256 },
            AnalyzerVersion = ArchiveAnalyzer.AnalyzerVersion,
            State = state,
            DetectedRoot = selectedRoot,
            SelectedRoot = selectedRoot,
            DetectedRoots = analysis.DetectedRoots,
            Entries = entries,
            ResultCode = resultCode,
            Fingerprint = fingerprint,
            ArchiveSha256 = identity.ArchiveSha256,
            PolicyVersion = ArchiveTrustPolicy.Version
        };
    }

    public static ArchiveAnalysisDraft CreateFomodSelectionRequiredDraft(
        PackageRecord package,
        IReadOnlyList<ArchiveRawEntry> rawEntries,
        FomodDefinition fomod,
        string contentSha,
        Action checkProcessingBoundary)
    {
        checkProcessingBoundary();
        var sha = ArchiveTrustPolicy.CanonicalizeSha256(contentSha)!;
        var fingerprint = ArchiveAnalyzer.BuildContentFingerprint(package, sha, "FOMOD");

        var entries = rawEntries.Select(raw => new ArchiveAnalysisEntry
        {
            OriginalPath = raw.OriginalPath,
            NormalizedPath = raw.NormalizedPath,
            EntrySize = raw.Size,
            IsDirectory = raw.IsDirectory,
            IsInstallable = false,
            WarningCode = FomodParser.IsFomodMetadataEntry(raw.NormalizedPath) ? "FomodMetadata" : "FomodSelectionRequired"
        }).ToArray();

        return new ArchiveAnalysisDraft
        {
            Package = package with { Sha256 = sha },
            AnalyzerVersion = ArchiveAnalyzer.AnalyzerVersion,
            State = PackageAnalysisState.RequiresSelection,
            DetectedRoot = "FOMOD",
            SelectedRoot = "FOMOD",
            DetectedRoots = ["FOMOD"],
            Entries = entries,
            ResultCode = ArchiveAnalyzerResultCodes.FomodSelectionRequired,
            ResultMessage = fomod.ModuleName,
            Fingerprint = fingerprint,
            ArchiveSha256 = sha,
            PolicyVersion = ArchiveTrustPolicy.Version
        };
    }

    public static ArchiveAnalysisDraft MapDetectedRoot(
        PackageRecord package,
        IReadOnlyList<ArchiveRawEntry> rawEntries,
        string contentSha,
        Action checkProcessingBoundary,
        Action? afterRootDetection)
    {
        checkProcessingBoundary();
        var candidates = CheckEach(rawEntries, checkProcessingBoundary)
            .Where(entry => !entry.IsDirectory)
            .Select(entry => DetectCandidate(entry.NormalizedPath))
            .Where(candidate => candidate is not null)
            .Cast<RootCandidate>()
            .ToArray();
        checkProcessingBoundary();
        var roots = candidates
            .Select(candidate => candidate.Root)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(root => root, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        afterRootDetection?.Invoke();
        checkProcessingBoundary();

        if (roots.Length == 0)
        {
            var files = CheckEach(rawEntries, checkProcessingBoundary)
                .Where(entry => !entry.IsDirectory)
                .ToArray();
            var nestedArchiveOnly = files.Length > 0 &&
                files.All(entry => ArchiveExtensions.Contains(
                    Path.GetExtension(entry.NormalizedPath)));
            return CreateDraft(
                package,
                PackageAnalysisState.Blocked,
                rawEntries,
                [],
                nestedArchiveOnly
                    ? ArchiveAnalyzerResultCodes.NestedArchiveOnly
                    : ArchiveAnalyzerResultCodes.UnknownRoot,
                contentSha: contentSha,
                checkProcessingBoundary: checkProcessingBoundary);
        }

        if (roots.Length > 1)
        {
            return CreateDraft(
                package,
                PackageAnalysisState.RequiresSelection,
                rawEntries,
                roots,
                ArchiveAnalyzerResultCodes.MultipleInstallRoots,
                contentSha: contentSha,
                checkProcessingBoundary: checkProcessingBoundary);
        }

        return MapSingleRoot(
            package,
            rawEntries,
            roots[0],
            contentSha,
            checkProcessingBoundary);
    }

    public static ArchiveAnalysisDraft CreateDraft(
        PackageRecord package,
        PackageAnalysisState state,
        IEnumerable<ArchiveRawEntry> rawEntries,
        IReadOnlyList<string> roots,
        string resultCode,
        string? resultMessage = null,
        string? contentSha = null,
        Action? checkProcessingBoundary = null)
    {
        checkProcessingBoundary?.Invoke();
        string fingerprint;
        var sha = ArchiveTrustPolicy.CanonicalizeSha256(contentSha);
        var packageForDraft = package;
        if (sha is not null)
        {
            fingerprint = ArchiveAnalyzer.BuildContentFingerprint(
                package,
                sha,
                roots.Count == 1 ? roots[0] : null);
            packageForDraft = package with { Sha256 = sha };
        }
        else
        {
            fingerprint = ArchiveAnalyzer.ComputeLegacyFingerprint(package, null);
        }

        var draft = new ArchiveAnalysisDraft
        {
            Package = packageForDraft,
            AnalyzerVersion = ArchiveAnalyzer.AnalyzerVersion,
            State = state,
            DetectedRoots = roots,
            Entries = CheckEach(rawEntries, checkProcessingBoundary)
                .Select(entry => new ArchiveAnalysisEntry
            {
                OriginalPath = entry.OriginalPath,
                NormalizedPath = entry.NormalizedPath,
                EntrySize = entry.Size,
                IsDirectory = entry.IsDirectory,
                IsInstallable = false,
                WarningCode = entry.WarningCode ?? resultCode
            }).ToArray(),
            ResultCode = resultCode,
            ResultMessage = resultMessage,
            Fingerprint = fingerprint,
            ArchiveSha256 = sha,
            PolicyVersion = ArchiveTrustPolicy.Version
        };
        checkProcessingBoundary?.Invoke();
        return draft;
    }

    private static ArchiveAnalysisDraft MapSingleRoot(
        PackageRecord package,
        IEnumerable<ArchiveRawEntry> rawEntries,
        string root,
        string contentSha,
        Action checkProcessingBoundary)
    {
        var entries = CheckEach(rawEntries, checkProcessingBoundary).Select(entry =>
        {
            checkProcessingBoundary();
            var candidate = DetectCandidate(entry.NormalizedPath);
            var belongsToRoot = candidate is not null &&
                string.Equals(
                    candidate.Root,
                    root,
                    StringComparison.OrdinalIgnoreCase);
            return new ArchiveAnalysisEntry
            {
                OriginalPath = entry.OriginalPath,
                NormalizedPath = entry.NormalizedPath,
                RelativeInstallPath = belongsToRoot
                    ? candidate!.RelativePath
                    : null,
                EntrySize = entry.Size,
                IsDirectory = entry.IsDirectory,
                IsInstallable = belongsToRoot,
                WarningCode = belongsToRoot
                    ? entry.WarningCode
                    : "OutsideDetectedRoot"
            };
        }).ToArray();
        checkProcessingBoundary();

        var duplicate = FindDuplicate(entries, checkProcessingBoundary);
        checkProcessingBoundary();
        if (duplicate is not null)
        {
            var duplicateDraft = CreateMappedDraft(
                package,
                PackageAnalysisState.Blocked,
                root,
                DisableDuplicate(entries, duplicate),
                ArchiveAnalyzerResultCodes.DuplicateInstallPath,
                contentSha);
            checkProcessingBoundary();
            return duplicateDraft;
        }
        if (!CheckEach(entries, checkProcessingBoundary).Any(entry =>
                entry.IsInstallable && !entry.IsDirectory))
        {
            var emptyDraft = CreateMappedDraft(
                package,
                PackageAnalysisState.Blocked,
                root,
                entries,
                ArchiveAnalyzerResultCodes.NoInstallableFiles,
                contentSha);
            checkProcessingBoundary();
            return emptyDraft;
        }
        var result = CreateMappedDraft(
            package,
            PackageAnalysisState.Ready,
            root,
            entries,
            null,
            contentSha);
        checkProcessingBoundary();
        return result;
    }

    private static ArchiveAnalysisDraft CreateMappedDraft(
        PackageRecord package,
        PackageAnalysisState state,
        string root,
        IReadOnlyList<ArchiveAnalysisEntry> entries,
        string? resultCode,
        string contentSha)
    {
        var sha = ArchiveTrustPolicy.CanonicalizeSha256(contentSha)!;
        return new ArchiveAnalysisDraft
        {
            Package = package with { Sha256 = sha },
            AnalyzerVersion = ArchiveAnalyzer.AnalyzerVersion,
            State = state,
            DetectedRoot = root,
            SelectedRoot = root,
            DetectedRoots = [root],
            Entries = entries,
            ResultCode = resultCode,
            Fingerprint = ArchiveAnalyzer.BuildContentFingerprint(
                package, sha, root),
            ArchiveSha256 = sha,
            PolicyVersion = ArchiveTrustPolicy.Version
        };
    }

    private static ArchiveContentIdentity RequireCurrentIdentity(
        PackageRecord package,
        PackageAnalysisRecord analysis,
        string selectedRoot)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedRoot);
        if (!ArchiveAnalyzer.IsCurrent(analysis, package))
            throw new InvalidOperationException("The archive changed after analysis.");
        if (!analysis.DetectedRoots.Contains(
                selectedRoot,
                StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The selected root is not part of the current analysis.");
        }
        if (!ArchiveContentIdentity.TryParse(analysis.Fingerprint, out var identity))
        {
            throw new InvalidOperationException(
                "Legacy analysis cannot authorize root selection for install. Reanalyze first.");
        }
        return identity;
    }

    private static string? FindDuplicate(
        IEnumerable<ArchiveAnalysisEntry> entries,
        Action? checkProcessingBoundary = null) =>
        CheckEach(entries, checkProcessingBoundary)
            .Where(entry =>
                entry.IsInstallable &&
                !entry.IsDirectory &&
                entry.RelativeInstallPath is not null)
            .GroupBy(
                entry => entry.RelativeInstallPath!,
                StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)
            ?.Key;

    private static IEnumerable<T> CheckEach<T>(
        IEnumerable<T> values,
        Action? checkProcessingBoundary)
    {
        foreach (var value in values)
        {
            checkProcessingBoundary?.Invoke();
            yield return value;
        }
        checkProcessingBoundary?.Invoke();
    }

    private static ArchiveAnalysisEntry[] DisableDuplicate(
        IEnumerable<ArchiveAnalysisEntry> entries,
        string duplicate) =>
        entries.Select(entry =>
            string.Equals(
                entry.RelativeInstallPath,
                duplicate,
                StringComparison.OrdinalIgnoreCase)
                ? entry with
                {
                    IsInstallable = false,
                    WarningCode = "DuplicateInstallPath"
                }
                : entry).ToArray();

    private static RootCandidate? DetectCandidate(string path)
    {
        var segments = path.Split(
            '\\',
            StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            if (!GameRoots.Contains(segments[index]))
                continue;
            return new RootCandidate(
                index == 0
                    ? "."
                    : string.Join('\\', segments.Take(index)),
                string.Join('\\', segments.Skip(index)));
        }
        return null;
    }

    private sealed record RootCandidate(
        string Root,
        string RelativePath);
}
