using System.Security.Cryptography;
using RipperWorks.Core;

namespace RipperWorks.Organizer;

public sealed record ContentStoreObject(string Hash, long FileSize);

public sealed class ContentStoreService
{
    private readonly string _root;

    public ContentStoreService(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    public string Root => _root;

    public string GetObjectPath(string hash)
    {
        ValidateHash(hash);
        return Path.Combine(_root, hash[..2], hash);
    }

    public async Task<ContentStoreObject> PutFileAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        using var timing = OperationPerformanceDiagnostics.MeasureMetric(
            "CONTENT_STORE",
            "content_store_operation");
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var source = Path.GetFullPath(sourcePath);
        string hash;
        if (OperationPerformanceDiagnostics.CurrentContext ==
            OperationPerformanceContext.Install)
        {
            OperationPerformanceDiagnostics.AddCounter(
                "live_file_hash_count");
            ObserveBytes(source, "live_file_bytes_hashed");
            using var liveHashTiming =
                OperationPerformanceDiagnostics.MeasureMetric(
                    "LIVE_FILE_HASH",
                    "live_file_hash");
            hash = await ComputeHashAsync(source, cancellationToken);
        }
        else
        {
            hash = await ComputeHashAsync(source, cancellationToken);
        }
        var destination = GetObjectPath(hash);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        if (!File.Exists(destination))
        {
            var temporary = Path.Combine(
                _root,
                $".{Guid.NewGuid():N}.tmp");
            try
            {
                await CopyFileAsync(source, temporary, cancellationToken);
                ObserveStoreWrite(source);
                var temporaryHash = await ComputeHashAsync(
                    temporary,
                    cancellationToken);
                if (!string.Equals(
                        hash,
                        temporaryHash,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Content Store copy failed SHA-256 verification.");
                }

                try
                {
                    File.Move(temporary, destination);
                }
                catch (IOException) when (File.Exists(destination))
                {
                    File.Delete(temporary);
                }
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }

        var storedHash = await ComputeHashAsync(
            destination,
            cancellationToken);
        ObserveStoreRead(destination);
        OperationPerformanceDiagnostics.AddCounter(
            "content_store_verify_count");
        if (!string.Equals(hash, storedHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Content Store object is corrupt: {hash}");
        }
        return new ContentStoreObject(
            hash,
            new FileInfo(destination).Length);
    }

    public async Task CopyVerifiedAsync(
        string hash,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        using var timing = OperationPerformanceDiagnostics.MeasureMetric(
            "CONTENT_STORE",
            "content_store_operation");
        var source = GetObjectPath(hash);
        if (!File.Exists(source))
            throw new FileNotFoundException(
                "Content Store object is missing.",
                source);
        var sourceHash = await ComputeHashAsync(source, cancellationToken);
        var sourceSize = ObserveStoreRead(source);
        OperationPerformanceDiagnostics.AddCounter(
            "content_store_verify_count");
        if (!string.Equals(hash, sourceHash, StringComparison.Ordinal))
            throw new InvalidDataException($"Content Store object is corrupt: {hash}");

        await CopyFileAsync(source, destinationPath, cancellationToken);
        if (sourceSize is not null)
            ObserveStoreRead(sourceSize.Value);
        var copiedHash = await ComputeHashAsync(
            destinationPath,
            cancellationToken);
        if (!string.Equals(hash, copiedHash, StringComparison.Ordinal))
            throw new InvalidDataException("Restored file failed SHA-256 verification.");
    }

    public static async Task<string> ComputeHashAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
    }

    private static void ValidateHash(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        if (hash.Length != 64 ||
            hash.Any(character =>
                !char.IsAsciiHexDigit(character) ||
                char.IsUpper(character)))
        {
            throw new ArgumentException(
                "Expected a lowercase SHA-256 hash.",
                nameof(hash));
        }
    }

    private static long? ObserveStoreRead(string path)
    {
        if (!OperationPerformanceDiagnostics.IsEnabled)
            return null;
        try
        {
            var bytes = new FileInfo(path).Length;
            ObserveStoreRead(bytes);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    private static void ObserveStoreRead(long bytes)
    {
        OperationPerformanceDiagnostics.AddCounter(
            "content_store_read_count");
        OperationPerformanceDiagnostics.AddCounter(
            "content_store_bytes_read",
            bytes);
    }

    private static void ObserveStoreWrite(string path)
    {
        if (!OperationPerformanceDiagnostics.IsEnabled)
            return;
        long bytes;
        try
        {
            bytes = new FileInfo(path).Length;
        }
        catch
        {
            return;
        }
        OperationPerformanceDiagnostics.AddCounter(
            "content_store_write_count");
        OperationPerformanceDiagnostics.AddCounter(
            "content_store_bytes_written",
            bytes);
    }

    private static void ObserveBytes(string path, string counter)
    {
        if (!OperationPerformanceDiagnostics.IsEnabled)
            return;
        try
        {
            OperationPerformanceDiagnostics.AddCounter(
                counter,
                new FileInfo(path).Length);
        }
        catch
        {
            // The authoritative operation owns any real I/O failure.
        }
    }
}
