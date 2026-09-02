using System.Buffers;

namespace RipperWorks.Organizer;

internal sealed record ArchiveResourcePolicy
{
    public static ArchiveResourcePolicy Production { get; } = new(
        maxEntryCount: 100_000,
        maxTotalDeclaredUncompressedBytes: 64L * 1024 * 1024 * 1024,
        maxEntryDeclaredUncompressedBytes: 16L * 1024 * 1024 * 1024,
        maxCompressionExpansionRatio: 1_000,
        maxActualBytesPerEntry: 16L * 1024 * 1024 * 1024,
        maxActualBytesPerOperation: 64L * 1024 * 1024 * 1024,
        analysisTimeBudget: TimeSpan.FromMinutes(10),
        extractionTimeBudget: TimeSpan.FromMinutes(30));

    public ArchiveResourcePolicy(
        int maxEntryCount,
        long maxTotalDeclaredUncompressedBytes,
        long maxEntryDeclaredUncompressedBytes,
        long maxCompressionExpansionRatio,
        long maxActualBytesPerEntry,
        long maxActualBytesPerOperation,
        TimeSpan analysisTimeBudget,
        TimeSpan extractionTimeBudget)
    {
        if (maxEntryCount <= 0 ||
            maxTotalDeclaredUncompressedBytes <= 0 ||
            maxEntryDeclaredUncompressedBytes <= 0 ||
            maxCompressionExpansionRatio <= 0 ||
            maxActualBytesPerEntry <= 0 ||
            maxActualBytesPerOperation <= 0 ||
            analysisTimeBudget <= TimeSpan.Zero ||
            extractionTimeBudget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxEntryCount),
                "Archive resource limits must all be positive.");
        }

        MaxEntryCount = maxEntryCount;
        MaxTotalDeclaredUncompressedBytes =
            maxTotalDeclaredUncompressedBytes;
        MaxEntryDeclaredUncompressedBytes =
            maxEntryDeclaredUncompressedBytes;
        MaxCompressionExpansionRatio = maxCompressionExpansionRatio;
        MaxActualBytesPerEntry = maxActualBytesPerEntry;
        MaxActualBytesPerOperation = maxActualBytesPerOperation;
        AnalysisTimeBudget = analysisTimeBudget;
        ExtractionTimeBudget = extractionTimeBudget;
    }

    public int MaxEntryCount { get; }
    public long MaxTotalDeclaredUncompressedBytes { get; }
    public long MaxEntryDeclaredUncompressedBytes { get; }
    public long MaxCompressionExpansionRatio { get; }
    public long MaxActualBytesPerEntry { get; }
    public long MaxActualBytesPerOperation { get; }
    public TimeSpan AnalysisTimeBudget { get; }
    public TimeSpan ExtractionTimeBudget { get; }
}

internal static class ArchiveResourceResultCodes
{
    public const string EntryCountExceeded = "ArchiveEntryCountLimitExceeded";
    public const string TotalDeclaredSizeExceeded =
        "ArchiveTotalExpandedSizeLimitExceeded";
    public const string EntryDeclaredSizeExceeded =
        "ArchiveEntryExpandedSizeLimitExceeded";
    public const string CompressionRatioExceeded =
        "ArchiveCompressionRatioLimitExceeded";
    public const string InvalidMetadata = "ArchiveResourceMetadataInvalid";
    public const string AnalysisDeadlineExceeded =
        "ArchiveAnalysisDeadlineExceeded";
    public const string ActualEntrySizeExceeded =
        "ArchiveActualEntrySizeLimitExceeded";
    public const string ActualTotalSizeExceeded =
        "ArchiveActualTotalSizeLimitExceeded";
    public const string ActualSizeMismatch = "ArchiveActualSizeMismatch";
    public const string ExtractionDeadlineExceeded =
        "ArchiveExtractionDeadlineExceeded";
}

internal sealed class ArchiveResourceBudgetException(
    string code,
    string message)
    : IOException(message)
{
    public string Code { get; } = code;
}

internal sealed class ArchiveResourceBudgetScope
{
    private readonly ArchiveResourcePolicy _policy;
    private readonly TimeProvider _timeProvider;
    private readonly long _startedAt;
    private readonly bool _analysis;
    private int _entryCount;
    private long _declaredTotal;
    private long _actualTotal;

    private ArchiveResourceBudgetScope(
        ArchiveResourcePolicy policy,
        TimeProvider timeProvider,
        bool analysis)
    {
        _policy = policy;
        _timeProvider = timeProvider;
        _analysis = analysis;
        _startedAt = timeProvider.GetTimestamp();
    }

    public static ArchiveResourceBudgetScope ForAnalysis(
        ArchiveResourcePolicy policy,
        TimeProvider timeProvider) =>
        new(policy, timeProvider, analysis: true);

