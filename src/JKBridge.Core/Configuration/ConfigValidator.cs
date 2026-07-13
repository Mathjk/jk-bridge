using System.Net;
using System.Text.RegularExpressions;
using JKBridge.Core.Models;
using JKBridge.Core.Security;

namespace JKBridge.Core.Configuration;

public static class ConfigValidator
{
    private const int MaximumServices = 64;

    private static readonly Regex ServiceIdPattern = new(
        "^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SshUserPattern = new(
        "^[A-Za-z_][A-Za-z0-9_.-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HostKeyAliasPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$",
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
        ValidateServices(config.Services, issues);

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
            ValidatePort(service.LocalPort, $"{path}.localPort", issues);
            ValidatePort(service.RemotePort, $"{path}.remotePort", issues);

            if (service.LocalPort is >= 1 and <= 65535 && !localPorts.Add(service.LocalPort))
            {
                issues.Add(new(
                    $"{path}.localPort",
                    "service.duplicateLocalPort",
                    "Local ports must be unique."));
            }

            if (!string.Equals(
                    service.RemoteHost,
                    ServiceDefinition.LoopbackHost,
                    StringComparison.Ordinal))
            {
                issues.Add(new(
                    $"{path}.remoteHost",
                    "service.remoteHostNotLoopback",
                    "Remote host must be exactly 127.0.0.1."));
            }

            ValidateOpenUrl(service, path, issues);

            if (!IsSafeHealthPath(service.HealthPath))
            {
                issues.Add(new(
                    $"{path}.healthPath",
                    "service.invalidHealthPath",
                    "Health path must be a local absolute path beginning with a single slash."));
            }

            if (!Enum.IsDefined(service.HealthKind))
            {
                issues.Add(new(
                    $"{path}.healthKind",
                    "service.invalidHealthKind",
                    "Health-check kind is invalid."));
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

    private static void ValidateOpenUrl(
        ServiceDefinition service,
        string path,
        ICollection<ConfigValidationIssue> issues)
    {
        if (!Uri.TryCreate(service.OpenUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.Equals(uri.Host, ServiceDefinition.LoopbackHost, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.Port != service.LocalPort)
        {
            issues.Add(new(
                $"{path}.openUrl",
                "service.invalidOpenUrl",
                "Open URL must use HTTP(S) on 127.0.0.1 and the configured local port."));
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

    private static bool IsSafeHealthPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.Length <= 2048 &&
        path.StartsWith("/", StringComparison.Ordinal) &&
        !path.StartsWith("//", StringComparison.Ordinal) &&
        !path.Contains('#') &&
        !ContainsControlCharacters(path) &&
        Uri.IsWellFormedUriString(path, UriKind.Relative);

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
