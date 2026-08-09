using JKBridge.Core.Pairing;

namespace JKBridge.Core.Tests.Pairing;

public sealed class LinuxLocalServiceUrlTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8384/", "http", 8384, "/", false)]
    [InlineData("https://localhost:8443/admin", "https", 8443, "/admin", false)]
    [InlineData("http://127.0.0.1:8384/ui?token=secret#/login", "http", 8384, "/ui", true)]
    public void TryParse_NormalizesLoopbackAndNeverRetainsQueryOrFragment(
        string input,
        string scheme,
        int port,
        string path,
        bool removed)
    {
        Assert.True(LinuxLocalServiceUrl.TryParse(input, out var parsed, out var error), error);
        Assert.NotNull(parsed);
        Assert.Equal(scheme, parsed.Scheme);
        Assert.Equal(port, parsed.Port);
        Assert.Equal(path, parsed.LaunchPath);
        Assert.Equal(removed, parsed.RemovedQueryOrFragment);
        Assert.DoesNotContain("token", parsed.SanitizedUrl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#", parsed.SanitizedUrl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://0.0.0.0:8384/")]
    [InlineData("http://192.168.1.10:8384/")]
    [InlineData("http://user@127.0.0.1:8384/")]
    [InlineData("ftp://127.0.0.1:21/")]
    [InlineData("not a URL")]
    public void TryParse_RejectsNonLocalOrCredentialBearingAddresses(string input)
    {
        Assert.False(LinuxLocalServiceUrl.TryParse(input, out _, out var error));
        Assert.NotEmpty(error);
    }
}
