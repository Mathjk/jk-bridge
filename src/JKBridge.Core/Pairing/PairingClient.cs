using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JKBridge.Core.Security;

namespace JKBridge.Core.Pairing;

public sealed class PairingClient : IDisposable
{
    public const int ProtocolVersion = 1;
    private const int MaximumBodyBytes = 64 * 1024;
    private const int MaximumPorts = 32;

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public PairingClient(HttpClient? httpClient = null)
    {
        if (httpClient is not null)
        {
            _httpClient = httpClient;
            return;
        }

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            UseProxy = false,
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(90),
        };
        _ownsClient = true;
    }

    public async Task<PairingResponse> PairAsync(
        PairingCode code,
        string clientPublicKey,
        IEnumerable<int> remotePorts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (!Ed25519PublicKey.TryNormalize(clientPublicKey, out var normalizedClientKey))
        {
            throw new PairingException("生成的客户端公钥格式无效，配对已停止。");
        }

        var ports = remotePorts.Distinct().Order().ToArray();
        if (ports.Length == 0 || ports.Length > MaximumPorts || ports.Any(port => port is < 1 or > 65535))
        {
            throw new PairingException($"必须选择 1–{MaximumPorts} 个有效的 Linux 回环端口。");
        }

        var payload = new PairingRequest(ProtocolVersion, normalizedClientKey, ports);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, code.Endpoint)
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", code.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new PairingException("无法连接 Linux 配对脚本。请确认脚本仍在等待，并且两台设备位于同一 tailnet。", exception);
        }

        using (response)
        {
            var responseBytes = await ReadLimitedAsync(response, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var detail = TryReadError(responseBytes);
                throw new PairingException(detail ?? $"Linux 配对脚本返回 HTTP {(int)response.StatusCode}。");
            }

            PairingResponse? result;
            try
            {
                result = JsonSerializer.Deserialize<PairingResponse>(responseBytes, _jsonOptions);
            }
            catch (JsonException exception)
            {
                throw new PairingException("Linux 配对响应不是有效 JSON。", exception);
            }

            return ValidateResponse(
                result,
                code,
                ports,
                Ed25519PublicKey.ComputeFingerprint(normalizedClientKey));
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private static PairingResponse ValidateResponse(
        PairingResponse? response,
        PairingCode code,
        IReadOnlyList<int> requestedPorts,
        string expectedClientFingerprint)
    {
        if (response is null || response.ProtocolVersion != ProtocolVersion)
        {
            throw new PairingException("Linux 配对脚本版本与此应用不兼容。");
        }

        if (!IPAddress.TryParse(response.TailscaleIpv4, out var responseAddress) ||
            !responseAddress.Equals(code.Address))
        {
            throw new PairingException("Linux 返回的 Tailscale 身份与配对码不一致。");
        }

        var host = response.ServerHost.Trim().TrimEnd('.').ToLowerInvariant();
        var suffix = response.TailnetDnsSuffix.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0 || suffix.Length == 0 ||
            Uri.CheckHostName(host) != UriHostNameType.Dns ||
            Uri.CheckHostName(suffix) != UriHostNameType.Dns ||
            !host.EndsWith('.' + suffix, StringComparison.OrdinalIgnoreCase))
        {
            throw new PairingException("Linux 返回的 MagicDNS 主机名或 tailnet 后缀无效。");
        }

        if (!Ed25519PublicKey.TryNormalize(response.HostPublicKey, out var normalizedHostKey))
        {
            throw new PairingException("Linux 未返回有效的 Ed25519 SSH 主机公钥。");
        }

        if (response.SshPort is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(response.SshUser) ||
            response.SshUser.Length > 64 ||
            response.SshUser.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-')))
        {
            throw new PairingException("Linux 返回的 SSH 用户或端口无效。");
        }

        if (!string.Equals(
                response.ClientKeyFingerprint,
                expectedClientFingerprint,
                StringComparison.Ordinal))
        {
            throw new PairingException("Linux 确认的客户端密钥与 Windows 本地密钥不一致。");
        }

        var permitted = response.PermitPorts.Distinct().Order().ToArray();
        if (!permitted.SequenceEqual(requestedPorts))
        {
            throw new PairingException("Linux 实际授权端口与 Windows 请求不一致，配置未保存。");
        }

        var displayName = response.DisplayName.Trim();
        if (displayName.Length is 0 or > 128 || displayName.Any(char.IsControl))
        {
            displayName = host.Split('.')[0];
        }

        return response with
        {
            ServerHost = host,
            TailnetDnsSuffix = suffix,
            HostPublicKey = normalizedHostKey,
            DisplayName = displayName,
        };
    }

    private static async Task<byte[]> ReadLimitedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaximumBodyBytes)
        {
            throw new PairingException("Linux 配对响应超过允许大小。");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return destination.ToArray();
            }

            if (destination.Length + read > MaximumBodyBytes)
            {
                throw new PairingException("Linux 配对响应超过允许大小。");
            }

            destination.Write(buffer, 0, read);
        }
    }

    private static string? TryReadError(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.TryGetProperty("error", out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                var message = value.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(message))
                {
                    return null;
                }

                var safe = new string(message
                    .Where(character => !char.IsControl(character) || character == ' ')
                    .Take(300)
                    .ToArray());
                return safe.Length == 0 ? null : safe;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}
