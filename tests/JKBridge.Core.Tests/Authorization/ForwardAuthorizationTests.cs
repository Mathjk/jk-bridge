using JKBridge.Core.Authorization;
using JKBridge.Core.Models;

namespace JKBridge.Core.Tests.Authorization;

public sealed class ForwardAuthorizationTests
{
    [Fact]
    public void Evaluate_UnknownReceiptRequiresAllEnabledPortsToBeReconfirmed()
    {
        var configured = TestConfigs.CreateConfigured();
        var config = configured with { Authorization = new ForwardAuthorizationGrant() };

        var result = ForwardAuthorization.Evaluate(config);

        Assert.Equal(ForwardAuthorizationStatus.Unknown, result.Status);
        Assert.Equal([80, 8765, 8793], result.MissingPorts);
    }

    [Fact]
    public void Evaluate_WhenAnEnabledRouteIsNotGranted_RequiresPairingBeforeConnect()
    {
        var configured = TestConfigs.CreateConfigured();
        var config = configured with
        {
            Authorization = new ForwardAuthorizationGrant
            {
                IsKnown = true,
                ClientKeyFingerprint = "SHA256:" + new string('A', 43),
                RemotePorts = [80, 8765],
            },
        };

        var result = ForwardAuthorization.Evaluate(config);

        Assert.Equal(ForwardAuthorizationStatus.PairingRequired, result.Status);
        Assert.Equal([8793], result.MissingPorts);
        Assert.Empty(result.ExtraPorts);
    }

    [Fact]
    public void Evaluate_WhenLinuxRetainsAnUnusedGrant_RecommendsPairingWithoutBlocking()
    {
        var configured = TestConfigs.CreateConfigured();
        configured.Services[2] = configured.Services[2] with { Enabled = false };
        var config = configured with
        {
            Authorization = new ForwardAuthorizationGrant
            {
                IsKnown = true,
                ClientKeyFingerprint = "SHA256:" + new string('A', 43),
                RemotePorts = [80, 8765, 8793],
            },
        };

        var result = ForwardAuthorization.Evaluate(config);

        Assert.Equal(ForwardAuthorizationStatus.PairingRecommended, result.Status);
        Assert.Empty(result.MissingPorts);
        Assert.Equal([8793], result.ExtraPorts);
    }
}
