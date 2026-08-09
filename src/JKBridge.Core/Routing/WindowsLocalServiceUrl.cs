using System.Net;

namespace JKBridge.Core.Routing;

/// <summary>
/// Builds canonical HTTP(S) URLs for endpoints bound directly to Windows
/// loopback. Only literal IPv4 and IPv6 loopback addresses are accepted.
/// </summary>
public static class WindowsLocalServiceUrl
{
    public const string Ipv4Loopback = "127.0.0.1";
    public const string Ipv6Loopback = "::1";

    public static string Build(string host, string scheme, int port, string launchPath)
    {
        if (!TryBuild(host, scheme, port, launchPath, out var url))
        {
            throw new ArgumentException("The Windows-local service URL is invalid.");
        }

        return url;
    }

    public static bool TryBuild(
        string? host,
        string? scheme,
        int port,
        string? launchPath,
        out string url)
    {
        url = string.Empty;
        if (!IsLoopbackHost(host) ||
            !IsSupportedScheme(scheme) ||
            port is < 1 or > 65535 ||
            !ServiceOpenUrl.IsSafeLaunchPath(launchPath))
        {
            return false;
        }

        var normalizedHost = NormalizeHost(host!);
        var hostLiteral = normalizedHost == Ipv6Loopback
            ? $"[{Ipv6Loopback}]"
            : Ipv4Loopback;
        var candidate = $"{scheme!.ToLowerInvariant()}://{hostLiteral}:{port}{launchPath}";
        if (!TryParse(candidate, normalizedHost, port, out _))
        {
            return false;
        }

        url = candidate;
        return true;
    }

    public static bool TryParse(
        string? url,
        string expectedHost,
        int expectedPort,
        out Uri? uri)
    {
        uri = null;
        if (!IsLoopbackHost(expectedHost) ||
            expectedPort is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(url) ||
            url.Length > ServiceOpenUrl.MaximumLaunchPathLength + 128 ||
            url.Any(char.IsControl) ||
            url.Contains('\\') ||
            !Uri.IsWellFormedUriString(url, UriKind.Absolute) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var candidate) ||
            !IsSupportedScheme(candidate.Scheme) ||
            !string.IsNullOrEmpty(candidate.UserInfo) ||
            candidate.Port != expectedPort)
        {
            return false;
        }

        var candidateHost = NormalizeHost(candidate.Host.Trim('[', ']'));
        if (!string.Equals(candidateHost, NormalizeHost(expectedHost), StringComparison.Ordinal))
        {
            return false;
        }

        if (!IPAddress.TryParse(candidateHost, out var address) || !IPAddress.IsLoopback(address))
        {
            return false;
        }

        var launchPath = candidate.AbsolutePath + candidate.Query + candidate.Fragment;
        if (!ServiceOpenUrl.IsSafeLaunchPath(launchPath))
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    public static bool IsLoopbackHost(string? host)
    {
        var normalized = NormalizeHost(host);
        return normalized is Ipv4Loopback or Ipv6Loopback;
    }

    private static string NormalizeHost(string? host) =>
        (host ?? string.Empty).Trim().Trim('[', ']').ToLowerInvariant();

    private static bool IsSupportedScheme(string? scheme) =>
        string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
}
