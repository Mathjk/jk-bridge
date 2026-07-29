using JKBridge.Core.Models;

namespace JKBridge.Core.Routing;

public static class ServiceTemplateCatalog
{
    public const string Overleaf = "overleaf";
    public const string Sub2Api = "sub2api";
    public const string GenericHttp = "generic-http";
    public const string GenericHttps = "generic-https";

    private static readonly IReadOnlyList<ServiceTemplate> Templates =
    [
        new(Overleaf, "Overleaf", new ServiceDefinition
        {
            Id = "overleaf",
            Name = "Overleaf",
            Description = "论文协作与 LaTeX 编辑",
            Icon = "Document",
            Enabled = true,
            Favorite = true,
            LocalPort = 8080,
            RemoteHost = ServiceDefinition.LoopbackHost,
            RemotePort = 80,
            OpenUrl = ServiceOpenUrl.Build("http", 8080, "/project"),
            HealthPath = "/project",
            HealthKind = HealthCheckKind.HttpStatus,
        }),
        new(Sub2Api, "Sub2API", new ServiceDefinition
        {
            Id = "sub2api",
            Name = "Sub2API",
            Description = "本机 API 管理与转发服务",
            Icon = "Globe",
            Enabled = true,
            Favorite = true,
            LocalPort = 8081,
            RemoteHost = ServiceDefinition.LoopbackHost,
            RemotePort = 8081,
            OpenUrl = ServiceOpenUrl.Build("http", 8081, "/"),
            HealthPath = "/",
            HealthKind = HealthCheckKind.HttpStatus,
        }),
        new(GenericHttp, "HTTP service", new ServiceDefinition
        {
            Id = "http-service",
            Name = "HTTP service",
            Description = "Linux 回环 HTTP 服务",
            Icon = "Globe",
            Enabled = true,
            LocalPort = 9000,
            RemoteHost = ServiceDefinition.LoopbackHost,
            RemotePort = 9000,
            OpenUrl = ServiceOpenUrl.Build("http", 9000, "/"),
            HealthPath = "/",
            HealthKind = HealthCheckKind.HttpStatus,
        }),
        new(GenericHttps, "HTTPS service", new ServiceDefinition
        {
            Id = "https-service",
            Name = "HTTPS service",
            Description = "Linux 回环 HTTPS 服务",
            Icon = "Globe",
            Enabled = true,
            LocalPort = 9443,
            RemoteHost = ServiceDefinition.LoopbackHost,
            RemotePort = 443,
            OpenUrl = ServiceOpenUrl.Build("https", 9443, "/"),
            HealthPath = "/",
            HealthKind = HealthCheckKind.TcpConnect,
        }),
    ];

    public static IReadOnlyList<ServiceTemplate> GetAll() =>
        Templates.Select(Clone).ToArray();

    public static bool TryCreate(string? templateId, out ServiceDefinition? service)
    {
        var template = Templates.FirstOrDefault(candidate =>
            string.Equals(candidate.TemplateId, templateId, StringComparison.Ordinal));
        service = template is null ? null : template.Service with { };
        return service is not null;
    }

    public static ServiceDefinition Create(string templateId)
    {
        if (!TryCreate(templateId, out var service))
        {
            throw new KeyNotFoundException($"Unknown service template '{templateId}'.");
        }

        return service!;
    }

    private static ServiceTemplate Clone(ServiceTemplate template) =>
        template with { Service = template.Service with { } };
}

public sealed record ServiceTemplate(
    string TemplateId,
    string DisplayName,
    ServiceDefinition Service);
