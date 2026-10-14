using JKBridge.Core.Configuration;
using JKBridge.Core.Models;
using JKBridge.Core.Routing;

namespace JKBridge.App.Services;

/// <summary>
/// Keeps platform file-picker concerns outside the service-profile transfer workflow.
/// A later Avalonia adapter can implement this interface without giving the transfer
/// service access to windowing or storage-provider APIs.
/// </summary>
public interface IServiceProfilePathPicker
{
    Task<string?> PickExportPathAsync(
        string suggestedFileName,
        CancellationToken cancellationToken = default);

    Task<string?> PickImportPathAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Exports and imports the deliberately non-secret JK Bridge Service Profile format.
/// Import is split into preview and apply so the UI can obtain explicit user consent.
/// </summary>
public sealed class ServiceProfileTransferService
{
    public const string DefaultExportFileName = "jkbridge-services.json";

    private readonly ConfigStore _configStore;
    private readonly IServiceProfilePathPicker _pathPicker;

    public ServiceProfileTransferService(
        ConfigStore configStore,
        IServiceProfilePathPicker pathPicker)
    {
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _pathPicker = pathPicker ?? throw new ArgumentNullException(nameof(pathPicker));
    }

    /// <summary>
    /// Asks the platform picker for a destination and atomically exports only the
    /// ServiceProfile allow-list. A null result means that the user cancelled.
    /// </summary>
    public async Task<ServiceProfileExportResult?> ExportWithPickerAsync(
        AppConfig current,
        string displayName = "JK Bridge services",
        CancellationToken cancellationToken = default)
    {
        var path = await _pathPicker
            .PickExportPathAsync(DefaultExportFileName, cancellationToken)
            .ConfigureAwait(false);
        return path is null
            ? null
            : await ExportToPathAsync(current, path, displayName, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Atomically exports a bounded profile to an explicitly supplied path. This
    /// overload is suitable for drag/drop, recent-file, and command-line adapters.
    /// </summary>
    public async Task<ServiceProfileExportResult> ExportToPathAsync(
        AppConfig current,
        string destinationPath,
        string displayName = "JK Bridge services",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        cancellationToken.ThrowIfCancellationRequested();

        // Create maps an explicit service-only allow-list. It cannot serialize a
        // server profile, identity path, or authorization receipt.
        var profile = ServiceProfileSerializer.Create(current, displayName);
        var document = ServiceProfileSerializer.SerializeToUtf8Bytes(profile);
        if (document.Length > ServiceProfileSerializer.MaximumDocumentBytes)
        {
            throw new ServiceProfileTransferException(
                $"Service profile exceeds the {ServiceProfileSerializer.MaximumDocumentBytes}-byte size limit.");
        }

        var fullPath = NormalizeFilePath(destinationPath, "Export destination");
        await WriteAtomicallyAsync(fullPath, document, cancellationToken).ConfigureAwait(false);
        return new(fullPath, profile.Services.Count, document.Length);
    }

    /// <summary>
    /// Asks the platform picker for a source, validates it without mutation, and
    /// returns a reviewable change summary. A null result means user cancellation.
    /// </summary>
    public async Task<ServiceProfileImportPreview?> PreviewImportWithPickerAsync(
        AppConfig current,
        CancellationToken cancellationToken = default)
    {
        var path = await _pathPicker
            .PickImportPathAsync(cancellationToken)
            .ConfigureAwait(false);
        return path is null
            ? null
            : await PreviewImportFromPathAsync(current, path, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads at most the Service Profile format limit, rejects unsupported content,
    /// and prepares a non-mutating import preview.
    /// </summary>
    public async Task<ServiceProfileImportPreview> PreviewImportFromPathAsync(
        AppConfig current,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        ConfigValidator.EnsureValid(current);

        var fullPath = NormalizeFilePath(sourcePath, "Import source");
        var document = await ReadBoundedAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var importDocument = ServiceProfileSerializer.DeserializeImportDocument(document);
        var profile = importDocument.Profile;
        var plan = ServiceProfileSerializer.PrepareImport(current, profile);
        var canonicalDocument = ServiceProfileSerializer.SerializeToUtf8Bytes(profile);
        var summary = ServiceProfileDiffSummary.From(plan.Changes);

        return new ServiceProfileImportPreview(
            fullPath,
            importDocument.Source,
            profile.DisplayName,
            profile.Services.Count,
            canonicalDocument,
            current.Services,
            summary);
    }

    /// <summary>
    /// Applies an accepted preview through ConfigStore's durable atomic replace and
    /// recoverable-backup path. All device/server fields come from current; only the
    /// service list is replaced. If services changed after preview, the operation
    /// fails closed and the user must preview again.
    /// </summary>
    public async Task<ServiceProfileImportResult> ApplyImportAsync(
        AppConfig current,
        ServiceProfileImportPreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(preview);
        ConfigValidator.EnsureValid(current);

        if (!preview.MatchesServiceBaseline(current.Services))
        {
            throw new ServiceProfileTransferException(
                "Services changed after the import preview. Preview the profile again before applying it.");
        }

        var profile = preview.DeserializeProfile();
        var plan = ServiceProfileSerializer.PrepareImport(current, profile);
        var summary = ServiceProfileDiffSummary.From(plan.Changes);
        if (!summary.SemanticallyEquals(preview.Diff))
        {
            throw new ServiceProfileTransferException(
                "The import result no longer matches the approved preview. Preview the profile again.");
        }

        EnsureDeviceStatePreserved(current, plan.CandidateConfig);
        await _configStore.SaveAsync(plan.CandidateConfig, cancellationToken).ConfigureAwait(false);
        var backupPath = File.Exists(_configStore.BackupPath)
            ? _configStore.BackupPath
            : null;
        return new(plan.CandidateConfig, summary, backupPath);
    }

    private static string NormalizeFilePath(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException($"{description} path is required.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        if (string.IsNullOrEmpty(Path.GetFileName(fullPath)))
        {
            throw new ServiceProfileTransferException($"{description} must name a file.");
        }

        return fullPath;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        string path,
        CancellationToken cancellationToken)
    {
        RejectReparsePoint(path, "Import source");

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > ServiceProfileSerializer.MaximumDocumentBytes)
        {
            throw new ServiceProfileSerializationException(
                $"Service profile exceeds the {ServiceProfileSerializer.MaximumDocumentBytes}-byte size limit.");
        }

        // Read one byte beyond the format limit so a file that grows after the
        // length check still cannot cause unbounded allocation or parsing.
        var buffer = new byte[ServiceProfileSerializer.MaximumDocumentBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var count = await stream
                .ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            total += count;
        }

        if (total > ServiceProfileSerializer.MaximumDocumentBytes)
        {
            throw new ServiceProfileSerializationException(
                $"Service profile exceeds the {ServiceProfileSerializer.MaximumDocumentBytes}-byte size limit.");
        }

        return buffer[..total];
    }

    private static async Task WriteAtomicallyAsync(
        string destinationPath,
        ReadOnlyMemory<byte> document,
        CancellationToken cancellationToken)
    {
        RejectReparsePoint(destinationPath, "Export destination", allowMissing: true);

        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new ServiceProfileTransferException(
                "Export destination has no parent directory.");
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                "The selected export directory does not exist.");
        }

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(document, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(destinationPath))
            {
                File.Replace(temporaryPath, destinationPath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, destinationPath);
            }

            temporaryPath = string.Empty;
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
                    // Cleanup failure must not hide the original export error.
                }
                catch (UnauthorizedAccessException)
                {
                    // Cleanup failure must not hide the original export error.
                }
            }
        }
    }

