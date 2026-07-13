using System.Net;
using System.Net.Sockets;
using System.Text;

namespace JKBridge.App.Services;

public interface IKnownHostsManager
{
    Task EnsureAsync(
        string hostKeyAlias,
        string pinnedHostKey,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns the app-specific known_hosts file. The configured alias is the only host entry written.
/// </summary>
public sealed class KnownHostsManager : IKnownHostsManager
{
    public KnownHostsManager(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    public async Task EnsureAsync(
        string hostKeyAlias,
        string pinnedHostKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostKeyAlias);
        ArgumentException.ThrowIfNullOrWhiteSpace(pinnedHostKey);

        var expected = $"{hostKeyAlias.Trim()} {pinnedHostKey.Trim()}{Environment.NewLine}";
        if (File.Exists(FilePath))
        {
            var current = await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false);
            if (string.Equals(NormalizeNewlines(current), NormalizeNewlines(expected), StringComparison.Ordinal))
            {
                return;
            }
        }

        var directory = Path.GetDirectoryName(FilePath)
            ?? throw new InvalidOperationException("The known_hosts path has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(expected);
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a later startup can remove an abandoned temp file.
            }
            catch (UnauthorizedAccessException)
            {
                // The final file is already authoritative.
            }
        }
    }

    private static string NormalizeNewlines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal);
}

public interface ILocalPortChecker
{
    Task<IReadOnlyList<int>> FindConflictsAsync(
        IEnumerable<int> localPorts,
        CancellationToken cancellationToken = default);
}

public sealed class LocalPortChecker : ILocalPortChecker
{
    public Task<IReadOnlyList<int>> FindConflictsAsync(
        IEnumerable<int> localPorts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localPorts);
        var conflicts = new List<int>();
        foreach (var port in localPorts.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                {
                    ExclusiveAddressUse = true,
                };
                socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
                socket.Listen(1);
            }
            catch (SocketException)
            {
                conflicts.Add(port);
            }
        }

        return Task.FromResult<IReadOnlyList<int>>(conflicts);
    }
}

public interface IRuntimeDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemRuntimeDelay : IRuntimeDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

public sealed record BridgeRuntimeOptions
{
    public required string KnownHostsPath { get; init; }

    public TimeSpan ReadinessDelay { get; init; } = TimeSpan.FromMilliseconds(750);

    public TimeSpan HealthCheckInterval { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan StableConnectionReset { get; init; } = TimeSpan.FromMinutes(2);

    public Func<int, TimeSpan> RetryDelay { get; init; } =
        attempt => JKBridge.Core.Tunneling.TunnelRetrySchedule.GetDelay(attempt, Random.Shared.NextDouble());

    public Func<string, string> ExpandPath { get; init; } = RuntimePathExpander.Expand;

    public static BridgeRuntimeOptions CreateDefault(string? appDataDirectory = null)
    {
        var root = appDataDirectory;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root))
            {
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jkbridge");
            }

            root = Path.Combine(root, "JKBridge");
        }

        return new BridgeRuntimeOptions
        {
            KnownHostsPath = Path.Combine(root, "ssh", "known_hosts"),
        };
    }
}

public static class RuntimePathExpander
{
    public static string Expand(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var expanded = Environment.ExpandEnvironmentVariables(value);
        expanded = ReplaceWindowsVariable(expanded, "USERPROFILE", Environment.SpecialFolder.UserProfile);
        expanded = ReplaceWindowsVariable(expanded, "LOCALAPPDATA", Environment.SpecialFolder.LocalApplicationData);
        expanded = ReplaceWindowsVariable(expanded, "APPDATA", Environment.SpecialFolder.ApplicationData);

        if (expanded == "~" || expanded.StartsWith($"~{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            expanded = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                expanded.Length == 1 ? string.Empty : expanded[2..]);
        }

        return expanded;
    }

    private static string ReplaceWindowsVariable(
        string value,
        string name,
        Environment.SpecialFolder folder)
    {
        var token = $"%{name}%";
        if (!value.Contains(token, StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var replacement = Environment.GetFolderPath(folder);
        return value.Replace(token, replacement, StringComparison.OrdinalIgnoreCase);
    }
}
