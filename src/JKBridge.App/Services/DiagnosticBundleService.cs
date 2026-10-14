using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JKBridge.Core.Diagnostics;
using JKBridge.Core.Models;
using JKBridge.Core.Tunneling;

namespace JKBridge.App.Services;

/// <summary>
/// Keeps the save-picker outside the diagnostic export workflow. Implementations
/// must return a user-selected file path and must not read from the clipboard.
/// </summary>
public interface IDiagnosticBundlePathPicker
{
    Task<string?> PickExportPathAsync(
        string suggestedFileName,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Injectable persistence boundary for tests and platform adapters.
/// </summary>
public interface IDiagnosticBundleStorage
{
    Task WriteAtomicallyAsync(
        string destinationPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Writes a deliberately small, allow-listed support archive. The implementation
/// never serializes AppConfig or TunnelSnapshot directly: config is consulted only
/// to redact known sensitive values, and runtime services receive anonymous labels.
/// </summary>
public sealed class DiagnosticBundleService
{
    public const string DefaultExportFileName = "jkbridge-diagnostics.zip";
    public const int MaximumLogEvents = 200;
    public const int MaximumLogBytes = 192 * 1024;
    public const int MaximumServiceStatuses = 64;

    private const int FormatVersion = 1;
    private const int MaximumApplicationVersionLength = 64;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex SafeVersion = new(
        "\\A[vV]?(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly IDiagnosticBundlePathPicker _pathPicker;
    private readonly IDiagnosticBundleStorage _storage;
    private readonly TimeProvider _timeProvider;

    public DiagnosticBundleService(
        IDiagnosticBundlePathPicker pathPicker,
        IDiagnosticBundleStorage? storage = null,
        TimeProvider? timeProvider = null)
    {
        _pathPicker = pathPicker ?? throw new ArgumentNullException(nameof(pathPicker));
        _storage = storage ?? new FileSystemDiagnosticBundleStorage();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<DiagnosticBundleExportResult?> ExportWithPickerAsync(
        DiagnosticBundleInput input,
        CancellationToken cancellationToken = default)
    {
        var path = await _pathPicker
            .PickExportPathAsync(DefaultExportFileName, cancellationToken)
            .ConfigureAwait(false);
        return path is null
            ? null
            : await ExportToPathAsync(input, path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DiagnosticBundleExportResult> ExportToPathAsync(
        DiagnosticBundleInput input,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Config);
        ArgumentNullException.ThrowIfNull(input.Snapshot);
        ArgumentNullException.ThrowIfNull(input.LogEvents);
        cancellationToken.ThrowIfCancellationRequested();

        ValidateApplicationVersion(input.ApplicationVersion);
        var fullPath = NormalizeDestinationPath(destinationPath);
        var generatedAt = _timeProvider.GetUtcNow();
        var redactor = new DiagnosticBundleRedactor(input.Config, input.Snapshot);
        var logDocument = CreateLogExcerpt(input.LogEvents, redactor, out var includedLogEvents);
        var archive = CreateArchive(input, logDocument);

        await _storage
            .WriteAtomicallyAsync(fullPath, archive, cancellationToken)
            .ConfigureAwait(false);
        return new(fullPath, archive.Length, includedLogEvents, generatedAt);
    }

    private static void ValidateApplicationVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaximumApplicationVersionLength ||
            !SafeVersion.IsMatch(value))
        {
            throw new DiagnosticBundleException("Application version is invalid.");
        }
    }

    private static string NormalizeDestinationPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Diagnostic bundle destination is required.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        if (string.IsNullOrEmpty(Path.GetFileName(fullPath)))
        {
            throw new DiagnosticBundleException(
                "Diagnostic bundle destination must name a file.");
        }

        return fullPath;
    }

    private static byte[] CreateArchive(
        DiagnosticBundleInput input,
        ReadOnlySpan<byte> logDocument)
    {
        var manifest = JsonSerializer.SerializeToUtf8Bytes(
            new DiagnosticBundleManifest(
                "JK Bridge Diagnostic Bundle",
                FormatVersion,
                input.ApplicationVersion),
            JsonOptions);

        var anonymousServices = input.Snapshot.Services
            .Take(MaximumServiceStatuses)
            .Select((service, index) => new AnonymousServiceStatus(
                $"service-{index + 1:D3}",
                Enum.IsDefined(service.Phase) ? service.Phase.ToString() : "Unknown"))
            .ToArray();
        var status = JsonSerializer.SerializeToUtf8Bytes(
            new AnonymousRuntimeStatus(
                Enum.IsDefined(input.Snapshot.Phase)
                    ? input.Snapshot.Phase.ToString()
                    : "Unknown",
                input.Snapshot.DesiredConnected,
                anonymousServices.Length,
                anonymousServices),
            JsonOptions);

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "manifest.json", manifest);
            WriteEntry(archive, "status.json", status);
            WriteEntry(archive, "logs.jsonl", logDocument);
        }

        return output.ToArray();
    }

    private static byte[] CreateLogExcerpt(
        IReadOnlyList<DiagnosticEvent> events,
        DiagnosticBundleRedactor redactor,
        out int includedEvents)
    {
        using var output = new MemoryStream(capacity: MaximumLogBytes);
        var firstIndex = Math.Max(0, events.Count - MaximumLogEvents);
        includedEvents = 0;
        for (var index = firstIndex; index < events.Count; index++)
        {
            var entry = events[index];
            var safeEntry = new DiagnosticBundleLogEntry(
                entry.Timestamp,
                Enum.IsDefined(entry.Severity) ? entry.Severity.ToString() : "Unknown",
                redactor.Redact(entry.Category, 64),
                redactor.Redact(entry.Message, 512),
                redactor.Redact(entry.Detail, 1_024));
            var line = JsonSerializer.SerializeToUtf8Bytes(safeEntry, JsonOptions);
            var requiredBytes = line.Length + 1;
            if (output.Length + requiredBytes > MaximumLogBytes)
            {
                break;
            }

            output.Write(line);
            output.WriteByte((byte)'\n');
            includedEvents++;
        }

        return output.ToArray();
    }

    private static void WriteEntry(
        ZipArchive archive,
        string name,
        ReadOnlySpan<byte> content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private sealed record DiagnosticBundleManifest(
        string Format,
        int FormatVersion,
        string ApplicationVersion);

    private sealed record AnonymousRuntimeStatus(
        string TunnelPhase,
        bool DesiredConnected,
        int ServiceCount,
        IReadOnlyList<AnonymousServiceStatus> Services);

    private sealed record AnonymousServiceStatus(string AnonymousId, string Phase);

    private sealed record DiagnosticBundleLogEntry(
        DateTimeOffset Timestamp,
        string Severity,
        string Category,
        string Message,
        string? Detail);
}

/// <summary>
/// Inputs are kept separate from the archive DTOs so adding a property here can
/// never accidentally add it to a support bundle.
/// </summary>
public sealed record DiagnosticBundleInput(
    string ApplicationVersion,
    AppConfig Config,
    TunnelSnapshot Snapshot,
    IReadOnlyList<DiagnosticEvent> LogEvents);

public sealed record DiagnosticBundleExportResult(
    string Path,
    int ArchiveBytes,
    int IncludedLogEvents,
    DateTimeOffset GeneratedAt);

public sealed class DiagnosticBundleException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Real filesystem implementation. Every existing path component is checked so
/// an export cannot traverse or replace a symbolic link/reparse point. The archive
/// is durably written beside its destination and replaced atomically.
/// </summary>
public sealed class FileSystemDiagnosticBundleStorage : IDiagnosticBundleStorage
{
    public async Task WriteAtomicallyAsync(
        string destinationPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        RejectReparsePoints(fullPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new DiagnosticBundleException(
                "Diagnostic bundle destination has no parent directory.");
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                "The selected diagnostic bundle directory does not exist.");
        }

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            // Check again immediately before the atomic replace to reduce the
            // opportunity for a path substitution between validation and write.
            RejectReparsePoints(fullPath);
            if (File.Exists(fullPath))
            {
                File.Replace(temporaryPath, fullPath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }

            temporaryPath = string.Empty;
        }
        catch (DiagnosticBundleException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new DiagnosticBundleException(
                "The diagnostic bundle could not be written safely.",
                exception);
        }
        finally
        {
            if (temporaryPath.Length > 0)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // Cleanup failure must not hide the original safe-export error.
                }
                catch (UnauthorizedAccessException)
                {
                    // Cleanup failure must not hide the original safe-export error.
                }
            }
        }
    }

