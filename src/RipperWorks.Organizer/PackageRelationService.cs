using RipperWorks.Core;

namespace RipperWorks.Organizer;

public sealed class PackageRelationService(OrganizerRepository repository)
{
    internal IApplicationMutationRoute? ApplicationMutationRoute { get; set; }

    public async Task<RelationMutationResult> AddAsync(
        PackageId fromPackageId,
        PackageId toPackageId,
        PackageRelationType relationType,
        PackageRelationSource source,
        bool isConfirmed = true,
        CancellationToken cancellationToken = default)
    {
        if (ApplicationMutationRoute is not null)
        {
            return await ApplicationMutationRoute.ConfirmRelationAsync(
                fromPackageId,
                toPackageId,
                relationType,
                source,
                isConfirmed,
                cancellationToken);
        }
        return await AddPrimitiveAsync(
            fromPackageId,
            toPackageId,
            relationType,
            source,
            isConfirmed,
            cancellationToken);
    }

    internal async Task<RelationMutationResult> AddPrimitiveAsync(
        PackageId fromPackageId,
        PackageId toPackageId,
        PackageRelationType relationType,
        PackageRelationSource source,
        bool isConfirmed = true,
        CancellationToken cancellationToken = default)
    {
        if (fromPackageId == toPackageId)
            return new(false, "RelationSelf");
        if (!await repository.PackageExistsAsync(
                fromPackageId,
                cancellationToken) ||
            !await repository.PackageExistsAsync(
                toPackageId,
                cancellationToken))
        {
            return new(false, "RelationPackageMissing");
        }
        if (await repository.RelationExistsAsync(
                fromPackageId,
                toPackageId,
                cancellationToken))
        {
            return new(false, "RelationDuplicate");
        }
        if (await repository.WouldCreateRelationCycleAsync(
                fromPackageId,
                toPackageId,
                cancellationToken))
        {
            return new(false, "RelationCycle");
        }

        var relation = await repository.InsertPackageRelationAsync(
            fromPackageId,
            toPackageId,
            relationType,
            source,
            isConfirmed,
            cancellationToken);
        return new(true, Relation: relation);
    }

    public Task DeleteAsync(
        long relationId,
        CancellationToken cancellationToken = default) =>
        repository.DeletePackageRelationAsync(
            relationId,
            cancellationToken);

    public Task<IReadOnlyList<PackageRelationView>> LoadForPackageAsync(
        PackageId packageId,
        CancellationToken cancellationToken = default) =>
        repository.LoadPackageRelationsAsync(
            packageId,
            cancellationToken);

    public async Task<DependencyHealth> GetDependencyHealthAsync(
        PackageId packageId,
        CancellationToken cancellationToken = default)
    {
        var missing = await repository.LoadMissingDependencyNamesAsync(
            cancellationToken);
        return missing.ContainsKey(packageId)
            ? DependencyHealth.Missing
            : DependencyHealth.Satisfied;
    }

    public Task<IReadOnlyDictionary<PackageId, IReadOnlyList<string>>>
        LoadMissingDependencyNamesAsync(
            CancellationToken cancellationToken = default) =>
        repository.LoadMissingDependencyNamesAsync(cancellationToken);

    public Task<IReadOnlyList<PackageRelationView>>
        LoadInstalledDependentsAsync(
            PackageId packageId,
            CancellationToken cancellationToken = default) =>
        repository.LoadInstalledDependentRelationsAsync(
            packageId,
            cancellationToken);

    public async Task<IReadOnlyList<PackageId>> BuildCascadeRemovalOrderAsync(
        PackageId rootPackageId,
        CancellationToken cancellationToken = default)
    {
        var order = new List<PackageId>();
        var visited = new HashSet<PackageId>();
        var visiting = new HashSet<PackageId>();

        async Task VisitAsync(PackageId packageId)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(packageId))
                return;
            if (!visiting.Add(packageId))
                throw new InvalidOperationException(
                    "Relation graph contains a cycle.");
            var dependents = await LoadInstalledDependentsAsync(
                packageId,
                cancellationToken);
            foreach (var dependent in dependents)
                await VisitAsync(dependent.Relation.FromPackageId);
            visiting.Remove(packageId);
            order.Add(packageId);
        }

        await VisitAsync(rootPackageId);
        return order;
    }
}
