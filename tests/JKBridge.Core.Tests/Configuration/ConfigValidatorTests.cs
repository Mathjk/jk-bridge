using JKBridge.Core.Configuration;
using JKBridge.Core.Models;

namespace JKBridge.Core.Tests.Configuration;

public sealed class ConfigValidatorTests
{
    [Fact]
    public void DefaultConfiguration_IsValidAndContainsExpectedServices()
    {
        var config = AppConfig.CreateDefault();

        var result = ConfigValidator.Validate(config);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Issues));
        Assert.Equal(AppConfig.CurrentSchemaVersion, config.SchemaVersion);
        Assert.False(config.SetupCompleted);
        Assert.Empty(config.ExpectedTailnetDnsSuffix);
        Assert.Empty(config.Server.Host);
        Assert.Empty(config.Server.User);
        Assert.Empty(config.Server.PinnedHostKey);
        Assert.Equal(["overleaf", "gpu-dashboard", "investment-news"], config.Services.Select(service => service.Id));
        Assert.All(config.Services, service => Assert.Equal("127.0.0.1", service.RemoteHost));
    }

    [Fact]
    public void DefaultConfiguration_ReturnsIndependentServiceCollections()
    {
        var first = AppConfig.CreateDefault();
        var second = AppConfig.CreateDefault();

        first.Services.Clear();

        Assert.Empty(first.Services);
        Assert.Equal(3, second.Services.Count);
    }

    [Fact]
    public void Validate_RejectsPersistedIpAddressAsServerIdentity()
    {
        var config = WithServer(server => server with { Host = "100.64.0.10" });

        var result = ConfigValidator.Validate(config);

        AssertIssue(result, "server.magicDnsRequired", "server.host");
    }

    [Fact]
    public void Validate_RejectsServerOutsideExpectedTailnet()
    {
        var config = WithServer(server => server with { Host = "linux.other-tailnet.ts.net" });

        var result = ConfigValidator.Validate(config);

        AssertIssue(result, "server.tailnetMismatch", "server.host");
    }

    [Fact]
    public void Validate_AllowsEmptyExpectedTailnetGuard()
    {
        var defaults = TestConfigs.CreateConfigured();
        var config = defaults with
        {
            ExpectedTailnetDnsSuffix = string.Empty,
            Server = defaults.Server with { Host = "linux.example.ts.net" },
        };

        Assert.True(ConfigValidator.Validate(config).IsValid);
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("ssh-rsa AAAA")]
    [InlineData("ssh-ed25519 AAAA")]
    [InlineData("")]
    public void Validate_RejectsInvalidPinnedEd25519Key(string key)
    {
        var config = WithServer(server => server with { PinnedHostKey = key });

        var result = ConfigValidator.Validate(config);

        AssertIssue(result, "server.invalidPinnedHostKey", "server.pinnedHostKey");
    }

    [Fact]
    public void Validate_RejectsDuplicateServiceIdsIgnoringCase()
    {
        var config = AppConfig.CreateDefault();
        config.Services[1] = config.Services[1] with { Id = "OVERLEAF" };

        var result = ConfigValidator.Validate(config);

        Assert.Contains(result.Issues, issue =>
            issue.Code is "service.invalidId" or "service.duplicateId");
    }

    [Fact]
    public void Validate_RejectsDuplicateLocalPortsEvenWhenOneServiceIsDisabled()
    {
        var config = AppConfig.CreateDefault();
        config.Services[1] = config.Services[1] with
        {
            Enabled = false,
            LocalPort = config.Services[0].LocalPort,
            OpenUrl = "http://127.0.0.1:8080/",
        };

        var result = ConfigValidator.Validate(config);

        AssertIssue(result, "service.duplicateLocalPort", "services[1].localPort");
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("0.0.0.0")]
    [InlineData("192.168.1.5")]
    [InlineData("::1")]
    public void Validate_RejectsAnyRemoteHostExceptIpv4Loopback(string remoteHost)
    {
        var config = AppConfig.CreateDefault();
        config.Services[0] = config.Services[0] with { RemoteHost = remoteHost };

        var result = ConfigValidator.Validate(config);

        AssertIssue(result, "service.remoteHostNotLoopback", "services[0].remoteHost");
    }

    [Fact]
    public void Validate_RejectsInvalidKnownAuthorizationReceipt()
    {
        var config = AppConfig.CreateDefault() with
        {
            Authorization = new ForwardAuthorizationGrant
            {
                IsKnown = true,
                ClientKeyFingerprint = "not-a-fingerprint",
                RemotePorts = [0, 80, 80],
            },
        };

        var result = ConfigValidator.Validate(config);

        AssertIssue(result, "authorization.invalidFingerprint", "authorization.clientKeyFingerprint");
        AssertIssue(result, "authorization.invalidPorts", "authorization.remotePorts");
    }

    [Fact]
    public void Validate_RejectsNullAuthorizationReceiptFieldsWithoutThrowing()
    {
        var config = AppConfig.CreateDefault() with
        {
            Authorization = new ForwardAuthorizationGrant
            {
                IsKnown = true,
                ClientKeyFingerprint = null!,
                RemotePorts = null!,
            },
        };

        var result = ConfigValidator.Validate(config);

        AssertIssue(result, "authorization.invalidFingerprint", "authorization.clientKeyFingerprint");
        AssertIssue(result, "authorization.invalidPorts", "authorization.remotePorts");
    }

    [Fact]
    public void Validate_RejectsNullUnknownAuthorizationFieldsWithoutThrowing()
    {
        var config = AppConfig.CreateDefault() with
        {
            Authorization = new ForwardAuthorizationGrant
            {
                IsKnown = false,
                ClientKeyFingerprint = null!,
                RemotePorts = null!,
            },
        };

        var result = ConfigValidator.Validate(config);

        AssertIssue(result, "authorization.invalidFingerprint", "authorization.clientKeyFingerprint");
        AssertIssue(result, "authorization.invalidPorts", "authorization.remotePorts");
    }

    [Theory]
    [InlineData("http://localhost:8080/project")]
    [InlineData("http://127.0.0.1:9000/project")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("https://example.com/")]
    public void Validate_RejectsUnsafeOrMismatchedOpenUrl(string openUrl)
    {
        var config = AppConfig.CreateDefault();
        config.Services[0] = config.Services[0] with { OpenUrl = openUrl };

        var result = ConfigValidator.Validate(config);

        AssertIssue(result, "service.invalidOpenUrl", "services[0].openUrl");
    }

    [Theory]
    [InlineData("")]
    [InlineData("health")]
    [InlineData("//example.com/path")]
    [InlineData("/health#fragment")]
    [InlineData("/health\nInjected")]
    public void Validate_RejectsUnsafeHealthPath(string healthPath)
    {
        var config = AppConfig.CreateDefault();
        config.Services[0] = config.Services[0] with { HealthPath = healthPath };

        var result = ConfigValidator.Validate(config);

        AssertIssue(result, "service.invalidHealthPath", "services[0].healthPath");
    }

    [Fact]
    public void Validate_RejectsUnknownEnumValues()
    {
        var defaults = TestConfigs.CreateConfigured();
        var config = defaults with
        {
            Behavior = defaults.Behavior with { Theme = (ThemePreference)999 },
        };

        var result = ConfigValidator.Validate(config);

        AssertIssue(result, "behavior.invalidTheme", "behavior.theme");
    }

    [Fact]
    public void EnsureValid_ThrowsWithAllIssues()
    {
        var defaults = TestConfigs.CreateConfigured();
        var config = defaults with
        {
            SchemaVersion = 999,
            Server = defaults.Server with { User = "bad user" },
        };

        var exception = Assert.Throws<ConfigValidationException>(() =>
            ConfigValidator.EnsureValid(config));

        Assert.True(exception.Issues.Count >= 2);
        Assert.Contains("schemaVersion", exception.Message, StringComparison.Ordinal);
    }

    private static AppConfig WithServer(Func<ServerProfile, ServerProfile> update)
    {
        var defaults = TestConfigs.CreateConfigured();
        return defaults with { Server = update(defaults.Server) };
    }

    private static void AssertIssue(ConfigValidationResult result, string code, string path)
    {
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == code && issue.Path == path);
    }
}
