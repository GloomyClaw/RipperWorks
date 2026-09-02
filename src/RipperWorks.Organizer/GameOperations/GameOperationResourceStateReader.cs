using System.Security.Cryptography;
using System.Text;

namespace RipperWorks.Organizer.GameOperations;

internal enum GameOperationResourceKind
{
    Missing,
    RegularFile,
    Directory,
    ReparsePoint,
    Inaccessible,
    InvalidOrUnsafe
}

internal sealed record GameOperationResourceState(
    GameOperationResourceKind Kind,
    string? Sha256 = null,
    string? SyntheticText = null);

internal interface IGameOperationResourceStateReader
{
    Task<GameOperationResourceState> ReadAsync(
        string path,
        bool readSha256,
        bool readSyntheticText,
        CancellationToken cancellationToken = default);
}

/// <summary>Single read-only filesystem occupancy/identity authority for RF-06.</summary>
internal sealed class FileSystemGameOperationResourceStateReader
    : IGameOperationResourceStateReader
{
    public static FileSystemGameOperationResourceStateReader Shared { get; } = new();

    private FileSystemGameOperationResourceStateReader()
    {
    }

    public async Task<GameOperationResourceState> ReadAsync(
        string path,
        bool readSha256,
        bool readSyntheticText,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(GameOperationResourceKind.InvalidOrUnsafe);
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(full);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(GameOperationResourceKind.Missing);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or
                System.Security.SecurityException)
        {
            return new(GameOperationResourceKind.Inaccessible);
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
            return new(GameOperationResourceKind.ReparsePoint);
        if (attributes.HasFlag(FileAttributes.Directory))
            return new(GameOperationResourceKind.Directory);

        try
        {
            string? sha = null;
            string? text = null;
            if (readSha256)
            {
                await using var stream = new FileStream(
                    full,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                sha = Convert.ToHexString(
                        await SHA256.HashDataAsync(stream, cancellationToken)
                            .ConfigureAwait(false))
                    .ToLowerInvariant();
            }

            if (readSyntheticText)
            {
                text = await File.ReadAllTextAsync(
                        full,
                        Encoding.UTF8,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return new(GameOperationResourceKind.RegularFile, sha, text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or
                System.Security.SecurityException)
        {
            return new(GameOperationResourceKind.Inaccessible);
        }
    }
}

internal static class GameOperationResourceIdentityValidator
{
    public static async Task<bool> RequireRegularFileOrMissingAsync(
        IGameOperationResourceStateReader reader,
        string path,
        CancellationToken cancellationToken)
    {
        var state = await reader.ReadAsync(
                path,
                readSha256: false,
                readSyntheticText: false,
                cancellationToken)
            .ConfigureAwait(false);

        return state.Kind switch
        {
            GameOperationResourceKind.Missing => false,
            GameOperationResourceKind.RegularFile => true,
            GameOperationResourceKind.Directory => throw new InvalidOperationException(
                "ResourceKindMismatch"),
            GameOperationResourceKind.ReparsePoint => throw new InvalidOperationException(
                "UnsafeReparsePath"),
            GameOperationResourceKind.Inaccessible => throw new InvalidOperationException(
                "ResourceStateReadFailed"),
            _ => throw new InvalidOperationException("ResourceStateReadFailed")
        };
    }

    public static async Task<PreconditionOutcome> ValidateAsync(
        IGameOperationResourceStateReader reader,
        string path,
        string? expectedIdentity,
        bool allowSyntheticText,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedIdentity))
            return PreconditionOutcome.Allow();

        var wantsSha = GameOperationStepIdentity.TryGetSha256(
            expectedIdentity,
            out var expectedSha);
        var wantsText = !GameOperationStepIdentity.IsMissing(expectedIdentity) &&
                        !wantsSha;
        if (wantsText && !allowSyntheticText)
            return PreconditionOutcome.Block("InvalidResourceIdentity");

        var state = await reader.ReadAsync(
                path,
                readSha256: wantsSha,
                readSyntheticText: wantsText,
                cancellationToken)
            .ConfigureAwait(false);

        if (state.Kind == GameOperationResourceKind.ReparsePoint)
            return PreconditionOutcome.Block("UnsafeReparsePath");
        if (state.Kind == GameOperationResourceKind.Directory)
            return PreconditionOutcome.Block("ResourceKindMismatch");
        if (state.Kind == GameOperationResourceKind.Inaccessible)
            return PreconditionOutcome.Block("ResourceStateReadFailed", "filesystem object unreadable");
        if (state.Kind == GameOperationResourceKind.InvalidOrUnsafe)
            return PreconditionOutcome.Block("ResourceStateReadFailed", "filesystem path invalid");

        if (GameOperationStepIdentity.IsMissing(expectedIdentity))
        {
            return state.Kind == GameOperationResourceKind.Missing
                ? PreconditionOutcome.Allow()
                : PreconditionOutcome.Block("ExpectedBeforeNotMissing");
        }

        if (state.Kind == GameOperationResourceKind.Missing)
            return PreconditionOutcome.Block("ExpectedBeforeMissing");
        if (state.Kind != GameOperationResourceKind.RegularFile)
            return PreconditionOutcome.Block("ResourceKindMismatch");

        if (wantsSha)
        {
            return string.Equals(state.Sha256, expectedSha, StringComparison.OrdinalIgnoreCase)
                ? PreconditionOutcome.Allow()
                : PreconditionOutcome.Block("ExpectedBeforeHashMismatch");
        }

        return string.Equals(
                state.SyntheticText,
                expectedIdentity,
                StringComparison.Ordinal)
            ? PreconditionOutcome.Allow()
            : PreconditionOutcome.Block("ExpectedBeforeMismatch");
    }

    public static string ToAfterError(string beforeError) => beforeError switch
    {
        "ExpectedBeforeNotMissing" => "ExpectedAfterNotMissing",
        "ExpectedBeforeMissing" => "ExpectedAfterMissing",
        "ExpectedBeforeHashMismatch" => "ExpectedAfterHashMismatch",
        "ExpectedBeforeMismatch" => "ExpectedAfterMismatch",
        _ => beforeError
    };
}
