namespace RipperWorks.Organizer.GameOperations;

/// <summary>
/// Explicit immutable authority for RF-06 shadow mutation below one dedicated
/// system-TEMP harness. Creating a permit performs no filesystem mutation.
/// </summary>
public sealed class ShadowHarnessPermit
{
    private readonly IFileSystemEntryInspector _entryInspector;

    private ShadowHarnessPermit(
        string harnessRoot,
        IFileSystemEntryInspector entryInspector)
    {
        HarnessRoot = harnessRoot;
        _entryInspector = entryInspector;
    }

    public string HarnessRoot { get; }

    public static string SystemTempRoot { get; } =
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));

    /// <summary>
    /// Creates an authority for a path strictly below system TEMP. The path
    /// need not exist yet; no directory is created. Existing ancestors from
    /// system TEMP through the harness must not be reparse points.
    /// </summary>
    public static ShadowHarnessPermit Create(string harnessRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessRoot);
        var full = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(harnessRoot.Trim()));

        var inspector = FileSystemEntryInspector.Shared;
        if (!IsStrictDescendant(full, SystemTempRoot) ||
            HasReparsePointOnExistingPath(SystemTempRoot, full, inspector))
        {
            throw new InvalidOperationException("ShadowRootNotAllowed:harness");
        }

        return new ShadowHarnessPermit(full, inspector);
    }

    internal static ShadowHarnessPermit CreateForTesting(
        string harnessRoot,
        IFileSystemEntryInspector entryInspector)
    {
        ArgumentNullException.ThrowIfNull(entryInspector);
        var full = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(harnessRoot));
        if (!IsStrictDescendant(full, SystemTempRoot) ||
            HasReparsePointOnExistingPath(SystemTempRoot, full, entryInspector))
        {
            throw new InvalidOperationException("ShadowRootNotAllowed:harness");
        }

        return new ShadowHarnessPermit(full, entryInspector);
    }

    public PreconditionOutcome ValidateMutationRoots(
        GameOperationPlan plan,
        string? journalPath,
        string? contentStoreRoot = null,
        string? organizerDatabasePath = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!IsHarnessUsable() ||
            !IsAllowedResource(plan.ProfileKey.Value))
        {
            return PreconditionOutcome.Block("ShadowRootNotAllowed");
        }

        if (!string.IsNullOrWhiteSpace(journalPath) &&
            !IsAllowedResource(journalPath))
        {
            return PreconditionOutcome.Block("ShadowRootNotAllowed");
        }

        if (!string.IsNullOrWhiteSpace(plan.SourceArchivePath) &&
            !IsAllowedResource(plan.SourceArchivePath))
        {
            return PreconditionOutcome.Block("ShadowRootNotAllowed");
        }

        if (!string.IsNullOrWhiteSpace(contentStoreRoot) &&
            !IsAllowedResource(contentStoreRoot))
        {
            return PreconditionOutcome.Block("ShadowRootNotAllowed");
        }

        if (!string.IsNullOrWhiteSpace(organizerDatabasePath) &&
            !IsAllowedResource(organizerDatabasePath))
        {
            return PreconditionOutcome.Block("ShadowRootNotAllowed");
        }

        return PreconditionOutcome.Allow();
    }

    /// <summary>
    /// Authorizes only the configured journal location before any durable
    /// idempotency lookup. This is deliberately narrower than live mutation
    /// root validation and performs no filesystem creation or SQLite access.
    /// </summary>
    internal PreconditionOutcome ValidateJournalAuthority(string? journalPath)
    {
        if (!TryNormalize(journalPath, out var full) ||
            !IsStrictDescendant(full, HarnessRoot) ||
            HasReparsePointOnExistingPath(
                SystemTempRoot,
                HarnessRoot,
                _entryInspector) ||
            HasReparsePointOnExistingPath(
                HarnessRoot,
                full,
                _entryInspector))
        {
            return PreconditionOutcome.Block("ShadowRootNotAllowed");
        }

        // A directory cannot be a journal file. Attribute reads are used
        // directly so dangling reparse points and access failures never look
        // like a missing, therefore safe, journal.
        if (_entryInspector.Inspect(full) == FileSystemEntryKind.Ordinary)
        {
            try
            {
                if (File.GetAttributes(full).HasFlag(FileAttributes.Directory))
                    return PreconditionOutcome.Block("ShadowRootNotAllowed");
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException or
                    System.Security.SecurityException or ArgumentException or
                    NotSupportedException or PathTooLongException)
            {
                return PreconditionOutcome.Block("ShadowRootNotAllowed");
            }
        }

        return PreconditionOutcome.Allow();
    }

    public PreconditionOutcome ValidateBoundResources(
        params string?[] absolutePaths)
    {
        if (!IsHarnessUsable())
            return PreconditionOutcome.Block("ShadowRootNotAllowed");

        foreach (var path in absolutePaths)
        {
            if (!IsAllowedResource(path))
                return PreconditionOutcome.Block("ShadowRootNotAllowed");
        }

        return PreconditionOutcome.Allow();
    }

    public void EnsureAllowed(string absolutePath, string role)
    {
        if (!IsAllowedResource(absolutePath))
        {
            throw new InvalidOperationException(
                "ShadowRootNotAllowed:" + role);
        }
    }

    public bool IsAllowedPath(string? absolutePath) =>
        IsAllowedResource(absolutePath);

    public bool HasReparseAncestorUnderHarness(string absolutePath)
    {
        if (!TryNormalize(absolutePath, out var full) ||
            !IsStrictDescendant(full, HarnessRoot))
        {
            return true;
        }

        return HasReparsePointOnExistingPath(
                   SystemTempRoot,
                   HarnessRoot,
                   _entryInspector) ||
               HasReparsePointOnExistingPath(
                   HarnessRoot,
                   full,
                   _entryInspector);
    }

    public static bool IsStrictDescendantOfSystemTemp(string absolutePath)
    {
        if (!TryNormalize(absolutePath, out var full))
            return false;
        return IsStrictDescendant(full, SystemTempRoot);
    }

    public static bool IsReparsePoint(string path)
        => FileSystemEntryInspector.Shared.Inspect(path) is
            FileSystemEntryKind.ReparsePoint or FileSystemEntryKind.Unsafe;

    private bool IsHarnessUsable() =>
        Directory.Exists(HarnessRoot) &&
        IsStrictDescendant(HarnessRoot, SystemTempRoot) &&
        !HasReparsePointOnExistingPath(
            SystemTempRoot,
            HarnessRoot,
            _entryInspector);

    private bool IsAllowedResource(string? absolutePath)
    {
        if (!TryNormalize(absolutePath, out var full) ||
            !IsStrictDescendant(full, HarnessRoot))
        {
            return false;
        }

        return !HasReparsePointOnExistingPath(
                   SystemTempRoot,
                   HarnessRoot,
                   _entryInspector) &&
               !HasReparsePointOnExistingPath(
                   HarnessRoot,
                   full,
                   _entryInspector);
    }

    private static bool HasReparsePointOnExistingPath(
        string ancestor,
        string descendant,
        IFileSystemEntryInspector entryInspector)
    {
        if (!TryNormalize(ancestor, out var root) ||
            !TryNormalize(descendant, out var target) ||
            (!IsStrictDescendant(target, root) &&
             !string.Equals(target, root, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (IsUnsafeEntry(root, entryInspector))
            return true;

        var relative = Path.GetRelativePath(root, target);
        if (relative is "." or "")
            return false;

        var probe = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            probe = Path.Combine(probe, segment);
            if (IsUnsafeEntry(probe, entryInspector))
                return true;
        }

        return false;
    }

    private static bool IsUnsafeEntry(
        string path,
        IFileSystemEntryInspector entryInspector) =>
        entryInspector.Inspect(path) is
            FileSystemEntryKind.ReparsePoint or FileSystemEntryKind.Unsafe;

    private static bool IsStrictDescendant(string path, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(root));
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalize(string? path, out string full)
    {
        full = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            full = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(path.Trim()));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
