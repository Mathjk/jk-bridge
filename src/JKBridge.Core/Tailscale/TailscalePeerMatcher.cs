namespace JKBridge.Core.Tailscale;

public static class TailscalePeerMatcher
{
    public static TailscalePeerMatch FindExact(
        TailscaleStatusSnapshot status,
        string configuredHost)
    {
        ArgumentNullException.ThrowIfNull(status);

        var target = TailscaleStatusParser.NormalizeDnsName(configuredHost);
        if (target.Length == 0)
        {
            return new(TailscalePeerMatchKind.NotFound, null);
        }

        var isFullyQualified = target.Contains('.');
        var matches = status.Peers
            .Where(peer => IsExactMatch(peer, target, isFullyQualified))
            .GroupBy(CreateIdentityKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        return matches.Length switch
        {
            0 => new(TailscalePeerMatchKind.NotFound, null),
            > 1 => new(TailscalePeerMatchKind.Ambiguous, null),
            _ when matches[0].Online => new(TailscalePeerMatchKind.Online, matches[0]),
            _ => new(TailscalePeerMatchKind.Offline, matches[0]),
        };
    }

    public static bool MatchesExpectedTailnet(
        TailscaleStatusSnapshot status,
        string? expectedDnsSuffix)
    {
        ArgumentNullException.ThrowIfNull(status);
        var expected = TailscaleStatusParser.NormalizeDnsName(expectedDnsSuffix);
        return expected.Length == 0 ||
               string.Equals(
                   expected,
                   TailscaleStatusParser.NormalizeDnsName(status.Tailnet.MagicDnsSuffix),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExactMatch(TailscaleNode peer, string target, bool isFullyQualified)
    {
        var dnsName = TailscaleStatusParser.NormalizeDnsName(peer.DnsName);
        if (isFullyQualified)
        {
            return string.Equals(dnsName, target, StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(peer.HostName.Trim().TrimEnd('.'), target, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var separator = dnsName.IndexOf('.');
        var machineName = separator < 0 ? dnsName : dnsName[..separator];
        return string.Equals(machineName, target, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateIdentityKey(TailscaleNode peer) =>
        peer.Id.Length > 0
            ? $"id:{peer.Id}"
            : $"name:{peer.DnsName}|{peer.HostName}";
}

public sealed record TailscalePeerMatch(TailscalePeerMatchKind Kind, TailscaleNode? Peer)
{
    public bool Found => Kind is TailscalePeerMatchKind.Online or TailscalePeerMatchKind.Offline;
}

public enum TailscalePeerMatchKind
{
    NotFound,
    Ambiguous,
    Offline,
    Online,
}