    private static void RejectReparsePoint(
        string path,
        string description,
        bool allowMissing = false)
    {
        if (!File.Exists(path))
        {
            if (allowMissing)
            {
                return;
            }

            throw new FileNotFoundException($"{description} does not exist.", path);
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ServiceProfileTransferException(
                $"{description} cannot be a symbolic link or reparse point.");
        }
    }

    private static void EnsureDeviceStatePreserved(AppConfig current, AppConfig candidate)
    {
        if (current.SchemaVersion != candidate.SchemaVersion ||
            current.SetupCompleted != candidate.SetupCompleted ||
            !string.Equals(
                current.ExpectedTailnetDnsSuffix,
                candidate.ExpectedTailnetDnsSuffix,
                StringComparison.Ordinal) ||
            !Equals(current.Server, candidate.Server) ||
            !Equals(current.Behavior, candidate.Behavior) ||
            !ReferenceEquals(current.Authorization, candidate.Authorization))
        {
            throw new ServiceProfileTransferException(
                "The import attempted to alter device, server, or authorization state and was rejected.");
        }
    }
}

public sealed record ServiceProfileExportResult(
    string Path,
    int ServiceCount,
    int DocumentBytes);

public sealed class ServiceProfileImportPreview
{
    private readonly byte[] _canonicalProfileDocument;
    private readonly ServiceDefinition[] _serviceBaseline;

