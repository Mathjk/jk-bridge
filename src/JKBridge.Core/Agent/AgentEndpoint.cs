using System.Net;
using System.Net.Sockets;

namespace JKBridge.Core.Agent;

/// <summary>
/// A JK Bridge Agent endpoint that is pinned to one Tailscale IPv4 address.
/// Credentials are deliberately not part of this value or its URI.
/// </summary>
public sealed class AgentEndpoint : IEquatable<AgentEndpoint>
{
    private AgentEndpoint(IPAddress address, int port)
    {
        Address = address;
        Port = port;
        BaseUri = new Uri($"http://{address}:{port}/", UriKind.Absolute);
    }

    public IPAddress Address { get; }

    public int Port { get; }

    public Uri BaseUri { get; }

    public static AgentEndpoint Create(IPAddress address, int port)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!IsTailscaleIpv4(address))
        {
            throw new ArgumentException(
                "Agent address must be an IPv4 address in the Tailscale 100.64.0.0/10 range.",
                nameof(address));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Agent port must be between 1 and 65535.");
        }

        return new AgentEndpoint(address, port);
    }

    public static bool TryParse(string? value, out AgentEndpoint? endpoint, out string error)
    {
        endpoint = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            error = "Agent endpoint is not a valid absolute URI.";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 ||
            uri.AbsolutePath != "/")
        {
            error = "Agent endpoint must be an HTTP origin without credentials, path, query, or fragment.";
            return false;
        }

        if (!IPAddress.TryParse(uri.Host, out var address) || !IsTailscaleIpv4(address))
        {
            error = "Agent endpoint must use an exact Tailscale IPv4 address, not a DNS name.";
            return false;
        }

        if (uri.Port is < 1 or > 65535)
        {
            error = "Agent endpoint port is invalid.";
            return false;
        }

        endpoint = new AgentEndpoint(address, uri.Port);
        return true;
    }

    public override string ToString() => BaseUri.AbsoluteUri;

    public bool Equals(AgentEndpoint? other) =>
        other is not null && Address.Equals(other.Address) && Port == other.Port;

    public override bool Equals(object? obj) => Equals(obj as AgentEndpoint);

    public override int GetHashCode() => HashCode.Combine(Address, Port);

    internal Uri Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            relativePath.StartsWith('/') ||
            relativePath.Contains('?') ||
            relativePath.Contains('#') ||
            relativePath.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Agent API path must be a safe relative path.", nameof(relativePath));
        }

        return new Uri(BaseUri, relativePath);
    }

    internal static bool IsTailscaleIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 100 && (bytes[1] & 0xC0) == 64;
    }
}
