using System.Text;
using JKBridge.Core.Diagnostics;
using JKBridge.Core.Models;
using JKBridge.Core.Routing;

namespace JKBridge.App.Services;

/// <summary>
/// Loopback-only service actions sent through the existing SSH tunnel.
/// The Linux service owns the restart itself; JK Bridge only POSTs to the
/// configured path on the Windows-local tunnel port and refreshes health.
/// </summary>
public sealed partial class AppController
{
    private const string RestartTokenHeader = "X-JKBridge-Restart-Token";

    private static readonly TimeSpan RestartRequestTimeout = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan RestartSettleDelay = TimeSpan.FromSeconds(3);

    public async Task RestartServiceAsync(string serviceId)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var service = _config.Services.FirstOrDefault(item => item.Id == serviceId);
        if (service is null || !service.HasRestartAction)
        {
            return;
        }

        var route = ServiceRouteDefinition.Evaluate(service).Route;
        if (route is null)
        {
            ShowControllerError("无法重启服务", "配置中的浏览器地址不是有效的 127.0.0.1 地址。");
            return;
        }

        var snapshot = _runtime.Snapshot.Services.FirstOrDefault(item => item.ServiceId == serviceId);
        if (!_runtime.Snapshot.IsConnected || !service.Enabled || snapshot is null || !snapshot.CanOpen)
        {
            ShowControllerError("服务尚不可重启", "请先建立 SSH 隧道并确认该服务已启用。");
            return;
        }

        var restartUri = new UriBuilder(
            route.OpenUri.Scheme,
            ServiceDefinition.LoopbackHost,
            service.LocalPort,
            service.RestartPath).Uri;

        var ownsClient = _restartHttpClient is null;
        var client = _restartHttpClient ?? new HttpClient(new HttpClientHandler { UseProxy = false });
        try
        {
            using var timeout = new CancellationTokenSource(RestartRequestTimeout);
            var accepted = false;
            string? rejection = null;
            using (var request = new HttpRequestMessage(HttpMethod.Post, restartUri)
            {
                // The Linux API requires an explicit JSON body on POST.
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            })
            {
                if (!string.IsNullOrWhiteSpace(service.RestartToken))
                {
                    request.Headers.Add(RestartTokenHeader, service.RestartToken.Trim());
                }

                try
                {
                    using var response = await client
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                        .ConfigureAwait(false);
                    accepted = response.IsSuccessStatusCode;
                    if (!accepted)
                    {
                        rejection = $"HTTP {(int)response.StatusCode}";
                    }
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    // The service accepted the restart and never finished the response.
                    accepted = true;
                }
                catch (HttpRequestException)
                {
                    // An in-place restart can drop the loopback connection before the
                    // response body is written. Treat a transport failure as "sent".
                    accepted = true;
                }
            }

            if (!accepted)
            {
                await SafeLogAsync(
                        DiagnosticSeverity.Warning,
                        "Service",
                        $"Restart rejected for {service.Name}.",
                        rejection)
                    .ConfigureAwait(false);
                ShowControllerError(
                    "重启被拒绝",
                    $"Linux 服务没有接受重启请求（{rejection ?? "未知原因"}）。请检查重启路径和令牌是否正确。");
                return;
            }

            await SafeLogAsync(
                    DiagnosticSeverity.Information,
                    "Service",
                    $"Restart requested for {service.Name}.")
                .ConfigureAwait(false);
            RunOnUi(() => ViewModel.ShowNotice(
                "重启指令已发送",
                $"{service.Name} 正在重启；稍等片刻会自动刷新健康状态。"));

            await Task.Delay(RestartSettleDelay).ConfigureAwait(false);
            await ProbeServiceAsync(service).ConfigureAwait(false);
        }
        finally
        {
            if (ownsClient)
            {
                client.Dispose();
            }
        }
    }
}
