using System.Text.Json;
using System.Text.Json.Serialization;
using RipperWorks.Core;

namespace RipperWorks.Organizer.GameOperations;

internal static class PlanSerialization
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static string Serialize(GameOperationPlan plan) =>
        SerializeCanonical(plan);

    public static string SerializeCanonical(GameOperationPlan plan)
    {
        var dto = ToDto(plan);
        return JsonSerializer.Serialize(dto, Options);
    }

    public static GameOperationPlan Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        PlanDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<PlanDto>(json, Options);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "MalformedPlan",
                exception);
        }

        if (dto is null)
            throw new InvalidOperationException("MalformedPlan");
        if (dto.PlanVersion != GameOperationPlanVersions.Current)
        {
            throw new InvalidOperationException(
                "UnsupportedPlanVersion");
        }

        if (!CanonicalProfileKey.TryCreate(
                dto.CanonicalProfileKey,
                out var key,
                out _) ||
            key is null)
        {
            throw new InvalidOperationException("InvalidPlanProfileKey");
        }

        if (string.IsNullOrWhiteSpace(dto.OperationKind) ||
            !Enum.TryParse<GameOperationKind>(
                dto.OperationKind,
                ignoreCase: false,
                out var kind) ||
            !Enum.IsDefined(kind))
        {
            throw new InvalidOperationException("MalformedPlan");
        }

        var steps = new List<GameOperationStepSpec>();
        foreach (var s in dto.Steps ?? [])
        {
            if (string.IsNullOrWhiteSpace(s.StepKind) ||
                !Enum.TryParse<GameOperationStepKind>(
                    s.StepKind,
                    ignoreCase: false,
                    out var stepKind) ||
                !Enum.IsDefined(stepKind))
            {
                throw new InvalidOperationException("MalformedPlan");
            }

            if (string.IsNullOrWhiteSpace(s.StepKey) ||
                string.IsNullOrWhiteSpace(s.RelativePath))
            {
                throw new InvalidOperationException("MalformedPlan");
            }

            steps.Add(new GameOperationStepSpec(
                s.Sequence,
                s.StepKey,
                stepKind,
                s.RelativePath,
                s.ContentIdentity,
                s.ExpectedBeforeIdentity,
                s.ExpectedAfterIdentity));
        }

        PackageId? packageId = string.IsNullOrWhiteSpace(dto.PackageId)
            ? null
            : new PackageId(dto.PackageId);
        var preconditions = dto.ExpectedPreconditions?
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        try
        {
            return new GameOperationPlan(
                dto.PlanVersion,
                kind,
                key,
                dto.OperationIdentity ?? "op",
                packageId,
                dto.ArchiveSha256,
                dto.AnalyzerVersion,
                dto.SelectedRoot,
                dto.PolicyVersion,
                steps,
                preconditions,
                dto.SourceArchivePath);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("MalformedPlan", exception);
        }
    }

    private static PlanDto ToDto(GameOperationPlan plan) =>
        new()
        {
            PlanVersion = plan.PlanVersion,
            OperationKind = plan.OperationKind.ToString(),
            CanonicalProfileKey = plan.ProfileKey.Value,
            OperationIdentity = plan.OperationIdentity,
            PackageId = plan.PackageId?.Value,
            ArchiveSha256 = plan.ArchiveSha256,
            AnalyzerVersion = plan.AnalyzerVersion,
            SelectedRoot = plan.SelectedRoot,
            PolicyVersion = plan.PolicyVersion,
            // SourceArchivePath is runtime-only for live revalidation; not in plan hash.
            ExpectedPreconditions = plan.ExpectedPreconditions
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => kv.Value),
            Steps = plan.Steps
                .OrderBy(s => s.Sequence)
                .Select(s => new StepDto
                {
                    Sequence = s.Sequence,
                    StepKey = s.StepKey,
                    StepKind = s.StepKind.ToString(),
                    RelativePath = s.RelativePath,
                    ContentIdentity = s.ContentIdentity,
                    ExpectedBeforeIdentity = s.ExpectedBeforeIdentity,
                    ExpectedAfterIdentity = s.ExpectedAfterIdentity
                })
                .ToList()
        };

    private sealed class PlanDto
    {
        public int PlanVersion { get; set; }
        public string? OperationKind { get; set; }
        public string? CanonicalProfileKey { get; set; }
        public string? OperationIdentity { get; set; }
        public string? PackageId { get; set; }
        public string? ArchiveSha256 { get; set; }
        public int? AnalyzerVersion { get; set; }
        public string? SelectedRoot { get; set; }
        public int? PolicyVersion { get; set; }
        public string? SourceArchivePath { get; set; }
        public Dictionary<string, string>? ExpectedPreconditions { get; set; }
        public List<StepDto>? Steps { get; set; }
    }

    private sealed class StepDto
    {
        public int Sequence { get; set; }
        public string? StepKey { get; set; }
        public string? StepKind { get; set; }
        public string? RelativePath { get; set; }
        public string? ContentIdentity { get; set; }
        public string? ExpectedBeforeIdentity { get; set; }
        public string? ExpectedAfterIdentity { get; set; }
    }
}
