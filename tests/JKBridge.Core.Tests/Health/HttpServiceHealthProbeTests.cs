using System.Net;
using System.Text;
using JKBridge.Core.Health;
using JKBridge.Core.Models;

namespace JKBridge.Core.Tests.Health;

public sealed class HttpServiceHealthProbeTests
{
    [Fact]
    public async Task HttpStatus_TreatsRedirectAsReachable()
    {
        Uri? requestedUri = null;
        using var client = new HttpClient(new StubHandler(request =>
        {
            requestedUri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.Found);
        }));
        using var probe = new HttpServiceHealthProbe(client);
        var service = CreateService(8080, "/project", HealthCheckKind.HttpStatus);

        var result = await probe.CheckAsync(service);

        Assert.Equal(ServiceHealthState.Available, result.State);
        Assert.Equal(302, result.HttpStatus);
        Assert.Equal("http://127.0.0.1:8080/project", requestedUri?.AbsoluteUri);
    }

    [Fact]
    public async Task JsonOk_RequiresLiteralTrue()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":false}", Encoding.UTF8, "application/json"),
        }));
        using var probe = new HttpServiceHealthProbe(client);

        var result = await probe.CheckAsync(CreateService(8765, "/api/health", HealthCheckKind.JsonOk));

        Assert.Equal(ServiceHealthState.Unavailable, result.State);
        Assert.Contains("ok=true", result.Detail);
    }

    [Fact]
    public async Task JsonOk_AcceptsHealthyResponse()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json"),
        }));
        using var probe = new HttpServiceHealthProbe(client);

        var result = await probe.CheckAsync(CreateService(8765, "/api/health", HealthCheckKind.JsonOk));

        Assert.Equal(ServiceHealthState.Available, result.State);
        Assert.Equal(200, result.HttpStatus);
    }

    [Fact]
    public async Task JsonOk_ReportsMalformedJsonWithoutThrowing()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not-json", Encoding.UTF8, "application/json"),
        }));
        using var probe = new HttpServiceHealthProbe(client);

        var result = await probe.CheckAsync(CreateService(8765, "/api/health", HealthCheckKind.JsonOk));

        Assert.Equal(ServiceHealthState.Unavailable, result.State);
        Assert.Contains("Invalid health JSON", result.Detail);
    }

    [Fact]
    public async Task JsonOk_TimesOutWhenBodyNeverCompletes()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BlockingReadStream()),
        }));
        using var probe = new HttpServiceHealthProbe(client, TimeSpan.FromMilliseconds(40));

        var result = await probe.CheckAsync(CreateService(8765, "/api/health", HealthCheckKind.JsonOk))
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(ServiceHealthState.Unavailable, result.State);
        Assert.Contains("timed out", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task JsonOk_RejectsOversizedBody()
    {
        var oversized = "{\"ok\":true,\"padding\":\"" + new string('x', 70 * 1024) + "\"}";
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(oversized, Encoding.UTF8, "application/json"),
        }));
        using var probe = new HttpServiceHealthProbe(client);

        var result = await probe.CheckAsync(CreateService(8765, "/api/health", HealthCheckKind.JsonOk));

        Assert.Equal(ServiceHealthState.Unavailable, result.State);
        Assert.Contains("64 KiB", result.Detail);
    }

    private static ServiceDefinition CreateService(
        int port,
        string healthPath,
        HealthCheckKind kind) => new()
        {
            Id = "service",
            Name = "Service",
            LocalPort = port,
            RemotePort = port,
            OpenUrl = $"http://127.0.0.1:{port}/",
            HealthPath = healthPath,
            HealthKind = kind,
        };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
