using RipperWorks.Core;

namespace RipperWorks.Organizer;

public sealed class GameProfileService(IGameProcessAdapter processAdapter)
{
    public async Task<GameProfileValidationResult> ValidateAsync(
        string? gameRoot,
        string? libraryRoot,
        CancellationToken cancellationToken = default)
    {
        string normalizedRoot;
        try
        {
            if (string.IsNullOrWhiteSpace(gameRoot))
                return Invalid(GameProfileValidationCodes.GameFolderNotFound);
            normalizedRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(gameRoot.Trim()));
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return Invalid(
                GameProfileValidationCodes.GameFolderNotFound,
                exception.Message);
        }

        if (!Directory.Exists(normalizedRoot))
            return Invalid(
                GameProfileValidationCodes.GameFolderNotFound,
                gameRoot: normalizedRoot);

        var executable = Cyberpunk2077Profile.GetExecutablePath(
            normalizedRoot);
        if (!File.Exists(executable) ||
            (File.GetAttributes(executable) & FileAttributes.Directory) != 0)
        {
            return Invalid(
                GameProfileValidationCodes.GameExecutableNotFound,
                gameRoot: normalizedRoot,
                gameFolderFound: true);
        }

        try
        {
            await using var stream = new FileStream(
                executable,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                1,
                FileOptions.Asynchronous);
            _ = await stream.ReadAsync(
                new byte[1],
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException)
        {
            return Invalid(
                GameProfileValidationCodes.GameExecutableUnreadable,
                exception.Message,
                normalizedRoot,
                executable,
                true,
                true);
        }

        if (!string.IsNullOrWhiteSpace(libraryRoot))
        {
            try
            {
                var normalizedLibrary = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(libraryRoot.Trim()));
                if (IsSameOrInside(normalizedRoot, normalizedLibrary) ||
                    IsSameOrInside(normalizedLibrary, normalizedRoot))
                {
                    return Invalid(
                        GameProfileValidationCodes.PathsOverlap,
                        gameRoot: normalizedRoot,
                        executablePath: executable,
                        gameFolderFound: true,
                        executableFound: true);
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                NotSupportedException or
                PathTooLongException)
            {
                return Invalid(
                    GameProfileValidationCodes.PathsOverlap,
                    exception.Message,
                    normalizedRoot,
                    executable,
                    true,
                    true);
            }
        }

        if (processAdapter.IsGameRunning())
        {
            return Invalid(
                GameProfileValidationCodes.GameRunning,
                gameRoot: normalizedRoot,
                executablePath: executable,
                gameFolderFound: true,
                executableFound: true);
        }

        var writeTestPath = Path.Combine(
            normalizedRoot,
            $".ripperworks-write-test-{Guid.NewGuid():N}.tmp");
        Exception? cleanupException = null;
        try
        {
            await File.WriteAllBytesAsync(
                writeTestPath,
                [],
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException)
        {
            return Invalid(
                GameProfileValidationCodes.GameFolderNotWritable,
                exception.Message,
                normalizedRoot,
                executable,
                true,
                true);
        }
        finally
        {
            try
            {
                if (File.Exists(writeTestPath))
                    File.Delete(writeTestPath);
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException)
            {
                cleanupException = exception;
            }
        }
        if (cleanupException is not null)
        {
            return Invalid(
                GameProfileValidationCodes.GameFolderNotWritable,
                cleanupException.Message,
                normalizedRoot,
                executable,
                true,
                true);
        }

        return new GameProfileValidationResult
        {
            IsValid = true,
            GameRoot = normalizedRoot,
            ExecutablePath = executable,
            GameFolderFound = true,
            ExecutableFound = true,
            WriteAccessConfirmed = true,
            NoForeignModifications = false
        };
    }

    public async Task<GameProfileValidationResult> ValidateFastAsync(
        string? gameRoot,
        string? libraryRoot,
        CancellationToken cancellationToken = default)
    {
        string normalizedRoot;
        try
        {
            if (string.IsNullOrWhiteSpace(gameRoot))
                return Invalid(GameProfileValidationCodes.GameFolderNotFound);
            normalizedRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(gameRoot.Trim()));
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return Invalid(
                GameProfileValidationCodes.GameFolderNotFound,
                exception.Message);
        }

        if (!Directory.Exists(normalizedRoot))
            return Invalid(
                GameProfileValidationCodes.GameFolderNotFound,
                gameRoot: normalizedRoot);

        var executable = Cyberpunk2077Profile.GetExecutablePath(normalizedRoot);
        if (!File.Exists(executable))
        {
            return Invalid(
                GameProfileValidationCodes.GameExecutableNotFound,
                gameRoot: normalizedRoot,
                gameFolderFound: true);
        }
        try
        {
            await using var stream = new FileStream(
                executable,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                1,
                FileOptions.Asynchronous);
            _ = await stream.ReadAsync(new byte[1], cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return Invalid(
                GameProfileValidationCodes.GameExecutableUnreadable,
                exception.Message,
                normalizedRoot,
                executable,
                true,
                true);
        }

        if (!string.IsNullOrWhiteSpace(libraryRoot))
        {
            try
            {
                var normalizedLibrary = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(libraryRoot.Trim()));
                if (IsSameOrInside(normalizedRoot, normalizedLibrary) ||
                    IsSameOrInside(normalizedLibrary, normalizedRoot))
                {
                    return Invalid(
                        GameProfileValidationCodes.PathsOverlap,
                        gameRoot: normalizedRoot,
                        executablePath: executable,
                        gameFolderFound: true,
                        executableFound: true);
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                NotSupportedException or
                PathTooLongException)
            {
                return Invalid(
                    GameProfileValidationCodes.PathsOverlap,
                    exception.Message,
                    normalizedRoot,
                    executable,
                    true,
                    true);
            }
        }

        if (processAdapter.IsGameRunning())
        {
            return Invalid(
                GameProfileValidationCodes.GameRunning,
                gameRoot: normalizedRoot,
                executablePath: executable,
                gameFolderFound: true,
                executableFound: true);
        }

        return new GameProfileValidationResult
        {
            IsValid = true,
            GameRoot = normalizedRoot,
            ExecutablePath = executable,
            GameFolderFound = true,
            ExecutableFound = true,
            WriteAccessConfirmed = true,
            NoForeignModifications = false
        };
    }

    private static bool IsSameOrInside(string path, string candidateParent)
    {
        if (string.Equals(
                path,
                candidateParent,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return path.StartsWith(
            candidateParent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static GameProfileValidationResult Invalid(
        string errorCode,
        string? errorDetail = null,
        string gameRoot = "",
        string executablePath = "",
        bool gameFolderFound = false,
        bool executableFound = false,
        bool writeAccessConfirmed = false,
        IReadOnlyList<string>? foreignFiles = null) =>
        new()
        {
            ErrorCode = errorCode,
            ErrorDetail = errorDetail,
            GameRoot = gameRoot,
            ExecutablePath = executablePath,
            GameFolderFound = gameFolderFound,
            ExecutableFound = executableFound,
            WriteAccessConfirmed = writeAccessConfirmed,
            NoForeignModifications = false,
            ForeignFiles = foreignFiles ?? []
        };
}
