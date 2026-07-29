using JKBridge.Core.Models;
using JKBridge.Core.Routing;

namespace JKBridge.App.ViewModels;

public sealed record ServiceTemplateOptionViewModel(
    string TemplateId,
    string DisplayName,
    string Summary)
{
    public static IReadOnlyList<ServiceTemplateOptionViewModel> CreateCatalog() =>
        ServiceTemplateCatalog.GetAll()
            .Select(template => new ServiceTemplateOptionViewModel(
                template.TemplateId,
                LocalizeDisplayName(template),
                Describe(template.Service)))
            .ToArray();

    private static string LocalizeDisplayName(ServiceTemplate template) => template.TemplateId switch
    {
        ServiceTemplateCatalog.GenericHttp => "通用 HTTP 服务",
        ServiceTemplateCatalog.GenericHttps => "通用 HTTPS 服务",
        _ => template.DisplayName,
    };

    private static string Describe(ServiceDefinition service)
    {
        ServiceOpenUrl.TryParse(service.OpenUrl, service.LocalPort, out var parts);
        var protocol = parts?.Scheme.ToUpperInvariant() ?? "HTTP(S)";
        var health = service.HealthKind switch
        {
            HealthCheckKind.TcpConnect => "TCP 端口",
            HealthCheckKind.JsonOk => $"JSON ok=true · {service.HealthPath}",
            _ => $"HTTP 状态码 · {service.HealthPath}",
        };
        return $"Windows :{service.LocalPort} → Linux :{service.RemotePort} · {protocol} · {health}";
    }
}
