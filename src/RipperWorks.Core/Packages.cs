using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RipperWorks.Core;

public readonly record struct PackageId(string Value)
{
    public static PackageId CreateTemporary(
        string archivePath,
        long fileSize,
        DateTime lastWriteUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        if (fileSize < 0)
            throw new ArgumentOutOfRangeException(nameof(fileSize));

        var normalizedPath = Path.GetFullPath(archivePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .ToUpperInvariant();
        var normalizedTimestamp = lastWriteUtc.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(lastWriteUtc, DateTimeKind.Utc)
            : lastWriteUtc.ToUniversalTime();
        var identity = string.Create(
            CultureInfo.InvariantCulture,
            $"{normalizedPath}\n{fileSize}\n{normalizedTimestamp.Ticks}");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return new($"tmp-{Convert.ToHexString(hash).ToLowerInvariant()}");
    }

    public override string ToString() => Value;
}

public readonly record struct LibraryModId(string Value)
{
    public override string ToString() => Value;
}

public enum PackageSource
{
    Local,
    Nexus,
    Other,
    Manual
}

public enum PackageAnalysisState
{
    NotAnalyzed,
    Analyzing,
    Ready,
    RequiresSelection,
    Blocked,
    Error,
    Stale
}

public enum PackageInstallationState
{
    NotInstalled = 0,
    Installed = 1,
    PartiallyInstalled = 2,
    Unknown = 3
}

public sealed record NexusIdentity(
    long? ModId,
    long? FileId,
    string? Url);

public sealed record PackageMetadata
{
    public string? DisplayName { get; init; }
    public PackageSource Source { get; init; } = PackageSource.Local;
    public string? GameDomain { get; init; }
    public NexusIdentity Nexus { get; init; } = new(null, null, null);
    public string? NexusFileUuid { get; init; }
    public string? ArchiveFamilyKey { get; init; }
    public string? Version { get; init; }
    public string? Author { get; init; }
    public string? Category { get; init; }
    public string? Sha256 { get; init; }
    public string MetadataSource { get; init; } = PackageMetadataSources.None;
    public int MetadataSchemaVersion { get; init; }
    public bool HasMetadata { get; init; }
}

public static class PackageMetadataSources
{
    public const string None = "None";
    public const string Corrupt = "Corrupt";
    public const string Unknown = "Unknown";
    public const string LegacyDownloaderPascalCase = "LegacyDownloaderPascalCase";
    public const string LegacyDownloaderCamelCase = "LegacyDownloaderCamelCase";
}

public sealed record PackageRecord
{
    public required PackageId PackageId { get; init; }
    public required string ArchivePath { get; init; }
    public required string DisplayName { get; init; }
    public required string ArchiveFileName { get; init; }
    public required string ArchiveFormat { get; init; }
    public long FileSize { get; init; }
    public DateTime LastWriteUtc { get; init; }
    public PackageSource Source { get; init; } = PackageSource.Local;
    public LibraryModId? LibraryModId { get; init; }
    public string? GameDomain { get; init; }
    public long? NexusModId { get; init; }
    public long? NexusFileId { get; init; }
    public string? NexusUrl { get; init; }
    public string? NexusFileUuid { get; init; }
    public string? ArchiveFamilyKey { get; init; }
    public string? Version { get; init; }
    public string? Author { get; init; }
    public string? Category { get; init; }
    public string? Sha256 { get; init; }
    public DateTime? DownloadedAtUtc { get; init; }
    public string MetadataSource { get; init; } = PackageMetadataSources.None;
    public int MetadataSchemaVersion { get; init; }
    public bool HasMetadata { get; init; }
    public PackageAnalysisState AnalysisState { get; init; } =
        PackageAnalysisState.NotAnalyzed;
    public PackageInstallationState InstallationState { get; init; } =
        PackageInstallationState.NotInstalled;
}

public sealed record LibraryIndexResult(
    IReadOnlyList<PackageRecord> Packages,
    int SkippedDirectoryCount);