    public static ArchiveResourceBudgetScope ForExtraction(
        ArchiveResourcePolicy policy,
        TimeProvider timeProvider) =>
        new(policy, timeProvider, analysis: false);

    public void CheckDeadline()
    {
        var budget = _analysis
            ? _policy.AnalysisTimeBudget
            : _policy.ExtractionTimeBudget;
        if (_timeProvider.GetElapsedTime(_startedAt) < budget)
            return;
        throw Reject(
            _analysis
                ? ArchiveResourceResultCodes.AnalysisDeadlineExceeded
                : ArchiveResourceResultCodes.ExtractionDeadlineExceeded,
            "Archive processing exceeded its resource deadline.");
    }

    public void ObserveDeclaredEntry(
        long declaredSize,
        long compressedSize,
        bool isDirectory)
    {
        CheckDeadline();
        if (_entryCount == _policy.MaxEntryCount)
        {
            throw Reject(
                ArchiveResourceResultCodes.EntryCountExceeded,
                "Archive entry-count resource limit was exceeded.");
        }
        _entryCount++;

        if (declaredSize < 0 || compressedSize < 0)
        {
            throw Reject(
                ArchiveResourceResultCodes.InvalidMetadata,
                "Archive entry contains invalid size metadata.");
        }
        if (declaredSize > _policy.MaxEntryDeclaredUncompressedBytes)
        {
            throw Reject(
                ArchiveResourceResultCodes.EntryDeclaredSizeExceeded,
                "Archive entry declared-size resource limit was exceeded.");
        }
        if (declaredSize >
            _policy.MaxTotalDeclaredUncompressedBytes - _declaredTotal)
        {
            throw Reject(
                ArchiveResourceResultCodes.TotalDeclaredSizeExceeded,
                "Archive total declared-size resource limit was exceeded.");
        }
        _declaredTotal += declaredSize;

        // Zero is treated as unknown for non-empty entries. Some archive
        // formats do not expose a useful compressed size. Actual-byte limits
        // remain authoritative for extraction.
        if (isDirectory || declaredSize == 0 || compressedSize == 0)
            return;
        if (compressedSize <= long.MaxValue /
                _policy.MaxCompressionExpansionRatio &&
            declaredSize > compressedSize *
                _policy.MaxCompressionExpansionRatio)
        {
            throw Reject(
                ArchiveResourceResultCodes.CompressionRatioExceeded,
                "Archive compression expansion-ratio limit was exceeded.");
        }
    }

    public void ObserveActualBytes(
        ref long entryTotal,
        int bytes,
        long declaredSize)
    {
        CheckDeadline();
        if (bytes < 0 || declaredSize < 0)
        {
            throw Reject(
                ArchiveResourceResultCodes.InvalidMetadata,
                "Archive stream contains invalid size state.");
        }
        if (bytes > _policy.MaxActualBytesPerEntry - entryTotal)
        {
            throw Reject(
                ArchiveResourceResultCodes.ActualEntrySizeExceeded,
                "Archive entry actual-byte resource limit was exceeded.");
        }
        if (bytes > _policy.MaxActualBytesPerOperation - _actualTotal)
        {
            throw Reject(
                ArchiveResourceResultCodes.ActualTotalSizeExceeded,
                "Archive operation actual-byte resource limit was exceeded.");
        }
        if (bytes > declaredSize - entryTotal)
        {
            throw Reject(
                ArchiveResourceResultCodes.ActualSizeMismatch,
                "Archive entry emitted more bytes than its declared size.");
        }
        entryTotal += bytes;
        _actualTotal += bytes;
    }

    public void CompleteActualEntry(
        long actualBytes,
        long declaredSize)
    {
        CheckDeadline();
        if (actualBytes != declaredSize)
        {
            throw Reject(
                ArchiveResourceResultCodes.ActualSizeMismatch,
                "Archive entry emitted fewer bytes than its declared size.");
        }
    }

    private static ArchiveResourceBudgetException Reject(
        string code,
        string message) =>
        new(code, message);
}

internal static class ArchiveResourceStreamCopier
{
    private const int BufferSize = 128 * 1024;

    public static async Task<long> CopyEntryAsync(
        Stream source,
        Stream destination,
        long declaredSize,
        ArchiveResourceBudgetScope budget,
        CancellationToken cancellationToken,
        Action? afterChunk = null)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long entryTotal = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                budget.CheckDeadline();
                var read = await source.ReadAsync(
                        buffer.AsMemory(0, BufferSize),
                        cancellationToken)
                    .ConfigureAwait(false);
                budget.CheckDeadline();
                if (read == 0)
                {
                    budget.CompleteActualEntry(entryTotal, declaredSize);
                    return entryTotal;
                }
                budget.ObserveActualBytes(
                    ref entryTotal,
                    read,
                    declaredSize);
                await destination.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken)
                    .ConfigureAwait(false);
                afterChunk?.Invoke();
                budget.CheckDeadline();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
