using RipperWorks.Core;

namespace RipperWorks.Organizer;

public static class FomodPlanner
{
    public static bool IsSafeRelativePath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return true;
        var p = rawPath.Replace('/', '\\');
        if (p.StartsWith("\\") || p.StartsWith("/")) return false;
        if (p.Length >= 2 && p[1] == ':') return false;
        if (!ArchivePathSafety.TryNormalizeArchivePath(rawPath, out var normalized, out var warning)) return false;
        if (!string.IsNullOrEmpty(warning) && (warning == "UnsafePath" || warning == "RootedPath" || warning == "LinkEntry")) return false;
        return true;
    }

    public static string? ValidateSelection(FomodDefinition fomod, ISet<string> selectedPluginIds)
    {
        ArgumentNullException.ThrowIfNull(fomod);
        ArgumentNullException.ThrowIfNull(selectedPluginIds);

        if (!fomod.IsSupported)
            return fomod.UnsupportedReason ?? "Unsupported FOMOD archive.";

        var activeFlags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var step in fomod.Steps)
        {
            foreach (var group in step.Groups)
            {
                foreach (var plugin in group.Plugins)
                {
                    if (selectedPluginIds.Contains(plugin.Id))
                    {
                        foreach (var flag in plugin.ConditionFlags)
                        {
                            activeFlags[flag.Name] = flag.Value;
                        }
                    }
                }
            }
        }

        foreach (var step in fomod.Steps)
        {
            foreach (var group in step.Groups)
            {
                var selectedCount = 0;
                foreach (var plugin in group.Plugins)
                {
                    var isSelected = selectedPluginIds.Contains(plugin.Id);
                    if (plugin.Type == FomodPluginType.Required && !isSelected)
                    {
                        return $"Required option '{plugin.Name}' in step '{step.Name}' must be selected.";
                    }
                    if (plugin.Type == FomodPluginType.NotUsable && isSelected)
                    {
                        return $"Option '{plugin.Name}' in step '{step.Name}' is not usable.";
                    }
                    if (isSelected)
                    {
                        selectedCount++;
                    }
                }

                switch (group.Type)
                {
                    case FomodGroupType.SelectExactlyOne:
                        if (selectedCount != 1)
                            return $"Step '{step.Name}', group '{group.Name}': exactly one option must be selected.";
                        break;

                    case FomodGroupType.SelectAtLeastOne:
                        if (selectedCount < 1)
                            return $"Step '{step.Name}', group '{group.Name}': at least one option must be selected.";
                        break;

                    case FomodGroupType.SelectAtMostOne:
                        if (selectedCount > 1)
                            return $"Step '{step.Name}', group '{group.Name}': at most one option can be selected.";
                        break;

                    case FomodGroupType.SelectAll:
                        if (selectedCount != group.Plugins.Count)
                            return $"Step '{step.Name}', group '{group.Name}': all options must be selected.";
                        break;
                }
            }
        }
        return null;
    }

    internal static ArchiveAnalysisDraft BuildDraft(
        PackageRecord package,
        FomodDefinition fomod,
        ISet<string> selectedPluginIds,
        IReadOnlyList<ArchiveRawEntry> rawEntries,
        string contentSha)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(fomod);
        ArgumentNullException.ThrowIfNull(selectedPluginIds);
        ArgumentNullException.ThrowIfNull(rawEntries);

        var validationError = ValidateSelection(fomod, selectedPluginIds);
        if (validationError is not null)
        {
            return ArchiveAnalysisDraftBuilder.CreateDraft(
                package,
                PackageAnalysisState.Blocked,
                rawEntries,
                [],
                ArchiveAnalyzerResultCodes.NoInstallableFiles,
                resultMessage: validationError,
                contentSha: contentSha);
        }

        var activeFlags = new Dictionary<string, string>(StringComparer.Ordinal);
        var chosenPlugins = new List<FomodPlugin>();
        foreach (var step in fomod.Steps)
        {
            foreach (var group in step.Groups)
            {
                foreach (var plugin in group.Plugins)
                {
                    if (selectedPluginIds.Contains(plugin.Id))
                    {
                        chosenPlugins.Add(plugin);
                        foreach (var flag in plugin.ConditionFlags)
                        {
                            activeFlags[flag.Name] = flag.Value;
                        }
                    }
                }
            }
        }

        var fileInstalls = new List<FomodFileInstall>(fomod.RequiredFiles);
        foreach (var plugin in chosenPlugins)
        {
            fileInstalls.AddRange(plugin.Files);
        }
        foreach (var cond in fomod.ConditionalInstalls)
        {
            if (AreDependenciesMet(cond.FlagDependencies, activeFlags))
            {
                fileInstalls.AddRange(cond.Files);
            }
        }

        foreach (var install in fileInstalls)
        {
            if (!IsSafeRelativePath(install.Source) || !IsSafeRelativePath(install.Destination))
            {
                return ArchiveAnalysisDraftBuilder.CreateDraft(
                    package,
                    PackageAnalysisState.Blocked,
                    rawEntries,
                    [],
                    ArchiveAnalyzerResultCodes.UnsafeEntry,
                    resultMessage: $"Unsafe path in FOMOD mapping: source '{install.Source}', destination '{install.Destination}'.",
                    contentSha: contentSha);
            }
        }

        foreach (var install in fileInstalls)
        {
            if (!DoesMappingExistInRawEntries(install, rawEntries))
            {
                return ArchiveAnalysisDraftBuilder.CreateDraft(
                    package,
                    PackageAnalysisState.Blocked,
                    rawEntries,
                    [],
                    ArchiveAnalyzerResultCodes.NoInstallableFiles,
                    resultMessage: $"Source '{install.Source}' required by FOMOD selection does not exist in archive.",
                    contentSha: contentSha);
            }
        }

        var destinationMappings = new Dictionary<string, (string Source, string Destination)>(StringComparer.OrdinalIgnoreCase);
        var mappedEntries = new List<ArchiveAnalysisEntry>();

        foreach (var raw in rawEntries)
        {
            if (raw.IsDirectory)
            {
                mappedEntries.Add(new ArchiveAnalysisEntry
                {
                    OriginalPath = raw.OriginalPath,
                    NormalizedPath = raw.NormalizedPath,
                    EntrySize = raw.Size,
                    IsDirectory = raw.IsDirectory,
                    IsInstallable = false,
                    WarningCode = null
                });
                continue;
            }

            var relInstallPaths = MatchAndMapPaths(raw.NormalizedPath, fileInstalls);

            if (relInstallPaths.Count == 0 && FomodParser.IsFomodMetadataEntry(raw.NormalizedPath))
            {
                mappedEntries.Add(new ArchiveAnalysisEntry
                {
                    OriginalPath = raw.OriginalPath,
                    NormalizedPath = raw.NormalizedPath,
                    EntrySize = raw.Size,
                    IsDirectory = raw.IsDirectory,
                    IsInstallable = false,
                    WarningCode = "FomodMetadata"
                });
                continue;
            }
            if (relInstallPaths.Count > 1)
            {
                return ArchiveAnalysisDraftBuilder.CreateDraft(
                    package,
                    PackageAnalysisState.Blocked,
                    rawEntries,
                    [],
                    ArchiveAnalyzerResultCodes.UnsupportedFomod,
                    resultMessage: $"FOMOD contains unsupported one-to-many mapping for source entry '{raw.NormalizedPath}'.",
                    contentSha: contentSha);
            }

            if (relInstallPaths.Count == 1)
            {
                var relInstallPath = relInstallPaths[0];
                if (destinationMappings.ContainsKey(relInstallPath))
                {
                    return ArchiveAnalysisDraftBuilder.CreateDraft(
                        package,
                        PackageAnalysisState.Blocked,
                        rawEntries,
                        [],
                        ArchiveAnalyzerResultCodes.UnsupportedFomod,
                        resultMessage: $"FOMOD contains duplicate destination mapping for '{relInstallPath}'.",
                        contentSha: contentSha);
                }
                destinationMappings[relInstallPath] = (raw.NormalizedPath, relInstallPath);

                mappedEntries.Add(new ArchiveAnalysisEntry
                {
                    OriginalPath = raw.OriginalPath,
                    NormalizedPath = raw.NormalizedPath,
                    RelativeInstallPath = relInstallPath,
                    EntrySize = raw.Size,
                    IsDirectory = false,
                    IsInstallable = true
                });
            }
            else
            {
                mappedEntries.Add(new ArchiveAnalysisEntry
                {
                    OriginalPath = raw.OriginalPath,
                    NormalizedPath = raw.NormalizedPath,
                    EntrySize = raw.Size,
                    IsDirectory = false,
                    IsInstallable = false
                });
            }
        }

        if (!mappedEntries.Any(e => e.IsInstallable && !e.IsDirectory))
        {
            return ArchiveAnalysisDraftBuilder.CreateDraft(
                package,
                PackageAnalysisState.Blocked,
                rawEntries,
                [],
                ArchiveAnalyzerResultCodes.NoInstallableFiles,
                resultMessage: "No installable files were mapped by the selected FOMOD options.",
                contentSha: contentSha);
        }

        var sha = ArchiveTrustPolicy.CanonicalizeSha256(contentSha)!;
        var fingerprint = ArchiveAnalyzer.BuildContentFingerprint(package, sha, "FOMOD");

        return new ArchiveAnalysisDraft
        {
            Package = package with { Sha256 = sha },
            AnalyzerVersion = ArchiveAnalyzer.AnalyzerVersion,
            State = PackageAnalysisState.Ready,
            DetectedRoot = "FOMOD",
            SelectedRoot = "FOMOD",
            DetectedRoots = ["FOMOD"],
            Entries = mappedEntries,
            ResultCode = null,
            Fingerprint = fingerprint,
            ArchiveSha256 = sha,
            PolicyVersion = ArchiveTrustPolicy.Version
        };
    }

    private static bool DoesMappingExistInRawEntries(FomodFileInstall install, IReadOnlyList<ArchiveRawEntry> rawEntries)
    {
        var normSource = install.Source.Replace('/', '\\').Trim('\\');
        if (string.IsNullOrEmpty(normSource)) return rawEntries.Any(e => !e.IsDirectory);

        if (install.IsFolder)
        {
            return rawEntries.Any(e => !e.IsDirectory &&
                e.NormalizedPath.Replace('/', '\\').Trim('\\').StartsWith(normSource + "\\", StringComparison.OrdinalIgnoreCase));
        }

        return rawEntries.Any(e => !e.IsDirectory &&
            string.Equals(e.NormalizedPath.Replace('/', '\\').Trim('\\'), normSource, StringComparison.OrdinalIgnoreCase));
    }

    private static bool AreDependenciesMet(
        IReadOnlyList<FomodFlagDependency> deps,
        IReadOnlyDictionary<string, string> activeFlags)
    {
        foreach (var dep in deps)
        {
            if (!activeFlags.TryGetValue(dep.Name, out var actualVal))
            {
                if (dep.Value.Length > 0)
                    return false;
                continue;
            }

            if (dep.Value.Length == 0)
            {
                if (actualVal.Length > 0)
                    return false;
            }
            else
            {
                if (!string.Equals(actualVal, dep.Value, StringComparison.Ordinal))
                    return false;
            }
        }
        return true;
    }

    private static List<string> MatchAndMapPaths(
        string entryPath,
        IReadOnlyList<FomodFileInstall> installs)
    {
        var normEntry = entryPath.Replace('/', '\\').Trim('\\');
        var results = new List<string>();

        var sortedInstalls = installs.OrderByDescending(i => i.Priority).ToList();
        foreach (var install in sortedInstalls)
        {
            var normSource = install.Source.Replace('/', '\\').Trim('\\');
            var normDest = install.Destination.Replace('/', '\\').Trim('\\');

            string? resultRelative = null;
            if (install.IsFolder)
            {
                if (!string.IsNullOrEmpty(normSource) &&
                    normEntry.StartsWith(normSource + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    var suffix = normEntry.Substring(normSource.Length + 1);
                    resultRelative = string.IsNullOrEmpty(normDest) ? suffix : Path.Combine(normDest, suffix);
                }
                else if (string.IsNullOrEmpty(normSource))
                {
                    resultRelative = string.IsNullOrEmpty(normDest) ? normEntry : Path.Combine(normDest, normEntry);
                }
            }
            else
            {
                if (string.Equals(normSource, normEntry, StringComparison.OrdinalIgnoreCase))
                {
                    resultRelative = normDest;
                }
                else if (string.IsNullOrEmpty(normSource))
                {
                    resultRelative = string.IsNullOrEmpty(normDest) ? normEntry : Path.Combine(normDest, normEntry);
                }
            }

            if (resultRelative is not null &&
                ArchivePathSafety.TryNormalizeArchivePath(resultRelative, out var normalized, out _))
            {
                if (!results.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                {
                    results.Add(normalized);
                }
            }
        }

        return results;
    }
}
