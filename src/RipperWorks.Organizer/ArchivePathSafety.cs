namespace RipperWorks.Organizer;

public static class ArchivePathSafety
{
    private static readonly AsyncLocal<Func<string, bool>?>
        ReparsePointDetector = new();

    internal static Func<string, bool>? TestOnlyReparsePointDetector
    {
        get => ReparsePointDetector.Value;
        set => ReparsePointDetector.Value = value;
    }

    public static string ResolveSafeGamePath(
        string gameRoot,
        string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        if (!TryNormalizeArchivePath(
                relativePath,
                out var normalized,
                out var warningCode))
        {
            throw new InvalidDataException(
                $"Unsafe game path ({warningCode}): {relativePath}");
        }

        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(gameRoot));
        var destination = Path.GetFullPath(
            Path.Combine(root, normalized));
        var rootPrefix = root + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(
                rootPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Path escapes the game folder: {relativePath}");
        }
        return destination;
    }

    public static bool TryNormalizeArchivePath(
        string? archivePath,
        out string normalizedPath,
        out string? warningCode)
    {
        normalizedPath = string.Empty;
        warningCode = null;

        if (string.IsNullOrWhiteSpace(archivePath))
        {
            warningCode = "EmptyPath";
            return false;
        }

        var path = archivePath.Replace('/', '\\').Trim();
        if (path.StartsWith('\\') ||
            path.StartsWith("//", StringComparison.Ordinal) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) ||
            Path.IsPathRooted(path) ||
            (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':'))
        {
            warningCode = "AbsolutePath";
            return false;
        }

        var segments = path.Split(
            '\\',
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            warningCode = "EmptyPath";
            return false;
        }

        if (segments.Any(segment =>
                segment is ".." or "." ||
                segment.Contains(':') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            warningCode = "PathTraversal";
            return false;
        }

        normalizedPath = string.Join('\\', segments);
        return true;
    }

    internal static void EnsureNoReparsePoints(
        string root,
        string path,
        bool allowMissing = false)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(root));
        var current = Path.GetFullPath(path);
        while (current.Length >= normalizedRoot.Length)
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if (IsReparsePoint(current))
                {
                    throw new InvalidDataException(
                        $"Reparse point is not allowed: {current}");
                }
            }
            else if (!allowMissing)
            {
                throw new DirectoryNotFoundException(current);
            }
            if (string.Equals(
                    current,
                    normalizedRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            current = Path.GetDirectoryName(current) ??
                throw new InvalidDataException("Path escaped its root.");
        }
        throw new InvalidDataException("Path escaped its root.");
    }

    internal static void CreateDirectoriesWithoutReparse(
        string root,
        string directory)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(root));
        var destination = Path.GetFullPath(directory);
        EnsureNoReparsePoints(
            normalizedRoot,
            destination,
            allowMissing: true);
        var relative = Path.GetRelativePath(normalizedRoot, destination);
        var current = normalizedRoot;
        foreach (var segment in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current))
                Directory.CreateDirectory(current);
            if (IsReparsePoint(current))
            {
                throw new InvalidDataException(
                    $"Reparse point is not allowed: {current}");
            }
        }
    }

    private static bool IsReparsePoint(string path) =>
        TestOnlyReparsePointDetector?.Invoke(path) ??
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
