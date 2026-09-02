namespace RipperWorks.Downloader;

internal static class PublicationPathSafety
{
    private static readonly AsyncLocal<Func<string, bool>?>
        ReparsePointDetector = new();

    internal static Func<string, bool>? TestOnlyReparsePointDetector
    {
        get => ReparsePointDetector.Value;
        set => ReparsePointDetector.Value = value;
    }

    internal static string NormalizeRoot(string libraryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        return Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(libraryRoot));
    }

    internal static string RequireContainedPath(
        string libraryRoot,
        string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var root = NormalizeRoot(libraryRoot);
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Publication path escapes LibraryRoot.");
        }
        return fullPath;
    }

    internal static void CreateDirectories(string libraryRoot, string directory)
    {
        var root = NormalizeRoot(libraryRoot);
        var destination = RequireContainedOrEqual(root, directory);
        var existing = EnsureExistingAncestorsAreSafe(root);
        CreatePath(existing, root);
        CreatePath(root, destination);
    }

    internal static void RevalidateForMutation(
        string libraryRoot,
        params string[] paths)
    {
        var root = NormalizeRoot(libraryRoot);
        _ = EnsureExistingAncestorsAreSafe(root);
        foreach (var path in paths)
        {
            var full = RequireContainedPath(root, path);
            var current = File.Exists(full) || Directory.Exists(full)
                ? full
                : Path.GetDirectoryName(full)!;
            while (current.Length >= root.Length)
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    IsReparsePoint(current))
                {
                    throw new InvalidDataException(
                        $"Reparse point is not allowed: {current}");
                }
                if (string.Equals(
                        current,
                        root,
                        StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                current = Path.GetDirectoryName(current) ??
                    throw new InvalidDataException(
                        "Publication path escaped LibraryRoot.");
            }
        }
    }

    internal static void RevalidateOwnedDownloadSource(
        Guid entryId,
        string recordTemporaryPath,
        string completedSourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordTemporaryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(completedSourcePath);
        var temporary = Path.GetFullPath(recordTemporaryPath);
        var source = Path.GetFullPath(completedSourcePath);
        var root = Path.GetDirectoryName(temporary) ??
            throw new InvalidDataException(
                "Downloader temporary path has no directory.");
        if (!string.Equals(
                root,
                Path.GetDirectoryName(source),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new PublicationConflictException(
                "PublicationSourceOwnershipMismatch",
                "Completed source is outside its downloader job directory.");
        }
        var prefix = entryId.ToString("N");
        var temporaryName = Path.GetFileName(temporary);
        var sourceName = Path.GetFileName(source);
        if (!temporaryName.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase) ||
            !temporaryName.EndsWith(
                ".part",
                StringComparison.OrdinalIgnoreCase) ||
            !sourceName.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase) ||
            sourceName.EndsWith(
                ".part",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new PublicationConflictException(
                "PublicationSourceOwnershipMismatch",
                "Completed source does not match downloader job identity.");
        }
        _ = EnsureExistingAncestorsAreSafe(root);
        RevalidateForMutation(root, temporary, source);
    }

    private static string RequireContainedOrEqual(string root, string path)
    {
        var full = Path.GetFullPath(path);
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            return full;
        return RequireContainedPath(root, full);
    }

    private static string EnsureExistingAncestorsAreSafe(string root)
    {
        var existing = root;
        while (!Directory.Exists(existing))
        {
            existing = Path.GetDirectoryName(existing) ??
                throw new InvalidDataException("LibraryRoot has no parent.");
        }
        var volume = Path.GetPathRoot(existing)!;
        var current = volume;
        var relative = Path.GetRelativePath(volume, existing);
        foreach (var segment in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (IsReparsePoint(current))
            {
                throw new InvalidDataException(
                    $"Reparse point is not allowed: {current}");
            }
        }
        return existing;
    }

    private static void CreatePath(string parent, string destination)
    {
        if (string.Equals(
                parent,
                destination,
                StringComparison.OrdinalIgnoreCase))
        {
            CreateOne(destination);
            return;
        }
        var current = parent;
        var relative = Path.GetRelativePath(parent, destination);
        foreach (var segment in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            CreateOne(current);
        }
    }

    private static void CreateOne(string directory)
    {
        if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory);
        if (IsReparsePoint(directory))
        {
            throw new InvalidDataException(
                $"Reparse point is not allowed: {directory}");
        }
    }

    private static bool IsReparsePoint(string path) =>
        TestOnlyReparsePointDetector?.Invoke(path) ??
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
