using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Runtime.Versioning;
using JKBridge.App.Platform;
using JKBridge.Core.Pairing;
using JKBridge.Core.Processes;
using JKBridge.Core.Security;
using JKBridge.Core.Tailscale;

namespace JKBridge.App.Services;

public enum DevicePairingStage
{
    CheckingPrerequisites,
    PreparingKey,
    AuthorizingLinux,
    Completed,
}

public sealed record DevicePairingProgress(DevicePairingStage Stage, string Detail);

public sealed record DevicePairingRequest(
    string PairingCode,
    string IdentityFile,
    IReadOnlyList<int> RemotePorts);

public sealed record DevicePairingResult(
    PairingResponse Server,
    string PortableIdentityFile,
    string ClientFingerprint);

public interface IDevicePairingService
{
    Task<DevicePairingResult> PairAsync(
        DevicePairingRequest request,
        IProgress<DevicePairingProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class DevicePairingService : IDevicePairingService
{
    private readonly ICommandRunner _commandRunner;

    public DevicePairingService(ICommandRunner? commandRunner = null)
    {
        _commandRunner = commandRunner ?? new CommandRunner();
    }

    public async Task<DevicePairingResult> PairAsync(
        DevicePairingRequest request,
        IProgress<DevicePairingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsWindows())
        {
            throw new PairingException("Windows 一键配对只能在 Windows 10/11 上运行。");
        }

        if (!PairingCode.TryParse(request.PairingCode, out var code, out var codeError))
        {
            throw new PairingException(codeError);
        }

        progress?.Report(new(DevicePairingStage.CheckingPrerequisites, "正在检查 Tailscale 与 OpenSSH…"));
        await EnsureTailscaleRunningAsync(cancellationToken).ConfigureAwait(false);
        var keygen = ExecutableLocator.FindSshKeygen()
                     ?? throw new PairingException("找不到 Windows OpenSSH 的 ssh-keygen.exe。");

        progress?.Report(new(DevicePairingStage.PreparingKey, "正在创建或验证专用 Ed25519 密钥…"));
        var identityPath = ResolveIdentityPath(request.IdentityFile);
        var publicKey = await PrepareIdentityAsync(keygen, identityPath, cancellationToken).ConfigureAwait(false);
        var clientFingerprint = Ed25519PublicKey.ComputeFingerprint(publicKey);

        progress?.Report(new(DevicePairingStage.AuthorizingLinux, "正在通过一次性 Tailscale 通道授权 Linux…"));
        using var pairingClient = new PairingClient();
        var server = await pairingClient.PairAsync(
                code!,
                publicKey,
                request.RemotePorts,
                cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(new(DevicePairingStage.Completed, "Linux 已授权，正在保存固定主机身份…"));
        return new DevicePairingResult(server, ToPortableIdentityPath(identityPath), clientFingerprint);
    }

    private async Task EnsureTailscaleRunningAsync(CancellationToken cancellationToken)
    {
        var tailscale = ExecutableLocator.FindTailscale()
                        ?? throw new PairingException("找不到 Tailscale。请先安装、启动并登录。");
        var result = await _commandRunner.RunAsync(
                new CommandRequest(
                    tailscale,
                    ["status", "--json"],
                    TimeSpan.FromSeconds(10),
                    MaxOutputCharacters: 256 * 1024),
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess ||
            !TailscaleStatusParser.TryParse(result.StandardOutput, out var status, out _) ||
            status is null ||
            !status.IsRunning)
        {
            throw new PairingException("Tailscale 尚未处于 Running 状态，请先登录正确的 tailnet。");
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task<string> PrepareIdentityAsync(
        string keygen,
        string identityPath,
        CancellationToken cancellationToken)
    {
        AssertPathHasNoReparsePoints(identityPath, includeParents: true);
        var directory = Path.GetDirectoryName(identityPath)
                        ?? throw new PairingException("专用密钥路径没有父目录。");
        Directory.CreateDirectory(directory);
        AssertPathHasNoReparsePoints(identityPath, includeParents: true);

        if (!File.Exists(identityPath))
        {
            if (File.Exists(identityPath + ".pub"))
            {
                throw new PairingException("密钥目录中只有同名公钥而没有私钥，请换一个专用密钥路径。");
            }

            var generated = await _commandRunner.RunAsync(
                    new CommandRequest(
                        keygen,
                        [
                            "-q", "-t", "ed25519", "-a", "100", "-N", string.Empty,
                            "-C", "JK Bridge managed pairing key", "-f", identityPath,
                        ],
                        TimeSpan.FromSeconds(30),
                        MaxOutputCharacters: 32 * 1024),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!generated.IsSuccess || !File.Exists(identityPath))
            {
                throw new PairingException("OpenSSH 未能生成专用密钥。" + SafeCommandDetail(generated));
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
            throw new PairingException("专用私钥无效或需要口令；JK Bridge 不会保存密钥口令。");
        }

        var publicPath = identityPath + ".pub";
        AssertPathHasNoReparsePoints(publicPath, includeParents: true);
        await File.WriteAllTextAsync(
                publicPath,
                publicKey + " JK Bridge managed pairing key" + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken)
            .ConfigureAwait(false);
        return publicKey;
    }

    private static string ResolveIdentityPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value.Any(char.IsControl))
        {
            throw new PairingException("专用密钥路径无效。");
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
                    throw new PairingException($"拒绝使用符号链接或目录联接路径：{candidate}");
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
                         ?? throw new PairingException("无法读取当前 Windows 用户 SID。");
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var file = new FileInfo(path);
        var ownerSecurity = file.GetAccessControl(AccessControlSections.Owner);
        if (ownerSecurity.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
            !owner.Equals(currentSid))
        {
            throw new PairingException("专用私钥不属于当前 Windows 用户；请换用新的密钥路径。");
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
            throw new PairingException("Windows 无法验证专用私钥所有者。");
        }
        var rules = verified.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            currentSid.Value ?? throw new PairingException("当前 Windows 用户 SID 无效。"),
            systemSid.Value ?? throw new PairingException("Windows SYSTEM SID 无效。"),
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
            throw new PairingException("Windows 未能把私钥 ACL 精确限制为当前用户与 SYSTEM。");
        }
    }

    private static string SafeCommandDetail(CommandResult result)
    {
        return result.ExitCode == 0 ? string.Empty : $" OpenSSH 退出代码：{result.ExitCode}。";
    }
}
