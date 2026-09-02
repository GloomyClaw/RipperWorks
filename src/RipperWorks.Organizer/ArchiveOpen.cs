using SharpCompress.Archives;
using SharpCompress.Readers;

namespace RipperWorks.Organizer;

/// <summary>
/// Single place to open archives from a path or an already-owned stream.
/// Prefer stream-bound opens for content trust so analysis/extraction hash the
/// same bytes the handle holds.
/// </summary>
internal static class ArchiveOpen
{
    public static IArchive OpenPath(string path) =>
        ArchiveFactory.OpenArchive(path);

    /// <summary>
    /// Opens an archive over an already-owned stream. The caller retains
    /// ownership of <paramref name="stream"/> for the analysis/extraction
    /// lifetime; SharpCompress must not dispose it.
    /// </summary>
    public static IArchive OpenStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
            throw new NotSupportedException(
                "Content-bound archive open requires a seekable stream.");
        // SharpCompress 0.50 exposes OpenArchive overloads; leave stream owned
        // by the caller so extraction/analysis share one handle.
        return ArchiveFactory.OpenArchive(
            stream,
            new ReaderOptions { LeaveStreamOpen = true });
    }
}
