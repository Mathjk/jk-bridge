using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace JKBridge.App.Platform;

/// <summary>
/// Owns a Windows Job Object configured to terminate every assigned child process when disposed.
/// On non-Windows systems construction and disposal are harmless; assignment reports unsupported.
/// </summary>
public sealed class WindowsJobObject : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    private readonly object _gate = new();
    private SafeJobHandle? _handle;
    private bool _disposed;

    public WindowsJobObject(string? name = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _handle = NativeMethods.CreateJobObject(IntPtr.Zero, name);
        if (_handle.IsInvalid)
        {
            throw NewWin32Exception("Could not create the Windows Job Object");
        }

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };

        if (!NativeMethods.SetInformationJobObject(
                _handle,
                JobObjectExtendedLimitInformationClass,
                ref limits,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            var exception = NewWin32Exception("Could not enable KILL_ON_JOB_CLOSE");
            _handle.Dispose();
            _handle = null;
            throw exception;
        }
    }

    public bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// Assigns a started process to this job. Throws when unsupported or when Windows rejects the assignment.
    /// </summary>
    public void AssignProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (!TryAssignProcess(process, out var error))
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(error);
            }

            throw new InvalidOperationException(error);
        }
    }

    /// <summary>
    /// Attempts to assign a process. Returns false rather than throwing on unsupported platforms or Win32 errors.
    /// </summary>
    public bool TryAssignProcess(Process process, out string? error)
    {
        ArgumentNullException.ThrowIfNull(process);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!OperatingSystem.IsWindows())
            {
                error = "Windows Job Objects are not available on this operating system.";
                return false;
            }

            if (_handle is null || _handle.IsInvalid)
            {
                error = "The Windows Job Object is not available.";
                return false;
            }

            try
            {
                if (process.HasExited)
                {
                    error = "The process exited before it could be assigned to the Job Object.";
                    return false;
                }

                if (!NativeMethods.AssignProcessToJobObject(_handle, process.Handle))
                {
                    error = NewWin32Exception("Windows rejected the Job Object assignment").Message;
                    return false;
                }

                error = null;
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _handle?.Dispose();
            _handle = null;
        }

        GC.SuppressFinalize(this);
    }

    private static Win32Exception NewWin32Exception(string message)
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{message} (Win32 error {error}).");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeJobHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeJobHandle CreateJobObject(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetInformationJobObject(
            SafeJobHandle job,
            int jobObjectInformationClass,
            ref JobObjectExtendedLimitInformation jobObjectInformation,
            uint jobObjectInformationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
