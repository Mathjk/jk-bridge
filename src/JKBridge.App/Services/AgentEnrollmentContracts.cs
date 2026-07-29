using JKBridge.Core.Agent;
using JKBridge.Core.Models;

namespace JKBridge.App.Services;

/// <summary>
/// User-visible stages for the optional, explicitly initiated Linux Agent enrollment flow.
/// </summary>
public enum AgentEnrollmentStage
{
    CheckingAgent,
    PreparingKey,
    AwaitingAuthorization,
    FetchingCatalog,
    Completed,
}

public sealed record AgentEnrollmentProgress(AgentEnrollmentStage Stage, string Detail);

public sealed record AgentEnrollmentRequest(
    string Endpoint,
    string RecoveryCode,
    string DeviceLabel,
    string IdentityFile);

/// <summary>
/// Enrollment output. DeviceCredential is a secret wrapper whose ToString is redacted; the
/// controller must immediately place it in the injected OS secret store and persist only the
/// returned opaque reference.
/// </summary>
public sealed record AgentEnrollmentResult(
    string Endpoint,
    AgentCredential DeviceCredential,
    string DeviceId,
    string DeviceLabel,
    string CatalogRevision,
    string ServerDisplayName,
    string ServerHost,
    string TailnetDnsSuffix,
    string SshUser,
    int SshPort,
    string HostPublicKey,
    string PortableIdentityFile,
    string ClientFingerprint,
    IReadOnlyList<int> PermitPorts,
    IReadOnlyList<ServiceDefinition> Services,
    string TailscaleNodeId);

public interface IAgentEnrollmentService
{
    Task<AgentEnrollmentResult> EnrollAsync(
        AgentEnrollmentRequest request,
        IProgress<AgentEnrollmentProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Keeps Agent UI optional while the Agent protocol implementation is unavailable or omitted.
/// The existing one-shot pairing path remains fully operational.
/// </summary>
public sealed class UnavailableAgentEnrollmentService : IAgentEnrollmentService
{
    public Task<AgentEnrollmentResult> EnrollAsync(
        AgentEnrollmentRequest request,
        IProgress<AgentEnrollmentProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromException<AgentEnrollmentResult>(new AgentEnrollmentException(
            "此版本尚未包含 Linux Agent 客户端，请使用下方的一次性配对。"));
}

public sealed class AgentEnrollmentException : Exception
{
    public AgentEnrollmentException(string message)
        : base(message)
    {
    }

    public AgentEnrollmentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
