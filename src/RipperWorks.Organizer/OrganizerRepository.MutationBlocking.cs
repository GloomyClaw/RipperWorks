using Microsoft.Data.Sqlite;
using RipperWorks.Core;

namespace RipperWorks.Organizer;

public sealed record PackageMutationBlock
{
    public bool IsBlocked { get; init; }
    public string? Reason { get; init; }
    public InstallOperationStatus? BlockingOperationState { get; init; }
    public PackageInstallationState? PackagesState { get; init; }
    public PackageInstallationState? InstalledModsState { get; init; }
}

public sealed partial class OrganizerRepository
{
    public async Task<PackageMutationBlock> LoadPackageMutationBlockAsync(
        PackageId packageId,
        CancellationToken cancellationToken = default)
        => await LoadPackageMutationBlockAsync(
            packageId,
            ignoredOperationId: null,
            cancellationToken).ConfigureAwait(false);

    internal async Task<PackageMutationBlock> LoadPackageMutationBlockAsync(
        PackageId packageId,
        Guid? ignoredOperationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageId);

        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();

            command.CommandText =
                """
                SELECT
                    (
                        SELECT Status
                        FROM InstallOperations
                        WHERE (PackageId = $packageId OR
                               OperationType LIKE 'VersionSwitch|%')
                          AND Status IN ($recoveryRequired, $inProgress)
                          AND ($ignoredOperationId IS NULL OR
                               OperationId <> $ignoredOperationId)
                        ORDER BY CASE
                            WHEN Status = $recoveryRequired THEN 0
                            WHEN Status = $inProgress THEN 1
                            ELSE 2
                        END
                        LIMIT 1
                    ) AS OperationState,
                    (
                        SELECT InstallationState
                        FROM Packages
                        WHERE PackageId = $packageId
                        LIMIT 1
                    ) AS PackagesState,
                    (
                        SELECT InstallationState
                        FROM InstalledMods
                        WHERE PackageId = $packageId
                        LIMIT 1
                    ) AS InstalledModsState;
                """;

            command.Parameters.AddWithValue("$packageId", packageId.Value);
            command.Parameters.AddWithValue(
                "$ignoredOperationId",
                ignoredOperationId is null
                    ? DBNull.Value
                    : ignoredOperationId.Value.ToString("N"));
            command.Parameters.AddWithValue("$recoveryRequired", (int)InstallOperationStatus.RecoveryRequired);
            command.Parameters.AddWithValue("$inProgress", (int)InstallOperationStatus.InProgress);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new PackageMutationBlock { IsBlocked = false };
            }

            InstallOperationStatus? opState = reader.IsDBNull(0)
                ? null
                : (InstallOperationStatus)reader.GetInt32(0);
            PackageInstallationState? pkgsState = reader.IsDBNull(1)
                ? null
                : (PackageInstallationState)reader.GetInt32(1);
            PackageInstallationState? installedModsState = reader.IsDBNull(2)
                ? null
                : (PackageInstallationState)reader.GetInt32(2);

            // Precedence 1: Operation RecoveryRequired
            if (opState == InstallOperationStatus.RecoveryRequired)
            {
                return new PackageMutationBlock
                {
                    IsBlocked = true,
                    Reason = "OperationRecoveryRequired",
                    BlockingOperationState = opState,
                    PackagesState = pkgsState,
                    InstalledModsState = installedModsState
                };
            }

            // Precedence 2: Operation InProgress
            if (opState == InstallOperationStatus.InProgress)
            {
                return new PackageMutationBlock
                {
                    IsBlocked = true,
                    Reason = "OperationInProgress",
                    BlockingOperationState = opState,
                    PackagesState = pkgsState,
                    InstalledModsState = installedModsState
                };
            }

            // Precedence 3: Packages PartiallyInstalled
            if (pkgsState == PackageInstallationState.PartiallyInstalled)
            {
                return new PackageMutationBlock
                {
                    IsBlocked = true,
                    Reason = "PackageStatePartiallyInstalled",
                    PackagesState = pkgsState,
                    InstalledModsState = installedModsState
                };
            }

            // Precedence 4: Packages Unknown
            if (pkgsState == PackageInstallationState.Unknown)
            {
                return new PackageMutationBlock
                {
                    IsBlocked = true,
                    Reason = "PackageStateUnknown",
                    PackagesState = pkgsState,
                    InstalledModsState = installedModsState
                };
            }

            // Precedence 5: InstalledMods PartiallyInstalled
            if (installedModsState == PackageInstallationState.PartiallyInstalled)
            {
                return new PackageMutationBlock
                {
                    IsBlocked = true,
                    Reason = "InstalledModStatePartiallyInstalled",
                    PackagesState = pkgsState,
                    InstalledModsState = installedModsState
                };
            }

            // Precedence 6: InstalledMods Unknown
            if (installedModsState == PackageInstallationState.Unknown)
            {
                return new PackageMutationBlock
                {
                    IsBlocked = true,
                    Reason = "InstalledModStateUnknown",
                    PackagesState = pkgsState,
                    InstalledModsState = installedModsState
                };
            }

            return new PackageMutationBlock
            {
                IsBlocked = false,
                Reason = null,
                PackagesState = pkgsState,
                InstalledModsState = installedModsState
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PackageMutationBlock
            {
                IsBlocked = true,
                Reason = "DatabaseException",
                BlockingOperationState = InstallOperationStatus.RecoveryRequired
            };
        }
    }
}
