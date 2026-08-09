using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using JKBridge.Core.Pairing;

namespace JKBridge.Core.Tests.Pairing;

public sealed class LinuxAgentBootstrapTests
{
    [Fact]
    public void InstallCommand_EmbedsOneVerifiedOfflinePackage()
    {
        Assert.StartsWith(
            "python3 -c \"",
            LinuxAgentBootstrap.InstallCommand,
            StringComparison.Ordinal);
        Assert.EndsWith("\"", LinuxAgentBootstrap.InstallCommand, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{64}$", LinuxAgentBootstrap.BundleSha256);
        Assert.Contains(LinuxAgentBootstrap.BundleSha256, LinuxAgentBootstrap.InstallCommand);
        Assert.Contains("tempfile.mkdtemp", LinuxAgentBootstrap.InstallCommand);
        Assert.Contains("'--yes','--source-dir'", LinuxAgentBootstrap.InstallCommand);
        Assert.DoesNotContain(
            "http://",
            LinuxAgentBootstrap.InstallCommand,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "https://",
            LinuxAgentBootstrap.InstallCommand,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sudo", LinuxAgentBootstrap.InstallCommand, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("git clone", LinuxAgentBootstrap.InstallCommand, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("curl", LinuxAgentBootstrap.InstallCommand, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wget", LinuxAgentBootstrap.InstallCommand, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(LinuxAgentBootstrap.InstallCommand.Length, 20_000, 500_000);
    }

    [Fact]
    public void InstallCommandPayload_MatchesEveryEmbeddedAgentFile()
    {
        var match = Regex.Match(
            LinuxAgentBootstrap.InstallCommand,
            "base64\\.b64decode\\('(?<payload>[A-Za-z0-9+/=]+)'\\)");
        Assert.True(match.Success);

        var archiveBytes = Convert.FromBase64String(match.Groups["payload"].Value);
        var hash = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
        Assert.Equal(LinuxAgentBootstrap.BundleSha256, hash);

        var expected = LinuxAgentBootstrap.ReadEmbeddedFiles();
        using var archive = new ZipArchive(new MemoryStream(archiveBytes), ZipArchiveMode.Read);
        Assert.Equal(
            expected.Keys.Order(StringComparer.Ordinal),
            archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
        foreach (var entry in archive.Entries)
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            Assert.Equal(expected[entry.FullName], buffer.ToArray());
        }
    }
}
