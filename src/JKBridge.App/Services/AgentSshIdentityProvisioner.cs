using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using JKBridge.App.Platform;
using JKBridge.Core.Pairing;
using JKBridge.Core.Processes;
using JKBridge.Core.Security;

namespace JKBridge.App.Services;

public sealed record AgentSshIdentity(
    string PublicKey,
    string Fingerprint,
    string PortableIdentityFile);

public interface IAgentSshIdentityProvisioner
{
    Task<AgentSshIdentity> PrepareAsync(
        string identityFile,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates a per-Windows-device Ed25519 identity with the same exact ACL boundary as one-shot
/// pairing. It never imports a personal key and never returns private-key material.
/// </summary>
public sealed class AgentSshIdentityProvisioner : IAgentSshIdentityProvisioner
{
    private readonly ICommandRunner _commandRunner;

    public AgentSshIdentityProvisioner(ICommandRunner? commandRunner = null)
    {
        _commandRunner = commandRunner ?? new CommandRunner();
    }

    public async Task<AgentSshIdentity> PrepareAsync(
        string identityFile,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new AgentEnrollmentException("Agent 设备登记只能在 Windows 10/11 上运行。");
        }

        var keygen = ExecutableLocator.FindSshKeygen()
                     ?? throw new AgentEnrollmentException("找不到 Windows OpenSSH 的 ssh-keygen.exe。");
        var identityPath = ResolveIdentityPath(identityFile);
        var publicKey = await PrepareIdentityAsync(keygen, identityPath, cancellationToken)
            .ConfigureAwait(false);
        return new AgentSshIdentity(
            publicKey,
            Ed25519PublicKey.ComputeFingerprint(publicKey),
            ToPortableIdentityPath(identityPath));
    }

    [SupportedOSPlatform("windows")]
    private async Task<string> PrepareIdentityAsync(
        string keygen,
        string identityPath,
        CancellationToken cancellationToken)
    {
        AssertPathHasNoReparsePoints(identityPath, includeParents: true);
        var directory = Path.GetDirectoryName(identityPath)
                        ?? throw new AgentEnrollmentException("专用密钥路径没有父目录。");
        Directory.CreateDirectory(directory);
        AssertPathHasNoReparsePoints(identityPath, includeParents: true);

        if (!File.Exists(identityPath))
        {
            if (File.Exists(identityPath + ".pub"))
            {
                throw new AgentEnrollmentException("密钥目录中只有同名公钥而没有私钥，请换一个专用密钥路径。");
            }

            var generated = await _commandRunner.RunAsync(
                    new CommandRequest(
                        keygen,
                        [
                            "-q", "-t", "ed25519", "-a", "100", "-N", string.Empty,
                            "-C", "JK Bridge managed Agent key", "-f", identityPath,
                        ],
                        TimeSpan.FromSeconds(30),
                        MaxOutputCharacters: 32 * 1024),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!generated.IsSuccess || !File.Exists(identityPath))
            {
                throw new AgentEnrollmentException("OpenSSH 未能生成 Agent 专用密钥。" + SafeCommandDetail(generated));
            }
        }

        SetExactPrivateKeyAcl(identityPath);
        var derived = await _commandRunner.RunAsync(
                new CommandRequest(
                    keygen,
                    ["-y", "-P", string.Empty, "-f", identityPath],
                    TimeSpan.FromSeconds(15),
                    MaxOutputCharacters: 32 * 1024),
                cancellationToken)
            .ConfigureAwait(false);
        if (!derived.IsSuccess || !Ed25519PublicKey.TryNormalize(derived.StandardOutput, out var publicKey))
        {
            throw new AgentEnrollmentException("Agent 专用私钥无效或需要口令；JK Bridge 不会保存密钥口令。");
        }

        var publicPath = identityPath + ".pub";
        AssertPathHasNoReparsePoints(publicPath, includeParents: true);
        await File.WriteAllTextAsync(
                publicPath,
                publicKey + " JK Bridge managed Agent key" + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken)
            .ConfigureAwait(false);
        return publicKey;
    }

    private static string ResolveIdentityPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value.Any(char.IsControl))
        {
            throw new AgentEnrollmentException("专用密钥路径无效。");
        }

        return Path.GetFullPath(RuntimePathExpander.Expand(value.Trim()));
    }

    private static string ToPortableIdentityPath(string identityPath)
    {
        var profile = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (identityPath.StartsWith(profile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return "%USERPROFILE%" + identityPath[profile.Length..];
        }

        return identityPath;
    }

    private static void AssertPathHasNoReparsePoints(string path, bool includeParents)
    {
        var candidate = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(candidate))
        {
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                var attributes = File.GetAttributes(candidate);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new AgentEnrollmentException($"拒绝使用符号链接或目录联接路径：{candidate}");
                }
            }

            if (!includeParents)
            {
                break;
            }

            var parent = Path.GetDirectoryName(candidate);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            candidate = parent;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void SetExactPrivateKeyAcl(string path)
    {
        var currentSid = WindowsIdentity.GetCurrent().User
                         ?? throw new AgentEnrollmentException("无法读取当前 Windows 用户 SID。");
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var file = new FileInfo(path);
        var ownerSecurity = file.GetAccessControl(AccessControlSections.Owner);
        if (ownerSecurity.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
            !owner.Equals(currentSid))
        {
            throw new AgentEnrollmentException("专用私钥不属于当前 Windows 用户；请换用新的密钥路径。");
        }

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentSid,
            FileSystemRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            systemSid,
            FileSystemRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        file.SetAccessControl(security);

        var verified = file.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (verified.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier verifiedOwner)
        {
            throw new AgentEnrollmentException("Windows 无法验证专用私钥所有者。");
        }
        var rules = verified.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            currentSid.Value ?? throw new AgentEnrollmentException("当前 Windows 用户 SID 无效。"),
            systemSid.Value ?? throw new AgentEnrollmentException("Windows SYSTEM SID 无效。"),
        };
        var exact = verified.AreAccessRulesProtected &&
                    verifiedOwner.Equals(currentSid) &&
                    rules.Length == 2 &&
                    rules.All(rule =>
                        !rule.IsInherited &&
                        rule.AccessControlType == AccessControlType.Allow &&
                        rule.FileSystemRights == FileSystemRights.FullControl &&
                        rule.InheritanceFlags == InheritanceFlags.None &&
                        rule.PropagationFlags == PropagationFlags.None &&
                        rule.IdentityReference is SecurityIdentifier ruleSid &&
                        ruleSid.Value is not null &&
                        allowed.Contains(ruleSid.Value));
        if (!exact)
        {
            throw new AgentEnrollmentException("Windows 未能把私钥 ACL 精确限制为当前用户与 SYSTEM。");
        }
    }

    private static string SafeCommandDetail(CommandResult result) =>
        result.ExitCode == 0 ? string.Empty : $" OpenSSH 退出代码：{result.ExitCode}。";
}
