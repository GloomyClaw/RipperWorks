using RipperWorks.Core;

namespace RipperWorks.Organizer;

public sealed class LibraryArchiveDeletionService
{
    private readonly OrganizerRepository _repository;
    private readonly ModRemovalService _removal;
    private readonly Action<string>? _technicalWarning;

    public LibraryArchiveDeletionService(
        OrganizerRepository repository,
        ModRemovalService removal,
        Action<string>? technicalWarning = null)
    {
        _repository = repository;
        _removal = removal;
        _technicalWarning = technicalWarning;
    }

    public async Task<LibraryArchiveDeletionResult> DeleteAsync(
        PackageId archiveId,
        bool removeInstalled,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken = default,
        ModifiedFilesAuthorization? authorizedModifiedFiles = null)
    {
        var archive = (await _repository.LoadPackagesAsync(
                presentOnly: false,
                cancellationToken))
            .FirstOrDefault(item =>
                item.Package.PackageId == archiveId);
        if (archive is null)
        {
            return new LibraryArchiveDeletionResult
            {
                Status = LibraryArchiveDeletionStatus.ArchiveNotFound
            };
        }
        if (archive.Package.InstallationState !=
            PackageInstallationState.NotInstalled)
        {
            if (!removeInstalled)
            {
                return new LibraryArchiveDeletionResult
                {
                    Status =
                        LibraryArchiveDeletionStatus.InstalledArchiveProtected
                };
            }
            var plan = await _removal.BuildPreflightPlanAsync(
                archive,
                profile,
                libraryRoot,
                cancellationToken,
                authorizedModifiedFiles: authorizedModifiedFiles);
            if (!plan.CanRemove)
            {
                return new LibraryArchiveDeletionResult
                {
                    Status = LibraryArchiveDeletionStatus.RemovalFailed,
                    ErrorMessage = plan.ErrorCode ?? "RemovalBlocked"
                };
            }
            var removal = await _removal.RemoveAsync(
                archive,
                profile,
                libraryRoot,
                cancellationToken,
                authorizedModifiedFiles: authorizedModifiedFiles);
            if (!removal.Success)
            {
                return new LibraryArchiveDeletionResult
                {
                    Status = LibraryArchiveDeletionStatus.RemovalFailed,
                    ErrorMessage =
                        removal.ErrorMessage ?? removal.ErrorCode
                };
            }
        }

        var archivePath = Path.GetFullPath(archive.Package.ArchivePath);
        var archiveFolder = Path.GetDirectoryName(archivePath);
        if (string.IsNullOrWhiteSpace(archiveFolder) ||
            !Path.Exists(archiveFolder))
        {
            _technicalWarning?.Invoke(
                $"Library archive folder is missing: {archiveFolder ?? archivePath}");
            var missingDeletion =
                await _repository.DeleteLibraryArchiveRecordAsync(
                    archiveId,
                    cancellationToken);
            return FromRepository(missingDeletion);
        }

        var parentFolder = Path.GetDirectoryName(archiveFolder);
        if (string.IsNullOrWhiteSpace(parentFolder) ||
            string.Equals(
                archiveFolder,
                Path.GetPathRoot(archiveFolder),
                StringComparison.OrdinalIgnoreCase))
        {
            return new LibraryArchiveDeletionResult
            {
                Status = LibraryArchiveDeletionStatus.Failed,
                ErrorMessage = "Unsafe archive folder."
            };
        }

        var quarantine = Path.Combine(
            parentFolder,
            $".ripperworks-delete-{Guid.NewGuid():N}");
        try
        {
            Directory.Move(archiveFolder, quarantine);
            try
            {
                Directory.Delete(quarantine, recursive: true);
            }
            catch
            {
                if (Directory.Exists(quarantine) &&
                    !Directory.Exists(archiveFolder))
                {
                    Directory.Move(quarantine, archiveFolder);
                }
                throw;
            }
            var recordDeletion =
                await _repository.DeleteLibraryArchiveRecordAsync(
                    archiveId,
                    cancellationToken);
            if (!recordDeletion.Deleted)
            {
                return new LibraryArchiveDeletionResult
                {
                    Status = LibraryArchiveDeletionStatus.Failed,
                    ErrorMessage =
                        "Archive files were removed, but the library record " +
                        "could not be deleted. Retry the operation."
                };
            }
            return FromRepository(recordDeletion);
        }
        catch (Exception exception)
        {
            return new LibraryArchiveDeletionResult
            {
                Status = LibraryArchiveDeletionStatus.Failed,
                ErrorMessage = exception.Message
            };
        }
    }

