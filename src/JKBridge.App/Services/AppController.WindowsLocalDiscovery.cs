using JKBridge.App.ViewModels;
using JKBridge.Core.Diagnostics;

namespace JKBridge.App.Services;

public sealed partial class AppController
{
    private readonly IWindowsLocalServiceDiscovery _windowsLocalDiscovery;
    private readonly bool _ownsWindowsLocalDiscovery;
    private CancellationTokenSource? _windowsLocalDiscoveryCancellation;
    private IReadOnlyList<DiscoveredWindowsLocalService> _lastWindowsLocalDiscovery =
        Array.Empty<DiscoveredWindowsLocalService>();
    private bool _hasCompletedWindowsLocalDiscovery;

    public async Task ScanWindowsLocalServicesAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var previous = Interlocked.Exchange(
            ref _windowsLocalDiscoveryCancellation,
            cancellation);
        previous?.Cancel();
        previous?.Dispose();

        try
        {
            var tunnelPorts = _config.Services
                .Where(service => service.Enabled)
                .Select(service => service.LocalPort)
                .ToHashSet();
            var results = await _windowsLocalDiscovery
                .DiscoverAsync(tunnelPorts, cancellation.Token)
                .ConfigureAwait(false);
            _lastWindowsLocalDiscovery = results;
            _hasCompletedWindowsLocalDiscovery = true;

            var confirmedWeb = results.Count(result => result.IsWeb && !result.IsExistingTunnel);
            var tcpOnly = results.Count(result => !result.IsWeb && !result.IsExistingTunnel);
            var tunnels = results.Count(result => result.IsExistingTunnel);
            RunOnUi(() =>
            {
                ViewModel.SetDiscoveredWindowsLocalServices(
                    results.Select(ToDiscoveredWindowsLocalServiceUiState));
                RefreshWindowsLocalPresentation(preserveDrafts: true);
                ViewModel.WindowsLocalSummaryText =
                    $"{confirmedWeb} 个网页 · {tcpOnly} 个其他 TCP · {tunnels} 个现有隧道";
                ViewModel.WindowsLocalScanFeedback =
                    $"扫描完成：只检查了 {results.Count} 个已监听的回环端口；没有扫描磁盘或其他端口。";
            });
            await SafeLogAsync(
                    DiagnosticSeverity.Information,
                    "Windows local discovery",
                    $"Loopback discovery completed: {confirmedWeb} web, {tcpOnly} TCP, {tunnels} tunnel endpoints.")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            RunOnUi(() =>
            {
                ViewModel.WindowsLocalScanFeedback =
                    "扫描已取消或达到 8 秒上限；已保存的本机服务没有改变。";
                ViewModel.WindowsLocalSummaryText = "扫描未完成";
            });
            await SafeLogAsync(
                    DiagnosticSeverity.Information,
                    "Windows local discovery",
                    "Loopback discovery was cancelled or timed out.")
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is System.Net.NetworkInformation.NetworkInformationException or
                                          System.Net.Sockets.SocketException or
                                          InvalidOperationException or
                                          UnauthorizedAccessException)
        {
            RunOnUi(() =>
            {
                ViewModel.WindowsLocalScanFeedback =
                    "Windows 暂时无法读取或识别本机监听；没有修改任何服务。";
                ViewModel.WindowsLocalSummaryText = "扫描失败";
            });
            await SafeLogAsync(
                    DiagnosticSeverity.Warning,
                    "Windows local discovery",
                    "Loopback discovery failed.",
                    exception.Message)
                .ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _windowsLocalDiscoveryCancellation,
                        null,
                        cancellation),
                    cancellation))
            {
                cancellation.Dispose();
            }
        }
    }

    public void CancelWindowsLocalServiceScan()
    {
        Volatile.Read(ref _windowsLocalDiscoveryCancellation)?.Cancel();
        RunOnUi(() => ViewModel.WindowsLocalScanFeedback = "正在取消本机扫描…");
    }

    public Task RetryWindowsLocalServiceAsync(string serviceId)
    {
        if (!_config.WindowsLocalServices.Any(service => service.Id == serviceId))
        {
            return Task.CompletedTask;
        }

        return ScanWindowsLocalServicesAsync();
    }

    public async Task OpenDiscoveredWindowsLocalServiceAsync(
        DiscoveredWindowsLocalServiceUiState service)
    {
        var current = _lastWindowsLocalDiscovery.FirstOrDefault(result =>
            result.Key == service.Key &&
            result.CanSave &&
            string.Equals(result.OpenUrl, service.OpenUrl, StringComparison.Ordinal));
        if (current is null)
        {
            ShowControllerError(
                "发现结果已变化",
                "请重新扫描后再打开；JK Bridge 不会打开不在当前发现结果中的地址。");
            return;
        }

        await OpenWindowsLocalUriAsync(current.OpenUrl, $"本机端口 {current.Port}")
            .ConfigureAwait(false);
    }
}
