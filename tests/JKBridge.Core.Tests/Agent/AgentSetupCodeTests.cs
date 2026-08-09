using System.Text;
using JKBridge.Core.Agent;

namespace JKBridge.Core.Tests.Agent;

public sealed class AgentSetupCodeTests
{
    private static readonly string Credential = Convert.ToBase64String(
            Enumerable.Range(0, 32).Select(value => (byte)value).ToArray())
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    [Fact]
    public void TryParse_AcceptsStrictVersionOnePayloadWithoutRevealingSecret()
    {
        var value = Encode(
            $"{{\"endpoint\":\"http://100.64.0.10:45871/\",\"enrollmentCode\":\"{Credential}\",\"version\":1}}");

        var parsed = AgentSetupCode.TryParse(value, out var setupCode, out var error);

        Assert.True(parsed, error);
        Assert.NotNull(setupCode);
        Assert.Equal("http://100.64.0.10:45871/", setupCode.Endpoint.ToString());
        Assert.Equal(Credential, setupCode.EnrollmentCredential.Export());
        Assert.DoesNotContain(Credential, setupCode.ToString(), StringComparison.Ordinal);
        Assert.Equal("[REDACTED AGENT SETUP CODE]", setupCode.ToString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"endpoint\":\"http://127.0.0.1:45871/\",\"enrollmentCode\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"version\":1}")]
    [InlineData("{\"endpoint\":\"http://100.64.0.10:45871/\",\"enrollmentCode\":\"short\",\"version\":1}")]
    [InlineData("{\"endpoint\":\"http://100.64.0.10:45871/\",\"enrollmentCode\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"version\":2}")]
    [InlineData("{\"endpoint\":\"http://100.64.0.10:45871/\",\"enrollmentCode\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"version\":1,\"extra\":true}")]
    [InlineData("{\"endpoint\":\"http://100.64.0.10:45871/\",\"endpoint\":\"http://100.64.0.11:45871/\",\"enrollmentCode\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"version\":1}")]
    public void TryParse_RejectsNonCanonicalOrUnexpectedPayloads(string json)
    {
        Assert.False(AgentSetupCode.TryParse(Encode(json), out var setupCode, out var error));
        Assert.Null(setupCode);
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("JKBA1.")]
    [InlineData("JKBA1.!!!!")]
    [InlineData("jkba1.eyJ2ZXJzaW9uIjoxfQ")]
    public void TryParse_RejectsMalformedEnvelope(string value)
    {
        Assert.False(AgentSetupCode.TryParse(value, out _, out var error));
        Assert.NotEmpty(error);
    }

    private static string Encode(string json)
    {
        return AgentSetupCode.Prefix +
               Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
                   .TrimEnd('=')
                   .Replace('+', '-')
                   .Replace('/', '_');
    }
}
