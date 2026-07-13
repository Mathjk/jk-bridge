using JKBridge.Core.Diagnostics;

namespace JKBridge.Core.Tests;

public sealed class DiagnosticsTests
{
    [Fact]
    public void Redactor_ReplacesUserProfileAndNewLines()
    {
        var actual = Redactor.Sanitize(
            "C:\\Users\\Alice\\.ssh\\key\r\nsecret detail",
            "C:\\Users\\Alice");

        Assert.Equal("%USERPROFILE%\\.ssh\\key  secret detail", actual);
    }

    [Fact]
    public async Task RollingLogger_RetainsOnlyConfiguredMemoryCount()
    {
        var directory = Path.Combine(Path.GetTempPath(), "jkbridge-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var logger = new RollingFileLogger(directory, memoryCapacity: 2);
            await logger.WriteAsync(DiagnosticSeverity.Information, "Test", "one");
            await logger.WriteAsync(DiagnosticSeverity.Warning, "Test", "two");
            await logger.WriteAsync(DiagnosticSeverity.Error, "Test", "three");

            var snapshot = logger.Snapshot();
            Assert.Collection(
                snapshot,
                item => Assert.Equal("two", item.Message),
                item => Assert.Equal("three", item.Message));
            Assert.Single(Directory.EnumerateFiles(directory, "*.jsonl"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
