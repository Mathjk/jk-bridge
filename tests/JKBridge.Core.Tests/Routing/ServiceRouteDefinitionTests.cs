using JKBridge.Core.Models;
using JKBridge.Core.Routing;

namespace JKBridge.Core.Tests.Routing;

public sealed class ServiceRouteDefinitionTests
{
    [Fact]
    public void Evaluate_ValidServiceDerivesEveryRouteEndpoint()
    {
        var service = new ServiceDefinition
        {
            Id = "campus-login",
            Name = "Campus login",
            LocalPort = 19000,
            RemotePort = 19000,
            OpenUrl = "https://127.0.0.1:19000/#/login",
            HealthPath = "/health",
        };

        var result = ServiceRouteDefinition.Evaluate(service);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Issues));
        var route = Assert.IsType<ServiceRoute>(result.Route);
        Assert.Equal(19000, route.LocalPort);
        Assert.Equal(new ForwardTarget("127.0.0.1", 19000), route.RemoteTarget);
        Assert.Equal("https://127.0.0.1:19000/#/login", route.OpenUri.AbsoluteUri);
        Assert.Equal("https://127.0.0.1:19000/health", route.HealthUri.AbsoluteUri);
        Assert.Equal("127.0.0.1:19000:127.0.0.1:19000", route.SshForwardArgument);
    }

    [Fact]
    public void Evaluate_RejectsNonLoopbackRemoteTarget()
    {
        var service = new ServiceDefinition
        {
            Id = "unsafe",
            Name = "Unsafe",
            LocalPort = 19000,
            RemoteHost = "10.0.0.5",
            RemotePort = 9000,
            OpenUrl = "http://127.0.0.1:19000/",
            HealthPath = "/",
        };

        var result = ServiceRouteDefinition.Evaluate(service);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "service.remoteHostNotLoopback");
    }

    [Theory]
    [InlineData("http://localhost:19000/")]
    [InlineData("http://127.0.0.1:19001/")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("https://example.com/")]
    public void Evaluate_RejectsUnsafeBrowserOrigin(string openUrl)
    {
        var service = new ServiceDefinition
        {
            Id = "unsafe-origin",
            Name = "Unsafe origin",
            LocalPort = 19000,
            RemotePort = 19000,
            OpenUrl = openUrl,
            HealthPath = "/",
        };

        var result = ServiceRouteDefinition.Evaluate(service);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "service.invalidOpenUrl");
    }

    [Theory]
    [InlineData("")]
    [InlineData("health")]
    [InlineData("//example.com/path")]
    [InlineData("/health#fragment")]
    [InlineData("/health\nInjected")]
    public void Evaluate_RejectsUnsafeHealthPath(string healthPath)
    {
        var service = new ServiceDefinition
        {
            Id = "unsafe-health",
            Name = "Unsafe health",
            LocalPort = 19000,
            RemotePort = 19000,
            OpenUrl = "http://127.0.0.1:19000/",
            HealthPath = healthPath,
        };

        var result = ServiceRouteDefinition.Evaluate(service);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "service.invalidHealthPath");
    }

    [Theory]
    [InlineData(0, 19000, "localPort")]
    [InlineData(19000, 65536, "remotePort")]
    public void Evaluate_RejectsOutOfRangePorts(int localPort, int remotePort, string field)
    {
        var service = new ServiceDefinition
        {
            Id = "invalid-port",
            Name = "Invalid port",
            LocalPort = localPort,
            RemotePort = remotePort,
            OpenUrl = $"http://127.0.0.1:{Math.Max(localPort, 1)}/",
            HealthPath = "/",
        };

        var result = ServiceRouteDefinition.Evaluate(service);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Field == field && issue.Code == "port.outOfRange");
    }

    [Fact]
    public void DeriveHealthPath_UsesOnlyTheBrowserPath()
    {
        var path = ServiceRouteDefinition.DeriveHealthPath(
            "https://127.0.0.1:19002/api/status?full=true#/login",
            "/fallback");

        Assert.Equal("/api/status", path);
    }

    [Theory]
    [InlineData("http", 8080, "/project", "http://127.0.0.1:8080/project")]
    [InlineData("HTTP", 8081, "/", "http://127.0.0.1:8081/")]
    [InlineData("https", 19002, "/api/status?full=true&name=a%20b#/login", "https://127.0.0.1:19002/api/status?full=true&name=a%20b#/login")]
    public void ServiceOpenUrl_BuildsStrictLoopbackUrlWithoutLosingRouteParts(
        string scheme,
        int localPort,
        string launchPath,
        string expected)
    {
        var openUrl = ServiceOpenUrl.Build(scheme, localPort, launchPath);

        Assert.Equal(expected, openUrl);
        Assert.True(ServiceOpenUrl.TryParse(openUrl, localPort, out var parts));
        Assert.Equal(scheme.ToLowerInvariant(), parts!.Scheme);
        Assert.Equal(localPort, parts.LocalPort);
        Assert.Equal(launchPath, parts.LaunchPath);
    }

    [Theory]
    [InlineData("ftp", 8080, "/")]
    [InlineData("http", 0, "/")]
    [InlineData("http", 8080, "project")]
    [InlineData("http", 8080, "//example.com/path")]
    [InlineData("http", 8080, "/bad\\path")]
    [InlineData("http", 8080, "/bad\npath")]
    [InlineData("http", 8080, "/bad path")]
    public void ServiceOpenUrl_TryBuildRejectsUnsafeInput(
        string scheme,
        int localPort,
        string launchPath)
    {
        Assert.False(ServiceOpenUrl.TryBuild(scheme, localPort, launchPath, out var openUrl));
        Assert.Empty(openUrl);
    }

    [Theory]
    [InlineData("http://localhost:8080/")]
    [InlineData("http://127.0.0.1:8081/")]
    [InlineData("http://user@127.0.0.1:8080/")]
    [InlineData("file:///tmp/file")]
    [InlineData("http://127.0.0.1:8080\\@example.com/")]
    public void ServiceOpenUrl_TryParseRejectsAnythingOutsideExactListener(
        string openUrl)
    {
        Assert.False(ServiceOpenUrl.TryParse(openUrl, 8080, out var parts));
        Assert.Null(parts);
    }

    [Fact]
    public void Evaluate_ReportsEveryIndependentRouteProblemInOnePass()
    {
        var service = new ServiceDefinition
        {
            Id = "invalid",
            Name = "Invalid",
            LocalPort = 0,
            RemoteHost = "0.0.0.0",
            RemotePort = 70000,
            OpenUrl = "file:///tmp/not-a-browser-route",
            HealthPath = "health",
        };

        var result = ServiceRouteDefinition.Evaluate(service);

        Assert.Null(result.Route);
        Assert.Equal(
            [
                "port.outOfRange",
                "port.outOfRange",
                "service.remoteHostNotLoopback",
                "service.invalidOpenUrl",
                "service.invalidHealthPath",
            ],
            result.Issues.Select(issue => issue.Code));
    }
}
