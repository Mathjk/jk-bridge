using System.Net;
using System.Text.RegularExpressions;
using JKBridge.Core.Models;
using JKBridge.Core.Routing;
using JKBridge.Core.Security;

namespace JKBridge.Core.Configuration;

public static class ConfigValidator
{
    private const int MaximumServices = 64;
    private const int MaximumWindowsLocalServices = 128;

    private static readonly Regex ServiceIdPattern = new(
        "^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SshUserPattern = new(
        "^[A-Za-z_][A-Za-z0-9_.-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HostKeyAliasPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FingerprintPattern = new(
        "^SHA256:[A-Za-z0-9+/]{43}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CredentialReferencePattern = new(
        "^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CatalogRevisionPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static ConfigValidationResult Validate(AppConfig? config)
    {
        var issues = new List<ConfigValidationIssue>();

        if (config is null)
        {
            issues.Add(new("$", "config.required", "Configuration is required."));
            return new(issues);
        }

        if (config.SchemaVersion != AppConfig.CurrentSchemaVersion)
        {
            issues.Add(new(
                "schemaVersion",
                "schema.unsupported",
                $"Schema version must be {AppConfig.CurrentSchemaVersion}."));
        }

        var expectedSuffix = NormalizeDnsName(config.ExpectedTailnetDnsSuffix);
        if (expectedSuffix.Length > 0 && !IsDnsName(expectedSuffix))
        {
            issues.Add(new(
                "expectedTailnetDnsSuffix",
                "tailnet.invalidDnsSuffix",
                "Expected tailnet DNS suffix must be a valid DNS name."));
        }

        if (config.SetupCompleted)
        {
            ValidateServer(config.Server, expectedSuffix, issues);
        }
        ValidateBehavior(config.Behavior, issues);
        ValidateUpdateNetwork(config.UpdateNetwork, issues);
        ValidateAuthorization(config.Authorization, issues);
        ValidateServices(config.Services, issues);
        ValidateWindowsLocalServices(config.WindowsLocalServices, config.Services, issues);

        return new(issues);
    }

    public static void EnsureValid(AppConfig? config)
    {
        var result = Validate(config);
        if (!result.IsValid)
        {
            throw new ConfigValidationException(result.Issues);
        }
    }

    private static void ValidateServer(
        ServerProfile? server,
        string expectedSuffix,
        ICollection<ConfigValidationIssue> issues)
    {
        if (server is null)
        {
            issues.Add(new("server", "server.required", "Server profile is required."));
            return;
        }

        ValidateRequiredText(server.DisplayName, "server.displayName", 80, issues);

        var host = NormalizeDnsName(server.Host);
        if (!IsDnsName(host) || !host.Contains('.') ||
            IPAddress.TryParse(host, out _))
        {
            issues.Add(new(
                "server.host",
                "server.magicDnsRequired",
                "Server host must be a MagicDNS fully qualified domain name, not an IP address."));
        }
        else if (expectedSuffix.Length > 0 &&
                 !host.EndsWith('.' + expectedSuffix, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new(
                "server.host",
                "server.tailnetMismatch",
                "Server host must belong to the configured tailnet DNS suffix."));
        }

        if (string.IsNullOrWhiteSpace(server.User) || !SshUserPattern.IsMatch(server.User))
        {
            issues.Add(new(
                "server.user",
                "server.invalidUser",
                "SSH user contains unsupported characters."));
        }

        ValidatePort(server.Port, "server.port", issues);

        if (string.IsNullOrWhiteSpace(server.IdentityFile) ||
            server.IdentityFile.Length > 1024 ||
            ContainsControlCharacters(server.IdentityFile))
        {
            issues.Add(new(
                "server.identityFile",
                "server.invalidIdentityFile",
                "SSH identity file path is invalid."));
        }

        if (string.IsNullOrWhiteSpace(server.HostKeyAlias) ||
            !HostKeyAliasPattern.IsMatch(server.HostKeyAlias))
        {
            issues.Add(new(
                "server.hostKeyAlias",
                "server.invalidHostKeyAlias",
                "SSH host-key alias contains unsupported characters."));
        }

        if (!Ed25519PublicKey.TryNormalize(server.PinnedHostKey, out _))
        {
            issues.Add(new(
                "server.pinnedHostKey",
                "server.invalidPinnedHostKey",
                "Pinned host key must be a valid ssh-ed25519 public key."));
        }
    }

    private static void ValidateBehavior(
        BehaviorSettings? behavior,
        ICollection<ConfigValidationIssue> issues)
    {
        if (behavior is null)
        {
            issues.Add(new("behavior", "behavior.required", "Behavior settings are required."));
            return;
        }

        if (!Enum.IsDefined(behavior.Theme))
        {
            issues.Add(new("behavior.theme", "behavior.invalidTheme", "Theme preference is invalid."));
        }
    }

    private static void ValidateUpdateNetwork(
        UpdateNetworkSettings? settings,
        ICollection<ConfigValidationIssue> issues)
    {
        if (settings is null)
        {
            issues.Add(new(
                "updateNetwork",
                "updateNetwork.required",
                "Update network settings are required."));
            return;
        }

        if (!Enum.IsDefined(settings.Mode))
        {
            issues.Add(new(
                "updateNetwork.mode",
                "updateNetwork.invalidMode",
                "Update network mode is invalid."));
            return;
        }

        var proxy = settings.ManualProxyUrl?.Trim() ?? string.Empty;
        if (settings.Mode != UpdateNetworkMode.ManualProxy)
        {
            if (proxy.Length > 0)
            {
                issues.Add(new(
                    "updateNetwork.manualProxyUrl",
                    "updateNetwork.unusedProxy",
                    "A manual update proxy can be stored only in manual-proxy mode."));
            }
            return;
        }

        if (!TryParseLoopbackHttpProxy(proxy, out _))
        {
            issues.Add(new(
                "updateNetwork.manualProxyUrl",
                "updateNetwork.invalidProxy",
                "Manual update proxy must be an HTTP URL for 127.0.0.1, localhost, or ::1 with an explicit port and no credentials or path."));
        }
    }

    public static bool TryParseLoopbackHttpProxy(string? value, out Uri proxyUri)
    {
        proxyUri = null!;
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 256 ||
            ContainsControlCharacters(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed) ||
            !parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            parsed.IsDefaultPort ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            parsed.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment))
        {
            return false;
        }

