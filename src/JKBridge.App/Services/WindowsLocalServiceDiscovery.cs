using System.Collections.Concurrent;
using JKBridge.Core.Routing;

namespace JKBridge.App.Services;

public enum LocalEndpointProtocol
{
    Tcp,
    Http,
    Https,
}

public sealed record LocalTcpListener(
    string Host,
    int Port,
    int? ProcessId = null,
    string ProcessName = "");

public sealed record LocalWebProbeResult(
    LocalEndpointProtocol Protocol,
    int? HttpStatus = null,
    string SuggestedPath = "/",
    bool CertificateUnverified = false,
    string Detail = "")
{
    public bool IsWeb => Protocol is LocalEndpointProtocol.Http or LocalEndpointProtocol.Https;

    public static LocalWebProbeResult TcpOnly(string detail = "端口正在监听，但未确认是网页服务。") =>
        new(LocalEndpointProtocol.Tcp, Detail: detail);
}

public sealed record DiscoveredWindowsLocalService(
    string Key,
    string Host,
    int Port,
    int? ProcessId,
    string ProcessName,
    LocalEndpointProtocol Protocol,
    string OpenUrl,
    int? HttpStatus,
    bool CertificateUnverified,
    bool IsExistingTunnel,
    string Detail)
{
    public bool IsWeb => Protocol is LocalEndpointProtocol.Http or LocalEndpointProtocol.Https;

    public bool CanSave => IsWeb && !IsExistingTunnel;
}

public sealed record WindowsLocalDiscoveryOptions
{
    public int MaximumCandidates { get; init; } = 256;

    public int MaximumConcurrency { get; init; } = 8;
}

public interface ILocalTcpListenerSource
{
    Task<IReadOnlyList<LocalTcpListener>> GetListenersAsync(
        CancellationToken cancellationToken = default);
}

public interface ILocalWebProbe
{
    Task<LocalWebProbeResult> ProbeAsync(
        LocalTcpListener listener,
        CancellationToken cancellationToken = default);
}

public interface IWindowsLocalServiceDiscovery
{
    Task<IReadOnlyList<DiscoveredWindowsLocalService>> DiscoverAsync(
        IReadOnlySet<int> existingTunnelPorts,
        CancellationToken cancellationToken = default);
}

public sealed class WindowsLocalServiceDiscoveryService : IWindowsLocalServiceDiscovery, IDisposable
{
    private readonly ILocalTcpListenerSource _listenerSource;
    private readonly ILocalWebProbe _webProbe;
    private readonly WindowsLocalDiscoveryOptions _options;

    public WindowsLocalServiceDiscoveryService(
        ILocalTcpListenerSource listenerSource,
        ILocalWebProbe webProbe,
        WindowsLocalDiscoveryOptions? options = null)
    {
        _listenerSource = listenerSource ?? throw new ArgumentNullException(nameof(listenerSource));
        _webProbe = webProbe ?? throw new ArgumentNullException(nameof(webProbe));
        _options = options ?? new WindowsLocalDiscoveryOptions();
        if (_options.MaximumCandidates is < 1 or > 512)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Maximum candidates must be between 1 and 512.");
        }

        if (_options.MaximumConcurrency is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Maximum concurrency must be between 1 and 32.");
        }
    }

    public async Task<IReadOnlyList<DiscoveredWindowsLocalService>> DiscoverAsync(
        IReadOnlySet<int> existingTunnelPorts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(existingTunnelPorts);
        var raw = await _listenerSource.GetListenersAsync(cancellationToken).ConfigureAwait(false);
        var listeners = raw
            .Where(listener =>
                WindowsLocalServiceUrl.IsLoopbackHost(listener.Host) &&
                listener.Port is >= 1 and <= 65535)
            .GroupBy(listener => (listener.Host, listener.Port))
            .Select(group => group
                .OrderByDescending(listener => !string.IsNullOrWhiteSpace(listener.ProcessName))
                .ThenByDescending(listener => listener.ProcessId.HasValue)
                .First())
            .OrderBy(listener => listener.Port)
            .ThenBy(listener => listener.Host, StringComparer.Ordinal)
            .Take(_options.MaximumCandidates)
            .ToArray();

        var results = new ConcurrentBag<DiscoveredWindowsLocalService>();
        await Parallel.ForEachAsync(
            listeners,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = _options.MaximumConcurrency,
            },
            async (listener, token) =>
            {
                if (existingTunnelPorts.Contains(listener.Port))
                {
                    results.Add(CreateResult(
                        listener,
                        LocalWebProbeResult.TcpOnly("该端口属于已配置的 JK Bridge Linux 隧道。"),
                        isExistingTunnel: true));
                    return;
                }

                LocalWebProbeResult probe;
                try
                {
                    probe = await _webProbe.ProbeAsync(listener, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is IOException or
                                                  System.Net.Sockets.SocketException or
                                                  System.Net.Http.HttpRequestException)
                {
                    probe = LocalWebProbeResult.TcpOnly("网页识别失败；端口仍在监听。");
                }

                results.Add(CreateResult(listener, probe, isExistingTunnel: false));
            }).ConfigureAwait(false);

        return results
            .OrderBy(result => result.IsExistingTunnel)
            .ThenByDescending(result => result.IsWeb)
            .ThenBy(result => result.Port)
            .ThenBy(result => result.Host, StringComparer.Ordinal)
            .ToArray();
    }

    public void Dispose()
    {
        if (_webProbe is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static DiscoveredWindowsLocalService CreateResult(
        LocalTcpListener listener,
        LocalWebProbeResult probe,
        bool isExistingTunnel)
    {
        var scheme = probe.Protocol == LocalEndpointProtocol.Https ? "https" : "http";
        var openUrl = probe.IsWeb
            ? WindowsLocalServiceUrl.Build(listener.Host, scheme, listener.Port, probe.SuggestedPath)
            : string.Empty;
        return new(
            $"{listener.Host}:{listener.Port}:{listener.ProcessId?.ToString() ?? "unknown"}",
            listener.Host,
            listener.Port,
            listener.ProcessId,
            listener.ProcessName,
            probe.Protocol,
            openUrl,
            probe.HttpStatus,
            probe.CertificateUnverified,
            isExistingTunnel,
            probe.Detail);
    }
}
