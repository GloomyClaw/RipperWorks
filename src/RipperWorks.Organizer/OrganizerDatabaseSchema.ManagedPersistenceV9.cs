using System.Data;
using Microsoft.Data.Sqlite;

namespace RipperWorks.Organizer;

internal static partial class OrganizerDatabaseSchema
{
    public const string CanonicalInstallOperationsDdl = """
        CREATE TABLE IF NOT EXISTS InstallOperations (
            OperationId TEXT PRIMARY KEY,
            PackageId TEXT NOT NULL,
            OperationType TEXT NOT NULL,
            StartedAtUtc TEXT NOT NULL,
            CompletedAtUtc TEXT NULL,
            Status INTEGER NOT NULL,
            CurrentPhase INTEGER NOT NULL,
            ErrorMessage TEXT NULL,
            AppliedFileCount INTEGER NOT NULL DEFAULT 0,
            FOREIGN KEY (PackageId) REFERENCES Packages(PackageId) ON DELETE CASCADE);
        """;

    public const string CanonicalManagedPathsDdl = """
        CREATE TABLE IF NOT EXISTS ManagedPaths (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            RelativePath TEXT NOT NULL UNIQUE COLLATE NOCASE,
            BaseFileExisted INTEGER NOT NULL DEFAULT 0,
            BaseContentHash TEXT NULL);
        """;

    public const string CanonicalManagedPathLayersDdl = """
        CREATE TABLE IF NOT EXISTS ManagedPathLayers (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            RelativePath TEXT NOT NULL COLLATE NOCASE,
            LayerOrder INTEGER NOT NULL,
            PackageId TEXT NOT NULL,
            ContentHash TEXT NOT NULL,
            OperationId TEXT NULL,
            InstalledAtUtc TEXT NOT NULL,
            UNIQUE (RelativePath, LayerOrder),
            UNIQUE (RelativePath, PackageId),
            FOREIGN KEY (PackageId) REFERENCES Packages(PackageId) ON UPDATE CASCADE ON DELETE CASCADE,
            FOREIGN KEY (RelativePath) REFERENCES ManagedPaths(RelativePath) ON UPDATE CASCADE ON DELETE CASCADE);
        CREATE INDEX IF NOT EXISTS IX_ManagedPathLayers_PackageId ON ManagedPathLayers(PackageId);
        CREATE INDEX IF NOT EXISTS IX_ManagedPathLayers_RelativePath_LayerOrder ON ManagedPathLayers(RelativePath, LayerOrder);
        """;

