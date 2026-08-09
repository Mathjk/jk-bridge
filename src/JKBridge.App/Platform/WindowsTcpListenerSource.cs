using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using JKBridge.App.Services;
using JKBridge.Core.Routing;

namespace JKBridge.App.Platform;

/// <summary>
/// Reads the Windows owner-PID TCP listener tables without elevation. If the
/// native owner table is unavailable, endpoint-only .NET enumeration remains
/// available so discovery can continue without process attribution.
/// </summary>
public sealed class WindowsTcpListenerSource : ILocalTcpListenerSource
{
    private const int AddressFamilyInet = 2;
    private const int AddressFamilyInet6 = 23;
    private const uint ErrorInsufficientBuffer = 122;
    private const int TcpTableOwnerPidListener = 3;

    private readonly Func<int, string> _processNameResolver;

    public WindowsTcpListenerSource(Func<int, string>? processNameResolver = null)
    {
        _processNameResolver = processNameResolver ?? ResolveProcessName;
    }

    public Task<IReadOnlyList<LocalTcpListener>> GetListenersAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<LocalTcpListener> listeners;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                listeners = ReadOwnerTables(cancellationToken);
                return Task.FromResult(listeners);
            }
            catch (Exception exception) when (exception is Win32Exception or
                                              DllNotFoundException or
                                              EntryPointNotFoundException or
                                              BadImageFormatException)
            {
                // Continue with the endpoint-only managed fallback.
            }
        }

        listeners = ReadManagedListeners(cancellationToken);
        return Task.FromResult(listeners);
    }

    private IReadOnlyList<LocalTcpListener> ReadOwnerTables(CancellationToken cancellationToken)
    {
        var listeners = new List<LocalTcpListener>();
        listeners.AddRange(ReadIpv4Table(cancellationToken));
        listeners.AddRange(ReadIpv6Table(cancellationToken));
        return listeners
            .Where(listener => WindowsLocalServiceUrl.IsLoopbackHost(listener.Host))
            .GroupBy(listener => (listener.Host, listener.Port, listener.ProcessId))
            .Select(group => group.First())
            .OrderBy(listener => listener.Port)
            .ThenBy(listener => listener.Host, StringComparer.Ordinal)
            .ToArray();
    }

    private IReadOnlyList<LocalTcpListener> ReadIpv4Table(CancellationToken cancellationToken)
    {
        var rows = ReadTable<MibTcpRowOwnerPid>(AddressFamilyInet);
        var results = new List<LocalTcpListener>(rows.Count);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var address = new IPAddress(row.LocalAddress);
            if (!IPAddress.IsLoopback(address))
            {
                continue;
            }

            var processId = checked((int)row.OwningPid);
            results.Add(new(
                address.ToString(),
                DecodePort(row.LocalPort),
                processId,
                _processNameResolver(processId)));
        }

        return results;
    }

    private IReadOnlyList<LocalTcpListener> ReadIpv6Table(CancellationToken cancellationToken)
    {
        var rows = ReadTable<MibTcp6RowOwnerPid>(AddressFamilyInet6);
        var results = new List<LocalTcpListener>(rows.Count);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var address = new IPAddress(row.LocalAddress, row.LocalScopeId);
            if (!IPAddress.IsLoopback(address))
            {
                continue;
            }

            var processId = checked((int)row.OwningPid);
            results.Add(new(
                WindowsLocalServiceUrl.Ipv6Loopback,
                DecodePort(row.LocalPort),
                processId,
                _processNameResolver(processId)));
        }

        return results;
    }

    private static IReadOnlyList<T> ReadTable<T>(int addressFamily)
        where T : struct
    {
        var size = 0;
        var result = GetExtendedTcpTable(
            IntPtr.Zero,
            ref size,
            sort: true,
            addressFamily,
            TcpTableOwnerPidListener,
            0);
        if (result != ErrorInsufficientBuffer && result != 0)
        {
            throw new Win32Exception((int)result);
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            result = GetExtendedTcpTable(
                buffer,
                ref size,
                sort: true,
                addressFamily,
                TcpTableOwnerPidListener,
                0);
            if (result != 0)
            {
                throw new Win32Exception((int)result);
            }

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<T>();
            var current = IntPtr.Add(buffer, sizeof(uint));
            var rows = new List<T>(count);
            for (var index = 0; index < count; index++)
            {
                rows.Add(Marshal.PtrToStructure<T>(current));
                current = IntPtr.Add(current, rowSize);
            }

            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IReadOnlyList<LocalTcpListener> ReadManagedListeners(
        CancellationToken cancellationToken)
    {
        var results = new List<LocalTcpListener>();
        foreach (var endpoint in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IPAddress.IsLoopback(endpoint.Address))
            {
                continue;
            }

            results.Add(new(
                endpoint.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                    ? WindowsLocalServiceUrl.Ipv6Loopback
                    : endpoint.Address.ToString(),
                endpoint.Port));
        }

        return results
            .GroupBy(listener => (listener.Host, listener.Port))
            .Select(group => group.First())
            .OrderBy(listener => listener.Port)
            .ToArray();
    }

    private static int DecodePort(uint encodedPort)
    {
        var bytes = BitConverter.GetBytes(encodedPort);
        return (bytes[0] << 8) | bytes[1];
    }

    private static string ResolveProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          NotSupportedException or
                                          SystemException)
        {
            return string.Empty;
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool sort,
        int addressFamily,
        int tableClass,
        uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddress;

        public uint LocalScopeId;
        public uint LocalPort;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddress;

        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }
}
