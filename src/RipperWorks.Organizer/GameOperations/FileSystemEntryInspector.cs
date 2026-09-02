namespace RipperWorks.Organizer.GameOperations;

internal enum FileSystemEntryKind
{
    Missing,
    Ordinary,
    ReparsePoint,
    Unsafe
}

internal interface IFileSystemEntryInspector
{
    FileSystemEntryKind Inspect(string path);
}

/// <summary>
/// Reads the directory entry itself. It does not use Exists, which follows a
/// link and reports false for a dangling reparse point or access failure.
/// </summary>
internal sealed class FileSystemEntryInspector : IFileSystemEntryInspector
{
    public static FileSystemEntryInspector Shared { get; } = new();

    private FileSystemEntryInspector()
    {
    }

    public FileSystemEntryKind Inspect(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.ReparsePoint)
                ? FileSystemEntryKind.ReparsePoint
                : FileSystemEntryKind.Ordinary;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return FileSystemEntryKind.Missing;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or
                System.Security.SecurityException or ArgumentException or
                NotSupportedException or PathTooLongException)
        {
            return FileSystemEntryKind.Unsafe;
        }
    }
}
