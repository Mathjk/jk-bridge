using JKBridge.Core.Models;

namespace JKBridge.Core.Health;

public interface IServiceHealthProbe
{
    Task<ServiceHealth> CheckAsync(
        ServiceDefinition service,
        CancellationToken cancellationToken = default);
}
