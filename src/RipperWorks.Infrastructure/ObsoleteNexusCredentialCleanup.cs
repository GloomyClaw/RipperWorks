namespace RipperWorks.Infrastructure;

/// <summary>
/// One-way removal of obsolete Personal API Key files. The payloads are never
/// opened, read, hashed, decrypted, validated, copied, or migrated.
/// </summary>
public static class ObsoleteNexusCredentialCleanup
{
    public const string CleanupId = "nexus-personal-api-key-removal.001";
    public const string MarkerFileName =
        "nexus-personal-api-key-removal.001.completed";

    public static async Task<IReadOnlyList<string>> RunAsync(
        RipperWorksPaths paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        cancellationToken.ThrowIfCancellationRequested();

        var markerPath = Path.Combine(paths.DataRoot, MarkerFileName);
        if (File.Exists(markerPath))
            return [];

        var failures = new List<string>();
        foreach (var target in GetObsoletePaths(paths))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Delete(target);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{Path.GetFileName(target)}: {exception.Message}");
            }
        }

        if (failures.Count != 0)
            return failures;

        var temporaryMarker = markerPath + ".tmp";
        var markerFailure = await TryPublishCompletionMarkerAsync(
            temporaryMarker,
            markerPath,
            cancellationToken).ConfigureAwait(false);
        if (markerFailure is not null)
            failures.Add(markerFailure);

        return failures;
    }

    private static async Task<string?> TryPublishCompletionMarkerAsync(
        string temporaryMarker,
        string markerPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await File.WriteAllTextAsync(
                temporaryMarker,
                CleanupId,
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryMarker, markerPath, overwrite: true);
            return null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            TryDeleteTemporaryMarker(temporaryMarker);
            return $"{MarkerFileName}: {exception.Message}";
        }
    }

    private static void TryDeleteTemporaryMarker(string temporaryMarker)
    {
        try
        {
            File.Delete(temporaryMarker);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Marker bookkeeping is non-authoritative and best-effort.
        }
    }

    internal static IReadOnlyList<string> GetObsoletePaths(
        RipperWorksPaths paths)
    {
        var current = Path.Combine(paths.DataRoot, "nexus.key");
        var defaultDataRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "RipperWorks"));
        var legacy = string.Equals(
                Path.GetFullPath(paths.DataRoot),
                defaultDataRoot,
                StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "ModDownloader",
                "nexus.key")
            : Path.Combine(
                paths.DataRoot,
                "legacy-moddownloader",
                "nexus.key");
        var migrationState = Path.Combine(
            paths.DataRoot,
            "credential-migration.json");

        return
        [
            current,
            current + ".tmp",
            legacy,
            legacy + ".tmp",
            migrationState,
            migrationState + ".tmp"
        ];
    }
}