    private static void RejectReparsePoints(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            throw new DiagnosticBundleException(
                "Diagnostic bundle destination must be an absolute path.");
        }

        var relativePath = fullPath[root.Length..];
        var current = root;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new DiagnosticBundleException(
                    "Diagnostic bundle destination cannot contain a symbolic link or reparse point.");
            }
        }
    }
}

internal sealed partial class DiagnosticBundleRedactor
{
    private const string Redacted = "[redacted]";
    private readonly string[] _knownSensitiveValues;

    public DiagnosticBundleRedactor(AppConfig config, TunnelSnapshot snapshot)
    {
        _knownSensitiveValues = EnumerateSensitiveValues(config, snapshot)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => value.Length)
            .ToArray();
    }

    public string Redact(string? value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var result = value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        result = ControlCharactersRegex().Replace(result, " ");
        foreach (var sensitiveValue in _knownSensitiveValues)
        {
            result = result.Replace(
                sensitiveValue,
                Redacted,
                StringComparison.OrdinalIgnoreCase);
        }

        result = AuthorizationRegex().Replace(result, Redacted);
        result = KeyMaterialRegex().Replace(result, Redacted);
        result = UriRegex().Replace(result, Redacted);
        result = EmailRegex().Replace(result, Redacted);
        result = IpAddressRegex().Replace(result, Redacted);
        result = WindowsPathRegex().Replace(result, Redacted);
        result = UnixPathRegex().Replace(result, Redacted);
        result = HostNameRegex().Replace(result, Redacted);
        result = NamedIdentityRegex().Replace(result, Redacted);
        result = OpaqueSecretRegex().Replace(result, Redacted);

        return result.Length <= maximumLength
            ? result
            : result[..(maximumLength - 1)] + "…";
    }

    private static IEnumerable<string> EnumerateSensitiveValues(
        AppConfig config,
        TunnelSnapshot snapshot)
    {
        yield return config.ExpectedTailnetDnsSuffix;
        yield return config.Server.DisplayName;
        yield return config.Server.Host;
        yield return config.Server.User;
        yield return config.Server.IdentityFile;
        yield return config.Server.HostKeyAlias;
        yield return config.Server.PinnedHostKey;
        yield return config.Authorization.ClientKeyFingerprint;
        yield return snapshot.PeerDnsName ?? string.Empty;
        yield return snapshot.TargetHost ?? string.Empty;
        yield return snapshot.TailscaleSelfDnsName ?? string.Empty;
        yield return snapshot.TailscaleLoginName ?? string.Empty;
        yield return snapshot.CurrentTailnetDnsSuffix ?? string.Empty;

        foreach (var service in config.Services)
        {
            yield return service.Id;
            yield return service.Name;
            yield return service.Description;
            yield return service.RemoteHost;
            yield return service.OpenUrl;
        }

        foreach (var service in snapshot.Services)
        {
            yield return service.ServiceId;
            yield return service.Name;
            yield return service.Detail ?? string.Empty;
        }
    }

    [GeneratedRegex(
        "(?i)\\b(?:authorization|bearer|jkbridge-(?:enrollment|device)|token|secret|password|credential|recovery[-_ ]?code|enrollment[-_ ]?code)\\b\\s*(?:(?:is|was)\\s+|(?::|=)\\s*)?[^\\s,;]+",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex AuthorizationRegex();

    [GeneratedRegex(
        "(?i)(?:-----BEGIN [^-]+-----.*|ssh-(?:ed25519|rsa)\\s+\\S+|SHA256:[A-Za-z0-9+/=]+)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex KeyMaterialRegex();

    [GeneratedRegex(
        "(?i)https?://[^\\s\\\"'<>]+",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex UriRegex();

    [GeneratedRegex(
        "(?i)\\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,63}\\b",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(
        "\\b(?:25[0-5]|2[0-4][0-9]|1?[0-9]{1,2})(?:\\.(?:25[0-5]|2[0-4][0-9]|1?[0-9]{1,2})){3}\\b",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex IpAddressRegex();

    [GeneratedRegex(
        "(?i)(?:[A-Z]:\\\\|\\\\\\\\)[^\\s\\\"']+",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex(
        "/(?:[^/\\s\\\"']+/)*[^/\\s\\\"']+",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex UnixPathRegex();

    [GeneratedRegex(
        "(?i)\\b(?:[A-Z0-9](?:[A-Z0-9-]{0,61}[A-Z0-9])?\\.)+[A-Z]{2,63}\\b",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex HostNameRegex();

    [GeneratedRegex(
        "(?i)\\b(?:host|hostname|node|nodeid|node-id|login|user|endpoint|identityfile|identity-file|credential-reference)\\b\\s*(?:(?:is|was)\\s+|(?::|=)\\s*)?[^\\s,;]+",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex NamedIdentityRegex();

    [GeneratedRegex(
        "\\b[A-Za-z0-9_+/=-]{32,}\\b",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex OpaqueSecretRegex();

    [GeneratedRegex("[\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F\\x7F]", RegexOptions.NonBacktracking)]
    private static partial Regex ControlCharactersRegex();
}
