using JKBridge.Core.Models;

namespace JKBridge.Core.Configuration;

public static class ServiceProfileValidator
{
    public const int MaximumServices = 64;

    public static ServiceProfileValidationResult Validate(ServiceProfile? profile)
    {
        var issues = new List<ServiceProfileValidationIssue>();
        if (profile is null)
        {
            issues.Add(new("$", "profile.required", "Service profile is required."));
            return new(issues);
        }

        if (!string.Equals(
                profile.Format,
                ServiceProfile.ExpectedFormat,
                StringComparison.Ordinal))
        {
            issues.Add(new(
                "format",
                "profile.invalidFormat",
                $"Profile format must be '{ServiceProfile.ExpectedFormat}'."));
        }

        if (profile.FormatVersion != ServiceProfile.CurrentFormatVersion)
        {
            issues.Add(new(
                "formatVersion",
                "profile.unsupportedVersion",
                $"Profile format version must be {ServiceProfile.CurrentFormatVersion}."));
        }

        if (string.IsNullOrWhiteSpace(profile.DisplayName) ||
            profile.DisplayName.Length > 80 ||
            profile.DisplayName.Any(char.IsControl))
        {
            issues.Add(new(
                "displayName",
                "profile.invalidDisplayName",
                "Profile display name must contain 1–80 safe characters."));
        }

        if (profile.Services is null)
        {
            issues.Add(new("services", "services.required", "Services collection is required."));
            return new(issues);
        }

        var services = profile.Services
            .Select(entry => entry is null
                ? null!
                : ServiceProfileMapping.ToServiceDefinition(entry))
            .ToList();
        var candidate = AppConfig.CreateDefault() with { Services = services };
        foreach (var issue in ConfigValidator.Validate(candidate).Issues
                     .Where(issue => issue.Path.StartsWith("services", StringComparison.Ordinal)))
        {
            issues.Add(new(issue.Path, issue.Code, issue.Message));
        }

        return new(issues);
    }

    public static void EnsureValid(ServiceProfile? profile)
    {
        var result = Validate(profile);
        if (!result.IsValid)
        {
            throw new ServiceProfileValidationException(result.Issues);
        }
    }
}

public sealed record ServiceProfileValidationIssue(string Path, string Code, string Message);

public sealed class ServiceProfileValidationResult
{
    public ServiceProfileValidationResult(IEnumerable<ServiceProfileValidationIssue> issues)
    {
        Issues = issues.ToArray();
    }

    public IReadOnlyList<ServiceProfileValidationIssue> Issues { get; }

    public bool IsValid => Issues.Count == 0;
}

public sealed class ServiceProfileValidationException : Exception
{
    public ServiceProfileValidationException(IEnumerable<ServiceProfileValidationIssue> issues)
        : this(issues.ToArray())
    {
    }

    private ServiceProfileValidationException(IReadOnlyList<ServiceProfileValidationIssue> issues)
        : base(BuildMessage(issues))
    {
        Issues = issues;
    }

    public IReadOnlyList<ServiceProfileValidationIssue> Issues { get; }

    private static string BuildMessage(IReadOnlyList<ServiceProfileValidationIssue> issues) =>
        "Service profile validation failed: " +
        string.Join("; ", issues.Select(issue => $"{issue.Path}: {issue.Message}"));
}

internal static class ServiceProfileMapping
{
    public static ServiceProfileEntry FromServiceDefinition(ServiceDefinition service) => new()
    {
        Id = service.Id,
        Name = service.Name,
        Description = service.Description,
        Icon = service.Icon,
        Enabled = service.Enabled,
        Favorite = service.Favorite,
        WindowsLocalPort = service.LocalPort,
        LinuxLoopbackPort = service.RemotePort,
        OpenUrl = service.OpenUrl,
        HealthPath = service.HealthPath,
        HealthKind = service.HealthKind,
    };

    public static ServiceDefinition ToServiceDefinition(ServiceProfileEntry entry) => new()
    {
        Id = entry.Id,
        Name = entry.Name,
        Description = entry.Description,
        Icon = entry.Icon,
        Enabled = entry.Enabled,
        Favorite = entry.Favorite,
        LocalPort = entry.WindowsLocalPort,
        RemoteHost = ServiceDefinition.LoopbackHost,
        RemotePort = entry.LinuxLoopbackPort,
        OpenUrl = entry.OpenUrl,
        HealthPath = entry.HealthPath,
        HealthKind = entry.HealthKind,
    };
}