    private static async Task MigrateFromVersion8To9Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            await MigrateInstallOperationsFromV8ToV9Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            await MigrateManagedPathsFromV8ToV9Async(connection, transaction, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            throw new InvalidOperationException("Unsupported organizer v8 schema shape for schema v9 migration.", ex);
        }
    }

    private static async Task MigrateInstallOperationsFromV8ToV9Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var fp = await InspectTableFingerprintAsync(connection, transaction, "InstallOperations", cancellationToken).ConfigureAwait(false);
        if (fp is null)
        {
            // Missing table in v8 migration -> FAIL CLOSED!
            throw new InvalidOperationException("InstallOperations table is missing in v8 database.");
        }

        var opsCount = await GetTableRowCountAsync(connection, transaction, "InstallOperations", cancellationToken).ConfigureAwait(false);

        if (IsExactInstallOperationsCategoryOA(fp))
        {
            // Category O-A: Canonical active Status shape
            return;
        }

        if (IsExactInstallOperationsCategoryOB(fp, opsCount))
        {
            // Category O-B: Known empty legacy State-only shape
            await ExecuteScriptAsync(connection, transaction, "DROP TABLE IF EXISTS InstallOperations;", cancellationToken).ConfigureAwait(false);
            await ExecuteScriptAsync(connection, transaction, CanonicalInstallOperationsDdl, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Category O-C: Fail closed!
        throw new InvalidOperationException("Unsupported InstallOperations v8 shape for schema v9 migration.");
    }

    private static bool IsExactInstallOperationsCategoryOA(TableFingerprint fp)
    {
        var expectedCols = new[]
        {
            new ColumnFingerprint(0, "OperationId", "TEXT", false, null, 1),
            new ColumnFingerprint(1, "PackageId", "TEXT", true, null, 0),
            new ColumnFingerprint(2, "OperationType", "TEXT", true, null, 0),
            new ColumnFingerprint(3, "StartedAtUtc", "TEXT", true, null, 0),
            new ColumnFingerprint(4, "CompletedAtUtc", "TEXT", false, null, 0),
            new ColumnFingerprint(5, "Status", "INTEGER", true, null, 0),
            new ColumnFingerprint(6, "CurrentPhase", "INTEGER", true, null, 0),
            new ColumnFingerprint(7, "ErrorMessage", "TEXT", false, null, 0),
            new ColumnFingerprint(8, "AppliedFileCount", "INTEGER", true, "0", 0)
        };

        if (fp.Columns.Count != 9) return false;
        for (int i = 0; i < 9; i++)
        {
            if (!AreColumnsEqual(fp.Columns[i], expectedCols[i]))
                return false;
        }

        if (fp.ForeignKeys.Count != 1) return false;
        var fk = fp.ForeignKeys[0];
        if (!string.Equals(fk.FromColumn, "PackageId", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(fk.ToTable, "Packages", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(fk.ToColumn, "PackageId", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(fk.OnUpdate, "NO ACTION", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(fk.OnDelete, "CASCADE", StringComparison.OrdinalIgnoreCase))
            return false;

        if (fp.Indexes.Count != 0) return false;

        return true;
    }

    private static bool IsExactInstallOperationsCategoryOB(TableFingerprint fp, long opsCount)
    {
        if (opsCount != 0) return false;
        if (fp.Indexes.Count != 0) return false;

        // Form A: 7 columns
        if (fp.Columns.Count == 7)
        {
            var expectedCols = new[]
            {
                new ColumnFingerprint(0, "OperationId", "TEXT", false, null, 1),
                new ColumnFingerprint(1, "ProfileId", "TEXT", true, null, 0),
                new ColumnFingerprint(2, "PackageId", "TEXT", true, null, 0),
                new ColumnFingerprint(3, "State", "INTEGER", true, "0", 0),
                new ColumnFingerprint(4, "StartedAtUtc", "TEXT", true, null, 0),
                new ColumnFingerprint(5, "CompletedAtUtc", "TEXT", false, null, 0),
                new ColumnFingerprint(6, "ErrorMessage", "TEXT", false, null, 0)
            };

            for (int i = 0; i < 7; i++)
            {
                if (!AreColumnsEqual(fp.Columns[i], expectedCols[i]))
                    return false;
            }

            if (fp.ForeignKeys.Count != 2) return false;

            var hasProfFk = fp.ForeignKeys.Any(fk =>
                string.Equals(fk.FromColumn, "ProfileId", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(fk.ToTable, "GameProfiles", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(fk.ToColumn, "ProfileId", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(fk.OnUpdate, "NO ACTION", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(fk.OnDelete, "CASCADE", StringComparison.OrdinalIgnoreCase));

            var hasPkgFk = fp.ForeignKeys.Any(fk =>
                string.Equals(fk.FromColumn, "PackageId", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(fk.ToTable, "Packages", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(fk.ToColumn, "PackageId", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(fk.OnUpdate, "NO ACTION", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(fk.OnDelete, "CASCADE", StringComparison.OrdinalIgnoreCase));

            return hasProfFk && hasPkgFk;
        }

        // Form B: 10 columns (incremental v7->v8)
        if (fp.Columns.Count == 10)
        {
            var expectedCols = new[]
            {
                new ColumnFingerprint(0, "OperationId", "TEXT", false, null, 1),
                new ColumnFingerprint(1, "ProfileId", "TEXT", true, null, 0),
                new ColumnFingerprint(2, "PackageId", "TEXT", true, null, 0),
                new ColumnFingerprint(3, "State", "INTEGER", true, "0", 0),
                new ColumnFingerprint(4, "Status", "INTEGER", true, "0", 0),
                new ColumnFingerprint(5, "CurrentPhase", "INTEGER", true, "0", 0),
                new ColumnFingerprint(6, "AppliedFileCount", "INTEGER", true, "0", 0),
                new ColumnFingerprint(7, "StartedAtUtc", "TEXT", true, null, 0),
                new ColumnFingerprint(8, "CompletedAtUtc", "TEXT", false, null, 0),
                new ColumnFingerprint(9, "ErrorMessage", "TEXT", false, null, 0)
            };

            for (int i = 0; i < 10; i++)
            {
                if (!AreColumnsEqual(fp.Columns[i], expectedCols[i]))
                    return false;
            }

            if (fp.ForeignKeys.Count != 2) return false;

            var hasProfFk = fp.ForeignKeys.Any(fk =>
                string.Equals(fk.FromColumn, "ProfileId", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(fk.ToTable, "GameProfiles", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(fk.ToColumn, "ProfileId", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(fk.OnUpdate, "NO ACTION", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(fk.OnDelete, "CASCADE", StringComparison.OrdinalIgnoreCase));

            var hasPkgFk = fp.ForeignKeys.Any(fk =>
                string.Equals(fk.FromColumn, "PackageId", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(fk.ToTable, "Packages", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(fk.ToColumn, "PackageId", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(fk.OnUpdate, "NO ACTION", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(fk.OnDelete, "CASCADE", StringComparison.OrdinalIgnoreCase));

            return hasProfFk && hasPkgFk;
        }

        return false;
    }

    private static async Task MigrateManagedPathsFromV8ToV9Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var pathsFp = await InspectTableFingerprintAsync(connection, transaction, "ManagedPaths", cancellationToken).ConfigureAwait(false);
        var layersFp = await InspectTableFingerprintAsync(connection, transaction, "ManagedPathLayers", cancellationToken).ConfigureAwait(false);

        if (pathsFp is null || layersFp is null)
        {
            // Missing managed table in v8 migration -> FAIL CLOSED!
            throw new InvalidOperationException("ManagedPaths or ManagedPathLayers table is missing in v8 database.");
        }

        var pathsCount = await GetTableRowCountAsync(connection, transaction, "ManagedPaths", cancellationToken).ConfigureAwait(false);
        var layersCount = await GetTableRowCountAsync(connection, transaction, "ManagedPathLayers", cancellationToken).ConfigureAwait(false);

        if (IsExactManagedPathsCategoryMA(pathsFp) && IsExactManagedPathLayersCategoryMA(layersFp))
        {
            // Category M-A: Exact canonical active shape
            await ExecuteScriptAsync(connection, transaction, """
                CREATE INDEX IF NOT EXISTS IX_ManagedPathLayers_PackageId ON ManagedPathLayers(PackageId);
                CREATE INDEX IF NOT EXISTS IX_ManagedPathLayers_RelativePath_LayerOrder ON ManagedPathLayers(RelativePath, LayerOrder);
                """, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (IsExactManagedPathsCategoryMB(pathsFp, pathsCount) && IsExactManagedPathLayersCategoryMB(layersFp, layersCount))
        {
            var isSafeEmpty = await VerifySafeEmptyPlaceholderStateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (isSafeEmpty)
            {
                // Category M-B: Exact empty placeholder shape
                await ExecuteScriptAsync(connection, transaction, """
                    DROP TABLE IF EXISTS ManagedPathLayers;
                    DROP TABLE IF EXISTS ManagedPaths;
                    """, cancellationToken).ConfigureAwait(false);

                await ExecuteScriptAsync(connection, transaction, CanonicalManagedPathsDdl, cancellationToken).ConfigureAwait(false);
                await ExecuteScriptAsync(connection, transaction, CanonicalManagedPathLayersDdl, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        // Category M-C: Fail closed!
        throw new InvalidOperationException("Unsupported ManagedPaths/ManagedPathLayers v8 shape for schema v9 migration.");
    }

    private static bool IsExactManagedPathsCategoryMA(TableFingerprint fp)
    {
        if (!IsColumnCollationNoCase(fp.Sql, "RelativePath") ||
            !fp.Sql.Contains("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase))
            return false;

        var expectedCols = new[]
        {
            new ColumnFingerprint(0, "Id", "INTEGER", false, null, 1),
            new ColumnFingerprint(1, "RelativePath", "TEXT", true, null, 0),
            new ColumnFingerprint(2, "BaseFileExisted", "INTEGER", true, "0", 0),
            new ColumnFingerprint(3, "BaseContentHash", "TEXT", false, null, 0)
        };

        if (fp.Columns.Count != 4) return false;
        for (int i = 0; i < 4; i++)
        {
            if (!AreColumnsEqual(fp.Columns[i], expectedCols[i]))
                return false;
        }

        if (fp.ForeignKeys.Count != 0) return false;

        // Exactly 1 UNIQUE autoindex on RelativePath (origin = "u", non-partial)
        if (fp.Indexes.Count != 1) return false;
        var idx = fp.Indexes[0];
        if (!idx.IsUnique || idx.IsPartial || idx.Origin != "u" || idx.Columns.Count != 1 || !string.Equals(idx.Columns[0], "RelativePath", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static bool IsExactManagedPathLayersCategoryMA(TableFingerprint fp)
    {
        if (!IsColumnCollationNoCase(fp.Sql, "RelativePath") ||
            !fp.Sql.Contains("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase))
            return false;

        var expectedCols = new[]
        {
            new ColumnFingerprint(0, "Id", "INTEGER", false, null, 1),
            new ColumnFingerprint(1, "RelativePath", "TEXT", true, null, 0),
            new ColumnFingerprint(2, "LayerOrder", "INTEGER", true, null, 0),
            new ColumnFingerprint(3, "PackageId", "TEXT", true, null, 0),
            new ColumnFingerprint(4, "ContentHash", "TEXT", true, null, 0),
            new ColumnFingerprint(5, "OperationId", "TEXT", false, null, 0),
            new ColumnFingerprint(6, "InstalledAtUtc", "TEXT", true, null, 0)
        };

        if (fp.Columns.Count != 7) return false;
        for (int i = 0; i < 7; i++)
        {
            if (!AreColumnsEqual(fp.Columns[i], expectedCols[i]))
                return false;
        }

        // Foreign keys check (exactly 2)
        if (fp.ForeignKeys.Count != 2) return false;

        var hasPkgFk = fp.ForeignKeys.Any(fk =>
            string.Equals(fk.FromColumn, "PackageId", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(fk.ToTable, "Packages", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(fk.ToColumn, "PackageId", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(fk.OnUpdate, "CASCADE", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(fk.OnDelete, "CASCADE", StringComparison.OrdinalIgnoreCase));

        var hasPathFk = fp.ForeignKeys.Any(fk =>
            string.Equals(fk.FromColumn, "RelativePath", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(fk.ToTable, "ManagedPaths", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(fk.ToColumn, "RelativePath", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(fk.OnUpdate, "CASCADE", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(fk.OnDelete, "CASCADE", StringComparison.OrdinalIgnoreCase));

        if (!hasPkgFk || !hasPathFk) return false;

        // Exactly 4 indexes (2 unique autoindexes + 2 explicit non-unique non-partial indexes)
        if (fp.Indexes.Count != 4) return false;

        var uniqueIdxs = fp.Indexes.Where(idx => idx.IsUnique && idx.Origin == "u" && !idx.IsPartial).ToList();
        if (uniqueIdxs.Count != 2) return false;

        var hasUniquePathOrder = uniqueIdxs.Any(idx => idx.Columns.Count == 2 &&
            string.Equals(idx.Columns[0], "RelativePath", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(idx.Columns[1], "LayerOrder", StringComparison.OrdinalIgnoreCase));

        var hasUniquePathPkg = uniqueIdxs.Any(idx => idx.Columns.Count == 2 &&
            string.Equals(idx.Columns[0], "RelativePath", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(idx.Columns[1], "PackageId", StringComparison.OrdinalIgnoreCase));

        if (!hasUniquePathOrder || !hasUniquePathPkg) return false;

        var explicitIdxs = fp.Indexes.Where(idx => !idx.IsUnique && idx.Origin == "c" && !idx.IsPartial).ToList();
        if (explicitIdxs.Count != 2) return false;

        var hasPkgIdx = explicitIdxs.Any(idx => string.Equals(idx.Name, "IX_ManagedPathLayers_PackageId", StringComparison.OrdinalIgnoreCase) &&
            idx.Columns.Count == 1 && string.Equals(idx.Columns[0], "PackageId", StringComparison.OrdinalIgnoreCase));

        var hasPathOrderIdx = explicitIdxs.Any(idx => string.Equals(idx.Name, "IX_ManagedPathLayers_RelativePath_LayerOrder", StringComparison.OrdinalIgnoreCase) &&
            idx.Columns.Count == 2 && string.Equals(idx.Columns[0], "RelativePath", StringComparison.OrdinalIgnoreCase) && string.Equals(idx.Columns[1], "LayerOrder", StringComparison.OrdinalIgnoreCase));

        if (!hasPkgIdx || !hasPathOrderIdx) return false;

        return true;
    }

    private static bool IsExactManagedPathsCategoryMB(TableFingerprint fp, long count)
    {
        if (count != 0) return false;
        if (!IsColumnCollationNoCase(fp.Sql, "RelativePath") ||
            !fp.Sql.Contains("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase))
            return false;

        var expectedCols = new[]
        {
            new ColumnFingerprint(0, "Id", "INTEGER", false, null, 1),
            new ColumnFingerprint(1, "RelativePath", "TEXT", true, null, 0),
            new ColumnFingerprint(2, "AddedAtUtc", "TEXT", true, null, 0)
        };

        if (fp.Columns.Count != 3) return false;
        for (int i = 0; i < 3; i++)
        {
            if (!AreColumnsEqual(fp.Columns[i], expectedCols[i]))
                return false;
        }

        if (fp.ForeignKeys.Count != 0) return false;

        if (fp.Indexes.Count != 1) return false;
        var idx = fp.Indexes[0];
        if (!idx.IsUnique || idx.IsPartial || idx.Origin != "u" || idx.Columns.Count != 1 || !string.Equals(idx.Columns[0], "RelativePath", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static bool IsExactManagedPathLayersCategoryMB(TableFingerprint fp, long count)
    {
        if (count != 0) return false;
        if (!IsColumnCollationNoCase(fp.Sql, "LayerName") ||
            !fp.Sql.Contains("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase))
            return false;

        var expectedCols = new[]
        {
            new ColumnFingerprint(0, "Id", "INTEGER", false, null, 1),
            new ColumnFingerprint(1, "LayerName", "TEXT", true, null, 0),
            new ColumnFingerprint(2, "SortOrder", "INTEGER", true, null, 0),
            new ColumnFingerprint(3, "IsEnabled", "INTEGER", true, null, 0),
            new ColumnFingerprint(4, "AddedAtUtc", "TEXT", true, null, 0)
        };

        if (fp.Columns.Count != 5) return false;
        for (int i = 0; i < 5; i++)
        {
            if (!AreColumnsEqual(fp.Columns[i], expectedCols[i]))
                return false;
        }

        if (fp.ForeignKeys.Count != 0) return false;

        if (fp.Indexes.Count != 1) return false;
        var idx = fp.Indexes[0];
        if (!idx.IsUnique || idx.IsPartial || idx.Origin != "u" || idx.Columns.Count != 1 || !string.Equals(idx.Columns[0], "LayerName", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static bool IsColumnCollationNoCase(string tableSql, string columnName)
    {
        var lines = tableSql.Split('\n');
        foreach (var line in lines)
        {
            if (line.Contains(columnName, StringComparison.OrdinalIgnoreCase) &&
                line.Contains("COLLATE NOCASE", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static async Task<bool> VerifySafeEmptyPlaceholderStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        // 1. InstalledMods count == 0
        var hasInstalledMods = await TableExistsAsync(connection, transaction, "InstalledMods", cancellationToken).ConfigureAwait(false);
        if (hasInstalledMods)
        {
            var count = await GetTableRowCountAsync(connection, transaction, "InstalledMods", cancellationToken).ConfigureAwait(false);
            if (count > 0) return false;
        }

        // 2. InstalledFiles count == 0
        var hasInstalledFiles = await TableExistsAsync(connection, transaction, "InstalledFiles", cancellationToken).ConfigureAwait(false);
        if (hasInstalledFiles)
        {
            var count = await GetTableRowCountAsync(connection, transaction, "InstalledFiles", cancellationToken).ConfigureAwait(false);
            if (count > 0) return false;
        }

        // 3. Unsafe Packages installation states (1 = Installed, 2 = PartiallyInstalled, 3 = Unknown)
        var hasPackages = await TableExistsAsync(connection, transaction, "Packages", cancellationToken).ConfigureAwait(false);
        if (hasPackages)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT COUNT(*) FROM Packages WHERE InstallationState IN (1, 2, 3);";
            var unsafeCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            if (unsafeCount > 0) return false;
        }

        return true;
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name = $name;";
        cmd.Parameters.AddWithValue("$name", tableName);
        var res = Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        return res > 0;
    }

    private static async Task<long> GetTableRowCountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        var res = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(res);
    }
}
