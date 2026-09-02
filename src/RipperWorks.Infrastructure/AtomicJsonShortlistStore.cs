using System.Text.Json;
using RipperWorks.Core;

namespace RipperWorks.Infrastructure;

public sealed class AtomicJsonShortlistStore : IShortlistStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AtomicJsonShortlistStore(RipperWorksPaths paths)
        : this(paths.ShortlistPath)
    {
    }

    public AtomicJsonShortlistStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
    }

    public string FilePath => _filePath;

    public async Task<ShortlistDocument> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        ShortlistDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        Validate(document);
        var normalized = Normalize(document);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(normalized, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ShortlistEntryRecord>> LoadEntriesAsync(
        CancellationToken cancellationToken = default)
    {
        var document = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return document.Items;
    }

    public async Task<bool> AddAsync(
        string gameDomain,
        long nexusModId,
        string? name = null,
        string? author = null,
        string? lastKnownVersion = null,
        DateTimeOffset? addedAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        var key = new ShortlistEntryKey(gameDomain, nexusModId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var items = current.Items.ToList();
            var existingIndex = items.FindIndex(item => item.Key.EqualsCanonical(key));

            if (existingIndex >= 0)
            {
                var existing = items[existingIndex];
                var updatedName = !string.IsNullOrWhiteSpace(name)
                    ? name.Trim()
                    : existing.Name;
                var updatedAuthor = !string.IsNullOrWhiteSpace(author)
                    ? author.Trim()
                    : existing.Author;
                var updatedVersion = !string.IsNullOrWhiteSpace(lastKnownVersion)
                    ? lastKnownVersion.Trim()
                    : existing.LastKnownVersion;

                var metadataChanged =
                    !string.Equals(existing.Name, updatedName, StringComparison.Ordinal) ||
                    !string.Equals(existing.Author, updatedAuthor, StringComparison.Ordinal) ||
                    !string.Equals(existing.LastKnownVersion, updatedVersion, StringComparison.Ordinal);

                if (!metadataChanged)
                    return false;

                items[existingIndex] = existing with
                {
                    Name = updatedName,
                    Author = updatedAuthor,
                    LastKnownVersion = updatedVersion
                };

                var updatedDoc = current with { Items = items };
                await SaveCoreAsync(Normalize(updatedDoc), cancellationToken).ConfigureAwait(false);
                return false;
            }

            var newEntry = new ShortlistEntryRecord
            {
                GameDomain = key.GameDomain,
                NexusModId = key.NexusModId,
                AddedAtUtc = addedAtUtc ?? DateTimeOffset.UtcNow,
                Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
                Author = string.IsNullOrWhiteSpace(author) ? null : author.Trim(),
                LastKnownVersion = string.IsNullOrWhiteSpace(lastKnownVersion) ? null : lastKnownVersion.Trim()
            };

            items.Add(newEntry);
            var newDoc = current with { Items = items };
            await SaveCoreAsync(Normalize(newDoc), cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(
        string gameDomain,
        long nexusModId,
        CancellationToken cancellationToken = default)
    {
        var key = new ShortlistEntryKey(gameDomain, nexusModId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var items = current.Items.ToList();
            var removedCount = items.RemoveAll(item => item.Key.EqualsCanonical(key));

            if (removedCount == 0)
                return false;

            var updatedDoc = current with { Items = items };
            await SaveCoreAsync(Normalize(updatedDoc), cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ContainsAsync(
        string gameDomain,
        long nexusModId,
        CancellationToken cancellationToken = default)
    {
        var key = new ShortlistEntryKey(gameDomain, nexusModId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            return current.Items.Any(item => item.Key.EqualsCanonical(key));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ShortlistDocument> LoadCoreAsync(
        CancellationToken cancellationToken)
    {
        var temporaryPath = GetTemporaryPath();
        if (File.Exists(_filePath))
        {
            try
            {
                var document = await ReadAsync(
                    _filePath,
                    cancellationToken).ConfigureAwait(false);
                if (File.Exists(temporaryPath))
                {
                    await EnsureNoFutureTemporaryFileAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
                    File.Delete(temporaryPath);
                }

                return document;
            }
            catch (Exception ex) when (IsUnsupportedVersionException(ex))
            {
                // Never overwrite or move newer unsupported format versions to corrupt backup.
                throw;
            }
            catch (Exception exception) when (IsCorruptContent(exception))
            {
                if (File.Exists(temporaryPath))
                {
                    try
                    {
                        var recovered = await ReadAsync(
                            temporaryPath,
                            cancellationToken).ConfigureAwait(false);
                        MoveToCorruptBackup(_filePath);
                        File.Move(temporaryPath, _filePath, true);
                        return recovered;
                    }
                    catch (Exception temporaryException)
                        when (IsUnsupportedVersionException(temporaryException))
                    {
                        throw;
                    }
                    catch (Exception temporaryException)
                        when (IsCorruptContent(temporaryException))
                    {
                        MoveToCorruptBackup(_filePath);
                        MoveToCorruptBackup(temporaryPath);
                    }
                }
                else
                {
                    MoveToCorruptBackup(_filePath);
                }

                return new ShortlistDocument();
            }
        }

        if (!File.Exists(temporaryPath))
            return new ShortlistDocument();

        try
        {
            var recovered = await ReadAsync(
                temporaryPath,
                cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.Move(temporaryPath, _filePath, true);
            return recovered;
        }
        catch (Exception ex) when (IsUnsupportedVersionException(ex))
        {
            throw;
        }
        catch (Exception exception) when (IsCorruptContent(exception))
        {
            MoveToCorruptBackup(temporaryPath);
            return new ShortlistDocument();
        }
    }

    private async Task SaveCoreAsync(
        ShortlistDocument document,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporaryPath = GetTemporaryPath();

        await EnsureNoFutureTemporaryFileAsync(temporaryPath, cancellationToken).ConfigureAwait(false);

        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         16 * 1024,
                         FileOptions.WriteThrough | FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, _filePath, true);
    }

    private static async Task EnsureNoFutureTemporaryFileAsync(
        string temporaryPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(temporaryPath))
            return;

        try
        {
            await using var stream = new FileStream(
                temporaryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            using var jsonDoc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (jsonDoc.RootElement.TryGetProperty("FormatVersion", out var versionProp) &&
                versionProp.TryGetInt32(out var version) &&
                version > RipperWorksShortlistSchema.CurrentVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported Shortlist format version: {version}.");
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (IsCorruptContent(ex))
        {
            // Stale corrupt or current-version temp is handled normally by caller.
        }
    }

    private static async Task<ShortlistDocument> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var document = await JsonSerializer.DeserializeAsync<ShortlistDocument>(
                stream,
                JsonOptions,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new JsonException("Shortlist document is empty.");
        Validate(document);
        return Normalize(document);
    }

    private static ShortlistDocument Normalize(ShortlistDocument document)
    {
        var deduplicated = new Dictionary<ShortlistEntryKey, ShortlistEntryRecord>();

        if (document.Items is not null)
        {
            foreach (var item in document.Items)
            {
                if (item is null)
                    continue;

                if (string.IsNullOrWhiteSpace(item.GameDomain) || item.NexusModId <= 0)
                    continue;

                var key = new ShortlistEntryKey(item.GameDomain, item.NexusModId);
                if (!deduplicated.TryGetValue(key, out var existing))
                {
                    deduplicated[key] = item with
                    {
                        GameDomain = key.GameDomain,
                        Name = string.IsNullOrWhiteSpace(item.Name) ? null : item.Name.Trim(),
                        Author = string.IsNullOrWhiteSpace(item.Author) ? null : item.Author.Trim(),
                        LastKnownVersion = string.IsNullOrWhiteSpace(item.LastKnownVersion) ? null : item.LastKnownVersion.Trim()
                    };
                }
                else
                {
                    // Preserve original earliest AddedAtUtc, update non-empty metadata
                    deduplicated[key] = existing with
                    {
                        Name = !string.IsNullOrWhiteSpace(item.Name) ? item.Name.Trim() : existing.Name,
                        Author = !string.IsNullOrWhiteSpace(item.Author) ? item.Author.Trim() : existing.Author,
                        LastKnownVersion = !string.IsNullOrWhiteSpace(item.LastKnownVersion) ? item.LastKnownVersion.Trim() : existing.LastKnownVersion
                    };
                }
            }
        }

        var sortedItems = deduplicated.Values
            .OrderBy(e => e.AddedAtUtc)
            .ThenBy(e => e.NexusModId)
            .ToArray();

        return document with
        {
            FormatVersion = RipperWorksShortlistSchema.CurrentVersion,
            Items = sortedItems
        };
    }

    private static void Validate(ShortlistDocument document)
    {
        if (document.FormatVersion != RipperWorksShortlistSchema.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported Shortlist format version: {document.FormatVersion}.");
        }

        if (document.Items is null)
        {
            throw new InvalidDataException(
                "Shortlist document Items collection cannot be null.");
        }

        foreach (var item in document.Items)
        {
            if (item is null)
            {
                throw new InvalidDataException(
                    "Shortlist document contains a null item.");
            }

            if (string.IsNullOrWhiteSpace(item.GameDomain))
            {
                throw new InvalidDataException(
                    "Shortlist item GameDomain cannot be null or whitespace.");
            }

            if (item.NexusModId <= 0)
            {
                throw new InvalidDataException(
                    $"Shortlist item NexusModId must be positive, got {item.NexusModId}.");
            }

            if (item.AddedAtUtc == default)
            {
                throw new InvalidDataException(
                    "Shortlist item AddedAtUtc must be a valid non-default timestamp.");
            }
        }
    }

    private string GetTemporaryPath() => _filePath + ".tmp";

    private static bool IsUnsupportedVersionException(Exception exception) =>
        exception is InvalidDataException ex &&
        ex.Message.StartsWith("Unsupported Shortlist format version", StringComparison.Ordinal);

    private static bool IsCorruptContent(Exception exception) =>
        exception is JsonException or NotSupportedException or InvalidDataException;

    private static void MoveToCorruptBackup(string path)
    {
        if (!File.Exists(path))
            return;

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var backup = $"{path}.corrupt-{timestamp}";
        for (var suffix = 2; File.Exists(backup); suffix++)
            backup = $"{path}.corrupt-{timestamp}-{suffix}";
        File.Move(path, backup);
    }
}
