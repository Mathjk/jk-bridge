using JKBridge.Core.Models;

namespace JKBridge.Core.Routing;

public static class ServiceRouteDefinition
{
    public static string DeriveHealthPath(string openUrl, string fallback)
    {
        if (!Uri.TryCreate(openUrl, UriKind.Absolute, out var uri))
        {
            return fallback;
        }

        return string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
    }

    public static ServiceRouteResult Evaluate(ServiceDefinition service)
    {
        ArgumentNullException.ThrowIfNull(service);

        var issues = new List<ServiceRouteIssue>();

        if (service.LocalPort is < 1 or > 65535)
        {
            issues.Add(OutOfRange("localPort"));
        }

        if (service.RemotePort is < 1 or > 65535)
        {
            issues.Add(OutOfRange("remotePort"));
        }

        if (!string.Equals(
                service.RemoteHost,
                ServiceDefinition.LoopbackHost,
                StringComparison.Ordinal))
        {
            issues.Add(new(
                "remoteHost",
                "service.remoteHostNotLoopback",
                "Remote host must be exactly 127.0.0.1."));
        }

        var openUriIsValid = ServiceOpenUrl.TryParse(
            service.OpenUrl,
            service.LocalPort,
            out var openUrlParts);
        if (!openUriIsValid)
        {
            issues.Add(new(
                "openUrl",
                "service.invalidOpenUrl",
                "Open URL must use HTTP(S) on 127.0.0.1 and the configured local port."));
        }

        if (!IsSafeHealthPath(service.HealthPath))
        {
            issues.Add(new(
                "healthPath",
                "service.invalidHealthPath",
                "Health path must be a local absolute path beginning with a single slash."));
        }

        if (issues.Count > 0)
        {
            return new(null, issues);
        }

        var healthUri = new UriBuilder(
            openUrlParts!.Scheme,
            ServiceDefinition.LoopbackHost,
            service.LocalPort,
            service.HealthPath).Uri;
        var route = new ServiceRoute(
            service.LocalPort,
            new ForwardTarget(service.RemoteHost, service.RemotePort),
            openUrlParts.Uri,
            healthUri);
        return new(route, issues);
    }

    private static ServiceRouteIssue OutOfRange(string field) =>
        new(field, "port.outOfRange", "Port must be between 1 and 65535.");

    private static bool IsSafeHealthPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.Length <= 2048 &&
        path.StartsWith("/", StringComparison.Ordinal) &&
        !path.StartsWith("//", StringComparison.Ordinal) &&
        !path.Contains('#') &&
        !path.Any(char.IsControl) &&
        Uri.IsWellFormedUriString(path, UriKind.Relative);
}

public sealed record ServiceRoute(
    int LocalPort,
    ForwardTarget RemoteTarget,
    Uri OpenUri,
    Uri HealthUri)
{
    public string SshForwardArgument =>
        $"127.0.0.1:{LocalPort}:{RemoteTarget.Host}:{RemoteTarget.Port}";
}

public sealed record ForwardTarget(string Host, int Port);

public sealed record ServiceRouteIssue(string Field, string Code, string Message);

public sealed record ServiceRouteResult(ServiceRoute? Route, IReadOnlyList<ServiceRouteIssue> Issues)
{
    public bool IsValid => Route is not null && Issues.Count == 0;
}
