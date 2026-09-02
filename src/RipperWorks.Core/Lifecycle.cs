namespace RipperWorks.Core;

/// <summary>
/// Process-lifetime boundary implemented by each application feature module.
/// Constructors must be inert; long-lived work begins only in StartAsync.
/// </summary>
public interface IApplicationModule : IAsyncDisposable
{
    string Name { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
