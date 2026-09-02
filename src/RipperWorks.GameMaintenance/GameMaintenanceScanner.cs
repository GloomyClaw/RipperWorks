using System.Security.Cryptography;
using RipperWorks.Core;

namespace RipperWorks.GameMaintenance;

public static class GameMaintenanceScanner
{
    public static readonly string[] KnownModLocations =
    [
        @"archive\pc\mod",
        "red4ext",
        @"r6\scripts",
        @"r6\tweaks",
        @"bin\x64\plugins\cyber_engine_tweaks",
        "mods"
    ];

    public static bool TryValidateRootAndRuntime(
        string? gameRoot,
        IGameProcessAdapter? processAdapter,
        out string normalizedRoot,
        out GameMaintenanceScanStatus status,
        out string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(gameRoot))
        {
            normalizedRoot = string.Empty;
            status = GameMaintenanceScanStatus.InvalidGameRoot;
            errorMessage = "Game folder path is empty.";
            return false;
        }

        try
        {
            normalizedRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(gameRoot.Trim()));
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            normalizedRoot = gameRoot;
            status = GameMaintenanceScanStatus.InvalidGameRoot;
            errorMessage = ex.Message;
            return false;
        }

        if (!Directory.Exists(normalizedRoot))
        {
            status = GameMaintenanceScanStatus.InvalidGameRoot;
            errorMessage = "Game folder does not exist.";
            return false;
        }

        var executable = Cyberpunk2077Profile.GetExecutablePath(normalizedRoot);
        if (!File.Exists(executable) ||
            (File.GetAttributes(executable) & FileAttributes.Directory) != 0)
        {
            status = GameMaintenanceScanStatus.InvalidGameRoot;
            errorMessage = "Cyberpunk2077.exe was not found in the specified game folder.";
            return false;
        }

        if (processAdapter?.IsGameRunning() == true)
        {
            status = GameMaintenanceScanStatus.GameRunning;
            errorMessage = "Cyberpunk 2077 is currently running.";
            return false;
        }

        status = GameMaintenanceScanStatus.Clean;
        errorMessage = null;
        return true;
    }

    public static async Task<GameMaintenanceScanResult> ScanAsync(
        string? gameRoot,
        IGameProcessAdapter? processAdapter = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateRootAndRuntime(
            gameRoot,
            processAdapter,
            out var normalizedRoot,
            out var errorStatus,
            out var errorMsg))
        {
            return new GameMaintenanceScanResult(
                normalizedRoot,
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                errorStatus,
                [],
                errorMsg);
        }

        try
        {
            var candidates = new List<GameMaintenanceCandidateFile>();

            foreach (var relLoc in KnownModLocations)
            {
                var locDir = Path.Combine(normalizedRoot, relLoc);
                if (!Directory.Exists(locDir))
                    continue;

                var stack = new Stack<string>();
                stack.Push(locDir);

                while (stack.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var currentDir = stack.Pop();

                    if (!IsSafelyContained(currentDir, normalizedRoot))
                        continue;

                    var dirAttr = File.GetAttributes(currentDir);
                    if ((dirAttr & FileAttributes.ReparsePoint) != 0)
                        continue;

                    foreach (var file in Directory.EnumerateFiles(currentDir))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (!IsSafelyContained(file, normalizedRoot))
                            continue;

                        var fileAttr = File.GetAttributes(file);
                        if ((fileAttr & FileAttributes.ReparsePoint) != 0)
                            continue;

                        if (IsAllowedEpicStub(normalizedRoot, file))
                            continue;

                        var relPath = NormalizeRelativePath(
                            Path.GetRelativePath(normalizedRoot, file));
                        var fileInfo = new FileInfo(file);
                        var hash = await ComputeSha256Async(file, cancellationToken);

                        candidates.Add(new GameMaintenanceCandidateFile(
                            relPath,
                            fileInfo.Length,
                            hash));
                    }

                    foreach (var subDir in Directory.EnumerateDirectories(currentDir))
                    {
                        var subAttr = File.GetAttributes(subDir);
                        if ((subAttr & FileAttributes.ReparsePoint) == 0 &&
                            IsSafelyContained(subDir, normalizedRoot))
                        {
                            stack.Push(subDir);
                        }
                    }
                }
            }

            candidates.Sort((a, b) => string.Compare(a.RelativeGamePath, b.RelativeGamePath, StringComparison.OrdinalIgnoreCase));

            var status = candidates.Count > 0
                ? GameMaintenanceScanStatus.ForeignModificationDetected
                : GameMaintenanceScanStatus.Clean;

            return new GameMaintenanceScanResult(
                normalizedRoot,
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                status,
                candidates);
        }
        catch (Exception ex)
        {
            return new GameMaintenanceScanResult(
                normalizedRoot,
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                GameMaintenanceScanStatus.ScanFailed,
                [],
                ex.Message);
        }
    }

    public static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hashBytes);
    }

    public static bool IsSafelyContained(string path, string canonicalRoot)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(canonicalRoot));
            return fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsSafelyContainedWithoutReparse(string path, string canonicalRoot)
    {
        if (!IsSafelyContained(path, canonicalRoot))
            return false;

        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(canonicalRoot));

            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                var attr = File.GetAttributes(fullPath);
                if ((attr & FileAttributes.ReparsePoint) != 0)
                    return false;
            }

            var current = Path.GetDirectoryName(fullPath);
            while (current != null &&
                   current.Length >= root.Length &&
                   current.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(current))
                {
                    var attr = File.GetAttributes(current);
                    if ((attr & FileAttributes.ReparsePoint) != 0)
                        return false;
                }

                if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
                    break;

                current = Path.GetDirectoryName(current);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/').Trim().TrimStart('/');

    private static bool IsAllowedEpicStub(string gameRoot, string filePath)
    {
        var file = new FileInfo(filePath);
        var modsRoot = Path.GetFullPath(Path.Combine(gameRoot, "mods"));
        return string.Equals(file.Name, ".stub", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(file.DirectoryName, modsRoot, StringComparison.OrdinalIgnoreCase) &&
               file.Exists &&
               file.Length == 0 &&
               (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
    }
}
