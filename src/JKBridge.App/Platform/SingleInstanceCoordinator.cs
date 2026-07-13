using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace JKBridge.App.Platform;

/// <summary>
/// Coordinates a single application instance with an OS lock and a tiny named-pipe protocol.
/// Secondary instances send a single <c>SHOW</c> command to activate the primary window.
/// </summary>
public sealed class SingleInstanceCoordinator : IDisposable, IAsyncDisposable
{
    private static readonly byte[] ShowCommand = "SHOW\n"u8.ToArray();

    private readonly object _gate = new();
    private readonly bool _isPrimary;
    private readonly Thread? _mutexThread;
    private readonly ManualResetEventSlim? _mutexRelease;
    private readonly FileStream? _unixLock;
    private readonly string _pipeName;
    private CancellationTokenSource? _listenerCancellation;
    private Task? _listenerTask;
    private NamedPipeServerStream? _activeServer;
    private bool _disposed;

    private SingleInstanceCoordinator(
        bool isPrimary,
        string pipeName,
        Thread? mutexThread = null,
        ManualResetEventSlim? mutexRelease = null,
        FileStream? unixLock = null)
    {
        _isPrimary = isPrimary;
        _mutexThread = mutexThread;
        _mutexRelease = mutexRelease;
        _unixLock = unixLock;
        _pipeName = pipeName;
    }

    public bool IsPrimary => _isPrimary;

    public event EventHandler? ShowRequested;

    public static SingleInstanceCoordinator Acquire(string applicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);

