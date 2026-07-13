using JKBridge.Core.Pairing;

namespace JKBridge.Core.Tests.Pairing;

public sealed class PairingCodeTests
{
    private const string Token = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void TryParse_AcceptsStrictTailscalePairingCode()
    {
        var parsed = PairingCode.TryParse(
            $"jkbridge://100.64.0.10:45123/v1?token={Token}",
            out var code,
            out var error);

        Assert.True(parsed, error);
        Assert.NotNull(code);
        Assert.Equal("100.64.0.10", code.Address.ToString());
        Assert.Equal(45123, code.Port);
        Assert.Equal(Token, code.Token);
        Assert.Equal("http://100.64.0.10:45123/v1/pair", code.Endpoint.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://100.64.0.10:45123/v1?token=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("jkbridge://192.0.2.10:45123/v1?token=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("jkbridge://100.64.0.10:45123/v2?token=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("jkbridge://100.64.0.10:45123/v1?token=short")]
    [InlineData("jkbridge://100.64.0.10:45123/v1?token=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("jkbridge://100.64.0.10:45123/v1?token=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&extra=1")]
    public void TryParse_RejectsMalformedOrNonTailscaleCode(string value)
    {
        Assert.False(PairingCode.TryParse(value, out var code, out var error));
        Assert.Null(code);
        Assert.NotEmpty(error);
    }
}
