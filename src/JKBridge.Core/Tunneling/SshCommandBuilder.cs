using System.Net;
using JKBridge.Core.Configuration;
using JKBridge.Core.Models;

namespace JKBridge.Core.Tunneling;

public sealed class SshCommandBuilder
{
    public SshLaunchSpec Build(
        AppConfig config,
        string targetHost,
        string knownHostsPath,
        string sshExecutablePath = "ssh.exe",
        Func<string, string>? pathExpander = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ConfigValidator.EnsureValid(config);

        var target = ValidateTargetHost(targetHost);
        var expander = pathExpander ?? Environment.ExpandEnvironmentVariables;
        var identityFile = ExpandAndValidatePath(config.Server.IdentityFile, expander, "identity file");
        var knownHostsFile = ExpandAndValidatePath(knownHostsPath, expander, "known-hosts file");

        if (string.IsNullOrWhiteSpace(sshExecutablePath) || ContainsControlCharacters(sshExecutablePath))
        {
            throw new ArgumentException("SSH executable path is invalid.", nameof(sshExecutablePath));
        }

        var enabledServices = config.Services.Where(service => service.Enabled).ToArray();
        if (enabledServices.Length == 0)
        {
            throw new InvalidOperationException("At least one service must be enabled to create an SSH tunnel.");
        }

        var arguments = new List<string>
        {
            "-F",
            "none",
            "-n",
            "-N",
            "-T",
            "-i",
            identityFile,
            "-p",
            config.Server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        AddOption(arguments, "IdentitiesOnly=yes");
        AddOption(arguments, "BatchMode=yes");
        AddOption(arguments, "PreferredAuthentications=publickey");
        AddOption(arguments, "PasswordAuthentication=no");
        AddOption(arguments, "KbdInteractiveAuthentication=no");
        AddOption(arguments, "PermitLocalCommand=no");
        AddOption(arguments, "ProxyCommand=none");
        AddOption(arguments, $"HostKeyAlias={config.Server.HostKeyAlias}");
        AddOption(arguments, $"UserKnownHostsFile={knownHostsFile}");
        AddOption(arguments, "StrictHostKeyChecking=yes");
        AddOption(arguments, "CheckHostIP=no");
        AddOption(arguments, "UpdateHostKeys=no");
        AddOption(arguments, "ExitOnForwardFailure=yes");
        AddOption(arguments, "ConnectTimeout=10");
        AddOption(arguments, "ConnectionAttempts=1");
        AddOption(arguments, "ServerAliveInterval=15");
        AddOption(arguments, "ServerAliveCountMax=3");
        AddOption(arguments, "LogLevel=ERROR");

        foreach (var service in enabledServices)
        {
            arguments.Add("-L");
            arguments.Add(CreateForwardArgument(service));
        }

        arguments.Add($"{config.Server.User}@{target}");

        return new(
            ExecutablePath: sshExecutablePath,
            Arguments: arguments.ToArray(),
            TargetHost: target,
            LocalPorts: enabledServices.Select(service => service.LocalPort).ToArray());
    }

    private static void AddOption(ICollection<string> arguments, string option)
    {
        arguments.Add("-o");
        arguments.Add(option);
    }

    private static string CreateForwardArgument(ServiceDefinition service) =>
        $"127.0.0.1:{service.LocalPort}:{service.RemoteHost}:{service.RemotePort}";

    private static string ValidateTargetHost(string? targetHost)
    {
        var target = (targetHost ?? string.Empty).Trim().TrimEnd('.');
        if (target.Length == 0 ||
            target.Contains('@') ||
            target.Any(char.IsWhiteSpace) ||
            ContainsControlCharacters(target) ||
            Uri.CheckHostName(target) == UriHostNameType.Unknown)
        {
            throw new ArgumentException("Resolved SSH target must be a valid DNS name or IP address.", nameof(targetHost));
        }

        return IPAddress.TryParse(target, out var address)
            ? address.ToString()
            : target.ToLowerInvariant();
    }

    private static string ExpandAndValidatePath(
        string value,
        Func<string, string> expander,
        string description)
    {
        var expanded = expander(value)?.Trim() ?? string.Empty;
        if (expanded.Length == 0 || expanded.Length > 4096 || ContainsControlCharacters(expanded))
        {
            throw new ArgumentException($"Expanded {description} path is invalid.");
        }

        return expanded;
    }

    private static bool ContainsControlCharacters(string value) =>
        value.Any(character => char.IsControl(character));
}

public sealed record SshLaunchSpec(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string TargetHost,
    IReadOnlyList<int> LocalPorts);

public static class SshErrorClassifier
{
    public static SshFailureClassification Classify(string? standardError, int? exitCode = null)
    {
        var detail = NormalizeDetail(standardError);

        if (Contains(detail, "REMOTE HOST IDENTIFICATION HAS CHANGED") ||
            Contains(detail, "Offending ") && Contains(detail, " key in "))
        {
            return Failure(
                SshFailureKind.HostKeyMismatch,
                retryable: false,
                "The Linux host key changed. Verify the server before trusting a new key.",
                detail,
                exitCode);
        }

        if (Contains(detail, "Host key verification failed") ||
            Contains(detail, "No ED25519 host key is known") ||
            Contains(detail, "No host key is known"))
        {
            return Failure(
                SshFailureKind.HostKeyVerificationFailed,
                retryable: false,
                "The pinned Linux host key could not be verified.",
                detail,
                exitCode);
        }

        if (ContainsAny(
                detail,
                "Identity file") && ContainsAny(detail, "not accessible", "No such file") ||
            ContainsAny(
                detail,
                "Load key") && ContainsAny(detail, "invalid format", "Permission denied", "error in libcrypto") ||
            Contains(detail, "UNPROTECTED PRIVATE KEY FILE"))
        {
            return Failure(
                SshFailureKind.IdentityFileProblem,
                retryable: false,
                "The SSH private key is missing, unreadable, or invalid.",
                detail,
                exitCode);
        }

        if (ContainsAny(
                detail,
                "Permission denied (publickey",
                "Permission denied, please try again",
                "Too many authentication failures",
                "no mutual signature algorithm"))
        {
            return Failure(
                SshFailureKind.AuthenticationFailed,
                retryable: false,
                "The Linux server rejected the configured SSH key.",
                detail,
                exitCode);
        }

        if (Contains(detail, "Address already in use") ||
            Contains(detail, "cannot listen to port") ||
            Contains(detail, "Could not request local forwarding"))
        {
            return Failure(
                SshFailureKind.LocalPortConflict,
                retryable: false,
                "A configured Windows local port is already in use.",
                detail,
                exitCode);
        }

        if (ContainsAny(
                detail,
                "administratively prohibited",
                "port forwarding is disabled",
                "forwarding request denied"))
        {
            return Failure(
                SshFailureKind.ForwardingDenied,
                retryable: false,
                "The Linux SSH server denied a requested port forward.",
                detail,
                exitCode);
        }

        if (ContainsAny(
                detail,
                "Bad configuration option",
                "Bad local forwarding specification",
                "command-line line 0",
                "illegal option"))
        {
            return Failure(
                SshFailureKind.InvalidCommandLine,
                retryable: false,
                "The installed OpenSSH client rejected the tunnel configuration.",
                detail,
                exitCode);
        }

        if (Contains(detail, "Could not resolve hostname") ||
            Contains(detail, "Name or service not known"))
        {
            return Failure(
                SshFailureKind.NameResolutionFailed,
                retryable: true,
                "The Linux MagicDNS name could not be resolved yet.",
                detail,
                exitCode);
        }

        if (ContainsAny(detail, "Connection timed out", "Operation timed out", "Connection timeout"))
        {
            return Failure(
                SshFailureKind.ConnectionTimedOut,
                retryable: true,
                "The SSH connection timed out.",
                detail,
                exitCode);
        }

        if (Contains(detail, "Connection refused"))
        {
            return Failure(
                SshFailureKind.ConnectionRefused,
                retryable: true,
                "The Linux SSH service refused the connection.",
                detail,
                exitCode);
        }

        if (ContainsAny(detail, "No route to host", "Network is unreachable", "Network unreachable"))
        {
            return Failure(
                SshFailureKind.NetworkUnavailable,
                retryable: true,
                "The Tailscale route to the Linux host is unavailable.",
                detail,
                exitCode);
        }

        if (ContainsAny(
                detail,
                "Connection reset by peer",
                "Connection closed by remote host",
                "closed by remote host",
                "Broken pipe",
                "ssh_dispatch_run_fatal",
                "Connection reset"))
        {
            return Failure(
                SshFailureKind.ConnectionLost,
                retryable: true,
                "The SSH connection was interrupted.",
                detail,
                exitCode);
        }

        if (exitCode == 0)
        {
            return Failure(
                SshFailureKind.CleanExit,
                retryable: true,
                "The SSH tunnel exited and can be reconnected.",
                detail,
                exitCode);
        }

        if (exitCode is null && detail.Length == 0)
        {
            return new(SshFailureKind.None, Retryable: false, string.Empty, string.Empty, null);
        }

        return Failure(
            SshFailureKind.Unknown,
            retryable: false,
            "The SSH tunnel failed for an unrecognized reason.",
            detail,
            exitCode);
    }

    private static SshFailureClassification Failure(
        SshFailureKind kind,
        bool retryable,
        string message,
        string detail,
        int? exitCode) =>
        new(kind, retryable, message, detail, exitCode);

    private static bool Contains(string value, string fragment) =>
        value.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAny(string value, params string[] fragments) =>
        fragments.Any(fragment => Contains(value, fragment));

    private static string NormalizeDetail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var detail = value.Trim();
        return detail.Length <= 4096 ? detail : detail[..4096];
    }
}

public sealed record SshFailureClassification(
    SshFailureKind Kind,
    bool Retryable,
    string UserMessage,
    string TechnicalDetail,
    int? ExitCode);

public enum SshFailureKind
{
    None,
    CleanExit,
    HostKeyMismatch,
    HostKeyVerificationFailed,
    IdentityFileProblem,
    AuthenticationFailed,
    LocalPortConflict,
    ForwardingDenied,
    InvalidCommandLine,
    NameResolutionFailed,
    ConnectionTimedOut,
    ConnectionRefused,
    NetworkUnavailable,
    ConnectionLost,
    Unknown,
}
