using System.Text.Json;
using System.Text.Json.Serialization;

namespace RipperWorks.GameMaintenance;

public static class GameMaintenanceBackupStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string GetDefaultBackupRoot() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RipperWorks.Backups",
            "GameCleanup");

    public static async Task<string> CreateBackupAsync(
        string gameRoot,
        IReadOnlyList<GameMaintenanceCandidateFile> candidates,
        string? customBackupRoot = null,
        CancellationToken cancellationToken = default)
    {
        var backupRoot = customBackupRoot ?? GetDefaultBackupRoot();
        Directory.CreateDirectory(backupRoot);

        var backupId = Guid.NewGuid().ToString("N")[..8];
        var timestampStr = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ");
        var folderName = $"{timestampStr}-{backupId}";
        var backupDir = Path.Combine(backupRoot, folderName);
        var payloadDir = Path.Combine(backupDir, "payload");

        Directory.CreateDirectory(payloadDir);

        try
        {
            var manifestEntries = new List<GameMaintenanceBackupFileEntry>();

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var sourcePath = Path.Combine(gameRoot, candidate.RelativeGamePath.Replace('/', '\\'));
                if (!File.Exists(sourcePath))
                {
                    throw new FileNotFoundException(
                        $"Candidate file not found for backup: {candidate.RelativeGamePath}", sourcePath);
                }

                var targetPath = Path.Combine(payloadDir, candidate.RelativeGamePath.Replace('/', '\\'));
                var targetDir = Path.GetDirectoryName(targetPath)!;
                Directory.CreateDirectory(targetDir);

                File.Copy(sourcePath, targetPath, overwrite: true);

                var copiedHash = await GameMaintenanceScanner.ComputeSha256Async(targetPath, cancellationToken);
                if (!string.Equals(copiedHash, candidate.ObservedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Copied payload hash mismatch for {candidate.RelativeGamePath}. Expected {candidate.ObservedSha256}, got {copiedHash}");
                }

                manifestEntries.Add(new GameMaintenanceBackupFileEntry(
                    candidate.RelativeGamePath,
                    new FileInfo(targetPath).Length,
                    copiedHash));
            }

            var manifest = new GameMaintenanceBackupManifest(
                FormatVersion: 1,
                BackupId: backupId,
                CreatedAtUtc: DateTimeOffset.UtcNow,
                CanonicalGameRoot: Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot)),
                Files: manifestEntries);

            var manifestPath = Path.Combine(backupDir, "manifest.json");
            var json = JsonSerializer.Serialize(manifest, JsonOptions);
            await File.WriteAllTextAsync(manifestPath, json, cancellationToken);

            return backupId;
        }
        catch
        {
            try
            {
                if (Directory.Exists(backupDir))
                    Directory.Delete(backupDir, recursive: true);
            }
            catch { }

            throw;
        }
    }

    public static async Task<IReadOnlyList<GameMaintenanceBackupHeader>> ListBackupsAsync(
        string gameRoot,
        string? customBackupRoot = null,
        CancellationToken cancellationToken = default)
    {
        var backupRoot = customBackupRoot ?? GetDefaultBackupRoot();
        if (!Directory.Exists(backupRoot))
            return [];

        var canonicalGameRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot));
        var results = new List<GameMaintenanceBackupHeader>();

        foreach (var dir in Directory.EnumerateDirectories(backupRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath))
                continue;

            try
            {
                var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
                var manifest = JsonSerializer.Deserialize<GameMaintenanceBackupManifest>(json, JsonOptions);
                if (manifest is null || manifest.Files is null)
                    continue;

                if (!string.Equals(
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(manifest.CanonicalGameRoot)),
                        canonicalGameRoot,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                results.Add(new GameMaintenanceBackupHeader(
                    manifest.BackupId,
                    manifest.CreatedAtUtc,
                    manifest.CanonicalGameRoot,
                    manifest.Files.Count,
                    manifest.Files.Sum(f => f.ByteLength),
                    dir));
            }
            catch
            {
                // Ignore corrupt backup manifests
            }
        }

        results.Sort((a, b) => b.CreatedAtUtc.CompareTo(a.CreatedAtUtc));
        return results;
    }

    public static async Task<GameMaintenanceBackupManifest?> LoadManifestAsync(
        string backupId,
        string? customBackupRoot = null,
        CancellationToken cancellationToken = default)
    {
        var backupRoot = customBackupRoot ?? GetDefaultBackupRoot();
        if (!Directory.Exists(backupRoot))
            return null;

        foreach (var dir in Directory.EnumerateDirectories(backupRoot))
        {
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath))
                continue;

            try
            {
                var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
                var manifest = JsonSerializer.Deserialize<GameMaintenanceBackupManifest>(json, JsonOptions);
                if (manifest is not null && string.Equals(manifest.BackupId, backupId, StringComparison.OrdinalIgnoreCase))
                {
                    return manifest;
                }
            }
            catch { }
        }

        return null;
    }

    public static string? GetBackupDirectory(
        string backupId,
        string? customBackupRoot = null)
    {
        var backupRoot = customBackupRoot ?? GetDefaultBackupRoot();
        if (!Directory.Exists(backupRoot))
            return null;

        foreach (var dir in Directory.EnumerateDirectories(backupRoot))
        {
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath))
                continue;

            try
            {
                var json = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize<GameMaintenanceBackupManifest>(json, JsonOptions);
                if (manifest is not null && string.Equals(manifest.BackupId, backupId, StringComparison.OrdinalIgnoreCase))
                {
                    return dir;
                }
            }
            catch { }
        }

        return null;
    }
}
