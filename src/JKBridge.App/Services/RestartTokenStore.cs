using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace JKBridge.App.Services;

/// <summary>
/// Stores restart tokens outside config.json so the configuration stays shareable.
/// </summary>
public interface IRestartTokenStore
{
    /// <summary>Stores the token and returns the opaque config reference.</summary>
    Task<string> SaveAsync(
        string serviceId,
        string token,
        CancellationToken cancellationToken = default);

    Task<string?> LoadAsync(string reference, CancellationToken cancellationToken = default);

    Task DeleteAsync(string reference, CancellationToken cancellationToken = default);
}

/// <summary>
/// Windows Credential Manager implementation. Generic credentials are protected by
/// Windows for the current user; config persistence keeps only the reference.
/// </summary>
public sealed class WindowsRestartTokenStore : IRestartTokenStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const string TargetPrefix = "JKBridge.Restart/";
    private const int MaximumTokenBytes = 512;

    public Task<string> SaveAsync(
        string serviceId,
        string token,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        var reference = CreateReference(serviceId);
        var target = TargetPrefix + reference;
        var secret = Encoding.UTF8.GetBytes(token);
        if (secret.Length is 0 or > MaximumTokenBytes)
        {
            throw new InvalidOperationException("重启令牌长度必须在 1–512 字节之间。");
        }

        var secretPointer = Marshal.AllocHGlobal(secret.Length);
        try
        {
            Marshal.Copy(secret, 0, secretPointer, secret.Length);
            var native = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = checked((uint)secret.Length),
                CredentialBlob = secretPointer,
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref native, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return Task.FromResult(reference);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            ZeroAndFree(secretPointer, secret.Length);
        }
    }

    public Task<string?> LoadAsync(string reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        var target = ResolveTarget(reference);
        if (!CredRead(target, CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return Task.FromResult<string?>(null);
            }

            throw new Win32Exception(error);
        }

        try
        {
            var native = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (native.CredentialBlob == IntPtr.Zero || native.CredentialBlobSize is 0 or > MaximumTokenBytes)
            {
                return Task.FromResult<string?>(null);
            }

            var blobSize = checked((int)native.CredentialBlobSize);
            var secret = new byte[blobSize];
            try
            {
                Marshal.Copy(native.CredentialBlob, secret, 0, secret.Length);
                return Task.FromResult<string?>(Encoding.UTF8.GetString(secret));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public Task DeleteAsync(string reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        var target = ResolveTarget(reference);
        if (!CredDelete(target, CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(error);
            }
        }

        return Task.CompletedTask;
    }

    private static string CreateReference(string serviceId)
    {
        if (string.IsNullOrWhiteSpace(serviceId) || serviceId.Length > 64)
        {
            throw new InvalidOperationException("服务 id 无效，无法保存重启令牌。");
        }

        return "restart-" + serviceId;
    }

    private static string ResolveTarget(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) ||
            reference.Length > 128 ||
            reference.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new InvalidOperationException("重启令牌引用无效。");
        }

        return TargetPrefix + reference;
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("重启令牌只能在 Windows 凭据管理器中保存。");
        }
    }

    private static void ZeroAndFree(IntPtr pointer, int length)
    {
        if (pointer == IntPtr.Zero)
        {
            return;
        }

        for (var index = 0; index < length; index++)
        {
            Marshal.WriteByte(pointer, index, 0);
        }

        Marshal.FreeHGlobal(pointer);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint flags,
        out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