        var identity = OperatingSystem.IsWindows()
            ? applicationId
            : $"{applicationId}\0{Environment.UserName}\0{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}";
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..24];
        var pipeName = $"jkbridge-{suffix}";

        if (!OperatingSystem.IsWindows())
        {
            return AcquireUnixFileLock(suffix, pipeName);
        }

        var mutexName = $@"Local\JKBridge.{suffix}";

        using var acquisitionReady = new ManualResetEventSlim();
        var mutexRelease = new ManualResetEventSlim();
        var ownsMutex = false;
        Exception? acquisitionError = null;

        // Mutex ownership is thread-affine. A small dedicated thread owns it so that
        // Dispose can reliably release the instance lock from any caller thread.
        var mutexThread = new Thread(() =>
        {
            Mutex? mutex = null;
            try
            {
                mutex = new Mutex(initiallyOwned: false, mutexName);
                try
                {
                    ownsMutex = mutex.WaitOne(TimeSpan.Zero);
                }
                catch (AbandonedMutexException)
                {
                    // The previous owner crashed. WaitOne grants ownership in this case.
                    ownsMutex = true;
                }
            }
            catch (Exception ex)
            {
                acquisitionError = ex;
            }
            finally
            {
                acquisitionReady.Set();
            }

            if (acquisitionError is not null)
            {
                mutex?.Dispose();
                return;
            }

            if (mutex is null)
            {
                return;
            }

            if (!ownsMutex)
            {
                mutex.Dispose();
                return;
            }

            try
            {
                mutexRelease.Wait();
            }
            finally
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Process teardown can invalidate ownership; closing the handle is sufficient.
                }

                mutex.Dispose();
            }
        })
        {
            IsBackground = true,
            Name = "JK Bridge instance mutex",
        };
        mutexThread.Start();
        acquisitionReady.Wait();

        if (acquisitionError is not null)
        {
            mutexRelease.Set();
            mutexThread.Join();
            mutexRelease.Dispose();
            throw new InvalidOperationException("Could not acquire the single-instance mutex.", acquisitionError);
        }

        return new SingleInstanceCoordinator(
            ownsMutex,
            pipeName,
            mutexThread: mutexThread,
            mutexRelease: mutexRelease);
    }

    private static SingleInstanceCoordinator AcquireUnixFileLock(string suffix, string pipeName)
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share");
        }

        var directory = Path.Combine(localData, "JKBridge");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"instance-{suffix}.lock");
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            return new SingleInstanceCoordinator(true, pipeName, unixLock: stream);
        }
        catch (IOException)
        {
            stream?.Dispose();
            return new SingleInstanceCoordinator(false, pipeName);
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Starts the primary instance's SHOW listener. Invoke this once the UI can be activated.
    /// </summary>
    public void StartListening(Action? showAction = null, SynchronizationContext? synchronizationContext = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsPrimary)
            {
                throw new InvalidOperationException("Only the primary instance can listen for activation requests.");
            }

            if (_listenerTask is not null)
            {
                throw new InvalidOperationException("The activation listener is already running.");
            }

            synchronizationContext ??= SynchronizationContext.Current;
            _listenerCancellation = new CancellationTokenSource();
            _listenerTask = ListenAsync(showAction, synchronizationContext, _listenerCancellation.Token);
        }
    }

    /// <summary>
    /// Sends SHOW to the primary instance. Returns false on the primary instance, timeout, or pipe failure.
    /// </summary>
    public async Task<bool> NotifyPrimaryAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsPrimary)
        {
            return false;
        }

        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(3);
        if (effectiveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(effectiveTimeout);

        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            await client.ConnectAsync(timeoutCancellation.Token).ConfigureAwait(false);
            await client.WriteAsync(ShowCommand, timeoutCancellation.Token).ConfigureAwait(false);
            await client.FlushAsync(timeoutCancellation.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task ListenAsync(
        Action? showAction,
        SynchronizationContext? synchronizationContext,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var options = PipeOptions.Asynchronous;
                if (OperatingSystem.IsWindows())
                {
                    options |= PipeOptions.CurrentUserOnly;
                }

                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    options);

                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _activeServer = server;
                }

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var command = await ReadCommandAsync(server, cancellationToken).ConfigureAwait(false);
                if (string.Equals(command, "SHOW", StringComparison.Ordinal))
                {
                    DispatchShow(showAction, synchronizationContext);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // A connected client that sends no command timed out. Recreate the server and keep listening.
                await DelayAfterListenerFailure(cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                await DelayAfterListenerFailure(cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                await DelayAfterListenerFailure(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    _activeServer = null;
                }
            }
        }
    }

    private static async Task<string?> ReadCommandAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readCancellation.CancelAfter(TimeSpan.FromSeconds(2));

        var buffer = new byte[1];
        var command = new byte[8];
        var length = 0;

        while (length < command.Length)
        {
            var read = await stream.ReadAsync(buffer, readCancellation.Token).ConfigureAwait(false);
            if (read == 0 || buffer[0] == (byte)'\n')
            {
                break;
            }

            if (buffer[0] != (byte)'\r')
            {
                command[length++] = buffer[0];
            }
        }

        return Encoding.ASCII.GetString(command, 0, length);
    }

    private void DispatchShow(Action? showAction, SynchronizationContext? synchronizationContext)
    {
        void Invoke()
        {
            try
            {
                showAction?.Invoke();
                ShowRequested?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                // Activation is best-effort; a UI callback must not terminate the pipe listener.
            }
        }

        if (synchronizationContext is null)
        {
            Invoke();
        }
        else
        {
            synchronizationContext.Post(_ => Invoke(), null);
        }
    }

    private static async Task DelayAfterListenerFailure(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    public void Dispose()
    {
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        var listenerTask = DisposeCore();
        if (listenerTask is not null)
        {
            try
            {
                await listenerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
        }

        GC.SuppressFinalize(this);
    }

    private Task? DisposeCore()
    {
        Task? listenerTask;
        lock (_gate)
        {
            if (_disposed)
            {
                return _listenerTask;
            }

            _disposed = true;
            _listenerCancellation?.Cancel();
            _activeServer?.Dispose();
            listenerTask = _listenerTask;
        }

        _mutexRelease?.Set();
        if (_mutexThread is not null &&
            _mutexThread.Join(TimeSpan.FromSeconds(2)))
        {
            _mutexRelease?.Dispose();
        }
        _unixLock?.Dispose();
        _listenerCancellation?.Dispose();
        return listenerTask;
    }
}
