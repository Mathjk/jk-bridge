using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using JKBridge.Core.Agent;

namespace JKBridge.App.Services;

/// <summary>
/// Stores the durable Agent device credential outside config.json.
/// </summary>
public interface IAgentCredentialStore
{
    Task<string> SaveAsync(
        string endpoint,
        string deviceId,
        AgentCredential credential,
        CancellationToken cancellationToken = default);

    Task<AgentCredential?> LoadAsync(
        string credentialReference,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string credentialReference,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Windows Credential Manager implementation. Generic credentials are protected by Windows for
/// the current user. Config persistence receives only the opaque reference returned by SaveAsync.
/// </summary>
public sealed class WindowsAgentCredentialStore : IAgentCredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const string TargetPrefix = "JKBridge.Agent/";

    public Task<string> SaveAsync(
        string endpoint,
        string deviceId,
        AgentCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();

        var reference = CreateReference(endpoint, deviceId);
        var target = TargetPrefix + reference;
        var secret = Encoding.ASCII.GetBytes(credential.Export());
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
                throw new AgentEnrollmentException(
                    "Windows 无法安全保存 Agent 设备凭据。",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            return Task.FromResult(reference);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            ZeroAndFree(secretPointer, secret.Length);
        }
    }

    public Task<AgentCredential?> LoadAsync(
        string credentialReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        var target = ResolveTarget(credentialReference);
        if (!CredRead(target, CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return Task.FromResult<AgentCredential?>(null);
            }

            throw new AgentEnrollmentException(
                "Windows 无法读取 Agent 设备凭据。",
                new Win32Exception(error));
        }

        try
        {
            var native = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (native.CredentialBlob == IntPtr.Zero || native.CredentialBlobSize is 0 or > 512)
            {
                throw new AgentEnrollmentException("Windows 凭据库中的 Agent 凭据格式无效。");
            }

            var blobSize = checked((int)native.CredentialBlobSize);
            var secret = new byte[blobSize];
            try
            {
                Marshal.Copy(native.CredentialBlob, secret, 0, secret.Length);
                var value = Encoding.ASCII.GetString(secret);
                if (!AgentCredential.TryParse(value, out var credential))
                {
                    throw new AgentEnrollmentException("Windows 凭据库中的 Agent 凭据格式无效。");
                }

                return Task.FromResult<AgentCredential?>(credential);
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

    public Task DeleteAsync(
        string credentialReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        var target = ResolveTarget(credentialReference);
        if (!CredDelete(target, CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new AgentEnrollmentException(
                    "Windows 无法删除 Agent 设备凭据。",
                    new Win32Exception(error));
            }
        }

        return Task.CompletedTask;
    }

    private static string CreateReference(string endpoint, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(deviceId))
        {
            throw new AgentEnrollmentException("Agent 返回的设备身份不完整，凭据没有保存。");
        }

        var material = Encoding.UTF8.GetBytes(endpoint + "\n" + deviceId);
        try
        {
            return "agent-" + Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private static string ResolveTarget(string credentialReference)
    {
        if (string.IsNullOrWhiteSpace(credentialReference) ||
            credentialReference.Length > 128 ||
            credentialReference.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new AgentEnrollmentException("Agent 凭据引用无效。");
        }

        return TargetPrefix + credentialReference;
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new AgentEnrollmentException("Agent 设备凭据只能在 Windows 凭据管理器中保存。");
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
