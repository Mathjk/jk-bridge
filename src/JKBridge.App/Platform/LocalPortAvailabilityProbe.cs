using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace JKBridge.App.Platform;

/// <summary>
/// Reads the operating system's active TCP listeners without opening, reserving,
/// or changing a port. The real SSH bind remains the final authority because a
/// different process can always claim a port after this advisory check.
/// </summary>
public interface ILocalPortAvailabilityProbe
{
    LocalPortAvailabilityResult Check(int port);
}

public sealed record LocalPortAvailabilityResult(
    LocalPortAvailabilityStatus Status,
    int? SuggestedPort = null,
    string? Detail = null);

public enum LocalPortAvailabilityStatus
{
    Available,
    InUse,
    Unknown,
}

public sealed class SystemLocalPortAvailabilityProbe : ILocalPortAvailabilityProbe
{
    private const int FirstRecommendedPort = 1024;
    private const int LastPort = 65535;

    public static SystemLocalPortAvailabilityProbe Instance { get; } = new();

    private SystemLocalPortAvailabilityProbe()
    {
    }

    public LocalPortAvailabilityResult Check(int port)
    {
        if (port is < 1 or > LastPort)
        {
            return new(
                LocalPortAvailabilityStatus.Unknown,
                Detail: "端口必须是 1–65535 的整数。");
        }

        try
        {
            var activePorts = IPGlobalProperties
                .GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Select(endpoint => endpoint.Port)
                .ToHashSet();

            if (!activePorts.Contains(port))
            {
                return new(LocalPortAvailabilityStatus.Available);
            }

            return new(
                LocalPortAvailabilityStatus.InUse,
                FindSuggestedPort(port, activePorts));
        }
        catch (Exception exception) when (exception is NetworkInformationException or
                                          SocketException or
                                          PlatformNotSupportedException or
                                          UnauthorizedAccessException)
        {
            return new(
                LocalPortAvailabilityStatus.Unknown,
                Detail: "Windows 暂时无法读取本地 TCP 监听状态。");
        }
    }

    private static int? FindSuggestedPort(int preferredPort, IReadOnlySet<int> activePorts)
    {
        var rangeSize = LastPort - FirstRecommendedPort + 1;
        var start = preferredPort is >= FirstRecommendedPort and <= LastPort
            ? preferredPort
            : FirstRecommendedPort - 1;

        for (var offset = 1; offset <= rangeSize; offset++)
        {
            var candidate = FirstRecommendedPort + (start - FirstRecommendedPort + offset) % rangeSize;
            if (!activePorts.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
