using System.Security.Cryptography;
using System.Text;

namespace JKBridge.Core.Agent;

public sealed record AgentRelaySpec(
    string Slug,
    int RelayPort,
    string TargetHost,
    int TargetPort,
    string Scheme,
    string OpenPath);

public sealed record AgentRelayServicePresentation(
    string Name,
    string Description,
    string Icon,
    int SuggestedLocalPort,
    string HealthPath,
    string HealthKind,
    bool EnabledByDefault);

public sealed record AgentRelayPlanRequest(
    AgentRelaySpec Spec,
    AgentRelayServicePresentation Service,
    bool GrantToAllDevices);

public sealed record AgentRelayApplyRequest(
    AgentRelayPlanRequest Plan,
    AgentRelayPlanToken PlanToken,
    bool Confirmed);

public sealed record AgentRelayDeletionPlanRequest(string Slug);

public sealed record AgentRelayDeletionRequest(
    string Slug,
    AgentRelayPlanToken PlanToken,
    bool Confirmed);

public sealed record AgentRelayPlanResponse(
    int ProtocolVersion,
    AgentRelayPlanToken PlanToken,
    AgentRelayPlan RelayPlan,
    AgentRelayServiceChange ServiceChange,
    string NextServicesRevision);

public sealed record AgentRelayApplyResponse(
    AgentRelayPlanResponse Plan,
    bool Applied,
    bool BackupCreated);

public sealed record AgentRelayPlan(
    string Operation,
    string Action,
    string Slug,
    string UnitName,
    AgentRelaySpec? Spec,
    string? ObservedSha256,
    string Reason,
    bool RequiresConfirmation,
    bool CanApply);

public sealed record AgentRelayServiceChange(
    IReadOnlyList<int> AddedRemotePorts,
    IReadOnlyList<int> RemovedRemotePorts,
    IReadOnlyList<string> AffectedDeviceIds);

public sealed record AgentRelayProbeResponse(
    int ProtocolVersion,
    AgentRelaySpec Spec,
    bool DependencyAvailable,
    bool? RelayPortInUse,
    bool? TargetReachable,
    bool ReadyToInstall,
    string TargetError);

/// <summary>
/// State-bound confirmation token returned by a read-only relay plan. It is
/// transient and deliberately redacted from ordinary string formatting.
/// </summary>
public sealed class AgentRelayPlanToken : IEquatable<AgentRelayPlanToken>, IAgentSecretValue
{
    private readonly string _value;

    private AgentRelayPlanToken(string value)
    {
        _value = value;
    }

    public static AgentRelayPlanToken Parse(string value)
    {
        if (!TryParse(value, out var token))
        {
            throw new ArgumentException(
                "Relay plan token must be exactly 64 lowercase hexadecimal characters.",
                nameof(value));
        }

        return token!;
    }

    public static bool TryParse(string? value, out AgentRelayPlanToken? token)
    {
        token = null;
        if (value is null || value.Length != 64 ||
            value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            return false;
        }

        token = new AgentRelayPlanToken(value);
        return true;
    }

    public string Export() => _value;

    public override string ToString() => "[REDACTED]";

    public bool Equals(AgentRelayPlanToken? other) =>
        other is not null &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(_value),
            Encoding.ASCII.GetBytes(other._value));

    public override bool Equals(object? obj) => Equals(obj as AgentRelayPlanToken);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_value);

    string IAgentSecretValue.RevealForRedaction() => _value;
}

public sealed record AgentEnrollmentCodeResponse(
    int ProtocolVersion,
    AgentCredential EnrollmentCode,
    DateTimeOffset ExpiresAt);
