using JKBridge.Core.Models;
using JKBridge.Core.Routing;

namespace JKBridge.Core.Authorization;

public static class ForwardAuthorization
{
    public static ForwardAuthorizationAssessment Evaluate(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var desired = config.Services
            .Where(service => service.Enabled)
            .Select(service => ServiceRouteDefinition.Evaluate(service).Route!.RemoteTarget.Port)
            .Distinct()
            .Order()
            .ToArray();
        var granted = config.Authorization.RemotePorts
            .Distinct()
            .Order()
            .ToArray();

        if (!config.Authorization.IsKnown)
        {
            return new(ForwardAuthorizationStatus.Unknown, desired, granted, desired, []);
        }

        var missing = desired.Except(granted).ToArray();
        var extra = granted.Except(desired).ToArray();
        var status = missing.Length > 0
            ? ForwardAuthorizationStatus.PairingRequired
            : extra.Length > 0
                ? ForwardAuthorizationStatus.PairingRecommended
                : ForwardAuthorizationStatus.Current;
        return new(
            status,
            desired,
            granted,
            missing,
            extra);
    }
}

public sealed record ForwardAuthorizationAssessment(
    ForwardAuthorizationStatus Status,
    IReadOnlyList<int> DesiredPorts,
    IReadOnlyList<int> GrantedPorts,
    IReadOnlyList<int> MissingPorts,
    IReadOnlyList<int> ExtraPorts);

public enum ForwardAuthorizationStatus
{
    Unknown,
    Current,
    PairingRequired,
    PairingRecommended,
}
