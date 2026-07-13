using JKBridge.App.Services;
using JKBridge.Core.Models;
using JKBridge.Core.Processes;

namespace JKBridge.Core.Tests.Runtime;

public sealed class TailscaleClientTests
{
    [Fact]
    public async Task InspectAsync_PrefersMagicDns_WhenItResolves()
    {
        var runner = new FakeCommandRunner(SuccessStatus(online: true));
        var client = new TailscaleClient(
            runner,
            () => "tailscale.exe",
            new FakeResolver(resolves: true));

        var result = await client.InspectAsync(TestConfigs.CreateConfigured());

        Assert.Equal(TailscalePreflightKind.Ready, result.Kind);
        Assert.Equal(TestConfigs.LinuxHost, result.TargetHost);
        Assert.False(result.UsingIpFallback);
        Assert.Equal("user@example.com", result.Status?.LoginName);
        Assert.Equal(["status", "--json"], runner.LastRequest!.Arguments);
    }

    [Fact]
    public async Task InspectAsync_UsesRuntimeOnlyIpv4_WhenMagicDnsDoesNotResolve()
    {
        var client = new TailscaleClient(
            new FakeCommandRunner(SuccessStatus(online: true)),
            () => "tailscale.exe",
            new FakeResolver(resolves: false));

        var result = await client.InspectAsync(TestConfigs.CreateConfigured());

        Assert.Equal(TailscalePreflightKind.Ready, result.Kind);
        Assert.Equal("100.64.0.10", result.TargetHost);
        Assert.True(result.UsingIpFallback);
    }

    [Fact]
    public async Task InspectAsync_RejectsWrongTailnetBeforeSelectingPeer()
    {
        var configured = TestConfigs.CreateConfigured();
        var config = configured with
        {
            ExpectedTailnetDnsSuffix = "another-tailnet.ts.net",
            Server = configured.Server with
            {
                Host = "linux-box.another-tailnet.ts.net",
            },
        };
        var client = new TailscaleClient(
            new FakeCommandRunner(SuccessStatus(online: true)),
            () => "tailscale.exe",
            new FakeResolver(resolves: true));

        var result = await client.InspectAsync(config);

        Assert.Equal(TailscalePreflightKind.WrongTailnet, result.Kind);
    }

    [Fact]
    public async Task InspectAsync_ReportsOfflinePeerDistinctly()
    {
        var client = new TailscaleClient(
            new FakeCommandRunner(SuccessStatus(online: false)),
            () => "tailscale.exe",
            new FakeResolver(resolves: true));

        var result = await client.InspectAsync(TestConfigs.CreateConfigured());

        Assert.Equal(TailscalePreflightKind.PeerOffline, result.Kind);
        Assert.NotNull(result.Peer);
        Assert.True(result.IsRetryable);
    }

    [Fact]
    public async Task InspectAsync_ReportsMissingClientWithoutRunningCommand()
    {
        var runner = new FakeCommandRunner(SuccessStatus(online: true));
        var client = new TailscaleClient(runner, () => null, new FakeResolver(true));

        var result = await client.InspectAsync(TestConfigs.CreateConfigured());

        Assert.Equal(TailscalePreflightKind.ClientMissing, result.Kind);
        Assert.Null(runner.LastRequest);
    }

    [Fact]
    public async Task InspectAsync_DoesNotTryToParseFailedCommandOutput()
    {
        var command = new CommandResult(
            1,
            "not json",
            "failed to connect to local tailscaled",
            TimedOut: false,
            TimeSpan.FromMilliseconds(1));
        var client = new TailscaleClient(
            new FakeCommandRunner(command),
            () => "tailscale.exe",
            new FakeResolver(true));

        var result = await client.InspectAsync(TestConfigs.CreateConfigured());

        Assert.Equal(TailscalePreflightKind.CommandFailed, result.Kind);
        Assert.Contains("tailscaled", result.TechnicalDetail);
    }

    [Fact]
    public async Task InspectAsync_NotRunningIsRetryableDuringWindowsLoginStartup()
    {
        var command = new CommandResult(
            0,
            "{\"BackendState\":\"Stopped\"}",
            string.Empty,
            TimedOut: false,
            TimeSpan.FromMilliseconds(1));
        var client = new TailscaleClient(
            new FakeCommandRunner(command),
            () => "tailscale.exe",
            new FakeResolver(true));

        var result = await client.InspectAsync(TestConfigs.CreateConfigured());

        Assert.Equal(TailscalePreflightKind.NotRunning, result.Kind);
        Assert.True(result.IsRetryable);
    }

    private static CommandResult SuccessStatus(bool online) =>
        new(
            0,
            $$"""
            {
              "BackendState": "Running",
              "CurrentTailnet": {
                "Name": "Example tailnet",
                "MagicDNSSuffix": "example-tailnet.ts.net",
                "MagicDNSEnabled": true
              },
              "Self": {
                "UserID": 123,
                "HostName": "windows-client",
                "DNSName": "windows-client.example-tailnet.ts.net.",
                "Online": true,
                "TailscaleIPs": ["100.64.0.20"]
              },
              "User": {
                "123": {
                  "ID": 123,
                  "LoginName": "user@example.com"
                }
              },
              "Peer": {
                "node-key": {
                  "ID": "peer-id",
                  "HostName": "linux-box",
                  "DNSName": "linux-box.example-tailnet.ts.net.",
                  "Online": {{online.ToString().ToLowerInvariant()}},
                  "TailscaleIPs": ["fd7a:115c:a1e0::1234", "100.64.0.10"]
                }
              }
            }
            """,
            string.Empty,
            TimedOut: false,
            TimeSpan.FromMilliseconds(1));

    private sealed class FakeCommandRunner(CommandResult result) : ICommandRunner
    {
        public CommandRequest? LastRequest { get; private set; }

        public Task<CommandResult> RunAsync(
            CommandRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeResolver(bool resolves) : IHostNameResolver
    {
        public Task<bool> ResolvesAsync(string host, CancellationToken cancellationToken = default) =>
            Task.FromResult(resolves);
    }
}
