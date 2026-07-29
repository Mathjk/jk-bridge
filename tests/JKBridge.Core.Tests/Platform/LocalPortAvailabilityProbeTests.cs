using System.Net;
using System.Net.Sockets;
using JKBridge.App.Platform;

namespace JKBridge.Core.Tests.Platform;

public sealed class LocalPortAvailabilityProbeTests
{
    [Fact]
    public void Check_ReportsAnActiveTcpListenerAndSuggestsADifferentPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var occupiedPort = ((IPEndPoint)listener.LocalEndpoint).Port;

        var result = SystemLocalPortAvailabilityProbe.Instance.Check(occupiedPort);

        Assert.Equal(LocalPortAvailabilityStatus.InUse, result.Status);
        Assert.NotNull(result.SuggestedPort);
        Assert.NotEqual(occupiedPort, result.SuggestedPort);
        Assert.InRange(result.SuggestedPort!.Value, 1024, 65535);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Check_ReportsInvalidPortsWithoutInspectingOrChangingTheSystem(int port)
    {
        var result = SystemLocalPortAvailabilityProbe.Instance.Check(port);

        Assert.Equal(LocalPortAvailabilityStatus.Unknown, result.Status);
        Assert.Null(result.SuggestedPort);
        Assert.Contains("1–65535", result.Detail);
    }
}
