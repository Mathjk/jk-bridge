using JKBridge.Core.Models;

namespace JKBridge.Core.Routing;

public static class ServiceTemplateCatalog
{
    public const string Overleaf = "overleaf";
    public const string Sub2Api = "sub2api";
    public const string Syncthing = "syncthing";
    public const string Jupyter = "jupyter";
    public const string Grafana = "grafana";
    public const string Ollama = "ollama";
    public const string ComfyUi = "comfyui";
    public const string StableDiffusionWebUi = "stable-diffusion-webui";
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
        new(Syncthing, "Syncthing", new ServiceDefinition
        {
            Id = "syncthing",
            Name = "Syncthing",
            Description = "文件同步管理界面",
            Icon = "Sync",
            Enabled = true,
            LocalPort = 8384,
            RemoteHost = ServiceDefinition.LoopbackHost,
            RemotePort = 8384,
            OpenUrl = ServiceOpenUrl.Build("http", 8384, "/"),
            HealthPath = "/",
            HealthKind = HealthCheckKind.HttpStatus,
        }),
        new(Jupyter, "JupyterLab", new ServiceDefinition
        {
            Id = "jupyter",
            Name = "JupyterLab",
            Description = "Notebook 与远程研究环境",
            Icon = "Notebook",
            Enabled = true,
            LocalPort = 8888,
            RemoteHost = ServiceDefinition.LoopbackHost,
            RemotePort = 8888,
            OpenUrl = ServiceOpenUrl.Build("http", 8888, "/lab"),
            HealthPath = "/lab",
            HealthKind = HealthCheckKind.HttpStatus,
        }),
        new(Grafana, "Grafana", new ServiceDefinition
        {
            Id = "grafana",
            Name = "Grafana",
            Description = "指标与运行状态看板",
            Icon = "Chart",
            Enabled = true,
            LocalPort = 3000,
            RemoteHost = ServiceDefinition.LoopbackHost,
            RemotePort = 3000,
            OpenUrl = ServiceOpenUrl.Build("http", 3000, "/login"),
            HealthPath = "/login",
            HealthKind = HealthCheckKind.HttpStatus,
        }),
        new(Ollama, "Ollama", new ServiceDefinition
        {
            Id = "ollama",
            Name = "Ollama API",
            Description = "Linux 本机大模型 API",
            Icon = "Cpu",
            Enabled = true,
            LocalPort = 11434,
            RemoteHost = ServiceDefinition.LoopbackHost,
            RemotePort = 11434,
            OpenUrl = ServiceOpenUrl.Build("http", 11434, "/"),
            HealthPath = "/",
            HealthKind = HealthCheckKind.HttpStatus,
        }),
        new(ComfyUi, "ComfyUI", new ServiceDefinition
        {
            Id = "comfyui",
            Name = "ComfyUI",
            Description = "节点式图像生成工作流",
            Icon = "Image",
            Enabled = true,
            LocalPort = 8188,
            RemoteHost = ServiceDefinition.LoopbackHost,
            RemotePort = 8188,
            OpenUrl = ServiceOpenUrl.Build("http", 8188, "/"),
            HealthPath = "/",
            HealthKind = HealthCheckKind.HttpStatus,
        }),
        new(StableDiffusionWebUi, "Stable Diffusion WebUI", new ServiceDefinition
        {
            Id = "stable-diffusion-webui",
            Name = "Stable Diffusion WebUI",
            Description = "图像生成网页界面",
            Icon = "Image",
            Enabled = true,
            LocalPort = 7860,
            RemoteHost = ServiceDefinition.LoopbackHost,
            RemotePort = 7860,
            OpenUrl = ServiceOpenUrl.Build("http", 7860, "/"),
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

    public static string Recognize(string? processName, string? title, int port)
    {
        var haystack = $"{processName} {title}".ToLowerInvariant();
        var rules = new (string Needle, string TemplateId)[]
        {
            ("syncthing", Syncthing),
            ("jupyter", Jupyter),
            ("grafana", Grafana),
            ("ollama", Ollama),
            ("comfyui", ComfyUi),
            ("automatic1111", StableDiffusionWebUi),
            ("stable diffusion", StableDiffusionWebUi),
            ("overleaf", Overleaf),
            ("sub2api", Sub2Api),
        };
        foreach (var (needle, templateId) in rules)
        {
            if (haystack.Contains(needle, StringComparison.Ordinal))
            {
                return templateId;
            }
        }

        return port switch
        {
            8384 => Syncthing,
            8888 => Jupyter,
            3000 => Grafana,
            11434 => Ollama,
            8188 => ComfyUi,
            7860 => StableDiffusionWebUi,
            _ => GenericHttp,
        };
    }

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
