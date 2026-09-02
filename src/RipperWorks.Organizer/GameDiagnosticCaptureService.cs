using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using RipperWorks.Core;

namespace RipperWorks.Organizer;

public sealed class GameDiagnosticCaptureService : IGameDiagnosticCaptureService
{
    private const int MaxBoundaryBytes = 4096;
    private readonly IInstalledFrameworkStateProvider _frameworkStateProvider;
    private readonly string _localAppDataRoot;
    private readonly Func<string, IReadOnlyList<string>, (bool Success, bool Missing, IReadOnlyList<string> Files)>? _customFileEnumerator;

    private enum ContinuityCheckResult
    {
        Match,
        Mismatch,
        Unavailable
    }

    public GameDiagnosticCaptureService(
        IInstalledFrameworkStateProvider frameworkStateProvider,
        string? customLocalAppDataRoot = null,
        Func<string, IReadOnlyList<string>, (bool Success, bool Missing, IReadOnlyList<string> Files)>? customFileEnumerator = null)
    {
        _frameworkStateProvider = frameworkStateProvider ?? throw new ArgumentNullException(nameof(frameworkStateProvider));
        _localAppDataRoot = string.IsNullOrWhiteSpace(customLocalAppDataRoot)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : customLocalAppDataRoot;
        _customFileEnumerator = customFileEnumerator;
    }

    public async Task<DiagnosticSessionSnapshot> CapturePreLaunchSnapshotAsync(
        GameProfileRecord profile,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var timestampUtc = DateTimeOffset.UtcNow;
        var sources = new List<DiagnosticSourceSnapshot>();

        // 1. AlwaysActive sources (Cyberpunk / REDengine ReportQueue)
        foreach (var def in DiagnosticSourceRegistry.SupportedSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (def.AlwaysActive)
                sources.Add(CaptureSourcePreSnapshot(profile, def));
        }

        // 2. Query framework state provider fail-softly
        InstalledFrameworkState? installedState = null;
        try
        {
            installedState = await _frameworkStateProvider
                .GetInstalledFrameworkStateAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* Fail-soft: lookup error does not drop REDengine or block launch */ }

        // 3. Capture active framework sources if state was successfully resolved
        if (installedState is not null)
        {
            foreach (var def in DiagnosticSourceRegistry.SupportedSources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!def.AlwaysActive && def.IsActive(installedState))
                    sources.Add(CaptureSourcePreSnapshot(profile, def));
            }
        }

