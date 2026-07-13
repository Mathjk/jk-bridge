using System.Net;

namespace JKBridge.Core.Pairing;

public sealed record PairingCode(IPAddress Address, int Port, string Token)
{
    public Uri Endpoint => new($"http://{Address}:{Port}/v1/pair", UriKind.Absolute);

    public static bool TryParse(string? value, out PairingCode? code, out string error)
    {
        code = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            error = "配对码格式无效。请完整复制 Linux 脚本显示的 jkbridge:// 地址。";
            return false;
        }

        if (!string.Equals(uri.Scheme, "jkbridge", StringComparison.OrdinalIgnoreCase) ||
            uri.UserInfo.Length != 0 ||
            uri.Fragment.Length != 0 ||
            !string.Equals(uri.AbsolutePath.TrimEnd('/'), "/v1", StringComparison.Ordinal))
        {
            error = "配对码不是受支持的 JK Bridge V1 地址。";
            return false;
        }

        if (!IPAddress.TryParse(uri.Host, out var address) || !IsTailscaleIpv4(address))
        {
            error = "配对地址必须是 Linux 节点的 Tailscale IPv4 地址。";
            return false;
        }

        if (uri.Port is < 1 or > 65535)
        {
            error = "配对端口无效。";
            return false;
        }

        var queryParts = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries);
        if (queryParts.Length != 1)
        {
            error = "配对码参数无效。";
            return false;
        }

        var tokenParts = queryParts[0].Split('=', 2);
        if (tokenParts.Length != 2 || !string.Equals(tokenParts[0], "token", StringComparison.Ordinal))
        {
            error = "配对码缺少一次性令牌。";
            return false;
        }

        var token = Uri.UnescapeDataString(tokenParts[1]);
        if (!IsValidToken(token))
        {
            error = "配对令牌无效或不完整。";
            return false;
        }

        code = new PairingCode(address, uri.Port, token);
        return true;
    }

    private static bool IsTailscaleIpv4(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 100 && (bytes[1] & 0xC0) == 64;
    }

    private static bool IsValidToken(string token)
    {
        if (token.Length != 43 ||
            token.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            return false;
        }

        try
        {
            var base64 = token.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight((base64.Length + 3) / 4 * 4, '=');
            return Convert.FromBase64String(base64).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