    internal ServiceProfileImportPreview(
        string sourcePath,
        ServiceProfileImportSource source,
        string displayName,
        int serviceCount,
        ReadOnlySpan<byte> canonicalProfileDocument,
        IEnumerable<ServiceDefinition> serviceBaseline,
        ServiceProfileDiffSummary diff)
    {
        SourcePath = sourcePath;
        Source = source;
        DisplayName = displayName;
        ServiceCount = serviceCount;
        _canonicalProfileDocument = canonicalProfileDocument.ToArray();
        _serviceBaseline = serviceBaseline.Select(service => service with { }).ToArray();
        Diff = diff;
    }

    public string SourcePath { get; }

    public ServiceProfileImportSource Source { get; }

    public string DisplayName { get; }

    public int ServiceCount { get; }

    public ServiceProfileDiffSummary Diff { get; }

    internal bool MatchesServiceBaseline(IEnumerable<ServiceDefinition> services) =>
        _serviceBaseline.SequenceEqual(services);

    internal ServiceProfile DeserializeProfile() =>
        ServiceProfileSerializer.Deserialize(_canonicalProfileDocument);
}

public sealed record ServiceProfileServiceChangeSummary(
    string Id,
    string Name,
    ServiceChangeKind Kind,
    int? PreviousWindowsPort,
    int? WindowsPort,
    int? PreviousLinuxPort,
    int? LinuxPort);

public sealed record ServiceProfileDiffSummary(
    IReadOnlyList<ServiceProfileServiceChangeSummary> Services,
    int AddedServices,
    int ModifiedServices,
    int RemovedServices,
    int UnchangedServices,
    IReadOnlyList<int> AddedLinuxPorts,
    IReadOnlyList<int> RemovedLinuxPorts,
    RemoteAuthorizationImpact AuthorizationImpact,
    bool RequiresRemoteReauthorization,
    bool BlocksUntilReauthorized)
{
    internal bool SemanticallyEquals(ServiceProfileDiffSummary other) =>
        AddedServices == other.AddedServices &&
        ModifiedServices == other.ModifiedServices &&
        RemovedServices == other.RemovedServices &&
        UnchangedServices == other.UnchangedServices &&
        AuthorizationImpact == other.AuthorizationImpact &&
        RequiresRemoteReauthorization == other.RequiresRemoteReauthorization &&
        BlocksUntilReauthorized == other.BlocksUntilReauthorized &&
        Services.SequenceEqual(other.Services) &&
        AddedLinuxPorts.SequenceEqual(other.AddedLinuxPorts) &&
        RemovedLinuxPorts.SequenceEqual(other.RemovedLinuxPorts);

    internal static ServiceProfileDiffSummary From(ServiceChangeAssessment assessment)
    {
        var services = Array.AsReadOnly(assessment.Changes
            .Select(change => new ServiceProfileServiceChangeSummary(
                change.Id,
                change.After?.Name ?? change.Before?.Name ?? change.Id,
                change.Kind,
                change.Before?.LocalPort,
                change.After?.LocalPort,
                change.Before?.RemotePort,
                change.After?.RemotePort))
            .ToArray());
        return new(
            services,
            assessment.Changes.Count(change => change.Kind == ServiceChangeKind.Added),
            assessment.Changes.Count(change => change.Kind == ServiceChangeKind.Modified),
            assessment.Changes.Count(change => change.Kind == ServiceChangeKind.Removed),
            assessment.Changes.Count(change => change.Kind == ServiceChangeKind.Unchanged),
            Array.AsReadOnly(assessment.AddedRemotePorts.ToArray()),
            Array.AsReadOnly(assessment.RemovedRemotePorts.ToArray()),
            assessment.AuthorizationImpact,
            assessment.RequiresRemoteReauthorization,
            assessment.BlocksUntilReauthorized);
    }
}

public sealed record ServiceProfileImportResult(
    AppConfig Config,
    ServiceProfileDiffSummary Diff,
    string? BackupPath);

public sealed class ServiceProfileTransferException : Exception
{
    public ServiceProfileTransferException(string message)
        : base(message)
    {
    }

    public ServiceProfileTransferException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