        return new DiagnosticSessionSnapshot(sessionId, timestampUtc, sources);
    }

    public Task<GameDiagnosticCaptureResult> CapturePostSessionSnapshotAsync(
        GameProfileRecord profile,
        DiagnosticSessionSnapshot preLaunchSnapshot,
        CancellationToken cancellationToken = default)
    {
        var sourceDeltas = new List<DiagnosticSourceDelta>();
        var activeSourceIds = new List<string>();

        if (preLaunchSnapshot is null)
            return Task.FromResult(new GameDiagnosticCaptureResult(string.Empty, activeSourceIds, sourceDeltas));

        foreach (var preSource in preLaunchSnapshot.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            activeSourceIds.Add(preSource.SourceId);

            var def = FindDefinition(preSource.SourceId);
            if (def is not null)
                sourceDeltas.Add(CaptureSourcePostDelta(profile, def, preSource));
        }

        return Task.FromResult(new GameDiagnosticCaptureResult(preLaunchSnapshot.SessionId, activeSourceIds, sourceDeltas));
    }

    private DiagnosticSourceSnapshot CaptureSourcePreSnapshot(GameProfileRecord profile, DiagnosticSourceDefinition def)
    {
        var targetDir = ResolveTargetDirectory(profile, def);

        if (def.IsDirectorySource)
        {
            try
            {
                if (!Directory.Exists(targetDir))
                    return new DiagnosticSourceSnapshot(def.SourceId, def.DisplayName, DiagnosticCaptureStatus.Missing, [], []);

                var subDirs = Directory.GetDirectories(targetDir);
                var dirNames = new List<string>(subDirs.Length);
                foreach (var dir in subDirs) dirNames.Add(Path.GetFileName(dir));
                return new DiagnosticSourceSnapshot(def.SourceId, def.DisplayName, DiagnosticCaptureStatus.Success, [], dirNames);
            }
            catch
            {
                return new DiagnosticSourceSnapshot(def.SourceId, def.DisplayName, DiagnosticCaptureStatus.Unavailable, [], []);
            }
        }

        var (enumSuccess, enumMissing, matchedFiles) = EnumerateFiles(targetDir, def.FilePatterns);
        if (enumMissing)
            return new DiagnosticSourceSnapshot(def.SourceId, def.DisplayName, DiagnosticCaptureStatus.Missing, [], []);
        if (!enumSuccess)
            return new DiagnosticSourceSnapshot(def.SourceId, def.DisplayName, DiagnosticCaptureStatus.Unavailable, [], []);

        var fileSnapshots = new List<DiagnosticFileSnapshot>(matchedFiles.Count);
        foreach (var filePath in matchedFiles)
        {
            try
            {
                var fi = new FileInfo(filePath);
                if (!fi.Exists) continue;
                var (hHash, hLen, tHash, tLen, fpOk) = ComputeFingerprint(filePath, fi.Length);
                fileSnapshots.Add(new DiagnosticFileSnapshot(fi.FullName, true, fi.Length, fi.CreationTimeUtc, fi.LastWriteTimeUtc, hHash, hLen, tHash, tLen, fpOk));
            }
            catch
            {
                fileSnapshots.Add(new DiagnosticFileSnapshot(filePath, true, 0, null, null, null, 0, null, 0, false));
            }
        }

        var allFpOk = fileSnapshots.All(f => f.FingerprintAvailable);
        var preStatus = allFpOk ? DiagnosticCaptureStatus.Success : DiagnosticCaptureStatus.Unavailable;
        return new DiagnosticSourceSnapshot(def.SourceId, def.DisplayName, preStatus, fileSnapshots, []);
    }

    private DiagnosticSourceDelta CaptureSourcePostDelta(GameProfileRecord profile, DiagnosticSourceDefinition def, DiagnosticSourceSnapshot preSource)
    {
        var targetDir = ResolveTargetDirectory(profile, def);

        if (def.IsDirectorySource)
        {
            try
            {
                if (!Directory.Exists(targetDir))
                {
                    var st = preSource.Status == DiagnosticCaptureStatus.Missing ? DiagnosticCaptureStatus.Missing : DiagnosticCaptureStatus.Unavailable;
                    return new DiagnosticSourceDelta(def.SourceId, def.DisplayName, st, [], []);
                }

                var existingSet = new HashSet<string>(preSource.ExistingReportDirectories, StringComparer.OrdinalIgnoreCase);
                var newDirs = new List<DiagnosticReportDirectory>();
                foreach (var dir in Directory.GetDirectories(targetDir))
                {
                    var dirName = Path.GetFileName(dir);
                    if (!existingSet.Contains(dirName))
                    {
                        DateTimeOffset? c = null, lw = null;
                        try { var di = new DirectoryInfo(dir); c = di.CreationTimeUtc; lw = di.LastWriteTimeUtc; } catch { }
                        newDirs.Add(new DiagnosticReportDirectory(dir, dirName, c, lw));
                    }
                }
                return new DiagnosticSourceDelta(def.SourceId, def.DisplayName, DiagnosticCaptureStatus.Success, [], newDirs);
            }
            catch
            {
                return new DiagnosticSourceDelta(def.SourceId, def.DisplayName, DiagnosticCaptureStatus.Unavailable, [], []);
            }
        }

        var (enumSuccess, enumMissing, currentFiles) = EnumerateFiles(targetDir, def.FilePatterns);
        if (enumMissing)
        {
            var deletedFiles = new List<DiagnosticFileDelta>();
            foreach (var pf in preSource.Files)
                if (pf.Exists) deletedFiles.Add(new DiagnosticFileDelta(pf.FullPath, DiagnosticMutationKind.Deleted, 0, 0));

            var st = preSource.Status == DiagnosticCaptureStatus.Missing ? DiagnosticCaptureStatus.Missing : DiagnosticCaptureStatus.Unavailable;
            return new DiagnosticSourceDelta(def.SourceId, def.DisplayName, st, deletedFiles, []);
        }

        if (!enumSuccess)
            return new DiagnosticSourceDelta(def.SourceId, def.DisplayName, DiagnosticCaptureStatus.Unavailable, [], []);

        var preMap = new Dictionary<string, DiagnosticFileSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var pf in preSource.Files) preMap[pf.FullPath] = pf;

        var currentMap = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in currentFiles)
        {
            try { var fi = new FileInfo(f); if (fi.Exists) currentMap[fi.FullName] = fi; } catch { }
        }

        var allPaths = new HashSet<string>(preMap.Keys, StringComparer.OrdinalIgnoreCase);
        allPaths.UnionWith(currentMap.Keys);

        var fileDeltas = new List<DiagnosticFileDelta>();
        foreach (var path in allPaths)
        {
            var inPre = preMap.TryGetValue(path, out var preFile);
            var inPost = currentMap.TryGetValue(path, out var postInfo);

            if (!inPre && inPost)
                fileDeltas.Add(new DiagnosticFileDelta(path, DiagnosticMutationKind.Created, 0, postInfo!.Length));
            else if (inPre && !inPost)
                fileDeltas.Add(new DiagnosticFileDelta(path, DiagnosticMutationKind.Deleted, 0, 0));
            else if (inPre && inPost)
                fileDeltas.Add(ComputeFileDelta(preFile!, postInfo!));
        }

        var hasUnavailable = fileDeltas.Any(f => f.MutationKind == DiagnosticMutationKind.Unavailable) ||
                             preSource.Status == DiagnosticCaptureStatus.Unavailable;
        var captureStatus = hasUnavailable ? DiagnosticCaptureStatus.Unavailable : DiagnosticCaptureStatus.Success;

        return new DiagnosticSourceDelta(def.SourceId, def.DisplayName, captureStatus, fileDeltas, []);
    }

    private static DiagnosticFileDelta ComputeFileDelta(DiagnosticFileSnapshot preFile, FileInfo postInfo)
    {
        if (!preFile.FingerprintAvailable)
            return new DiagnosticFileDelta(postInfo.FullName, DiagnosticMutationKind.Unavailable, 0, 0);

        var postLength = postInfo.Length;
        var preLength = preFile.Length;

        if (postLength < preLength)
            return new DiagnosticFileDelta(postInfo.FullName, DiagnosticMutationKind.ReplacedOrTruncated, 0, postLength);

        if (postLength == preLength)
        {
            if (preLength == 0)
                return new DiagnosticFileDelta(postInfo.FullName, DiagnosticMutationKind.Unchanged, 0, 0);

            var (postHead, _, postTail, _, postFpOk) = ComputeFingerprint(postInfo.FullName, postLength);
            if (!postFpOk)
                return new DiagnosticFileDelta(postInfo.FullName, DiagnosticMutationKind.Unavailable, 0, 0);

            var headMatch = string.Equals(preFile.HeadHash, postHead, StringComparison.OrdinalIgnoreCase);
            var tailMatch = string.Equals(preFile.TailHash, postTail, StringComparison.OrdinalIgnoreCase);
            if (!headMatch || !tailMatch)
                return new DiagnosticFileDelta(postInfo.FullName, DiagnosticMutationKind.ReplacedOrTruncated, 0, postLength);

            var timeMatch = preFile.LastWriteUtc.HasValue && postInfo.LastWriteTimeUtc == preFile.LastWriteUtc.Value;
            var creationMatch = !preFile.CreationTimeUtc.HasValue || postInfo.CreationTimeUtc == preFile.CreationTimeUtc.Value;

            if (timeMatch && creationMatch)
                return new DiagnosticFileDelta(postInfo.FullName, DiagnosticMutationKind.Unchanged, 0, 0);

            if (preLength <= MaxBoundaryBytes * 2)
                return new DiagnosticFileDelta(postInfo.FullName, DiagnosticMutationKind.Unchanged, 0, 0);

            return new DiagnosticFileDelta(postInfo.FullName, DiagnosticMutationKind.ReplacedOrTruncated, 0, postLength);
        }

        if (preFile.CreationTimeUtc.HasValue && postInfo.CreationTimeUtc != preFile.CreationTimeUtc.Value)
            return new DiagnosticFileDelta(postInfo.FullName, DiagnosticMutationKind.ReplacedOrTruncated, 0, postLength);

        var continuity = VerifyAppendContinuity(postInfo.FullName, preLength, preFile.HeadHash, preFile.HeadLength, preFile.TailHash, preFile.TailLength);
        if (continuity == ContinuityCheckResult.Match)
            return new DiagnosticFileDelta(postInfo.FullName, DiagnosticMutationKind.Appended, preLength, postLength - preLength);

        if (continuity == ContinuityCheckResult.Mismatch)
            return new DiagnosticFileDelta(postInfo.FullName, DiagnosticMutationKind.ReplacedOrTruncated, 0, postLength);

        return new DiagnosticFileDelta(postInfo.FullName, DiagnosticMutationKind.Unavailable, 0, 0);
    }

    private string ResolveTargetDirectory(GameProfileRecord profile, DiagnosticSourceDefinition def) =>
        def.RootKind switch
        {
            DiagnosticSourceRootKind.LocalAppData => Path.Combine(_localAppDataRoot, def.RelativeDirectory.Replace('/', Path.DirectorySeparatorChar)),
            _ => Path.Combine(profile.GameRoot, def.RelativeDirectory.Replace('/', Path.DirectorySeparatorChar))
        };

    private (bool Success, bool Missing, IReadOnlyList<string> Files) EnumerateFiles(string directoryPath, IReadOnlyList<string> patterns)
    {
        if (_customFileEnumerator is not null)
            return _customFileEnumerator(directoryPath, patterns);

        try
        {
            if (!Directory.Exists(directoryPath))
                return (false, true, []);

            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pattern in patterns)
            {
                foreach (var f in Directory.GetFiles(directoryPath, pattern, SearchOption.TopDirectoryOnly))
                    results.Add(Path.GetFullPath(f));
            }
            return (true, false, [.. results]);
        }
        catch (DirectoryNotFoundException) { return (false, true, []); }
        catch { return (false, false, []); }
    }

    private static (string? HeadHash, int HeadLength, string? TailHash, int TailLength, bool Success) ComputeFingerprint(string filePath, long fileLength)
    {
        if (fileLength <= 0)
            return (null, 0, null, 0, true);

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var headLen = (int)Math.Min(fileLength, MaxBoundaryBytes);
            var headBuf = ReadBytes(stream, 0, headLen);
            if (headBuf is null) return (null, 0, null, 0, false);
            var headHash = HashBytes(headBuf);

            if (fileLength <= MaxBoundaryBytes)
                return (headHash, headLen, headHash, headLen, true);

            var tailLen = (int)Math.Min(fileLength, MaxBoundaryBytes);
            var tailBuf = ReadBytes(stream, fileLength - tailLen, tailLen);
            if (tailBuf is null) return (null, 0, null, 0, false);
            return (headHash, headLen, HashBytes(tailBuf), tailLen, true);
        }
        catch { return (null, 0, null, 0, false); }
    }

    private static ContinuityCheckResult VerifyAppendContinuity(string filePath, long preLength, string? preHeadHash, int preHeadLen, string? preTailHash, int preTailLen)
    {
        if (preLength == 0) return ContinuityCheckResult.Match;
        if (string.IsNullOrEmpty(preHeadHash) || preHeadLen <= 0 || string.IsNullOrEmpty(preTailHash) || preTailLen <= 0)
            return ContinuityCheckResult.Unavailable;

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < preLength) return ContinuityCheckResult.Mismatch;

            var headBuf = ReadBytes(stream, 0, preHeadLen);
            if (headBuf is null) return ContinuityCheckResult.Unavailable;
            if (!string.Equals(HashBytes(headBuf), preHeadHash, StringComparison.OrdinalIgnoreCase))
                return ContinuityCheckResult.Mismatch;

            var tailBuf = ReadBytes(stream, preLength - preTailLen, preTailLen);
            if (tailBuf is null) return ContinuityCheckResult.Unavailable;
            if (!string.Equals(HashBytes(tailBuf), preTailHash, StringComparison.OrdinalIgnoreCase))
                return ContinuityCheckResult.Mismatch;

            return ContinuityCheckResult.Match;
        }
        catch { return ContinuityCheckResult.Unavailable; }
    }

    private static byte[]? ReadBytes(Stream stream, long offset, int count)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var r = stream.Read(buffer, read, count - read);
            if (r == 0) break;
            read += r;
        }
        return read == count ? buffer : null;
    }

    private static string HashBytes(byte[] buffer)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(buffer));
    }

    private static DiagnosticSourceDefinition? FindDefinition(string sourceId)
    {
        foreach (var def in DiagnosticSourceRegistry.SupportedSources)
            if (string.Equals(def.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)) return def;
        return null;
    }
}
