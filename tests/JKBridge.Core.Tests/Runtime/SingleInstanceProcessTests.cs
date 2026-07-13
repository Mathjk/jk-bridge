using System.Diagnostics;

namespace JKBridge.Core.Tests.Runtime;

public sealed class SingleInstanceProcessTests
{
    [Fact]
    public async Task IndependentProcessSessions_HaveExactlyOnePrimaryInstance()
    {
        var identifier = Guid.NewGuid().ToString("N");
        using var primary = StartProbe(identifier, holdMilliseconds: 5_000);
        try
        {
            await Task.Delay(350);
            Assert.False(primary.HasExited);

            using var secondary = StartProbe(identifier, holdMilliseconds: 100);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await secondary.WaitForExitAsync(timeout.Token);

            Assert.Equal(23, secondary.ExitCode);
            Assert.False(primary.HasExited);
        }
        finally
        {
            if (!primary.HasExited)
            {
                primary.Kill(entireProcessTree: true);
                await primary.WaitForExitAsync();
            }
        }
    }

    private static Process StartProbe(string identifier, int holdMilliseconds)
    {
        var executable = Path.Combine(
            AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "JKBridge.exe" : "JKBridge");
        Assert.True(File.Exists(executable), $"Probe executable was not found: {executable}");

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add($"--instance-probe-id={identifier}");
        startInfo.ArgumentList.Add($"--instance-probe-hold-ms={holdMilliseconds}");
        return Process.Start(startInfo)
               ?? throw new InvalidOperationException("Could not start the JK Bridge instance probe.");
    }
}
