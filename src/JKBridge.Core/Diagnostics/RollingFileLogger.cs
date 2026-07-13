using System.Text.Json;

namespace JKBridge.Core.Diagnostics;

public sealed class RollingFileLogger : IAppLogger, IAsyncDisposable
{
    private readonly string _directory;
    private readonly string? _userProfile;
    private readonly int _memoryCapacity;
    private readonly long _maxFileBytes;
    private readonly long _maxTotalBytes;
    private readonly TimeSpan _retention;
    private readonly Queue<DiagnosticEvent> _events = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private bool _disposed;

    public RollingFileLogger(
        string directory,
        string? userProfile = null,
        int memoryCapacity = 500,
        long maxFileBytes = 5 * 1024 * 1024,
        long maxTotalBytes = 20 * 1024 * 1024,
        TimeSpan? retention = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memoryCapacity);

        _directory = directory;
        _userProfile = userProfile;
        _memoryCapacity = memoryCapacity;
        _maxFileBytes = maxFileBytes;
        _maxTotalBytes = maxTotalBytes;
        _retention = retention ?? TimeSpan.FromDays(7);
        Directory.CreateDirectory(_directory);
        CleanupOldFiles();
    }

    public event EventHandler<DiagnosticEvent>? EventWritten;

    public IReadOnlyList<DiagnosticEvent> Snapshot()
    {
        lock (_events)
        {
            return _events.ToArray();
        }
    }

    public async ValueTask WriteAsync(
        DiagnosticSeverity severity,
        string category,
        string message,
        string? detail = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var entry = new DiagnosticEvent(
            DateTimeOffset.Now,
            severity,
            Redactor.Sanitize(category, _userProfile) ?? "App",
            Redactor.Sanitize(message, _userProfile) ?? string.Empty,
            Redactor.Sanitize(detail, _userProfile));

        lock (_events)
        {
            _events.Enqueue(entry);
            while (_events.Count > _memoryCapacity)
            {
                _events.Dequeue();
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = GetWritableLogPath();
            var line = JsonSerializer.Serialize(entry, _jsonOptions) + Environment.NewLine;
            await File.AppendAllTextAsync(path, line, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        EventWritten?.Invoke(this, entry);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private string GetWritableLogPath()
    {
        var baseName = $"jkbridge-{DateTime.Now:yyyyMMdd}";
        for (var index = 0; ; index++)
        {
            var suffix = index == 0 ? string.Empty : $"-{index}";
            var path = Path.Combine(_directory, baseName + suffix + ".jsonl");
            if (!File.Exists(path) || new FileInfo(path).Length < _maxFileBytes)
            {
                return path;
            }
        }
    }

    private void CleanupOldFiles()
    {
        var files = new DirectoryInfo(_directory)
            .EnumerateFiles("jkbridge-*.jsonl")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();

        var cutoff = DateTime.UtcNow - _retention;
        long retainedBytes = 0;
        foreach (var file in files)
        {
            retainedBytes += file.Length;
            if (file.LastWriteTimeUtc < cutoff || retainedBytes > _maxTotalBytes)
            {
                try
                {
                    file.Delete();
                }
                catch (IOException)
                {
                    // A log reader can temporarily hold the file; retry next startup.
                }
                catch (UnauthorizedAccessException)
                {
                    // Logging must never prevent the app from starting.
                }
            }
        }
    }
}
