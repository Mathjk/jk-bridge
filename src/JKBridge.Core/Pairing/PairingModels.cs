namespace JKBridge.Core.Pairing;

public sealed record PairingRequest(
    int ProtocolVersion,
    string ClientPublicKey,
    IReadOnlyList<int> RemotePorts);

public sealed record PairingResponse
{
    public int ProtocolVersion { get; init; }

    public string ServerHost { get; init; } = string.Empty;

    public string TailnetDnsSuffix { get; init; } = string.Empty;

    public string TailscaleIpv4 { get; init; } = string.Empty;

    public string SshUser { get; init; } = string.Empty;

    public int SshPort { get; init; }

    public string HostPublicKey { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string ClientKeyFingerprint { get; init; } = string.Empty;

    public List<int> PermitPorts { get; init; } = [];
}

public sealed class PairingException : Exception
{
    public PairingException(string message)
        : base(message)
    {
    }

    public PairingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
