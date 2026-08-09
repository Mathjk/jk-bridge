using JKBridge.Core.Configuration;
using JKBridge.Core.Models;
using JKBridge.Core.Routing;

namespace JKBridge.Core.Tests.Configuration;

public sealed class WindowsLocalServiceTests
{
    [Theory]
    [InlineData("127.0.0.1", "http", 8384, "/", "http://127.0.0.1:8384/")]
    [InlineData("::1", "https", 8443, "/admin", "https://[::1]:8443/admin")]
    public void Build_CreatesCanonicalLoopbackUrl(
        string host,
        string scheme,
        int port,
        string path,
        string expected)
    {
        Assert.Equal(expected, WindowsLocalServiceUrl.Build(host, scheme, port, path));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("192.168.1.5")]
    public void TryBuild_RejectsNonLiteralLoopbackHosts(string host)
    {
        Assert.False(WindowsLocalServiceUrl.TryBuild(host, "http", 8080, "/", out _));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("ftp")]
    [InlineData("ws")]
    public void TryBuild_RejectsNonHttpSchemes(string scheme)
    {
        Assert.False(WindowsLocalServiceUrl.TryBuild("127.0.0.1", scheme, 8080, "/", out _));
    }

    [Fact]
    public void Validate_AcceptsIndependentWindowsLocalService()
    {
        var config = AppConfig.CreateDefault() with
        {
            WindowsLocalServices = [CreateSyncthing()],
        };

        var result = ConfigValidator.Validate(config);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Issues));
    }

    [Fact]
    public void Validate_RejectsDuplicateCanonicalLocalUrl()
    {
        var first = CreateSyncthing();
        var config = AppConfig.CreateDefault() with
        {
            WindowsLocalServices =
            [
                first,
                first with { Id = "syncthing-copy", Name = "Syncthing copy" },
            ],
        };

        var result = ConfigValidator.Validate(config);

        Assert.Contains(result.Issues, issue =>
            issue.Code == "windowsLocalService.duplicateUrl" &&
            issue.Path == "windowsLocalServices[1].launchPath");
    }

    [Fact]
    public void Validate_AllowsDifferentPathsOnOneLocalOrigin()
    {
        var first = CreateSyncthing();
        var config = AppConfig.CreateDefault() with
        {
            WindowsLocalServices =
            [
                first,
                first with
                {
                    Id = "syncthing-metrics",
                    Name = "Syncthing metrics",
                    LaunchPath = "/metrics",
                },
            ],
        };

        Assert.True(ConfigValidator.Validate(config).IsValid);
    }

    [Fact]
    public void Validate_RejectsEnabledTunnelPortCollision()
    {
        var local = CreateSyncthing() with { Port = 8080 };
        var config = AppConfig.CreateDefault() with
        {
            WindowsLocalServices = [local],
        };

        var result = ConfigValidator.Validate(config);

        Assert.Contains(result.Issues, issue =>
            issue.Code == "windowsLocalService.tunnelPortConflict" &&
            issue.Path == "windowsLocalServices[0].port");
    }

    [Fact]
    public void Validate_AllowsDisabledLocalBookmarkOnTunnelPort()
    {
        var local = CreateSyncthing() with { Port = 8080, Enabled = false };
        var config = AppConfig.CreateDefault() with
        {
            WindowsLocalServices = [local],
        };

        Assert.True(ConfigValidator.Validate(config).IsValid);
    }

    private static WindowsLocalServiceDefinition CreateSyncthing() => new()
    {
        Id = "windows-syncthing",
        Name = "Syncthing",
        Description = "Local synchronization dashboard",
        Port = 8384,
        ProcessNameHint = "syncthing",
    };
}
