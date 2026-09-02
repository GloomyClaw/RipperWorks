using RipperWorks.Core;

namespace RipperWorks.Organizer;

internal sealed record BatchDependencyRelation
{
    public required long RelationId { get; init; }
    public required PackageRelationType RelationType { get; init; }
    public required IReadOnlySet<PackageId> ChildPackageIds { get; init; }
    public required IReadOnlySet<PackageId> ParentPackageIds { get; init; }
    public bool IsLogical => RelationId < 0;

    public bool MatchesChild(PackageId packageId) =>
        ChildPackageIds.Contains(packageId);

    public bool MatchesParent(PackageId packageId) =>
        ParentPackageIds.Contains(packageId);
}

internal static class BatchRelationGraph
{
    public static async Task<IReadOnlyList<BatchDependencyRelation>> LoadAsync(
        OrganizerRepository repository,
        IEnumerable<PackageId> packageIds,
        CancellationToken cancellationToken)
    {
        var builders = new Dictionary<long, RelationBuilder>();
        var distinctPackageIds = packageIds.Distinct().ToArray();
        var packageRelations = await repository
            .LoadBatchDependencyRelationsAsync(
                distinctPackageIds,
                cancellationToken);
        foreach (var relation in packageRelations)
        {
            if (!builders.TryGetValue(relation.RelationId, out var builder))
            {
                builder = new(relation.RelationId, relation.RelationType);
                builders.Add(relation.RelationId, builder);
            }
            builder.ChildPackageIds.Add(relation.FromPackageId);
            builder.ParentPackageIds.Add(relation.ToPackageId);
        }
        return builders.Values.Select(builder => builder.Build()).ToArray();
    }

    public static IReadOnlyList<string> FindCyclePackages(
        IReadOnlyList<BatchPackageCandidate> candidates,
        IReadOnlyList<BatchDependencyRelation> relations)
    {
        var ordered = OrderCore(candidates, relations);
        if (ordered.Count == candidates.Count)
            return [];
        var orderedIds = ordered.Select(Id).ToHashSet();
        return candidates.Where(item => !orderedIds.Contains(Id(item)))
            .OrderBy(item => item.VisualOrder)
            .Select(item => item.DisplayName)
            .ToArray();
    }

    public static IReadOnlyList<T> Order<T>(
        IReadOnlyList<T> items,
        Func<T, BatchPackageCandidate> candidate,
        IReadOnlyList<BatchDependencyRelation> relations,
        bool reverse)
    {
        var candidates = items.Select(candidate).ToArray();
        var ids = OrderCore(candidates, relations).Select(Id).ToArray();
        var byId = items.ToDictionary(item => Id(candidate(item)));
        var result = ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        if (reverse)
            result.Reverse();
        return result;
    }

    private static IReadOnlyList<BatchPackageCandidate> OrderCore(
        IReadOnlyList<BatchPackageCandidate> candidates,
        IReadOnlyList<BatchDependencyRelation> relations)
    {
        var byId = candidates.ToDictionary(Id);
        var indegree = byId.Keys.ToDictionary(id => id, _ => 0);
        var edges = byId.Keys.ToDictionary(id => id, _ => new HashSet<PackageId>());
        foreach (var relation in relations)
        {
            var parents = relation.ParentPackageIds.Where(byId.ContainsKey);
            var children = relation.ChildPackageIds.Where(byId.ContainsKey);
            foreach (var parent in parents)
            foreach (var child in children)
            {
                if (parent != child && edges[parent].Add(child))
                    indegree[child]++;
            }
        }
        var result = new List<BatchPackageCandidate>();
        while (result.Count < candidates.Count)
        {
            var next = indegree.Where(pair => pair.Value == 0)
                .OrderBy(pair => byId[pair.Key].VisualOrder)
                .Select(pair => pair.Key)
                .FirstOrDefault();
            if (next == default || !indegree.Remove(next))
                break;
            result.Add(byId[next]);
            foreach (var child in edges[next])
                indegree[child]--;
        }
        return result;
    }

    private sealed class RelationBuilder(
        long relationId,
        PackageRelationType relationType)
    {
        public HashSet<PackageId> ChildPackageIds { get; } = [];
        public HashSet<PackageId> ParentPackageIds { get; } = [];

        public BatchDependencyRelation Build() => new()
        {
            RelationId = relationId,
            RelationType = relationType,
            ChildPackageIds = ChildPackageIds,
            ParentPackageIds = ParentPackageIds
        };
    }

    private static PackageId Id(BatchPackageCandidate item) =>
        item.Package.Package.PackageId;
}
