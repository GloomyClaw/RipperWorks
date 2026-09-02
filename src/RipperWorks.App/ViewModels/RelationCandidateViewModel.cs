using System;
using System.IO;
using System.Linq;
using RipperWorks.App.Services;
using RipperWorks.Core;
using RipperWorks.Organizer;

namespace RipperWorks.App.ViewModels;

public sealed record RelationCandidateViewModel
{
    public required PackageId PackageId { get; init; }
    public required LibraryModId LibraryModId { get; init; }
    public required string DisplayName { get; init; }
    public required string Component { get; init; }
    public long? NexusFileId { get; init; }
    public string? Version { get; init; }
    public required string Subtitle { get; init; }

    public static RelationCandidateViewModel Create(
        OrganizerPackageRecord archive,
        string displayName,
        LibraryModId libraryModId,
        LocalizationService localization)
    {
        var pkg = archive.Package;
        var name = new[]
        {
            pkg.ArchiveFileName,
            pkg.DisplayName,
            Path.GetFileName(pkg.ArchivePath)
        }.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ??
            localization.Get("ModCardLocalArchive");

        var component = pkg.NexusFileId is { } fileId
            ? string.Format(
                localization.Get("ModCardNexusComponentDescriptor"),
                name,
                fileId)
            : name;

        var effectiveVersion = archive.EffectiveVersion;
        var version = string.IsNullOrWhiteSpace(effectiveVersion) || effectiveVersion == "—"
            ? null
            : effectiveVersion;

        var subtitle = version is not null
            ? (version.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                ? $"{component} · {version}"
                : $"{component} · v{version}")
            : component;

        return new()
        {
            PackageId = pkg.PackageId,
            LibraryModId = libraryModId,
            DisplayName = displayName,
            Component = component,
            NexusFileId = pkg.NexusFileId,
            Version = version,
            Subtitle = subtitle
        };
    }
}
