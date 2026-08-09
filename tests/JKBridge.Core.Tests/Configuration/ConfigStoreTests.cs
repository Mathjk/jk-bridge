using JKBridge.Core.Configuration;
using JKBridge.Core.Models;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JKBridge.Core.Tests.Configuration;

public sealed class ConfigStoreTests
{
    [Fact]
    public async Task LoadWithResultAsync_WhenNoFilesExist_ReturnsDefaultsWithoutWriting()
    {
        using var directory = new TemporaryDirectory();
        using var store = new ConfigStore(directory.File("config.json"));

        var result = await store.LoadWithResultAsync();

        Assert.Equal(ConfigLoadSource.Defaults, result.Source);
        Assert.True(ConfigValidator.Validate(result.Config).IsValid);
        Assert.False(File.Exists(store.ConfigPath));
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsConfigurationAndStringEnums()
    {
        using var directory = new TemporaryDirectory();
        using var store = new ConfigStore(directory.File("nested/config.json"));
        var defaults = AppConfig.CreateDefault();
        var expected = defaults with
        {
            Behavior = defaults.Behavior with { Theme = ThemePreference.Dark },
            Server = defaults.Server with { DisplayName = "My Linux" },
        };

        await store.SaveAsync(expected);
        var loaded = await store.LoadWithResultAsync();
        var json = await File.ReadAllTextAsync(store.ConfigPath);

        Assert.Equal(ConfigLoadSource.Primary, loaded.Source);
        Assert.Equal("My Linux", loaded.Config.Server.DisplayName);
        Assert.Equal(ThemePreference.Dark, loaded.Config.Behavior.Theme);
        Assert.Contains("\"theme\": \"dark\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsOnlyTheAgentCredentialReference()
    {
        using var directory = new TemporaryDirectory();
        using var store = new ConfigStore(directory.File("config.json"));
        var expected = AppConfig.CreateDefault() with
        {
            Agent = new AgentRegistration
            {
                IsConfigured = true,
                Endpoint = "http://100.73.7.51:45821/",
                TailscaleNodeId = "nJKBridgeLinux123",
                DeviceId = "device_A1b2c3d4",
                DeviceLabel = "Office Windows",
                CredentialReference = "agent-" + new string('a', 64),
                CatalogRevision = "sha256:abcdef123456",
            },
        };

        await store.SaveAsync(expected);
        var loaded = await store.LoadAsync();
        var json = await File.ReadAllTextAsync(store.ConfigPath);

        Assert.Equal(expected.Agent, loaded.Agent);
        Assert.Contains(expected.Agent.CredentialReference, json, StringComparison.Ordinal);
        Assert.DoesNotContain("enrollmentToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pairingToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deviceCredential", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SecondSave_PreservesPreviousValidPrimaryAsBackup()
    {
        using var directory = new TemporaryDirectory();
        using var store = new ConfigStore(directory.File("config.json"));
        var first = WithDisplayName("First");
        var second = WithDisplayName("Second");

        await store.SaveAsync(first);
        await store.SaveAsync(second);

        using var backupStore = new ConfigStore(store.BackupPath);
        var backup = await backupStore.LoadAsync();
        var current = await store.LoadAsync();

        Assert.Equal("Second", current.Server.DisplayName);
        Assert.Equal("First", backup.Server.DisplayName);
    }

    [Fact]
    public async Task InvalidSave_DoesNotReplaceExistingConfiguration()
    {
        using var directory = new TemporaryDirectory();
        using var store = new ConfigStore(directory.File("config.json"));
        await store.SaveAsync(WithDisplayName("Stable"));
        var originalBytes = await File.ReadAllBytesAsync(store.ConfigPath);
        var invalid = AppConfig.CreateDefault() with { SchemaVersion = 999 };

        await Assert.ThrowsAsync<ConfigValidationException>(() => store.SaveAsync(invalid));

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(store.ConfigPath));
        Assert.Equal("Stable", (await store.LoadAsync()).Server.DisplayName);
    }

    [Fact]
    public async Task CorruptPrimary_LoadsLastValidBackup()
    {
        using var directory = new TemporaryDirectory();
        using var store = new ConfigStore(directory.File("config.json"));
        await store.SaveAsync(WithDisplayName("Backup version"));
        await store.SaveAsync(WithDisplayName("Primary version"));
        await File.WriteAllTextAsync(store.ConfigPath, "{ definitely-not-json");

        var result = await store.LoadWithResultAsync();

        Assert.Equal(ConfigLoadSource.Backup, result.Source);
        Assert.Equal("Backup version", result.Config.Server.DisplayName);
        Assert.NotNull(result.Warning);
    }

    [Fact]
    public async Task InvalidPrimaryAndBackup_ThrowsWithoutSilentlyResettingDefaults()
    {
        using var directory = new TemporaryDirectory();
        using var store = new ConfigStore(directory.File("config.json"));
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(store.ConfigPath, "[]");
        await File.WriteAllTextAsync(store.BackupPath, "{\"schemaVersion\":999}");

        var exception = await Assert.ThrowsAsync<ConfigLoadException>(() =>
            store.LoadWithResultAsync());

        Assert.Equal(store.ConfigPath, exception.ConfigPath);
        Assert.NotNull(exception.PrimaryError);
        Assert.NotNull(exception.BackupError);
    }

    [Fact]
    public async Task Save_DoesNotLeaveTemporaryFiles()
    {
        using var directory = new TemporaryDirectory();
        using var store = new ConfigStore(directory.File("config.json"));

        await store.SaveAsync(AppConfig.CreateDefault());

        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task LoadWithResultAsync_MigratesVersionOneConfigurationAsAlreadyConfigured()
    {
        using var directory = new TemporaryDirectory();
        using var store = new ConfigStore(directory.File("config.json"));
        var legacy = TestConfigs.CreateConfigured() with
        {
            SchemaVersion = 1,
            SetupCompleted = false,
        };
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(store.ConfigPath, JsonSerializer.Serialize(legacy));

        var result = await store.LoadWithResultAsync();

        Assert.True(result.WasMigrated);
        Assert.True(result.Config.SetupCompleted);
        Assert.Equal(AppConfig.CurrentSchemaVersion, result.Config.SchemaVersion);
        Assert.Equal(TestConfigs.LinuxHost, result.Config.Server.Host);
    }

    [Fact]
    public async Task LoadWithResultAsync_MigratesVersionTwoWithUnknownAuthorization()
    {
        using var directory = new TemporaryDirectory();
        using var store = new ConfigStore(directory.File("config.json"));
        var legacy = TestConfigs.CreateConfigured() with
        {
            SchemaVersion = 2,
            Authorization = new ForwardAuthorizationGrant(),
        };
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(store.ConfigPath, JsonSerializer.Serialize(legacy));

        var result = await store.LoadWithResultAsync();

        Assert.True(result.WasMigrated);
        Assert.Equal(AppConfig.CurrentSchemaVersion, result.Config.SchemaVersion);
        Assert.False(result.Config.Authorization.IsKnown);
        Assert.Empty(result.Config.Authorization.RemotePorts);
    }

    [Fact]
    public async Task LoadWithResultAsync_MigratesVersionThreeWithoutChangingRoutesOrReceipt()
    {
        using var directory = new TemporaryDirectory();
        using var store = new ConfigStore(directory.File("config.json"));
        var configured = TestConfigs.CreateConfigured();
        var legacy = configured with
        {
            SchemaVersion = 3,
            Services = configured.Services.Select(service => service with { }).ToList(),
        };
        legacy.Services[0] = legacy.Services[0] with
        {
            LocalPort = 18180,
            OpenUrl = "http://127.0.0.1:18180/project?from=v3#/editor",
        };
        var json = JsonNode.Parse(JsonSerializer.Serialize(legacy))!.AsObject();
        Assert.True(json.Remove("Agent"));
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(store.ConfigPath, json.ToJsonString());

        var result = await store.LoadWithResultAsync();

        Assert.True(result.WasMigrated);
        Assert.Equal(AppConfig.CurrentSchemaVersion, result.Config.SchemaVersion);
        Assert.Equal(legacy.SetupCompleted, result.Config.SetupCompleted);
        Assert.Equal(legacy.Server, result.Config.Server);
        Assert.Equal(legacy.Authorization.IsKnown, result.Config.Authorization.IsKnown);
        Assert.Equal(
            legacy.Authorization.ClientKeyFingerprint,
            result.Config.Authorization.ClientKeyFingerprint);
        Assert.Equal(
            legacy.Authorization.RemotePorts,
            result.Config.Authorization.RemotePorts);
        Assert.Equal(legacy.Services, result.Config.Services);
        Assert.Equal(
            "http://127.0.0.1:18180/project?from=v3#/editor",
            result.Config.Services[0].OpenUrl);
        Assert.False(result.Config.Agent.IsConfigured);
    }

    [Fact]
    public async Task LoadWithResultAsync_MigratesVersionFourWithEmptyWindowsLocalServices()
    {
        using var directory = new TemporaryDirectory();
        using var store = new ConfigStore(directory.File("config.json"));
        var legacy = TestConfigs.CreateConfigured() with
        {
            SchemaVersion = 4,
        };
        var json = JsonNode.Parse(JsonSerializer.Serialize(legacy))!.AsObject();
        Assert.True(json.Remove("WindowsLocalServices"));
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(store.ConfigPath, json.ToJsonString());

        var result = await store.LoadWithResultAsync();

        Assert.True(result.WasMigrated);
        Assert.Equal(AppConfig.CurrentSchemaVersion, result.Config.SchemaVersion);
        Assert.Empty(result.Config.WindowsLocalServices);
        Assert.Equal(legacy.Services, result.Config.Services);
        Assert.Equal(legacy.Authorization.IsKnown, result.Config.Authorization.IsKnown);
        Assert.Equal(
            legacy.Authorization.ClientKeyFingerprint,
            result.Config.Authorization.ClientKeyFingerprint);
        Assert.Equal(
            legacy.Authorization.RemotePorts,
            result.Config.Authorization.RemotePorts);
        Assert.Equal(legacy.Agent, result.Config.Agent);
    }

    [Fact]
    public async Task LoadWithResultAsync_MigratesVersionFiveAndShowsNewTutorialOnce()
    {
        using var directory = new TemporaryDirectory();
        using var store = new ConfigStore(directory.File("config.json"));
        var legacy = TestConfigs.CreateConfigured() with
        {
            SchemaVersion = 5,
        };
        var json = JsonNode.Parse(JsonSerializer.Serialize(legacy))!.AsObject();
        Assert.Null(json["Behavior"]!["WelcomeTutorialCompleted"]);
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(store.ConfigPath, json.ToJsonString());

        var result = await store.LoadWithResultAsync();

        Assert.True(result.WasMigrated);
        Assert.Equal(AppConfig.CurrentSchemaVersion, result.Config.SchemaVersion);
        Assert.False(result.Config.Behavior.WelcomeTutorialCompleted);
        Assert.Equal(legacy.Services, result.Config.Services);
        Assert.Equal(legacy.WindowsLocalServices, result.Config.WindowsLocalServices);
        Assert.Equal(legacy.Agent, result.Config.Agent);
    }

    private static AppConfig WithDisplayName(string displayName)
    {
        var defaults = AppConfig.CreateDefault();
        return defaults with { Server = defaults.Server with { DisplayName = displayName } };
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "JKBridge.Core.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string relativePath) => System.IO.Path.Combine(Path, relativePath);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
