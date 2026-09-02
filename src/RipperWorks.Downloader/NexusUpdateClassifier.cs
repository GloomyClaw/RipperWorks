using RipperWorks.Core;

namespace RipperWorks.Downloader;

public readonly record struct NexusUpdateCandidateKey(
    string GameDomain,
    long ModId,
    long FileId);

public static class NexusUpdateClassifier
{
    public const string UpdateAvailableFilterValue =
        "UpdateAvailable";

    public static NexusModUpdateCheckResult Classify(
        NexusUpdateFileVersion current,
        NexusUpdateFileVersion candidate)
    {
        if (current.NumericFileId == candidate.NumericFileId)
        {
            return Result(
                NexusUpdateCheckStatus.UpToDate,
                current,
                candidate,
                "Текущий и найденный файл имеют один Nexus file_id.",
                isCurrentLatest: true);
        }

        if (string.IsNullOrWhiteSpace(current.ModFileUuid) ||
            string.IsNullOrWhiteSpace(candidate.ModFileUuid))
        {
            return Result(
                NexusUpdateCheckStatus.UnknownComparison,
                current,
                candidate,
                "Nexus не предоставил устойчивую UUID-цепочку файла.");
        }

        if (!string.Equals(
                current.ModFileUuid,
                candidate.ModFileUuid,
                StringComparison.OrdinalIgnoreCase))
        {
            return Result(
                NexusUpdateCheckStatus.DifferentComponent,
                current,
                candidate,
                "Файлы принадлежат разным официальным UUID-цепочкам.");
        }

        var currentComparable =
            TryParseVersion(current.Version, out var currentVersion);
        var candidateComparable =
            TryParseVersion(candidate.Version, out var candidateVersion);
        var versionsComparable =
            currentComparable && candidateComparable;
        if (versionsComparable)
        {
            var comparison = candidateVersion!.CompareTo(currentVersion);
            if (comparison < 0)
            {
                return Result(
                    NexusUpdateCheckStatus.OlderVersion,
                    current,
                    candidate,
                    "Найденный файл имеет более низкую версию.");
            }
            if (comparison > 0)
            {
                return HasRecencyConflict(current, candidate)
                    ? UnknownRecency(current, candidate)
                    : Available(current, candidate);
            }
        }

        return HasReliableLaterPosition(current, candidate) &&
               !HasTimestampConflict(current, candidate)
            ? Available(current, candidate)
            : Result(
                NexusUpdateCheckStatus.UnknownComparison,
                current,
                candidate,
                versionsComparable
                    ? "Одинаковая версия не подтверждена более новым " +
                      "положением файла Nexus."
                    : "Версии нельзя безопасно сравнить, а надёжных " +
                      "данных о более новом файле недостаточно.");
    }

    public static NexusModUpdateCheckResult SelectCandidate(
        NexusUpdateFileVersion current,
        IReadOnlyCollection<NexusUpdateFileVersion> candidates)
    {
        var otherCandidates = candidates
            .Where(c => c.NumericFileId != current.NumericFileId)
            .OrderBy(c => c.Position)
            .ThenBy(c => c.NumericFileId)
            .ToArray();

        if (otherCandidates.Length == 0)
        {
            return Classify(current, current);
        }

        var classified = otherCandidates
            .Select(candidate => (
                Candidate: candidate,
                Result: Classify(current, candidate)))
            .ToArray();

        var updateAvailable = classified
            .Where(pair => pair.Result.Status == NexusUpdateCheckStatus.UpdateAvailable)
            .ToArray();

        if (updateAvailable.Length > 0)
        {
            var maxPosition = updateAvailable.Max(pair => pair.Candidate.Position);
            var topCandidates = updateAvailable
                .Where(pair => pair.Candidate.Position == maxPosition)
                .ToArray();

            if (topCandidates.Length != 1)
            {
                return Result(
                    NexusUpdateCheckStatus.UnknownComparison,
                    current,
                    topCandidates[0].Candidate,
                    "Найдено несколько кандидатов на обновление с одинаковым положением Nexus.");
            }

            return topCandidates[0].Result;
        }

        var unknown = classified
            .Where(pair => pair.Result.Status is
                NexusUpdateCheckStatus.UnknownComparison or
                NexusUpdateCheckStatus.DifferentComponent)
            .ToArray();

        if (unknown.Length > 0)
        {
            return unknown[0].Result;
        }

        var latestPosition = otherCandidates.Max(candidate => candidate.Position);
        var latestCandidates = otherCandidates
            .Where(candidate => candidate.Position == latestPosition)
            .ToArray();

        if (latestCandidates.Length != 1)
        {
            return Result(
                NexusUpdateCheckStatus.UnknownComparison,
                current,
                latestCandidates[0],
                "Найдено несколько файлов с одинаковым положением Nexus.");
        }

        return Classify(current, latestCandidates[0]);
    }

