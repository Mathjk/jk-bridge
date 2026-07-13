using Avalonia;
using JKBridge.App.Platform;

namespace JKBridge.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var probeId = ReadArgument(args, "--instance-probe-id=");
        var isProbe = probeId is not null;
        var applicationId = isProbe ? $"com.jk.jkbridge.probe.{probeId}" : "com.jk.jkbridge";
        using var instance = SingleInstanceCoordinator.Acquire(applicationId);

        if (isProbe)
        {
            if (!instance.IsPrimary)
            {
                return 23;
            }

            var holdText = ReadArgument(args, "--instance-probe-hold-ms=");
            var holdMilliseconds = int.TryParse(holdText, out var parsed) ? parsed : 1_000;
            Thread.Sleep(Math.Clamp(holdMilliseconds, 1, 10_000));
            return 0;
        }

        if (!instance.IsPrimary)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (instance.NotifyPrimaryAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult())
                {
                    return 0;
                }

                Thread.Sleep(150);
            }

            return 24;
        }

        App.InstanceCoordinator = instance;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static string? ReadArgument(IEnumerable<string> args, string prefix) =>
        args.FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ?[prefix.Length..];
}
