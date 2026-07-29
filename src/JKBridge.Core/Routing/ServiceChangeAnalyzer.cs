using JKBridge.Core.Models;

namespace JKBridge.Core.Routing;

/// <summary>
/// Describes both visible service edits and their exact remote-port authorization impact.
/// </summary>
public static class ServiceChangeAnalyzer
{
    public static ServiceChangeAssessment Analyze(
        IEnumerable<ServiceDefinition> current,
        IEnumerable<ServiceDefinition> proposed)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(proposed);

        var currentServices = current.ToList();
        var proposedServices = proposed.ToList();
        var currentById = currentServices.ToDictionary(service => service.Id, StringComparer.OrdinalIgnoreCase);
        var proposedById = proposedServices.ToDictionary(service => service.Id, StringComparer.OrdinalIgnoreCase);
        var ids = currentById.Keys
            .Union(proposedById.Keys, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);

        var changes = new List<ServiceChange>();
        foreach (var id in ids)
        {
            currentById.TryGetValue(id, out var before);
            proposedById.TryGetValue(id, out var after);
            var kind = before is null
                ? ServiceChangeKind.Added
                : after is null
                    ? ServiceChangeKind.Removed
                    : before == after
                        ? ServiceChangeKind.Unchanged
                        : ServiceChangeKind.Modified;
            changes.Add(new(id, kind, before, after));
        }

        var currentPorts = EnabledRemotePorts(currentServices);
        var proposedPorts = EnabledRemotePorts(proposedServices);
        var addedPorts = proposedPorts.Except(currentPorts).ToArray();
        var removedPorts = currentPorts.Except(proposedPorts).ToArray();
        var authorizationImpact = addedPorts.Length > 0
            ? RemoteAuthorizationImpact.ReauthorizationRequired
            : removedPorts.Length > 0
                ? RemoteAuthorizationImpact.ReauthorizationRecommended
                : RemoteAuthorizationImpact.None;

        return new(
            changes,
            currentPorts,
            proposedPorts,
            addedPorts,
            removedPorts,
            authorizationImpact);
    }

    private static int[] EnabledRemotePorts(IEnumerable<ServiceDefinition> services) =>
        services
            .Where(service => service.Enabled)
            .Select(service => service.RemotePort)
            .Distinct()
            .Order()
            .ToArray();
}

public sealed record ServiceChange(
    string Id,
    ServiceChangeKind Kind,
    ServiceDefinition? Before,
    ServiceDefinition? After);

public enum ServiceChangeKind
{
    Unchanged,
    Added,
    Removed,
    Modified,
}

public enum RemoteAuthorizationImpact
{
    None,
    ReauthorizationRecommended,
    ReauthorizationRequired,
}

public sealed record ServiceChangeAssessment(
    IReadOnlyList<ServiceChange> Changes,
    IReadOnlyList<int> CurrentRemotePorts,
    IReadOnlyList<int> ProposedRemotePorts,
    IReadOnlyList<int> AddedRemotePorts,
    IReadOnlyList<int> RemovedRemotePorts,
    RemoteAuthorizationImpact AuthorizationImpact)
{
    public bool RequiresRemoteReauthorization =>
        AuthorizationImpact != RemoteAuthorizationImpact.None;

    public bool BlocksUntilReauthorized =>
        AuthorizationImpact == RemoteAuthorizationImpact.ReauthorizationRequired;
}
