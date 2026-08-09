using System.Net;
using System.Net.Sockets;
using System.Text;
using JKBridge.App.Services;

namespace JKBridge.Core.Tests.ViewModels;

public sealed class WindowsLocalServiceDiscoveryTests
{
    [Fact]
    public async Task DiscoverAsync_FiltersNonLoopbackDeduplicatesAndSkipsTunnelProbe()
    {
        var source = new FakeListenerSource(
        [
            new("127.0.0.1", 8384, 42, "syncthing"),
            new("127.0.0.1", 8384),
            new("::1", 8443, 43, "secure-app"),
            new("0.0.0.0", 9000, 44, "public-app"),
            new("192.168.1.5", 9001, 45, "lan-app"),
            new("127.0.0.1", 8080, 99, "ssh"),
        ]);
        var probe = new FakeWebProbe(listener =>
            listener.Port == 8384
                ? new(LocalEndpointProtocol.Http, 200, "/", Detail: "HTTP")
                : new(LocalEndpointProtocol.Https, 401, "/login", true, "HTTPS"));
        var discovery = new WindowsLocalServiceDiscoveryService(source, probe);

        var results = await discovery.DiscoverAsync(new HashSet<int> { 8080 });

        Assert.Equal(3, results.Count);
        var syncthing = Assert.Single(results, result => result.Port == 8384);
        Assert.True(syncthing.IsWeb);
        Assert.True(syncthing.CanSave);
        Assert.Equal("http://127.0.0.1:8384/", syncthing.OpenUrl);
        Assert.Equal("syncthing", syncthing.ProcessName);
        var secure = Assert.Single(results, result => result.Port == 8443);
        Assert.Equal("https://[::1]:8443/login", secure.OpenUrl);
        Assert.True(secure.CertificateUnverified);
        var tunnel = Assert.Single(results, result => result.Port == 8080);
        Assert.True(tunnel.IsExistingTunnel);
        Assert.False(tunnel.CanSave);
        Assert.DoesNotContain(probe.ProbedPorts, port => port == 8080);
        Assert.DoesNotContain(results, result => result.Port is 9000 or 9001);
    }

    [Fact]
    public async Task DiscoverAsync_UsesBoundedCandidateCount()
    {
        var source = new FakeListenerSource(
            Enumerable.Range(1000, 10)
                .Select(port => new LocalTcpListener("127.0.0.1", port))
                .ToArray());
        var probe = new FakeWebProbe(_ => LocalWebProbeResult.TcpOnly());
        var discovery = new WindowsLocalServiceDiscoveryService(
            source,
            probe,
            new WindowsLocalDiscoveryOptions
            {
                MaximumCandidates = 3,
                MaximumConcurrency = 2,
            });

        var results = await discovery.DiscoverAsync(new HashSet<int>());

        Assert.Equal(3, results.Count);
        Assert.Equal([1000, 1001, 1002], results.Select(result => result.Port));
    }

    [Fact]
    public async Task DiscoverAsync_PropagatesCancellation()
    {
        var source = new FakeListenerSource([new("127.0.0.1", 8384)]);
        var probe = new BlockingWebProbe();
        var discovery = new WindowsLocalServiceDiscoveryService(source, probe);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            discovery.DiscoverAsync(new HashSet<int>(), cancellation.Token));
    }

    [Fact]
    public async Task LocalWebProbe_UsesHeadDoesNotFollowRedirectAndStripsQuerySuggestion()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string? requestText = null;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var buffer = new byte[4096];
            var count = await stream.ReadAsync(buffer);
            requestText = Encoding.ASCII.GetString(buffer, 0, count);
            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 302 Found\r\n" +
                "Location: /ui?token=secret#fragment\r\n" +
                "Content-Length: 0\r\n" +
                "Connection: close\r\n\r\n");
            await stream.WriteAsync(response);
        });
        using var probe = new LocalWebProbe(TimeSpan.FromSeconds(1));

        var result = await probe.ProbeAsync(new("127.0.0.1", port, 123, "test"));
        await server;

        Assert.Equal(LocalEndpointProtocol.Http, result.Protocol);
        Assert.Equal(302, result.HttpStatus);
        Assert.Equal("/ui", result.SuggestedPath);
        Assert.StartsWith("HEAD / HTTP/1.1", requestText, StringComparison.Ordinal);
        Assert.DoesNotContain("token=secret", result.SuggestedPath, StringComparison.Ordinal);
    }

    private sealed class FakeListenerSource(IReadOnlyList<LocalTcpListener> listeners)
        : ILocalTcpListenerSource
    {
        public Task<IReadOnlyList<LocalTcpListener>> GetListenersAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(listeners);
        }
    }

    private sealed class FakeWebProbe(Func<LocalTcpListener, LocalWebProbeResult> probe)
        : ILocalWebProbe
    {
        private readonly object _gate = new();

        public List<int> ProbedPorts { get; } = [];

        public Task<LocalWebProbeResult> ProbeAsync(
            LocalTcpListener listener,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ProbedPorts.Add(listener.Port);
            }

            return Task.FromResult(probe(listener));
        }
    }

    private sealed class BlockingWebProbe : ILocalWebProbe
    {
        public async Task<LocalWebProbeResult> ProbeAsync(
            LocalTcpListener listener,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return LocalWebProbeResult.TcpOnly();
        }
    }
}
