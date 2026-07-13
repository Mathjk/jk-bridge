using System.Text.Json;

namespace JKBridge.Core.Tailscale;

public static class TailscaleStatusParser
{
    public static TailscaleStatusSnapshot Parse(string json)
    {
        if (!TryParse(json, out var status, out var error))
        {
            throw new TailscaleStatusParseException(error ?? "Tailscale status JSON is invalid.");
        }

        return status!;
    }

    public static bool TryParse(
        string? json,
        out TailscaleStatusSnapshot? status,
        out string? error)
    {
        status = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Tailscale status output was empty.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Tailscale status root must be a JSON object.";
                return false;
            }

            var root = document.RootElement;
            var backendState = ReadString(root, "BackendState") ?? "Unknown";
            var self = TryGetProperty(root, "Self", out var selfElement) &&
                       selfElement.ValueKind == JsonValueKind.Object
                ? ParseNode(selfElement, "self")
                : null;

            var tailnet = ParseTailnet(root, self);
            var loginName = ParseLoginName(root, selfElement);
            if (loginName.Length == 0)
            {
                loginName = tailnet.Name;
            }
            var peers = ParsePeers(root);

            status = new(backendState, tailnet, self, peers, loginName);
            return true;
        }
        catch (JsonException exception)
        {
            error = $"Tailscale status JSON could not be parsed: {exception.Message}";
            return false;
        }
    }

    public static string NormalizeDnsName(string? value) =>
        (value ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

    private static TailscaleTailnet ParseTailnet(JsonElement root, TailscaleNode? self)
    {
        if (!TryGetProperty(root, "CurrentTailnet", out var element) ||
            element.ValueKind != JsonValueKind.Object)
        {
            return new(
                Name: string.Empty,
                MagicDnsSuffix: DeriveDnsSuffix(self?.DnsName),
                MagicDnsEnabled: false);
        }

        return new(
            Name: ReadString(element, "Name")?.Trim() ?? string.Empty,
            MagicDnsSuffix: NormalizeDnsName(ReadString(element, "MagicDNSSuffix")),
            MagicDnsEnabled: ReadBoolean(element, "MagicDNSEnabled"));
    }

    private static string ParseLoginName(JsonElement root, JsonElement self)
    {
        var userId = self.ValueKind == JsonValueKind.Object
            ? ReadString(self, "UserID")?.Trim()
            : null;
        if (string.IsNullOrEmpty(userId) ||
            !TryGetProperty(root, "User", out var users) ||
            users.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (var property in users.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var candidateId = ReadString(property.Value, "ID")?.Trim() ?? property.Name;
            if (string.Equals(candidateId, userId, StringComparison.OrdinalIgnoreCase))
            {
                return (ReadString(property.Value, "LoginName") ?? string.Empty).Trim();
            }
        }

        return string.Empty;
    }

    private static IReadOnlyList<TailscaleNode> ParsePeers(JsonElement root)
    {
        if (!TryGetProperty(root, "Peer", out var peersElement))
        {
            return Array.Empty<TailscaleNode>();
        }

        var peers = new List<TailscaleNode>();
        switch (peersElement.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in peersElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Object)
                    {
                        AddPeerIfIdentifiable(peers, ParseNode(property.Value, property.Name));
                    }
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var element in peersElement.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.Object)
                    {
                        AddPeerIfIdentifiable(peers, ParseNode(element, $"peer-{index}"));
                    }

                    index++;
                }

                break;
        }

        return peers;
    }

    private static void AddPeerIfIdentifiable(ICollection<TailscaleNode> peers, TailscaleNode peer)
    {
        if (peer.HostName.Length > 0 || peer.DnsName.Length > 0)
        {
            peers.Add(peer);
        }
    }

    private static TailscaleNode ParseNode(JsonElement element, string fallbackId)
    {
        var id = ReadString(element, "ID") ??
                 ReadString(element, "StableID") ??
                 ReadString(element, "PublicKey") ??
                 fallbackId;

        return new(
            Id: id.Trim(),
            HostName: (ReadString(element, "HostName") ?? string.Empty).Trim().TrimEnd('.'),
            DnsName: NormalizeDnsName(ReadString(element, "DNSName")),
            Online: ReadBoolean(element, "Online"),
            TailscaleIps: ReadStringArray(element, "TailscaleIPs"));
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return array
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim())
            .Where(item => !string.IsNullOrEmpty(item))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static bool ReadBoolean(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var result) => result,
            JsonValueKind.Number when value.TryGetInt32(out var number) => number != 0,
            _ => false,
        };
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string DeriveDnsSuffix(string? dnsName)
    {
        var normalized = NormalizeDnsName(dnsName);
        var separator = normalized.IndexOf('.');
        return separator < 0 ? string.Empty : normalized[(separator + 1)..];
    }
}

public sealed record TailscaleStatusSnapshot(
    string BackendState,
    TailscaleTailnet Tailnet,
    TailscaleNode? Self,
    IReadOnlyList<TailscaleNode> Peers,
    string LoginName = "")
{
    public bool IsRunning =>
        string.Equals(BackendState, "Running", StringComparison.OrdinalIgnoreCase);
}

public sealed record TailscaleTailnet(
    string Name,
    string MagicDnsSuffix,
    bool MagicDnsEnabled);

public sealed record TailscaleNode(
    string Id,
    string HostName,
    string DnsName,
    bool Online,
    IReadOnlyList<string> TailscaleIps);

public sealed class TailscaleStatusParseException : Exception
{
    public TailscaleStatusParseException(string message)
        : base(message)
    {
    }
}
