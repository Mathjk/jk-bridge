using JKBridge.Core.Configuration;
using JKBridge.Core.Models;
using System.Text.Json;

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
        Assert.Equal(3, result.Config.SchemaVersion);
        Assert.False(result.Config.Authorization.IsKnown);
        Assert.Empty(result.Config.Authorization.RemotePorts);
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
