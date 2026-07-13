using JKBridge.Core.Processes;

namespace JKBridge.Core.Tests;

public sealed class CommandRunnerTests
{
    [Fact]
    public async Task RunAsync_PassesArgumentsWithoutInvokingAShell()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runner = new CommandRunner();
        var request = new CommandRequest(
            "/bin/echo",
            ["literal;value", "$(not-executed)"],
            TimeSpan.FromSeconds(5));

        var result = await runner.RunAsync(request);

        Assert.True(result.IsSuccess, result.StandardError);
        Assert.Contains("literal;value $(not-executed)", result.StandardOutput);
    }

    [Fact]
    public async Task RunAsync_ReportsTimeoutAndStopsProcess()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runner = new CommandRunner();
        var result = await runner.RunAsync(new CommandRequest(
            "/bin/sh",
            ["-c", "sleep 5"],
            TimeSpan.FromMilliseconds(100)));

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
    }
}
