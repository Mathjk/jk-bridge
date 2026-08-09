using System.Text.Json;
using System.Text.Json.Serialization;
using JKBridge.Core.Models;

namespace JKBridge.Core.Configuration;

public sealed class ConfigStore : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _serializerOptions;
    private bool _disposed;

    public ConfigStore(string configPath, JsonSerializerOptions? serializerOptions = null)
    {
        if (string.IsNullOrWhiteSpace(configPath))
        {
            throw new ArgumentException("Configuration path is required.", nameof(configPath));
        }

        ConfigPath = Path.GetFullPath(configPath);
        BackupPath = ConfigPath + ".bak";
        _serializerOptions = serializerOptions is null
            ? CreateSerializerOptions()
            : new JsonSerializerOptions(serializerOptions);
    }

    public string ConfigPath { get; }

    public string BackupPath { get; }

    public static string GetDefaultConfigPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JKBridge",
            "config.json");

    public async Task<AppConfig> LoadAsync(CancellationToken cancellationToken = default) =>
        (await LoadWithResultAsync(cancellationToken).ConfigureAwait(false)).Config;

    public async Task<ConfigLoadResult> LoadWithResultAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var primary = await TryReadAsync(ConfigPath, cancellationToken).ConfigureAwait(false);
            if (primary.Config is not null)
            {
                return new(primary.Config, ConfigLoadSource.Primary, null, primary.WasMigrated);
            }

            var backup = await TryReadAsync(BackupPath, cancellationToken).ConfigureAwait(false);
            if (backup.Config is not null)
            {
                var warning = primary.Exists
                    ? "The primary configuration was invalid; the last valid backup was loaded."
                    : "The primary configuration was missing; the last valid backup was loaded.";
                return new(backup.Config, ConfigLoadSource.Backup, warning, backup.WasMigrated);
            }

            if (!primary.Exists && !backup.Exists)
            {
                return new(AppConfig.CreateDefault(), ConfigLoadSource.Defaults, null, false);
            }

            throw new ConfigLoadException(ConfigPath, primary.Error, backup.Error);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ConfigValidator.EnsureValid(config);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(ConfigPath)
                ?? throw new InvalidOperationException("Configuration path has no parent directory.");
            Directory.CreateDirectory(directory);

            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(ConfigPath)}.{Guid.NewGuid():N}.tmp");

            await WriteDurablyAsync(temporaryPath, config, cancellationToken).ConfigureAwait(false);

            if (!File.Exists(ConfigPath))
            {
                File.Move(temporaryPath, ConfigPath);
                temporaryPath = null;
                return;
            }

            var current = await TryReadAsync(ConfigPath, cancellationToken).ConfigureAwait(false);
            var backupPath = current.Config is null ? null : BackupPath;
            ReplaceFile(temporaryPath, ConfigPath, backupPath);
            temporaryPath = null;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // A failed cleanup must not hide the original save exception.
                }
                catch (UnauthorizedAccessException)
                {
                    // A failed cleanup must not hide the original save exception.
                }
            }

            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private async Task<ReadAttempt> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new(false, null, null);
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var config = await JsonSerializer.DeserializeAsync<AppConfig>(
                    stream,
                    _serializerOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            var sourceSchema = config?.SchemaVersion;
            var wasMigrated = sourceSchema is >= 1 and <= 6;
            config = sourceSchema switch
            {
                1 => config! with
                {
                    SchemaVersion = AppConfig.CurrentSchemaVersion,
                    SetupCompleted = true,
                    Authorization = new ForwardAuthorizationGrant(),
                },
                2 => config! with
                {
                    SchemaVersion = AppConfig.CurrentSchemaVersion,
                    Authorization = new ForwardAuthorizationGrant(),
                },
                3 => config! with
                {
                    SchemaVersion = AppConfig.CurrentSchemaVersion,
                },
                4 => config! with
                {
                    SchemaVersion = AppConfig.CurrentSchemaVersion,
                },
                5 => config! with
                {
                    SchemaVersion = AppConfig.CurrentSchemaVersion,
                },
                6 => config! with
                {
                    SchemaVersion = AppConfig.CurrentSchemaVersion,
                },
                _ => config,
            };

            ConfigValidator.EnsureValid(config);
            return new(true, config, null, wasMigrated);
        }
        catch (Exception exception) when (exception is JsonException or ConfigValidationException)
        {
            return new(true, null, exception, false);
        }
    }

    private async Task WriteDurablyAsync(
        string path,
        AppConfig config,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, config, _serializerOptions, cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static void ReplaceFile(string source, string destination, string? backup)
    {
        try
        {
            File.Replace(source, destination, backup, ignoreMetadataErrors: true);
        }
        catch (PlatformNotSupportedException)
        {
            FallbackReplace(source, destination, backup);
        }
    }

    private static void FallbackReplace(string source, string destination, string? backup)
    {
        if (backup is not null)
        {
            File.Copy(destination, backup, overwrite: true);
        }

        File.Move(source, destination, overwrite: true);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record ReadAttempt(
        bool Exists,
        AppConfig? Config,
        Exception? Error,
        bool WasMigrated = false);
}

public sealed record ConfigLoadResult(
    AppConfig Config,
    ConfigLoadSource Source,
    string? Warning,
    bool WasMigrated);

public enum ConfigLoadSource
{
    Primary,
    Backup,
    Defaults,
}

public sealed class ConfigLoadException : Exception
{
    public ConfigLoadException(string configPath, Exception? primaryError, Exception? backupError)
        : base("Neither the primary configuration nor its backup could be loaded.", primaryError ?? backupError)
    {
        ConfigPath = configPath;
        PrimaryError = primaryError;
        BackupError = backupError;
    }

    public string ConfigPath { get; }

    public Exception? PrimaryError { get; }

    public Exception? BackupError { get; }
}
