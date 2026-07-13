using System.Diagnostics;
using System.Text;
using JKBridge.App.Platform;
using JKBridge.Core.Tunneling;

namespace JKBridge.App.Services;

public sealed record TunnelProcessExit(int ExitCode, string StandardError);

public interface ITunnelProcess : IAsyncDisposable
{
    event EventHandler<string>? StandardErrorReceived;

    int? ProcessId { get; }

    bool HasExited { get; }

    Task<TunnelProcessExit> WaitForExitAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface ITunnelProcessFactory
{
    string? FindSshExecutable();

    Task<ITunnelProcess> StartAsync(
        SshLaunchSpec launchSpec,
        CancellationToken cancellationToken = default);
}

public sealed class TunnelProcessFactory : ITunnelProcessFactory
{
    private readonly Func<string?> _locateExecutable;

    public TunnelProcessFactory(Func<string?>? locateExecutable = null)
    {
        _locateExecutable = locateExecutable ?? ExecutableLocator.FindOpenSsh;
    }

    public string? FindSshExecutable() => _locateExecutable();

    public Task<ITunnelProcess> StartAsync(
        SshLaunchSpec launchSpec,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launchSpec);
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo
        {
            FileName = launchSpec.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
        };
        foreach (var argument in launchSpec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        WindowsJobObject? job = null;
        Process? process = null;
        try
        {
            job = new WindowsJobObject();
            process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException("OpenSSH could not be started.");
            }

            if (OperatingSystem.IsWindows())
            {
                job.AssignProcess(process);
            }

            return Task.FromResult<ITunnelProcess>(new TunnelProcess(process, job));
        }
        catch
        {
            if (process is not null)
            {
                TryTerminate(process);
                process.Dispose();
            }

            job?.Dispose();
            throw;
        }
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            // Best-effort startup rollback.
        }
    }
}

internal sealed class TunnelProcess : ITunnelProcess
{
    private const int MaximumErrorCharacters = 64 * 1024;

    private readonly Process _process;
    private readonly WindowsJobObject _job;
    private readonly Task<TunnelProcessExit> _exitTask;
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private int _disposed;

    public TunnelProcess(Process process, WindowsJobObject job)
    {
        _process = process;
        _job = job;
        _exitTask = MonitorExitAsync();
    }

    public int? ProcessId
    {
        get
        {
            try
            {
                return _process.Id;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    public event EventHandler<string>? StandardErrorReceived;

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public Task<TunnelProcessExit> WaitForExitAsync(CancellationToken cancellationToken = default) =>
        _exitTask.WaitAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _stopGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception) when (exception is
                InvalidOperationException or
                System.ComponentModel.Win32Exception)
            {
                // Job Object disposal is the final cleanup guard on Windows.
            }

            try
            {
                await _exitTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Job Object disposal below will terminate any process that survived Kill.
            }
        }
        finally
        {
            _stopGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _job.Dispose();
            _process.Dispose();
            _stopGate.Dispose();
        }
    }

    private async Task<TunnelProcessExit> MonitorExitAsync()
    {
        var errorTask = ReadBoundedErrorAsync(_process.StandardError);
        await _process.WaitForExitAsync().ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        return new TunnelProcessExit(_process.ExitCode, error);
    }

    private async Task<string> ReadBoundedErrorAsync(StreamReader reader)
    {
        var builder = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            var remaining = MaximumErrorCharacters - builder.Length;
            if (remaining > 0)
            {
                var count = Math.Min(line.Length, remaining);
                builder.Append(line, 0, count).AppendLine();
            }

            var handlers = StandardErrorReceived;
            if (handlers is not null)
            {
                foreach (EventHandler<string> handler in handlers.GetInvocationList())
                {
                    try
                    {
                        handler(this, line);
                    }
                    catch
                    {
                        // A diagnostics subscriber cannot terminate stderr draining.
                    }
                }
            }
        }

        if (builder.Length == MaximumErrorCharacters)
        {
            builder.Append("\n[stderr truncated]");
        }

        return builder.ToString();
    }
}
