using System.Net;
using System.Net.Sockets;
using JKBridge.Core.Health;
using JKBridge.Core.Models;

namespace JKBridge.Core.Tests.Health;

public sealed class ServiceHealthProbeTests
{
    [Fact]
    public async Task CheckAsync_TcpModeReportsAReachableForwardWithoutDoingTlsValidation()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var service = new ServiceDefinition
        {
            Id = "self-signed-https",
            Name = "Self-signed HTTPS",
            LocalPort = port,
            RemotePort = port,
            OpenUrl = $"https://127.0.0.1:{port}/#/login",
            HealthPath = "/",
            HealthKind = HealthCheckKind.TcpConnect,
        };
        using var probe = new ServiceHealthProbe(timeout: TimeSpan.FromSeconds(1));

        var result = await probe.CheckAsync(service);

        Assert.Equal(ServiceHealthState.Available, result.State);
        Assert.Null(result.HttpStatus);
        Assert.Equal("TCP connection succeeded.", result.Detail);
    }
}