    public async Task<LibraryModFullDeletionResult> DeleteModAsync(
        LibraryModId libraryModId,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken = default,
        ModifiedFilesAuthorization? authorizedModifiedFiles = null)
    {
        var mod = (await _repository.LoadLibraryModsAsync(
                cancellationToken))
            .FirstOrDefault(item =>
                item.LibraryModId == libraryModId);
        if (mod is null)
        {
            return new LibraryModFullDeletionResult
            {
                Status = LibraryModFullDeletionStatus.ModNotFound
            };
        }

        foreach (var installed in mod.InstalledArchives)
        {
            var plan = await _removal.BuildPreflightPlanAsync(
                installed,
                profile,
                libraryRoot,
                cancellationToken,
                authorizedModifiedFiles: authorizedModifiedFiles);
            if (!plan.CanRemove)
            {
                return new LibraryModFullDeletionResult
                {
                    Status = LibraryModFullDeletionStatus.RemovalFailed,
                    ErrorMessage = plan.ErrorCode ?? "RemovalBlocked"
                };
            }

            var removal = await _removal.RemoveAsync(
                installed,
                profile,
                libraryRoot,
                cancellationToken,
                authorizedModifiedFiles: authorizedModifiedFiles);
            if (!removal.Success)
            {
                return new LibraryModFullDeletionResult
                {
                    Status = LibraryModFullDeletionStatus.RemovalFailed,
                    ErrorMessage =
                        removal.ErrorMessage ?? removal.ErrorCode
                };
            }
        }

        var deleted = 0;
        var missingPaths = new List<string>();
        var possibleParentFolders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in mod.Archives)
        {
            var archivePath =
                Path.GetFullPath(archive.Package.ArchivePath);
            var archiveFolder = Path.GetDirectoryName(archivePath);
            if (!string.IsNullOrWhiteSpace(archiveFolder))
            {
                var parent = Path.GetDirectoryName(archiveFolder);
                if (!string.IsNullOrWhiteSpace(parent))
                    possibleParentFolders.Add(parent);
            }
            if (string.IsNullOrWhiteSpace(archiveFolder) ||
                !Directory.Exists(archiveFolder))
            {
                var missing = archiveFolder ?? archivePath;
                missingPaths.Add(missing);
                _technicalWarning?.Invoke(
                    $"Library archive folder is missing: {missing}");
            }

            var archiveDeletion = await DeleteAsync(
                archive.Package.PackageId,
                removeInstalled: false,
                profile,
                libraryRoot,
                cancellationToken);
            if (!archiveDeletion.Success)
            {
                return new LibraryModFullDeletionResult
                {
                    Status =
                        LibraryModFullDeletionStatus.FileSystemFailed,
                    DeletedArchiveCount = deleted,
                    MissingPaths = missingPaths,
                    ErrorMessage = archiveDeletion.ErrorMessage
                };
            }
            deleted++;
        }

        foreach (var parentFolder in possibleParentFolders)
        {
            TryDeleteEmptyParent(parentFolder, libraryRoot);
        }
        return new LibraryModFullDeletionResult
        {
            Status = LibraryModFullDeletionStatus.Deleted,
            DeletedArchiveCount = deleted,
            MissingPaths = missingPaths
        };
    }

    private void TryDeleteEmptyParent(
        string parentFolder,
        string? libraryRoot)
    {
        try
        {
            if (!Directory.Exists(parentFolder) ||
                Directory.EnumerateFileSystemEntries(parentFolder).Any())
            {
                return;
            }
            var fullParent = Path.GetFullPath(parentFolder);
            if (string.Equals(
                    fullParent,
                    Path.GetPathRoot(fullParent),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (!string.IsNullOrWhiteSpace(libraryRoot))
            {
                var fullLibrary = Path.GetFullPath(libraryRoot);
                if (string.Equals(
                        fullParent,
                        fullLibrary,
                        StringComparison.OrdinalIgnoreCase) ||
                    !fullParent.StartsWith(
                        fullLibrary.TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar) +
                        Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            Directory.Delete(fullParent);
        }
        catch (Exception exception)
        {
            _technicalWarning?.Invoke(
                $"Could not remove empty library parent '{parentFolder}': " +
                exception.Message);
        }
    }

    private static LibraryArchiveDeletionResult FromRepository(
        ArchiveRecordDeletionResult result) =>
        new()
        {
            Status = result.Deleted
                ? LibraryArchiveDeletionStatus.Deleted
                : LibraryArchiveDeletionStatus.Failed,
            ParentRemoved = result.ParentRemoved,
            ParentHadRelations = result.ParentHadRelations
        };
}