        var normalizedHost = parsed.Host.Trim('[', ']');
        var loopback = normalizedHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                       normalizedHost.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                       normalizedHost.Equals("::1", StringComparison.OrdinalIgnoreCase);
        if (!loopback)
        {
            return false;
        }

        proxyUri = parsed;
        return true;
    }

    private static void ValidateAuthorization(
        ForwardAuthorizationGrant? authorization,
        ICollection<ConfigValidationIssue> issues)
    {
        if (authorization is null)
        {
            issues.Add(new("authorization", "authorization.required", "Authorization state is required."));
            return;
        }

        var fingerprint = authorization.ClientKeyFingerprint;
        var remotePorts = authorization.RemotePorts;
        if (fingerprint is null)
        {
            issues.Add(new(
                "authorization.clientKeyFingerprint",
                "authorization.invalidFingerprint",
                "Authorization fingerprint cannot be null."));
        }

        if (remotePorts is null)
        {
            issues.Add(new(
                "authorization.remotePorts",
                "authorization.invalidPorts",
                "Authorization ports cannot be null."));
        }

        if (fingerprint is null || remotePorts is null)
        {
            return;
        }

        if (!authorization.IsKnown)
        {
            if (fingerprint.Length > 0 || remotePorts.Count > 0)
            {
                issues.Add(new(
                    "authorization",
                    "authorization.unknownHasReceipt",
                    "Unknown authorization state cannot contain a receipt."));
            }

            return;
        }

        if (!FingerprintPattern.IsMatch(fingerprint))
        {
            issues.Add(new(
                "authorization.clientKeyFingerprint",
                "authorization.invalidFingerprint",
                "Authorization fingerprint must be a valid SHA256 fingerprint."));
        }

        if (remotePorts.Count is < 1 or > 32 ||
            remotePorts.Any(port => port is < 1 or > 65535) ||
            remotePorts.Distinct().Count() != remotePorts.Count)
        {
            issues.Add(new(
                "authorization.remotePorts",
                "authorization.invalidPorts",
                "Authorization ports must contain 1–32 unique valid ports."));
        }
    }

            private static void ValidateServices(
        List<ServiceDefinition>? services,
        ICollection<ConfigValidationIssue> issues)
    {
        if (services is null)
        {
            issues.Add(new("services", "services.required", "Services collection is required."));
            return;
        }

        if (services.Count == 0)
        {
            issues.Add(new("services", "services.empty", "At least one service is required."));
            return;
        }

        if (services.Count > MaximumServices)
        {
            issues.Add(new(
                "services",
                "services.tooMany",
                $"No more than {MaximumServices} services are allowed."));
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localPorts = new HashSet<int>();
        var tunnelPorts = services
            .Where(service => service is not null && service.LocalPort is >= 1 and <= 65535)
            .Select(service => service!.LocalPort)
            .ToHashSet();

        for (var index = 0; index < services.Count; index++)
        {
            var service = services[index];
            var path = $"services[{index}]";
            if (service is null)
            {
                issues.Add(new(path, "service.required", "Service definition is required."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(service.Id) || !ServiceIdPattern.IsMatch(service.Id))
            {
                issues.Add(new(
                    $"{path}.id",
                    "service.invalidId",
                    "Service id must be a lowercase ASCII slug."));
            }
            else if (!ids.Add(service.Id))
            {
                issues.Add(new(
                    $"{path}.id",
                    "service.duplicateId",
                    "Service id must be unique."));
            }

            ValidateRequiredText(service.Name, $"{path}.name", 80, issues);
            ValidateOptionalText(service.Description, $"{path}.description", 160, issues);
            ValidateRequiredText(service.Icon, $"{path}.icon", 40, issues);

            var route = ServiceRouteDefinition.Evaluate(service);
            foreach (var routeIssue in route.Issues)
            {
                issues.Add(new(
                    $"{path}.{routeIssue.Field}",
                    routeIssue.Code,
                    routeIssue.Message));
            }

            if (service.LocalPort is >= 1 and <= 65535 && !localPorts.Add(service.LocalPort))
            {
                issues.Add(new(
                    $"{path}.localPort",
                    "service.duplicateLocalPort",
                    "Local ports must be unique."));
            }

            if (!Enum.IsDefined(service.HealthKind))
            {
                issues.Add(new(
                    $"{path}.healthKind",
                    "service.invalidHealthKind",
                    "Health-check kind is invalid."));
            }

            ValidateServiceRestartAction(service, path, issues, tunnelPorts);
        }
    }

    private static void ValidateServiceRestartAction(
        ServiceDefinition service,
        string path,
        ICollection<ConfigValidationIssue> issues,
        IReadOnlyCollection<int> tunnelPorts)
    {
        if (string.IsNullOrWhiteSpace(service.RestartPath))
        {
            if (!string.IsNullOrWhiteSpace(service.RestartToken))
            {
                issues.Add(new(
                    $"{path}.restartToken",
                    "service.restartTokenWithoutPath",
                    "A restart token requires a restart path."));
            }

            return;
        }

        if (!IsSafeRestartPath(service.RestartPath))
        {
            issues.Add(new(
                $"{path}.restartPath",
                "service.invalidRestartPath",
                "Restart path must be a single-slash relative path without #, backslash, query or control characters."));
        }

        if (service.RestartLocalPort is < 0 or > 65535)
        {
            issues.Add(new(
                $"{path}.restartLocalPort",
                "service.invalidRestartLocalPort",
                "Restart port must be 0 (reuse the service port) or between 1 and 65535."));
        }
        else if (service.RestartLocalPort != 0 &&
                 service.RestartLocalPort != service.LocalPort &&
                 !tunnelPorts.Contains(service.RestartLocalPort))
        {
            issues.Add(new(
                $"{path}.restartLocalPort",
                "service.restartPortWithoutTunnel",
                "Restart port must match a configured service's Windows local port so an SSH tunnel already exists."));
        }

        ValidateOptionalText(service.RestartToken, $"{path}.restartToken", 256, issues);
    }

    private static bool IsSafeRestartPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.Length <= 2048 &&
        path.StartsWith("/", StringComparison.Ordinal) &&
        !path.StartsWith("//", StringComparison.Ordinal) &&
        !path.Contains('#') &&
        !path.Contains('\\') &&
        !path.Contains('?') &&
        !path.Any(char.IsControl) &&
        Uri.IsWellFormedUriString(path, UriKind.Relative);

    private static void ValidateWindowsLocalServices(
        List<WindowsLocalServiceDefinition>? localServices,
        List<ServiceDefinition>? tunnelServices,
        ICollection<ConfigValidationIssue> issues)
    {
        if (localServices is null)
        {
            issues.Add(new(
                "windowsLocalServices",
                "windowsLocalServices.required",
                "Windows-local services collection is required."));
            return;
        }

        if (localServices.Count > MaximumWindowsLocalServices)
        {
            issues.Add(new(
                "windowsLocalServices",
                "windowsLocalServices.tooMany",
                $"No more than {MaximumWindowsLocalServices} Windows-local services are allowed."));
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enabledTunnelPorts = tunnelServices?
            .Where(service => service is { Enabled: true, LocalPort: >= 1 and <= 65535 })
            .Select(service => service.LocalPort)
            .ToHashSet() ?? [];

        for (var index = 0; index < localServices.Count; index++)
        {
            var service = localServices[index];
            var path = $"windowsLocalServices[{index}]";
            if (service is null)
            {
                issues.Add(new(path, "windowsLocalService.required", "Service definition is required."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(service.Id) || !ServiceIdPattern.IsMatch(service.Id))
            {
                issues.Add(new(
                    $"{path}.id",
                    "windowsLocalService.invalidId",
                    "Service id must be a lowercase ASCII slug."));
            }
            else if (!ids.Add(service.Id))
            {
                issues.Add(new(
                    $"{path}.id",
                    "windowsLocalService.duplicateId",
                    "Service id must be unique."));
            }

            ValidateRequiredText(service.Name, $"{path}.name", 80, issues);
            ValidateOptionalText(service.Description, $"{path}.description", 160, issues);
            ValidateRequiredText(service.Icon, $"{path}.icon", 40, issues);
            ValidateOptionalText(service.ProcessNameHint, $"{path}.processNameHint", 80, issues);
            ValidatePort(service.Port, $"{path}.port", issues);

            if (!WindowsLocalServiceUrl.IsLoopbackHost(service.Host))
            {
                issues.Add(new(
                    $"{path}.host",
                    "windowsLocalService.notLoopback",
                    "Host must be exactly 127.0.0.1 or ::1."));
            }

            if (!WindowsLocalServiceUrl.TryBuild(
                    service.Host,
                    service.Scheme,
                    service.Port,
                    service.LaunchPath,
                    out var url))
            {
                issues.Add(new(
                    $"{path}.launchPath",
                    "windowsLocalService.invalidUrl",
                    "URL must be canonical HTTP(S) on 127.0.0.1 or ::1 with a safe launch path."));
            }
            else if (!urls.Add(url))
            {
                issues.Add(new(
                    $"{path}.launchPath",
                    "windowsLocalService.duplicateUrl",
                    "Windows-local service URLs must be unique."));
            }

            if (!Enum.IsDefined(service.HealthKind))
            {
                issues.Add(new(
                    $"{path}.healthKind",
                    "windowsLocalService.invalidHealthKind",
                    "Health-check kind is invalid."));
            }

            if (service.Enabled && enabledTunnelPorts.Contains(service.Port))
            {
                issues.Add(new(
                    $"{path}.port",
                    "windowsLocalService.tunnelPortConflict",
                    "An enabled Windows-local service cannot share a port with an enabled SSH tunnel."));
            }
        }
    }

    private static void ValidateOptionalText(
        string? value,
        string path,
        int maximumLength,
        ICollection<ConfigValidationIssue> issues)
    {
        if (value is null || value.Length > maximumLength || ContainsControlCharacters(value))
        {
            issues.Add(new(path, "text.invalid", $"Text must be at most {maximumLength} characters and contain no control characters."));
        }
    }

    private static void ValidateRequiredText(
        string? value,
        string path,
        int maximumLength,
        ICollection<ConfigValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            ContainsControlCharacters(value))
        {
            issues.Add(new(path, "text.invalid", $"Value must contain 1 to {maximumLength} safe characters."));
        }
    }

    private static void ValidatePort(
        int port,
        string path,
        ICollection<ConfigValidationIssue> issues)
    {
        if (port is < 1 or > 65535)
        {
            issues.Add(new(path, "port.outOfRange", "Port must be between 1 and 65535."));
        }
    }

    private static bool ContainsControlCharacters(string value) =>
        value.Any(character => char.IsControl(character));

    private static string NormalizeDnsName(string? value) =>
        (value ?? string.Empty).Trim().TrimEnd('.');

    private static bool IsDnsName(string value) =>
        value.Length is > 0 and <= 253 &&
        Uri.CheckHostName(value) == UriHostNameType.Dns;

}

public sealed record ConfigValidationIssue(string Path, string Code, string Message);

public sealed class ConfigValidationResult
{
    public ConfigValidationResult(IEnumerable<ConfigValidationIssue> issues)
    {
        Issues = issues.ToArray();
    }

    public IReadOnlyList<ConfigValidationIssue> Issues { get; }

    public bool IsValid => Issues.Count == 0;
}

public sealed class ConfigValidationException : Exception
{
    public ConfigValidationException(IReadOnlyList<ConfigValidationIssue> issues)
        : base(CreateMessage(issues))
    {
        Issues = issues;
    }

    public IReadOnlyList<ConfigValidationIssue> Issues { get; }

    private static string CreateMessage(IReadOnlyList<ConfigValidationIssue> issues) =>
        issues.Count == 0
            ? "Configuration is invalid."
            : $"Configuration is invalid: {string.Join("; ", issues.Select(issue => $"{issue.Path}: {issue.Message}"))}";
}
