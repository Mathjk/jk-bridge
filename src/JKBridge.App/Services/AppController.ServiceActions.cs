using System.Text;
using JKBridge.App.ViewModels;
using JKBridge.App.Views;
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

    private readonly Dictionary<string, DateTimeOffset> _lastSuccessfulRestarts = new(StringComparer.Ordinal);

    /// <summary>
    /// Shows the reusable modal confirmation dialog. Returns false when no owner
    /// window exists, so callers fail safe for destructive actions.
    /// </summary>
    public Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        var owner = _getWindow();
        if (owner is null)
        {
            return Task.FromResult(false);
        }

        var dialog = new ConfirmDialogWindow(new ConfirmDialogViewModel
        {
            Title = request.Title,
            Message = request.Message,
            ConfirmText = request.ConfirmText,
            IsDangerous = request.IsDangerous,
        });
        return dialog.ShowDialog<bool>(owner);
    }

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
            service.RestartLocalPort is >= 1 and <= 65535 ? service.RestartLocalPort : service.LocalPort,
            service.RestartPath).Uri;

        var ownsClient = _restartHttpClient is null;
        var client = _restartHttpClient ?? new HttpClient(new HttpClientHandler { UseProxy = false });
        try
        {
            using var timeout = new CancellationTokenSource(RestartRequestTimeout);
            var accepted = false;
            var unconfirmed = false;
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
                    if (response.IsSuccessStatusCode)
                    {
                        accepted = true;
                    }
                    else
                    {
                        var code = (int)response.StatusCode;
                        rejection = code switch
                        {
                            401 or 403 => "令牌被拒绝，请检查重启令牌是否正确",
                            404 => "Linux 端没有这个重启路径，请检查路径配置",
                            400 or 405 or 415 => "Linux 端不接受这个请求格式",
                            >= 500 => $"Linux 端执行重启时出错（HTTP {code}）",
                            _ => $"HTTP {code}",
                        };
                    }
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    // The service accepted the restart and never finished the response.
                    accepted = true;
                    unconfirmed = true;
                }
                catch (HttpRequestException)
                {
                    // An in-place restart can drop the loopback connection before the
                    // response body is written. Treat a transport failure as "sent",
                    // but flag it as unconfirmed so the notice stays honest.
                    accepted = true;
                    unconfirmed = true;
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
                ShowControllerError("重启被拒绝", rejection ?? "未知原因");
                return;
            }

            if (!unconfirmed)
            {
                lock (_lastSuccessfulRestarts)
                {
                    _lastSuccessfulRestarts[service.Id] = DateTimeOffset.Now;
                }
            }

            await SafeLogAsync(
                    DiagnosticSeverity.Information,
                    "Service",
                    $"Restart requested for {service.Name}.",
                    unconfirmed ? "Delivery could not be confirmed." : null)
                .ConfigureAwait(false);
            RunOnUi(() => ViewModel.ShowNotice(
                unconfirmed ? "重启指令已发送（未能确认送达）" : "重启指令已发送",
                unconfirmed
                    ? $"{service.Name} 的连接在重启过程中中断，指令可能已送达。请稍后核对服务状态；若未生效请重试一次。"
                    : $"{service.Name} 正在重启；稍等片刻会自动刷新健康状态。"));

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
