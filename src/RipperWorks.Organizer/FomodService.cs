using RipperWorks.Core;
using SharpCompress.Archives;

namespace RipperWorks.Organizer;

public sealed class FomodService
{
    public const long MaxFomodXmlSizeBytes = 2 * 1024 * 1024; // 2 MB ceiling for XML metadata

    private readonly ArchiveResourcePolicy _resourcePolicy;
    private readonly TimeProvider _timeProvider;

    public FomodService()
        : this(ArchiveResourcePolicy.Production, TimeProvider.System)
    {
    }

    internal FomodService(
        ArchiveResourcePolicy resourcePolicy,
        TimeProvider timeProvider)
    {
        _resourcePolicy = resourcePolicy ?? throw new ArgumentNullException(nameof(resourcePolicy));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<FomodDefinition?> LoadFomodDefinitionAsync(
        OrganizerPackageRecord mod,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mod);
        if (!mod.IsPresent || !File.Exists(mod.Package.ArchivePath) || mod.Analysis is null)
            return null;

        if (!TryGetTrustedContentIdentity(mod.Analysis, mod.Package, out var identity))
            return null;
        var trustedSha = identity.ArchiveSha256;

        return await Task.Run(() =>
        {
            var budget = ArchiveResourceBudgetScope.ForAnalysis(_resourcePolicy, _timeProvider);
            using var stream = new FileStream(mod.Package.ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            var liveSha = ArchiveContentHasher.ComputeStreamSha256(stream, cancellationToken, budget.CheckDeadline);
            if (!string.Equals(liveSha, trustedSha, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            stream.Position = 0;

            using var archive = ArchiveOpen.OpenStream(stream);
            byte[]? moduleConfigBytes = null;
            byte[]? infoXmlBytes = null;
            var hasDuplicateModuleConfig = false;
            var hasDuplicateInfoXml = false;

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                budget.ObserveDeclaredEntry(entry.Size, entry.CompressedSize, entry.IsDirectory);

                if (!entry.IsDirectory && !entry.IsEncrypted &&
                    ArchivePathSafety.TryNormalizeArchivePath(entry.Key, out var normalized, out _))
                {
                    if (FomodParser.IsFomodEntry(normalized))
                    {
                        if (moduleConfigBytes is not null) hasDuplicateModuleConfig = true;
                        moduleConfigBytes = ReadEntryBytes(entry, budget, cancellationToken);
                    }
                    else if (string.Equals(normalized.Replace('\\', '/').TrimStart('/'), FomodParser.InfoXmlPath, StringComparison.OrdinalIgnoreCase))
                    {
                        if (infoXmlBytes is not null) hasDuplicateInfoXml = true;
                        infoXmlBytes = ReadEntryBytes(entry, budget, cancellationToken);
                    }
                }
            }

            if (hasDuplicateModuleConfig || hasDuplicateInfoXml || moduleConfigBytes is null || moduleConfigBytes.Length == 0)
                return null;

            return FomodParser.Parse(moduleConfigBytes, infoXmlBytes);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OrganizerPackageRecord> ApplyFomodSelectionAsync(
        OrganizerPackageRecord mod,
        ISet<string> selectedOptionIds,
        OrganizerRepository repository,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(selectedOptionIds);
        ArgumentNullException.ThrowIfNull(repository);

        if (mod.Analysis is null)
            throw new InvalidOperationException("The package has not been analyzed.");

        if (!TryGetTrustedContentIdentity(mod.Analysis, mod.Package, out var identity))
            throw new InvalidOperationException("Untrusted or legacy analysis identity. Reanalysis required.");
        var trustedSha = identity.ArchiveSha256;

        var draft = await Task.Run(() =>
        {
            var budget = ArchiveResourceBudgetScope.ForAnalysis(_resourcePolicy, _timeProvider);
            using var stream = new FileStream(mod.Package.ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            var liveSha = ArchiveContentHasher.ComputeStreamSha256(stream, cancellationToken, budget.CheckDeadline);
            if (!string.Equals(liveSha, trustedSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The archive content changed since analysis. Reanalysis required.");
            }
            stream.Position = 0;

            using var archive = ArchiveOpen.OpenStream(stream);
            var rawEntries = new List<ArchiveRawEntry>();
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
                        if (moduleConfigBytes is not null) hasDuplicateModuleConfig = true;
                        moduleConfigBytes = ReadEntryBytes(entry, budget, cancellationToken);
                    }
                    else if (string.Equals(normalized.Replace('\\', '/').TrimStart('/'), FomodParser.InfoXmlPath, StringComparison.OrdinalIgnoreCase))
                    {
                        if (infoXmlBytes is not null) hasDuplicateInfoXml = true;
                        infoXmlBytes = ReadEntryBytes(entry, budget, cancellationToken);
                    }
                }
            }

            if (hasDuplicateModuleConfig || hasDuplicateInfoXml)
                throw new InvalidOperationException("The archive contains duplicate FOMOD definition entries.");

            if (moduleConfigBytes is null || moduleConfigBytes.Length == 0)
                throw new InvalidOperationException("The archive does not contain a valid FOMOD definition.");

            var fomod = FomodParser.Parse(moduleConfigBytes, infoXmlBytes);
            return FomodPlanner.BuildDraft(mod.Package, fomod, selectedOptionIds, rawEntries, liveSha);
        }, cancellationToken).ConfigureAwait(false);

        var updatedRecord = await repository.SaveAnalysisAsync(draft, cancellationToken).ConfigureAwait(false);
        return mod with { Analysis = updatedRecord };
    }

    internal static byte[] ReadEntryBytes(
        IArchiveEntry entry,
        ArchiveResourceBudgetScope budget,
        CancellationToken cancellationToken)
    {
        budget.CheckDeadline();
        if (entry.Size > MaxFomodXmlSizeBytes)
        {
            throw new ArchiveResourceBudgetException(
                ArchiveResourceResultCodes.EntryDeclaredSizeExceeded,
                "FOMOD XML entry declared size exceeds the 2MB metadata ceiling.");
        }

        using var stream = entry.OpenEntryStream();
        using var memory = new MemoryStream();
        var buffer = new byte[4096];
        long entryTotal = 0;
        int bytesRead;
        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.ObserveActualBytes(ref entryTotal, bytesRead, entry.Size);
            if (entryTotal > MaxFomodXmlSizeBytes)
            {
                throw new ArchiveResourceBudgetException(
                    ArchiveResourceResultCodes.EntryDeclaredSizeExceeded,
                    "FOMOD XML actual size exceeds the 2MB metadata ceiling.");
            }
            memory.Write(buffer, 0, bytesRead);
        }
        budget.CompleteActualEntry(entryTotal, entry.Size);
        return memory.ToArray();
    }

    private static bool TryGetTrustedContentIdentity(
        PackageAnalysisRecord analysis,
        PackageRecord package,
        out ArchiveContentIdentity identity)
    {
        identity = default!;
        if (analysis is null || package is null) return false;
        if (analysis.AnalyzerVersion != ArchiveAnalyzer.AnalyzerVersion)
        {
            return false;
        }
        if (analysis.PackageId != package.PackageId)
        {
            return false;
        }
        if (!ArchiveContentIdentity.TryParse(analysis.Fingerprint, out identity))
        {
            return false;
        }
        if (identity.PackageId != package.PackageId ||
            identity.PackageId != analysis.PackageId)
        {
            return false;
        }
        if (identity.AnalyzerVersion != ArchiveAnalyzer.AnalyzerVersion ||
            identity.PolicyVersion != ArchiveTrustPolicy.Version)
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(identity.ArchiveSha256))
        {
            return false;
        }
        return true;
    }
}
