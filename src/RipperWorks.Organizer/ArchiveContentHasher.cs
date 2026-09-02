using System.Security.Cryptography;
using System.Threading;
using RipperWorks.Core;

namespace RipperWorks.Organizer;

/// <summary>
/// Streaming SHA-256 over archive bytes. Does not load whole files into memory.
/// </summary>
public static class ArchiveContentHasher
{
    public const int BufferSize = 128 * 1024;

    /// <summary>
    /// Test/diagnostics: completed full-stream SHA-256 passes (file or stream).
    /// </summary>
    private static int _fullHashPassCount;

    internal static int ObservedFullHashPassCount =>
        Volatile.Read(ref _fullHashPassCount);

    internal static void ResetObservedFullHashPassCount() =>
        Interlocked.Exchange(ref _fullHashPassCount, 0);

    private static void ObserveFullHashPass() =>
        Interlocked.Increment(ref _fullHashPassCount);

    public static async Task<string> ComputeFileSha256Async(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileStream stream;
        using (OperationPerformanceDiagnostics.MeasureMetric(
                   "ARCHIVE_OPEN",
                   "archive_open"))
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        await using (stream)
        {
            if (OperationPerformanceDiagnostics.CurrentContext is
                OperationPerformanceContext.Install or
                OperationPerformanceContext.InstallPreflight)
            {
                OperationPerformanceDiagnostics.AddCounter(
                    "archive_open_count");
            }
        return await ComputeStreamSha256Async(stream, cancellationToken)
            .ConfigureAwait(false);
        }
    }

    public static async Task<string> ComputeStreamSha256Async(
        Stream stream,
        CancellationToken cancellationToken = default)
        => await ComputeStreamSha256Async(
                stream,
                cancellationToken,
                afterChunk: null)
            .ConfigureAwait(false);

    internal static async Task<string> ComputeStreamSha256Async(
        Stream stream,
        CancellationToken cancellationToken,
        Action? afterChunk)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var algorithm = SHA256.Create();
        var buffer = new byte[BufferSize];
        int read;
        while ((read = await stream.ReadAsync(
                       buffer.AsMemory(0, buffer.Length),
                       cancellationToken)
                   .ConfigureAwait(false)) > 0)
        {
            algorithm.TransformBlock(buffer, 0, read, null, 0);
            afterChunk?.Invoke();
        }

        algorithm.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        ObserveFullHashPass();
        return Convert.ToHexString(algorithm.Hash!)
            .ToLowerInvariant();
    }

    public static string ComputeStreamSha256(
        Stream stream,
        CancellationToken cancellationToken = default)
        => ComputeStreamSha256(stream, cancellationToken, afterChunk: null);

    internal static string ComputeStreamSha256(
        Stream stream,
        CancellationToken cancellationToken,
        Action? afterChunk)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var algorithm = SHA256.Create();
        var buffer = new byte[BufferSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            algorithm.TransformBlock(buffer, 0, read, null, 0);
            afterChunk?.Invoke();
        }

        algorithm.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        ObserveFullHashPass();
        return Convert.ToHexString(algorithm.Hash!)
            .ToLowerInvariant();
    }

    public static string HashFileAndOpenStream(
        string path,
        out FileStream stream,
        CancellationToken cancellationToken = default)
    {
        stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.SequentialScan);
        try
        {
            var hash = ComputeStreamSha256(stream, cancellationToken);
            stream.Position = 0;
            return hash;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Hashes the entire stream from the current position, then seeks back to start.
    /// </summary>
    public static async Task<string> HashAndRewindAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
            throw new NotSupportedException("Hash-and-rewind requires a seekable stream.");
        var origin = stream.Position;
        var hash = await ComputeStreamSha256Async(stream, cancellationToken)
            .ConfigureAwait(false);
        stream.Position = origin;
        return hash;
    }
}
