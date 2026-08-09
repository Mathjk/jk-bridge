using JKBridge.Core.Configuration;
using JKBridge.Core.Models;
using JKBridge.Core.Routing;

namespace JKBridge.Core.Tests.Routing;

public sealed class ServiceTemplateCatalogTests
{
    [Fact]
    public void Catalog_ContainsValidLoopbackTemplatesIncludingSub2Api()
    {
        var templates = ServiceTemplateCatalog.GetAll();

        Assert.Equal(
            [
                ServiceTemplateCatalog.Overleaf,
                ServiceTemplateCatalog.Sub2Api,
                ServiceTemplateCatalog.Syncthing,
                ServiceTemplateCatalog.Jupyter,
                ServiceTemplateCatalog.Grafana,
                ServiceTemplateCatalog.Ollama,
                ServiceTemplateCatalog.ComfyUi,
                ServiceTemplateCatalog.StableDiffusionWebUi,
                ServiceTemplateCatalog.GenericHttp,
                ServiceTemplateCatalog.GenericHttps,
            ],
            templates.Select(template => template.TemplateId));
        Assert.All(templates, template =>
        {
            Assert.Equal(ServiceDefinition.LoopbackHost, template.Service.RemoteHost);
            var config = AppConfig.CreateDefault() with { Services = [template.Service] };
            Assert.True(
                ConfigValidator.Validate(config).IsValid,
                string.Join(Environment.NewLine, ConfigValidator.Validate(config).Issues));
        });

        var sub2Api = ServiceTemplateCatalog.Create(ServiceTemplateCatalog.Sub2Api);
        Assert.Equal(8081, sub2Api.LocalPort);
        Assert.Equal(8081, sub2Api.RemotePort);
        Assert.Equal("http://127.0.0.1:8081/", sub2Api.OpenUrl);
    }

    [Theory]
    [InlineData("syncthing", "", 9000, ServiceTemplateCatalog.Syncthing)]
    [InlineData("python", "JupyterLab", 9000, ServiceTemplateCatalog.Jupyter)]
    [InlineData("", "", 11434, ServiceTemplateCatalog.Ollama)]
    [InlineData("python", "Stable Diffusion WebUI", 7860, ServiceTemplateCatalog.StableDiffusionWebUi)]
    [InlineData("unknown", "Local tool", 4567, ServiceTemplateCatalog.GenericHttp)]
    public void Recognize_UsesStableNamesTitlesAndPorts(
        string processName,
        string title,
        int port,
        string expected)
    {
        Assert.Equal(expected, ServiceTemplateCatalog.Recognize(processName, title, port));
    }

    [Fact]
    public void Catalog_ReturnsIndependentTemplateValues()
    {
        var first = ServiceTemplateCatalog.GetAll();
        var second = ServiceTemplateCatalog.GetAll();

        Assert.NotSame(first, second);
        Assert.NotSame(first[0], second[0]);
        Assert.NotSame(first[0].Service, second[0].Service);
    }

    [Fact]
    public void Create_RejectsUnknownTemplate()
    {
        Assert.Throws<KeyNotFoundException>(() =>
            ServiceTemplateCatalog.Create("missing-template"));
    }
}
