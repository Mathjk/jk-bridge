using JKBridge.App.Services;
using JKBridge.Core.Configuration;
using JKBridge.Core.Models;
using JKBridge.Core.Routing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JKBridge.Core.Tests.ViewModels;

public sealed class ServiceProfileTransferServiceTests
{
    [Fact]
    public async Task ExportWithPicker_WritesBoundedAllowListAndReplacesAtomically()
    {
        using var temporary = new TemporaryDirectory();
        var exportPath = temporary.File("services.json");
        await File.WriteAllTextAsync(exportPath, "previous content");
        var picker = new FakePathPicker { ExportPath = exportPath };
        using var store = new ConfigStore(temporary.File("config.json"));
        var service = new ServiceProfileTransferService(store, picker);
        var current = CreateSensitiveConfig();

        var result = await service.ExportWithPickerAsync(current, "Portable services");

        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath(exportPath), result.Path);
        Assert.Equal(current.Services.Count, result.ServiceCount);
        Assert.InRange(
            result.DocumentBytes,
            1,
            ServiceProfileSerializer.MaximumDocumentBytes);
        Assert.Equal(
            ServiceProfileTransferService.DefaultExportFileName,
            picker.SuggestedExportFileName);

        var json = await File.ReadAllTextAsync(exportPath);
        Assert.Contains("\"windowsLocalPort\"", json, StringComparison.Ordinal);
        Assert.Contains("\"linuxLoopbackPort\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain(current.Server.Host, json, StringComparison.Ordinal);
        Assert.DoesNotContain(current.Server.IdentityFile, json, StringComparison.Ordinal);
        Assert.DoesNotContain(current.Server.PinnedHostKey, json, StringComparison.Ordinal);
        Assert.DoesNotContain(current.ExpectedTailnetDnsSuffix, json, StringComparison.Ordinal);
        Assert.DoesNotContain(current.Authorization.ClientKeyFingerprint, json, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, ".*.tmp"));
    }

    [Fact]
    public async Task PreviewImport_ReportsServiceDiffAndExactAuthorizationExpansionWithoutMutation()
    {
        using var temporary = new TemporaryDirectory();
        var configPath = temporary.File("config.json");
        var profilePath = temporary.File("services.json");
        var current = CreateSensitiveConfig();
        var proposed = current with
        {
            Services = current.Services
                .Append(ServiceTemplateCatalog.Create(ServiceTemplateCatalog.Sub2Api))
                .ToList(),
        };
        await WriteProfileAsync(profilePath, proposed);
        var picker = new FakePathPicker { ImportPath = profilePath };
        using var store = new ConfigStore(configPath);
        await store.SaveAsync(current);
        var before = await File.ReadAllBytesAsync(configPath);
        var service = new ServiceProfileTransferService(store, picker);

        var preview = await service.PreviewImportWithPickerAsync(current);

        Assert.NotNull(preview);
        Assert.Equal(ServiceProfileImportSource.ServiceProfile, preview.Source);
        Assert.Equal("Portable services", preview.DisplayName);
        Assert.Equal(proposed.Services.Count, preview.ServiceCount);
        Assert.Equal(1, preview.Diff.AddedServices);
        Assert.Equal(0, preview.Diff.ModifiedServices);
        Assert.Equal(RemoteAuthorizationImpact.ReauthorizationRequired, preview.Diff.AuthorizationImpact);
        Assert.True(preview.Diff.RequiresRemoteReauthorization);
        Assert.True(preview.Diff.BlocksUntilReauthorized);
        Assert.Equal([8081], preview.Diff.AddedLinuxPorts);
        Assert.Contains(preview.Diff.Services, change =>
            change.Id == ServiceTemplateCatalog.Sub2Api &&
            change.Kind == ServiceChangeKind.Added &&
            change.WindowsPort == 8081 &&
            change.LinuxPort == 8081);
        Assert.Equal(before, await File.ReadAllBytesAsync(configPath));
    }

    [Fact]
    public async Task PreviewImport_LegalV21ConfigUsesLegacySourceButOnlyImportsServices()
    {
        using var temporary = new TemporaryDirectory();
        var configPath = temporary.File("config.json");
        var legacyPath = temporary.File("legacy-v21-config.json");
        var current = CreateSensitiveConfig();
        var legacy = current with
        {
            SchemaVersion = 3,
            Services = current.Services
                .Append(ServiceTemplateCatalog.Create(ServiceTemplateCatalog.Sub2Api))
                .ToList(),
        };
        await File.WriteAllTextAsync(legacyPath, SerializeLegacyV3(legacy));
        using var store = new ConfigStore(configPath);
        await store.SaveAsync(current);
        var before = await File.ReadAllBytesAsync(configPath);
        var service = new ServiceProfileTransferService(store, new FakePathPicker());

        var preview = await service.PreviewImportFromPathAsync(current, legacyPath);

        Assert.Equal(ServiceProfileImportSource.LegacyConfigV3, preview.Source);
        Assert.Equal("JK Bridge v2.1 services", preview.DisplayName);
        Assert.Equal(legacy.Services.Count, preview.ServiceCount);
        Assert.Equal([8081], preview.Diff.AddedLinuxPorts);
        Assert.Equal(before, await File.ReadAllBytesAsync(configPath));
    }

    [Fact]
    public async Task ApplyImport_LegacyDeviceIdentityAndAuthorizationNeverReplaceCurrentState()
    {
        using var temporary = new TemporaryDirectory();
        var legacyPath = temporary.File("other-device-v21-config.json");
        var current = CreateSensitiveConfig();
        var legacy = TestConfigs.CreateConfigured() with
        {
            SchemaVersion = 3,
            Server = TestConfigs.CreateConfigured().Server with
            {
                DisplayName = "Other legacy server",
                Host = "old-linux.example-tailnet.ts.net",
                IdentityFile = @"C:\other-device\private-key",
            },
            Authorization = new ForwardAuthorizationGrant
            {
                IsKnown = true,
                ClientKeyFingerprint = "SHA256:" + new string('B', 43),
                RemotePorts = [80, 8081, 8765, 8793],
            },
            Services = TestConfigs.CreateConfigured().Services
                .Append(ServiceTemplateCatalog.Create(ServiceTemplateCatalog.Sub2Api))
                .ToList(),
        };
        await File.WriteAllTextAsync(legacyPath, SerializeLegacyV3(legacy));
        using var store = new ConfigStore(temporary.File("config.json"));
        await store.SaveAsync(current);
        var service = new ServiceProfileTransferService(store, new FakePathPicker());
        var preview = await service.PreviewImportFromPathAsync(current, legacyPath);

        var result = await service.ApplyImportAsync(current, preview);
        var loaded = await store.LoadAsync();

        Assert.Equal(ServiceProfileImportSource.LegacyConfigV3, preview.Source);
        Assert.Equal(current.Server, loaded.Server);
        AssertAuthorizationEqual(current.Authorization, loaded.Authorization);
        Assert.Equal(current.Behavior, loaded.Behavior);
        Assert.Equal(legacy.Services, loaded.Services);
        Assert.Equal(current.Server, result.Config.Server);
        AssertAuthorizationEqual(current.Authorization, result.Config.Authorization);
    }

    [Fact]
    public async Task ApplyImport_WritesAtomicBackupAndPreservesAllDeviceState()
    {
        using var temporary = new TemporaryDirectory();
        var profilePath = temporary.File("services.json");
        var current = CreateSensitiveConfig();
        var proposed = current with
        {
            Services = current.Services
                .Append(ServiceTemplateCatalog.Create(ServiceTemplateCatalog.Sub2Api))
                .ToList(),
        };
        await WriteProfileAsync(profilePath, proposed);
        using var store = new ConfigStore(temporary.File("config.json"));
        await store.SaveAsync(current);
        var service = new ServiceProfileTransferService(
            store,
            new FakePathPicker { ImportPath = profilePath });
        var preview = await service.PreviewImportWithPickerAsync(current);

        var result = await service.ApplyImportAsync(current, preview!);
        var loaded = await store.LoadAsync();

        Assert.Equal(store.BackupPath, result.BackupPath);
        Assert.True(File.Exists(store.BackupPath));
        Assert.Equal(current.SchemaVersion, loaded.SchemaVersion);
        Assert.Equal(current.SetupCompleted, loaded.SetupCompleted);
        Assert.Equal(current.ExpectedTailnetDnsSuffix, loaded.ExpectedTailnetDnsSuffix);
        Assert.Equal(current.Server, loaded.Server);
        Assert.Equal(current.Behavior, loaded.Behavior);
        AssertAuthorizationEqual(current.Authorization, loaded.Authorization);
        Assert.Equal(proposed.Services, loaded.Services);
        Assert.Equal(proposed.Services, result.Config.Services);

        using var backupStore = new ConfigStore(store.BackupPath);
        var backup = await backupStore.LoadAsync();
        Assert.Equal(current.Services, backup.Services);
        Assert.Equal(current.Server, backup.Server);
        AssertAuthorizationEqual(current.Authorization, backup.Authorization);
    }

    [Fact]
    public async Task ApplyImport_WhenServicesChangedAfterPreview_FailsWithoutWriting()
    {
        using var temporary = new TemporaryDirectory();
        var profilePath = temporary.File("services.json");
        var current = CreateSensitiveConfig();
        var proposed = current with
        {
            Services = current.Services
                .Append(ServiceTemplateCatalog.Create(ServiceTemplateCatalog.Sub2Api))
                .ToList(),
        };
        await WriteProfileAsync(profilePath, proposed);
        using var store = new ConfigStore(temporary.File("config.json"));
        await store.SaveAsync(current);
        var service = new ServiceProfileTransferService(
            store,
            new FakePathPicker { ImportPath = profilePath });
        var preview = await service.PreviewImportWithPickerAsync(current);
        var changedServices = current.Services.ToList();
        changedServices[0] = changedServices[0] with
        {
            LocalPort = 18080,
            OpenUrl = ServiceOpenUrl.Build("http", 18080, "/project"),
        };
        var changedCurrent = current with { Services = changedServices };

        var exception = await Assert.ThrowsAsync<ServiceProfileTransferException>(() =>
            service.ApplyImportAsync(changedCurrent, preview!));

        Assert.Contains("changed after", exception.Message, StringComparison.Ordinal);
        var loaded = await store.LoadAsync();
        Assert.Equal(current.Services, loaded.Services);
        Assert.False(File.Exists(store.BackupPath));
    }

    [Fact]
    public async Task PreviewImport_LocalPortAndUrlOnly_DoesNotRequireLinuxAuthorization()
    {
        using var temporary = new TemporaryDirectory();
        var profilePath = temporary.File("services.json");
        var current = CreateSensitiveConfig();
        var services = current.Services.ToList();
        services[0] = services[0] with
        {
            LocalPort = 18080,
            OpenUrl = ServiceOpenUrl.Build("http", 18080, "/project#/document"),
        };
        await WriteProfileAsync(profilePath, current with { Services = services });
        using var store = new ConfigStore(temporary.File("config.json"));
        var service = new ServiceProfileTransferService(store, new FakePathPicker());

        var preview = await service.PreviewImportFromPathAsync(current, profilePath);

        Assert.Equal(1, preview.Diff.ModifiedServices);
        Assert.Equal(RemoteAuthorizationImpact.None, preview.Diff.AuthorizationImpact);
        Assert.False(preview.Diff.RequiresRemoteReauthorization);
        Assert.False(preview.Diff.BlocksUntilReauthorized);
        Assert.Empty(preview.Diff.AddedLinuxPorts);
        Assert.Empty(preview.Diff.RemovedLinuxPorts);
    }

    [Fact]
    public async Task PreviewImport_RejectsOversizedDocumentBeforeParsing()
    {
        using var temporary = new TemporaryDirectory();
        var profilePath = temporary.File("oversized.json");
        await using (var stream = new FileStream(profilePath, FileMode.CreateNew, FileAccess.Write))
        {
            stream.SetLength(ServiceProfileSerializer.MaximumDocumentBytes + 1L);
        }
        using var store = new ConfigStore(temporary.File("config.json"));
        var service = new ServiceProfileTransferService(store, new FakePathPicker());

        var exception = await Assert.ThrowsAsync<ServiceProfileSerializationException>(() =>
            service.PreviewImportFromPathAsync(CreateSensitiveConfig(), profilePath));

        Assert.Contains("size limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PickerCancellation_ReturnsNullAndDoesNotCreateOrChangeFiles()
    {
        using var temporary = new TemporaryDirectory();
        using var store = new ConfigStore(temporary.File("config.json"));
        var service = new ServiceProfileTransferService(store, new FakePathPicker());
        var current = CreateSensitiveConfig();

        var export = await service.ExportWithPickerAsync(current);
        var import = await service.PreviewImportWithPickerAsync(current);

        Assert.Null(export);
        Assert.Null(import);
        Assert.Empty(Directory.EnumerateFiles(temporary.Path));
    }

    private static async Task WriteProfileAsync(string path, AppConfig config)
    {
        var profile = ServiceProfileSerializer.Create(config, "Portable services");
        await File.WriteAllBytesAsync(path, ServiceProfileSerializer.SerializeToUtf8Bytes(profile));
    }

    private static string SerializeLegacyV3(AppConfig config)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 3,
            config.SetupCompleted,
            config.ExpectedTailnetDnsSuffix,
            config.Server,
            config.Behavior,
            config.Authorization,
            config.Services,
        }, options);
    }

    private static AppConfig CreateSensitiveConfig()
    {
        var configured = TestConfigs.CreateConfigured();
        return configured with
        {
            Server = configured.Server with
            {
                IdentityFile = @"C:\private\sentinel-jk-bridge-ed25519",
            },
        };
    }

    private static void AssertAuthorizationEqual(
        ForwardAuthorizationGrant expected,
        ForwardAuthorizationGrant actual)
    {
        Assert.Equal(expected.IsKnown, actual.IsKnown);
        Assert.Equal(expected.ClientKeyFingerprint, actual.ClientKeyFingerprint);
        Assert.Equal(expected.RemotePorts, actual.RemotePorts);
    }

    private sealed class FakePathPicker : IServiceProfilePathPicker
    {
        public string? ExportPath { get; init; }

        public string? ImportPath { get; init; }

        public string? SuggestedExportFileName { get; private set; }

        public Task<string?> PickExportPathAsync(
            string suggestedFileName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SuggestedExportFileName = suggestedFileName;
            return Task.FromResult(ExportPath);
        }

        public Task<string?> PickImportPathAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ImportPath);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "jkbridge-service-profile-tests",
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
