using RipperWorks.GameMaintenance;
using RipperWorks.Organizer;

namespace RipperWorks.App.Services;

public sealed class OrganizerGameMaintenanceManagedStateProvider(OrganizerRepository repository)
    : IGameMaintenanceManagedStateProvider
{
    private readonly OrganizerRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    public Task<bool> HasManagedInstallationsAsync(
        string gameRoot,
        CancellationToken cancellationToken = default) =>
        _repository.HasManagedInstallationsAsync(gameRoot, cancellationToken);
}
