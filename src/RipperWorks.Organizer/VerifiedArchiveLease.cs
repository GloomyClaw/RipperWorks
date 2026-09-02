using RipperWorks.Core;

namespace RipperWorks.Organizer;

/// <summary>
/// Owned read lease over archive bytes: exclusive share prevents concurrent
/// write/delete/replace of the path while analysis identity is committed.
/// Handle is released only after dispose (post commit/rollback).
/// </summary>
public sealed class VerifiedArchiveLease : IAsyncDisposable, IDisposable
{
    private FileStream? _stream;
    private bool _disposed;

    private VerifiedArchiveLease(
        FileStream stream,
        string path,
        string sha256)
    {
        _stream = stream;
        Path = path;
        Sha256 = sha256;
    }

    public string Path { get; }
    public string Sha256 { get; }

    /// <summary>
    /// Seekable stream still owned by this lease. Callers must not dispose it.
    /// </summary>
    public FileStream Stream =>
        _stream ?? throw new ObjectDisposedException(nameof(VerifiedArchiveLease));

    /// <summary>
    /// Opens the path exclusively, hashes, and verifies against
    /// <paramref name="expectedSha256"/> (canonical 64 lowercase hex).
    /// </summary>
    public static async Task<VerifiedArchiveLease> AcquireAsync(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken = default) =>
        await AcquireCoreAsync(
                path,
                expectedSha256,
                cancellationToken,
                afterHashChunk: null)
            .ConfigureAwait(false);

    internal static async Task<VerifiedArchiveLease> AcquireAsync(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken,
        Action afterHashChunk) =>
        await AcquireCoreAsync(
                path,
                expectedSha256,
                cancellationToken,
                afterHashChunk)
            .ConfigureAwait(false);

    private static async Task<VerifiedArchiveLease> AcquireCoreAsync(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken,
        Action? afterHashChunk)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var expected = ArchiveTrustPolicy.CanonicalizeSha256(expectedSha256)
            ?? throw new InvalidOperationException(
                "Expected archive SHA is missing or malformed.");

        FileStream stream;
        try
        {
            // FileShare.None: no concurrent writer/deleter while lease is held.
            using (OperationPerformanceDiagnostics.MeasureMetric(
                       "ARCHIVE_OPEN",
                       "archive_open"))
            {
                stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None,
                    ArchiveContentHasher.BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            if (OperationPerformanceDiagnostics.CurrentContext ==
                OperationPerformanceContext.Install)
            {
                OperationPerformanceDiagnostics.AddCounter(
                    "archive_open_count");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Archive unreadable while acquiring content lease.",
                exception);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OperationPerformanceDiagnostics.CurrentContext ==
                OperationPerformanceContext.Install)
            {
                OperationPerformanceDiagnostics.AddCounter(
                    "archive_hash_pass_count");
                OperationPerformanceDiagnostics.AddCounter(
                    "archive_bytes_hashed",
                    stream.Length);
            }
            string sha;
            using (OperationPerformanceDiagnostics.MeasureMetric(
                       "ARCHIVE_SHA",
                       "archive_sha"))
            {
                sha = await ArchiveContentHasher.ComputeStreamSha256Async(
                        stream,
                        cancellationToken,
                        afterHashChunk)
                    .ConfigureAwait(false);
            }
            if (!string.Equals(sha, expected, StringComparison.Ordinal))
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException(
                    "Archive content identity mismatch; lease refused.");
            }

            stream.Position = 0;
            return new VerifiedArchiveLease(stream, path, sha);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Lease for a draft that already carries ArchiveSha256 from analysis.
    /// </summary>
    public static Task<VerifiedArchiveLease> AcquireForDraftAsync(
        ArchiveAnalysisDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var expected = ArchiveTrustPolicy.CanonicalizeSha256(draft.ArchiveSha256);
        if (expected is null)
        {
            if (draft.State is PackageAnalysisState.Ready or
                PackageAnalysisState.RequiresSelection)
            {
                return Task.FromException<VerifiedArchiveLease>(
                    new InvalidOperationException(
                        "Analysis draft missing content SHA; lease refused."));
            }

            // Non-trusted incomplete drafts: open exclusive without SHA bind.
            return AcquireWithoutExpectedShaAsync(
                draft.Package.ArchivePath,
                cancellationToken);
        }

        return AcquireAsync(
            draft.Package.ArchivePath,
            expected,
            cancellationToken);
    }

    private static async Task<VerifiedArchiveLease> AcquireWithoutExpectedShaAsync(
        string path,
        CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None,
                ArchiveContentHasher.BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Archive unreadable while acquiring content lease.",
                exception);
        }

        try
        {
            var sha = await ArchiveContentHasher.ComputeStreamSha256Async(
                    stream,
                    cancellationToken)
                .ConfigureAwait(false);
            stream.Position = 0;
            return new VerifiedArchiveLease(stream, path, sha);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stream?.Dispose();
        _stream = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }
    }
}
