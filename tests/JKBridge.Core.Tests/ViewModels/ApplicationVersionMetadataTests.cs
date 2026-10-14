using System.Reflection;
using JKBridge.App.Services;

namespace JKBridge.Core.Tests.ViewModels;

public sealed class ApplicationVersionMetadataTests
{
    [Fact]
    public void FileVersionAndInformationalVersionAgree()
    {
        var assembly = typeof(AppController).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? string.Empty;
        var fileVersion = assembly
            .GetCustomAttribute<AssemblyFileVersionAttribute>()?
            .Version ?? string.Empty;

        // InformationalVersion carries the git hash after '+' when built by CI.
        var semantic = informational.Split('+', 2)[0];

        Assert.True(Version.TryParse(semantic, out var semanticVersion), $"'{semantic}' is not a version.");
        Assert.True(Version.TryParse(fileVersion, out var fileParsed), $"'{fileVersion}' is not a version.");
        Assert.Equal(semanticVersion!.Major, fileParsed!.Major);
        Assert.Equal(semanticVersion.Minor, fileParsed.Minor);
        Assert.Equal(semanticVersion.Build, fileParsed.Build);
    }
}