    public static bool IsUpdateAvailable(DownloaderEntry entry) =>
        entry.UpdateCheckStatus ==
            NexusUpdateCheckStatus.UpdateAvailable &&
        entry.Source == DownloaderSource.Nexus &&
        entry.NexusModId is not null &&
        entry.AvailableFileId is not null &&
        !string.IsNullOrWhiteSpace(entry.GameDomain);

    public static bool TryGetCandidateKey(
        DownloaderEntry entry,
        out NexusUpdateCandidateKey key)
    {
        if (!IsUpdateAvailable(entry))
        {
            key = default;
            return false;
        }
        key = new(
            entry.GameDomain.Trim().ToLowerInvariant(),
            entry.NexusModId!.Value,
            entry.AvailableFileId!.Value);
        return true;
    }

    public static bool TryGetCurrentFileKey(
        DownloaderEntry entry,
        out NexusUpdateCandidateKey key)
    {
        if (entry.Source != DownloaderSource.Nexus ||
            entry.NexusModId is null ||
            entry.NexusFileId is null ||
            string.IsNullOrWhiteSpace(entry.GameDomain))
        {
            key = default;
            return false;
        }
        key = new(
            entry.GameDomain.Trim().ToLowerInvariant(),
            entry.NexusModId.Value,
            entry.NexusFileId.Value);
        return true;
    }

    private static bool TryParseVersion(
        string value,
        out Version? version)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return Version.TryParse(trimmed, out version) &&
            version.Major >= 0 &&
            version.Minor >= 0;
    }

    private static bool HasRecencyConflict(
        NexusUpdateFileVersion current,
        NexusUpdateFileVersion candidate) =>
        current.Position > 0 &&
        candidate.Position > 0 &&
        candidate.Position <= current.Position ||
        HasTimestampConflict(current, candidate);

    private static bool HasReliableLaterPosition(
        NexusUpdateFileVersion current,
        NexusUpdateFileVersion candidate) =>
        current.Position > 0 &&
        candidate.Position > current.Position;

    private static bool HasTimestampConflict(
        NexusUpdateFileVersion current,
        NexusUpdateFileVersion candidate) =>
        current.UploadedAt is { } currentTime &&
        candidate.UploadedAt is { } candidateTime &&
        candidateTime <= currentTime;

    private static NexusModUpdateCheckResult Available(
        NexusUpdateFileVersion current,
        NexusUpdateFileVersion candidate) =>
        Result(
            NexusUpdateCheckStatus.UpdateAvailable,
            current,
            candidate,
            "Найдена более новая версия той же официальной " +
            "файловой цепочки.");

    private static NexusModUpdateCheckResult UnknownRecency(
        NexusUpdateFileVersion current,
        NexusUpdateFileVersion candidate) =>
        Result(
            NexusUpdateCheckStatus.UnknownComparison,
            current,
            candidate,
            "Версия выше, но структурированные признаки новизны " +
            "Nexus противоречат друг другу.");

    private static NexusModUpdateCheckResult Result(
        NexusUpdateCheckStatus status,
        NexusUpdateFileVersion current,
        NexusUpdateFileVersion candidate,
        string message,
        bool isCurrentLatest = false) =>
        new(
            status,
            current,
            candidate,
            isCurrentLatest,
            status is NexusUpdateCheckStatus.DifferentComponent or
                NexusUpdateCheckStatus.UnknownComparison,
            false,
            message);
}
