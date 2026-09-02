using RipperWorks.Downloader;

namespace RipperWorks.App.Services;

public sealed record NexusRequirementRefreshSummary(
    int Success,
    int Partial,
    int Failure,
    int Skipped);

public sealed record NexusRequirementRefreshProgress(
    int Completed,
    int Total);

public sealed class NexusRequirementRefreshRunner(
    INexusRequirementRefreshService service,
    Action<Exception>? observeError = null)
{
    private readonly INexusRequirementRefreshService _service =
        service ?? throw new ArgumentNullException(nameof(service));
    private readonly Action<Exception>? _observeError = observeError;

    public async Task<NexusRequirementRefreshSummary> RunSequentialAsync(
        IEnumerable<NexusModIdentity> identities,
        int skipped,
        IProgress<NexusRequirementRefreshProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identities);
        var success = 0;
        var partial = 0;
        var failure = 0;
        var distinct = identities.Distinct().ToArray();
        var completed = 0;
        foreach (var identity in distinct)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await _service.RefreshAsync(
                    identity,
                    clearDismissals: true,
                    cancellationToken).ConfigureAwait(false);
                switch (result.Outcome)
                {
                    case NexusRequirementSyncOutcome.Success:
                        success++;
                        break;
                    case NexusRequirementSyncOutcome.Partial:
                        partial++;
                        break;
                    case NexusRequirementSyncOutcome.Failure:
                        failure++;
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unknown Nexus sync outcome: {result.Outcome}.");
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failure++;
                _observeError?.Invoke(exception);
            }
            completed++;
            progress?.Report(new(completed, distinct.Length));
        }
        return new(success, partial, failure, skipped);
    }
}
