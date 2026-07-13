using System.Net;
using System.Net.Sockets;
using JKBridge.App.Services;
using JKBridge.Core.Tunneling;

namespace JKBridge.Core.Tests.Runtime;

public sealed class RuntimeInfrastructureTests
{
    [Fact]
    public async Task KnownHostsManager_WritesOnlyPinnedAliasEntry_AndRepairsOldContent()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "ssh", "known_hosts");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "old-host ssh-ed25519 AAAA\n");
            var manager = new KnownHostsManager(path);

            await manager.EnsureAsync("jk-bridge-linux", "ssh-ed25519 AAAACorrectKey");

            var content = await File.ReadAllTextAsync(path);
            Assert.Equal(
                $"jk-bridge-linux ssh-ed25519 AAAACorrectKey{Environment.NewLine}",
                content);
            Assert.DoesNotContain("old-host", content);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LocalPortChecker_ReportsOccupiedLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var conflicts = await new LocalPortChecker().FindConflictsAsync([port]);

        Assert.Equal([port], conflicts);
    }

    [Theory]
    [InlineData(0, 0.0, 0.8)]
    [InlineData(0, 0.5, 1.0)]
    [InlineData(0, 1.0, 1.2)]
    [InlineData(2, 0.5, 5.0)]
    [InlineData(99, 0.5, 30.0)]
    public void RetrySchedule_IsBoundedAndCapsAtThirtySeconds(
        int attempt,
        double jitter,
        double expectedSeconds)
    {
        var delay = TunnelRetrySchedule.GetDelay(attempt, jitter);

        Assert.Equal(expectedSeconds, delay.TotalSeconds, precision: 6);
    }

    [Fact]
    public void SshClassifier_DoesNotMistakeRemoteConnectionRefusalForPolicyDenial()
    {
        var result = SshErrorClassifier.Classify(
            "channel 3: open failed: connect failed: Connection refused");

        Assert.NotEqual(SshFailureKind.ForwardingDenied, result.Kind);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jkbridge-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
