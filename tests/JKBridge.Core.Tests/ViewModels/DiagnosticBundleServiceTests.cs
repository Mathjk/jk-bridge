using System.IO.Compression;
using System.Text;
using System.Text.Json;
using JKBridge.App.Services;
using JKBridge.Core.Diagnostics;
using JKBridge.Core.Models;
using JKBridge.Core.Tunneling;

namespace JKBridge.Core.Tests.ViewModels;

public sealed class DiagnosticBundleServiceTests
{
    private static readonly DateTimeOffset GeneratedAt =
        new(2026, 7, 29, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Export_WritesOnlyAllowListedAnonymousAndRedactedContent()
    {
        using var temporary = new TemporaryDirectory();
        var destination = temporary.File("support.zip");
        var picker = new FakePathPicker(destination);
        var service = new DiagnosticBundleService(
            picker,
            timeProvider: new FixedTimeProvider(GeneratedAt));
        var input = CreateSensitiveInput();

        var result = await service.ExportWithPickerAsync(input);

        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath(destination), result.Path);
        Assert.Equal(GeneratedAt, result.GeneratedAt);
        Assert.Equal(DiagnosticBundleService.DefaultExportFileName, picker.SuggestedFileName);
        Assert.Equal(2, result.IncludedLogEvents);
        Assert.True(result.ArchiveBytes > 0);

        var entries = ReadArchive(destination);
        Assert.Equal(
            ["logs.jsonl", "manifest.json", "status.json"],
            entries.Keys.Order(StringComparer.Ordinal).ToArray());

        using var manifest = JsonDocument.Parse(entries["manifest.json"]);
        Assert.Equal(
            ["applicationVersion", "format", "formatVersion"],
            manifest.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal("2.2.0", manifest.RootElement.GetProperty("applicationVersion").GetString());

        using var status = JsonDocument.Parse(entries["status.json"]);
        Assert.Equal(
            ["desiredConnected", "serviceCount", "services", "tunnelPhase"],
            status.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        var services = status.RootElement.GetProperty("services").EnumerateArray().ToArray();
        Assert.Equal(2, services.Length);
        Assert.Equal("service-001", services[0].GetProperty("anonymousId").GetString());
        Assert.Equal("Available", services[0].GetProperty("phase").GetString());
        Assert.Equal("service-002", services[1].GetProperty("anonymousId").GetString());
        Assert.Equal("ForwardDenied", services[1].GetProperty("phase").GetString());

        var allContent = string.Join('\n', entries.Values);
        Assert.Contains("[redacted]", allContent, StringComparison.Ordinal);
        foreach (var forbidden in ForbiddenSentinels(input))
        {
            Assert.DoesNotContain(forbidden, allContent, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("config.json", allContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("identityFile", allContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credentialReference", allContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clientKeyFingerprint", allContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("remotePort", allContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localPort", allContent, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, ".*.tmp"));
    }

    [Fact]
    public async Task Export_BoundsLogCountFieldLengthsAndUncompressedLogBytes()
    {
        var events = Enumerable.Range(0, 260)
            .Select(index => new DiagnosticEvent(
                GeneratedAt.AddSeconds(index),
                DiagnosticSeverity.Warning,
                new string('C', 2_000),
                $"event-{index}-" + new string('M', 8_000),
                new string('D', 8_000)))
            .ToArray();
        var baseline = CreateSensitiveInput();
        var manyServices = Enumerable.Range(0, 80)
            .Select(index => new ServiceSnapshot(
                $"service-id-{index}",
                $"service-name-{index}",
                ServicePhase.Available))
            .ToArray();
        var input = baseline with
        {
            Snapshot = baseline.Snapshot with { Services = manyServices },
            LogEvents = events,
        };
        var storage = new RecordingStorage();
        var service = new DiagnosticBundleService(
            new FakePathPicker(null),
            storage,
            new FixedTimeProvider(GeneratedAt));

        var result = await service.ExportToPathAsync(input, "diagnostics.zip");

        Assert.InRange(result.IncludedLogEvents, 1, DiagnosticBundleService.MaximumLogEvents);
        Assert.NotNull(storage.Content);
        var entries = ReadArchive(storage.Content!);
        Assert.True(
            Encoding.UTF8.GetByteCount(entries["logs.jsonl"]) <=
            DiagnosticBundleService.MaximumLogBytes);
        var lines = entries["logs.jsonl"].Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(result.IncludedLogEvents, lines.Length);
        using var status = JsonDocument.Parse(entries["status.json"]);
        Assert.Equal(
            DiagnosticBundleService.MaximumServiceStatuses,
            status.RootElement.GetProperty("serviceCount").GetInt32());
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            Assert.InRange(document.RootElement.GetProperty("category").GetString()!.Length, 0, 64);
            Assert.InRange(document.RootElement.GetProperty("message").GetString()!.Length, 0, 512);
            Assert.InRange(document.RootElement.GetProperty("detail").GetString()!.Length, 0, 1_024);
        }
    }

    [Fact]
    public async Task Export_UsesInjectableStorageAndNeverWritesWhenPickerIsCancelled()
    {
        var storage = new RecordingStorage();
        var service = new DiagnosticBundleService(new FakePathPicker(null), storage);

        var result = await service.ExportWithPickerAsync(CreateSensitiveInput());

        Assert.Null(result);
        Assert.Null(storage.Path);
        Assert.Null(storage.Content);
    }

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("2.02.0")]
    [InlineData("http://100.64.0.1/token")]
    public async Task Export_RejectsNonVersionManifestValueBeforeStorage(
        string applicationVersion)
    {
        var storage = new RecordingStorage();
        var service = new DiagnosticBundleService(new FakePathPicker(null), storage);
        var input = CreateSensitiveInput() with { ApplicationVersion = applicationVersion };

        await Assert.ThrowsAsync<DiagnosticBundleException>(() =>
            service.ExportToPathAsync(input, "diagnostics.zip"));

        Assert.Null(storage.Content);
    }

    [Fact]
    public async Task FileStorage_AtomicallyReplacesExistingArchiveWithoutTemporaryResidue()
    {
        using var temporary = new TemporaryDirectory();
        var destination = temporary.File("support.zip");
        await File.WriteAllTextAsync(destination, "old-content");
        var service = new DiagnosticBundleService(new FakePathPicker(null));

        await service.ExportToPathAsync(CreateSensitiveInput(), destination);

        Assert.Equal("PK", Encoding.ASCII.GetString((await File.ReadAllBytesAsync(destination))[..2]));
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, ".*.tmp"));
    }

    [Fact]
    public async Task FileStorage_RejectsSymbolicLinkDestinationWithoutChangingTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows CI only compiles this test project. Creating symlinks there
            // requires an environment-specific privilege and is not deterministic.
            return;
        }

        using var temporary = new TemporaryDirectory();
        var target = temporary.File("target.zip");
        var link = temporary.File("linked.zip");
        await File.WriteAllTextAsync(target, "sentinel-target");
        File.CreateSymbolicLink(link, target);
        var service = new DiagnosticBundleService(new FakePathPicker(null));

        var exception = await Assert.ThrowsAsync<DiagnosticBundleException>(() =>
            service.ExportToPathAsync(CreateSensitiveInput(), link));

        Assert.Contains("symbolic link", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("sentinel-target", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, ".*.tmp"));
    }

    private static DiagnosticBundleInput CreateSensitiveInput()
    {
        var configured = TestConfigs.CreateConfigured();
        var config = configured with
        {
            Server = configured.Server with
            {
                IdentityFile = @"C:\Users\Alice\.ssh\sentinel-private-key",
                HostKeyAlias = "sentinel-host-key-alias",
            },
            Agent = new AgentRegistration
            {
                IsConfigured = true,
                Endpoint = "http://100.64.12.34:45871/",
                TailscaleNodeId = "node-sentinel-identity",
                DeviceId = "00112233445566778899aabbccddeeff",
                DeviceLabel = "Alice private laptop",
                CredentialReference = "sentinel-credential-reference",
                CatalogRevision = "sha256:sentinel-catalog-revision",
            },
        };
        var services = new[]
        {
            new ServiceSnapshot(
                "private-overleaf-id",
                "Private Overleaf",
                ServicePhase.Available,
                Detail: "reached linux-box.example-tailnet.ts.net"),
            new ServiceSnapshot(
                "private-sub2api-id",
                "Private Sub2API",
                ServicePhase.ForwardDenied,
                Detail: "denied for alice@example.com"),
        };
        var snapshot = new TunnelSnapshot(
            TunnelPhase.Degraded,
            DesiredConnected: true,
            "Connected to private host.",
            GeneratedAt,
            services,
            PeerDnsName: "linux-box.example-tailnet.ts.net",
            TargetHost: "100.64.12.34",
            TailscaleSelfDnsName: "windows-private.example-tailnet.ts.net",
            TailscaleLoginName: "alice@example.com",
            CurrentTailnetDnsSuffix: "example-tailnet.ts.net");
        var logs = new[]
        {
            new DiagnosticEvent(
                GeneratedAt.AddMinutes(-1),
                DiagnosticSeverity.Information,
                "Agent node-sentinel-identity",
                "host=linux-box.example-tailnet.ts.net login=alice@example.com " +
                "endpoint=http://100.64.12.34:45871/",
                @"IdentityFile=C:\Users\Alice\.ssh\sentinel-private-key " +
                "credential=sentinel-credential-reference token=super-secret-token"),
            new DiagnosticEvent(
                GeneratedAt,
                DiagnosticSeverity.Error,
                "Tunnel",
                "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAISENTINELKEY",
                "recovery-code=correct-horse-battery-staple /home/alice/.ssh/private-key"),
        };
        return new("2.2.0", config, snapshot, logs);
    }

    private static IEnumerable<string> ForbiddenSentinels(DiagnosticBundleInput input)
    {
        yield return input.Config.ExpectedTailnetDnsSuffix;
        yield return input.Config.Server.Host;
        yield return input.Config.Server.User;
        yield return input.Config.Server.IdentityFile;
        yield return input.Config.Server.HostKeyAlias;
        yield return input.Config.Server.PinnedHostKey;
        yield return input.Config.Authorization.ClientKeyFingerprint;
        yield return input.Config.Agent.Endpoint;
        yield return input.Config.Agent.TailscaleNodeId;
        yield return input.Config.Agent.DeviceId;
        yield return input.Config.Agent.DeviceLabel;
        yield return input.Config.Agent.CredentialReference;
        yield return input.Config.Agent.CatalogRevision;
        yield return "alice@example.com";
        yield return "super-secret-token";
        yield return "correct-horse-battery-staple";
        yield return "AAAAC3NzaC1lZDI1NTE5AAAAISENTINELKEY";
        yield return "/home/alice/.ssh/private-key";
        yield return "private-overleaf-id";
        yield return "Private Overleaf";
        yield return "private-sub2api-id";
        yield return "Private Sub2API";
    }

    private static Dictionary<string, string> ReadArchive(string path) =>
        ReadArchive(File.ReadAllBytes(path));

    private static Dictionary<string, string> ReadArchive(byte[] content)
    {
        using var stream = new MemoryStream(content);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(
            entry => entry.FullName,
            entry =>
            {
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                return reader.ReadToEnd();
            },
            StringComparer.Ordinal);
    }

    private sealed class FakePathPicker(string? path) : IDiagnosticBundlePathPicker
    {
        public string? SuggestedFileName { get; private set; }

        public Task<string?> PickExportPathAsync(
            string suggestedFileName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SuggestedFileName = suggestedFileName;
            return Task.FromResult(path);
        }
    }

    private sealed class RecordingStorage : IDiagnosticBundleStorage
    {
        public string? Path { get; private set; }

        public byte[]? Content { get; private set; }

        public Task WriteAtomicallyAsync(
            string destinationPath,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Path = destinationPath;
            Content = content.ToArray();
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "jkbridge-diagnostic-bundle-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup for test-only temporary data.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup for test-only temporary data.
            }
        }
    }
}
