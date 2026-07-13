using System.Net;
using System.Text;
using System.Text.Json;
using JKBridge.Core.Pairing;
using JKBridge.Core.Security;

namespace JKBridge.Core.Tests.Pairing;

public sealed class PairingClientTests
{
    private const string Token = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public async Task PairAsync_SendsTokenOnlyInHeaderAndValidatesReceipt()
    {
        var handler = new RecordingHandler(_ => JsonResponse(new PairingResponse
        {
            ProtocolVersion = 1,
            ServerHost = TestConfigs.LinuxHost,
            TailnetDnsSuffix = TestConfigs.TailnetSuffix,
            TailscaleIpv4 = "100.64.0.10",
            SshUser = "alice",
            SshPort = 22,
            HostPublicKey = TestConfigs.TestHostKey + " server comment",
            DisplayName = "Research Linux",
            ClientKeyFingerprint = Ed25519PublicKey.ComputeFingerprint(TestConfigs.TestHostKey),
            PermitPorts = [80, 8765],
        }));
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        using var client = new PairingClient(httpClient);
        Assert.True(PairingCode.TryParse(
            $"jkbridge://100.64.0.10:45123/v1?token={Token}",
            out var code,
            out _));

        var result = await client.PairAsync(
            code!,
            TestConfigs.TestHostKey + " client comment",
            [8765, 80, 80]);

        Assert.NotNull(handler.Request);
        Assert.Equal("http://100.64.0.10:45123/v1/pair", handler.Request.RequestUri!.AbsoluteUri);
        Assert.DoesNotContain(Token, handler.Request.RequestUri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("Bearer", handler.Request.Headers.Authorization?.Scheme);
        Assert.Equal(Token, handler.Request.Headers.Authorization?.Parameter);
        Assert.Equal(TestConfigs.TestHostKey, result.HostPublicKey);
        Assert.Equal([80, 8765], result.PermitPorts);

        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal(1, payload.RootElement.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(2, payload.RootElement.GetProperty("remotePorts").GetArrayLength());
    }

    [Fact]
    public async Task PairAsync_RejectsReceiptForDifferentTailscaleAddress()
    {
        var handler = new RecordingHandler(_ => JsonResponse(new PairingResponse
        {
            ProtocolVersion = 1,
            ServerHost = TestConfigs.LinuxHost,
            TailnetDnsSuffix = TestConfigs.TailnetSuffix,
            TailscaleIpv4 = "100.64.0.99",
            SshUser = "alice",
            SshPort = 22,
            HostPublicKey = TestConfigs.TestHostKey,
            ClientKeyFingerprint = Ed25519PublicKey.ComputeFingerprint(TestConfigs.TestHostKey),
            PermitPorts = [80],
        }));
        using var httpClient = new HttpClient(handler);
        using var client = new PairingClient(httpClient);
        Assert.True(PairingCode.TryParse(
            $"jkbridge://100.64.0.10:45123/v1?token={Token}",
            out var code,
            out _));

        var exception = await Assert.ThrowsAsync<PairingException>(() =>
            client.PairAsync(code!, TestConfigs.TestHostKey, [80]));

        Assert.Contains("身份", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PairAsync_RejectsAuthorizationThatDoesNotMatchRequestedPorts()
    {
        var handler = new RecordingHandler(_ => JsonResponse(new PairingResponse
        {
            ProtocolVersion = 1,
            ServerHost = TestConfigs.LinuxHost,
            TailnetDnsSuffix = TestConfigs.TailnetSuffix,
            TailscaleIpv4 = "100.64.0.10",
            SshUser = "alice",
            SshPort = 22,
            HostPublicKey = TestConfigs.TestHostKey,
            ClientKeyFingerprint = Ed25519PublicKey.ComputeFingerprint(TestConfigs.TestHostKey),
            PermitPorts = [80, 9000],
        }));
        using var httpClient = new HttpClient(handler);
        using var client = new PairingClient(httpClient);
        Assert.True(PairingCode.TryParse(
            $"jkbridge://100.64.0.10:45123/v1?token={Token}",
            out var code,
            out _));

        var exception = await Assert.ThrowsAsync<PairingException>(() =>
            client.PairAsync(code!, TestConfigs.TestHostKey, [80, 8765]));

        Assert.Contains("授权端口", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PairAsync_RejectsOversizedResponse()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[70_000]),
        });
        using var httpClient = new HttpClient(handler);
        using var client = new PairingClient(httpClient);
        Assert.True(PairingCode.TryParse(
            $"jkbridge://100.64.0.10:45123/v1?token={Token}",
            out var code,
            out _));

        await Assert.ThrowsAsync<PairingException>(() =>
            client.PairAsync(code!, TestConfigs.TestHostKey, [80]));
    }

    [Fact]
    public async Task PairAsync_RejectsReceiptForDifferentClientKey()
    {
        var handler = new RecordingHandler(_ => JsonResponse(new PairingResponse
        {
            ProtocolVersion = 1,
            ServerHost = TestConfigs.LinuxHost,
            TailnetDnsSuffix = TestConfigs.TailnetSuffix,
            TailscaleIpv4 = "100.64.0.10",
            SshUser = "alice",
            SshPort = 22,
            HostPublicKey = TestConfigs.TestHostKey,
            ClientKeyFingerprint = "SHA256:not-the-local-key",
            PermitPorts = [80],
        }));
        using var httpClient = new HttpClient(handler);
        using var client = new PairingClient(httpClient);
        Assert.True(PairingCode.TryParse(
            $"jkbridge://100.64.0.10:45123/v1?token={Token}",
            out var code,
            out _));

        var exception = await Assert.ThrowsAsync<PairingException>(() =>
            client.PairAsync(code!, TestConfigs.TestHostKey, [80]));

        Assert.Contains("客户端密钥", exception.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage JsonResponse(PairingResponse response) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                Encoding.UTF8,
                "application/json"),
        };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public byte[]? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return responseFactory(request);
        }
    }
}
