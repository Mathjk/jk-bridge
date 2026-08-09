using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using JKBridge.Core.Pairing;

namespace JKBridge.Core.Tests.Pairing;

public sealed class LinuxPairingBootstrapTests
{
    [Fact]
    public void PairCommand_EmbedsVerifiedHelperWithoutLinuxNetworkDependency()
    {
        Assert.Matches("^[0-9a-f]{40}$", LinuxPairingBootstrap.SourceCommit);
        Assert.Matches("^[0-9a-f]{64}$", LinuxPairingBootstrap.HelperSha256);
        Assert.Equal(
            $"https://github.com/Mathjk/jk-bridge/blob/{LinuxPairingBootstrap.SourceCommit}/scripts/jkbridge-linux.py",
            LinuxPairingBootstrap.HelperSourceUri.AbsoluteUri);
        Assert.Contains(LinuxPairingBootstrap.HelperSha256, LinuxPairingBootstrap.PairCommand);
        Assert.Contains("gzip.decompress", LinuxPairingBootstrap.PairCommand);
        Assert.Contains("base64.b64decode", LinuxPairingBootstrap.PairCommand);
        Assert.StartsWith("python3 -c \"", LinuxPairingBootstrap.PairCommand, StringComparison.Ordinal);
        Assert.EndsWith("\" pair", LinuxPairingBootstrap.PairCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", LinuxPairingBootstrap.PairCommand, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", LinuxPairingBootstrap.PairCommand, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sudo", LinuxPairingBootstrap.PairCommand, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("git clone", LinuxPairingBootstrap.PairCommand, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(LinuxPairingBootstrap.PairCommand.Length, 5_000, 20_000);
    }

    [Fact]
    public void PairCommandPayload_DecompressesToEmbeddedVerifiedHelper()
    {
        var match = Regex.Match(
            LinuxPairingBootstrap.PairCommand,
            "b64decode\\('(?<payload>[A-Za-z0-9+/=]+)'\\)");
        Assert.True(match.Success);

        var compressed = Convert.FromBase64String(match.Groups["payload"].Value);
        using var source = new MemoryStream(compressed);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);

        Assert.Equal(LinuxPairingBootstrap.ReadEmbeddedHelper(), output.ToArray());
    }

    [Fact]
    public async Task PairCommandPayload_RunsReadOnlyStatusWithoutNetwork()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const string prefix = "python3 -c \"";
        const string suffix = "\" pair";
        var command = LinuxPairingBootstrap.PairCommand;
        var pythonCode = command[prefix.Length..^suffix.Length];
        var startInfo = new ProcessStartInfo("python3")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(pythonCode);
        startInfo.ArgumentList.Add("status");
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await process.WaitForExitAsync(timeout.Token);
        var standardOutput = await process.StandardOutput.ReadToEndAsync(timeout.Token);
        var standardError = await process.StandardError.ReadToEndAsync(timeout.Token);

        Assert.True(
            process.ExitCode == 0,
            $"Embedded helper status failed. stdout: {standardOutput}; stderr: {standardError}");
    }

    [Fact]
    public async Task EmbeddedChecksum_MatchesRepositoryHelper()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            ".."));
        var helperPath = Path.Combine(repositoryRoot, "scripts", "jkbridge-linux.py");

        Assert.True(File.Exists(helperPath), $"Linux pairing helper not found: {helperPath}");
        var bytes = await File.ReadAllBytesAsync(helperPath);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        Assert.Equal(LinuxPairingBootstrap.HelperSha256, hash);
        Assert.Equal(bytes, LinuxPairingBootstrap.ReadEmbeddedHelper());
    }
}
