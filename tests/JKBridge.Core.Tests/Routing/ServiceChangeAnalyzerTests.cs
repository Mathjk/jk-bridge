using JKBridge.Core.Models;
using JKBridge.Core.Routing;

namespace JKBridge.Core.Tests.Routing;

public sealed class ServiceChangeAnalyzerTests
{
    [Fact]
    public void Analyze_LocalPortAndLaunchPathOnly_DoNotRequireRemoteReauthorization()
    {
        var current = AppConfig.CreateDefault().Services;
        var proposed = current.Select(service => service with { }).ToList();
        proposed[0] = proposed[0] with
        {
            LocalPort = 18080,
            OpenUrl = ServiceOpenUrl.Build("http", 18080, "/project?mode=edit#/doc"),
        };

        var result = ServiceChangeAnalyzer.Analyze(current, proposed);

        Assert.Equal(RemoteAuthorizationImpact.None, result.AuthorizationImpact);
        Assert.False(result.RequiresRemoteReauthorization);
        Assert.False(result.BlocksUntilReauthorized);
        Assert.Empty(result.AddedRemotePorts);
        Assert.Empty(result.RemovedRemotePorts);
        Assert.Equal(ServiceChangeKind.Modified, result.Changes.Single(change => change.Id == "overleaf").Kind);
    }

    [Fact]
    public void Analyze_NewEnabledRemotePort_BlocksUntilReauthorized()
    {
        var current = AppConfig.CreateDefault().Services;
        var proposed = current
            .Append(ServiceTemplateCatalog.Create(ServiceTemplateCatalog.Sub2Api))
            .ToList();

        var result = ServiceChangeAnalyzer.Analyze(current, proposed);

        Assert.Equal(RemoteAuthorizationImpact.ReauthorizationRequired, result.AuthorizationImpact);
        Assert.True(result.RequiresRemoteReauthorization);
        Assert.True(result.BlocksUntilReauthorized);
        Assert.Equal([8081], result.AddedRemotePorts);
        Assert.Empty(result.RemovedRemotePorts);
    }

    [Fact]
    public void Analyze_RemovedRemotePort_RecommendsLeastPrivilegeRepairWithoutBlocking()
    {
        var current = AppConfig.CreateDefault().Services;
        var proposed = current.Where(service => service.Id != "investment-news").ToList();

        var result = ServiceChangeAnalyzer.Analyze(current, proposed);

        Assert.Equal(RemoteAuthorizationImpact.ReauthorizationRecommended, result.AuthorizationImpact);
        Assert.True(result.RequiresRemoteReauthorization);
        Assert.False(result.BlocksUntilReauthorized);
        Assert.Equal([8793], result.RemovedRemotePorts);
    }

    [Fact]
    public void Analyze_DisabledServiceOrSharedRemotePort_DoesNotExpandAuthorization()
    {
        var current = AppConfig.CreateDefault().Services;
        var disabled = ServiceTemplateCatalog.Create(ServiceTemplateCatalog.Sub2Api) with
        {
            Enabled = false,
        };
        var sameRemotePort = ServiceTemplateCatalog.Create(ServiceTemplateCatalog.GenericHttp) with
        {
            Id = "overleaf-copy",
            LocalPort = 18080,
            RemotePort = 80,
            OpenUrl = ServiceOpenUrl.Build("http", 18080, "/"),
        };
        var proposed = current.Append(disabled).Append(sameRemotePort).ToList();

        var result = ServiceChangeAnalyzer.Analyze(current, proposed);

        Assert.Equal(RemoteAuthorizationImpact.None, result.AuthorizationImpact);
        Assert.Empty(result.AddedRemotePorts);
    }
}
