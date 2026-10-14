using System.ComponentModel;
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

    private static readonly TimeSpan LogRequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Service id of the optional Linux control daemon (jkbridge-ctl). Its entry
    /// carries the shared control token and owns the restart/log tunnel port.
    /// </summary>
    private const string ControlServiceId = "jkbridge-ctl";

    private readonly Dictionary<string, DateTimeOffset> _lastSuccessfulRestarts = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _restartTokenCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Synchronous view of the effective restart token for UI state mapping.
    /// Populated by migration, warm-up, and saves.
    /// </summary>
    private string CachedRestartToken(ServiceDefinition service)
    {
        if (!string.IsNullOrWhiteSpace(service.RestartToken))
        {
            return service.RestartToken.Trim();
        }

        if (string.IsNullOrWhiteSpace(service.RestartTokenReference))
        {
            return string.Empty;
        }

        lock (_restartTokenCache)
        {
            return _restartTokenCache.TryGetValue(service.RestartTokenReference, out var token)
                ? token
                : string.Empty;
        }
    }

    /// <summary>
    /// Loads credential-store tokens into the cache so card state can show them.
    /// </summary>
    private async Task WarmRestartTokenCacheAsync(CancellationToken cancellationToken)
    {
        foreach (var service in _config.Services)
        {
            if (string.IsNullOrWhiteSpace(service.RestartTokenReference))
            {
                continue;
            }

            try
            {
                var token = await _restartTokenStore
                    .LoadAsync(service.RestartTokenReference, cancellationToken)
                    .ConfigureAwait(false);
                lock (_restartTokenCache)
                {
                    if (string.IsNullOrEmpty(token))
                    {
                        _restartTokenCache.Remove(service.RestartTokenReference);
                    }
                    else
                    {
                        _restartTokenCache[service.RestartTokenReference] = token;
                    }
                }
            }
            catch (Exception exception) when (exception is
                InvalidOperationException or PlatformNotSupportedException or Win32Exception)
            {
                // Leave the cache empty; the restart action reports the failure.
            }
        }
    }

    /// <summary>
    /// Opens the Linux service log viewer. Logs travel through the control
    /// daemon's tunnel as bounded, read-only text.
    /// </summary>
    public async Task OpenServiceLogAsync(string serviceId)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var service = _config.Services.FirstOrDefault(item => item.Id == serviceId);
        var control = _config.Services.FirstOrDefault(item => item.Id == ControlServiceId);
        if (service is null || control is null || !control.Enabled)
        {
            ShowControllerError(
                "\u65e0\u6cd5\u67e5\u770b\u65e5\u5fd7",
                "\u672a\u914d\u7f6e\u201c\u91cd\u542f\u63a7\u5236\u201d\u670d\u52a1\u3002\u8bf7\u5728\u670d\u52a1\u9875\u9762\u6dfb\u52a0 id \u4e3a jkbridge-ctl \u7684\u670d\u52a1"
                + "\uff08Windows \u4e0e Linux \u7aef\u53e3\u5747\u4e3a 8890\uff09\uff0c\u5e76\u5728 Linux \u4e0a\u5b89\u88c5 jkbridge-ctl \u5b88\u62a4\u8fdb\u7a0b\u3002");
            return;
        }

        var route = ServiceRouteDefinition.Evaluate(control).Route;
        var snapshot = _runtime.Snapshot.Services.FirstOrDefault(item => item.ServiceId == control.Id);
        if (route is null || !_runtime.Snapshot.IsConnected || snapshot is null || !snapshot.CanOpen)
        {
            ShowControllerError(
                "\u65e0\u6cd5\u67e5\u770b\u65e5\u5fd7",
                "\u91cd\u542f\u63a7\u5236\u670d\u52a1\u7684\u96a7\u9053\u5c1a\u672a\u5c31\u7ee3\uff0c\u8bf7\u5148\u5efa\u7acb\u8fde\u63a5\u3002");
            return;
        }

        var owner = _getWindow();
        if (owner is null)
        {
            return;
        }

        var logPath = $"/api/log/{service.Id}";
        var viewModel = new LogWindowViewModel(lines => FetchServiceLogAsync(control, logPath, lines))
        {
            Title = $"Linux \u65e5\u5fd7 \u00b7 {service.Name}",
        };
        var window = new LogWindow(viewModel);
        window.Show(owner);
        await viewModel.RefreshCommand.ExecuteAsync(null).ConfigureAwait(false);
    }

    private async Task<string> FetchServiceLogAsync(
        ServiceDefinition control,
        string logPath,
        int lines)
    {
        var route = ServiceRouteDefinition.Evaluate(control).Route
            ?? throw new InvalidOperationException("\u91cd\u542f\u63a7\u5236\u670d\u52a1\u7684\u5730\u5740\u65e0\u6548\u3002");
        var token = await ResolveRestartTokenAsync(control, CancellationToken.None).ConfigureAwait(false);
        var uri = new UriBuilder(route.OpenUri.Scheme, ServiceDefinition.LoopbackHost, control.LocalPort, logPath)
        {
            Query = $"lines={Math.Clamp(lines, 1, 500)}",
        }.Uri;

        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Add(RestartTokenHeader, token);
        }

        using var timeout = new CancellationTokenSource(LogRequestTimeout);
        using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode} {body}".Trim());
        }

        return body;
    }

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
        var token = await ResolveRestartTokenAsync(service, CancellationToken.None).ConfigureAwait(false);
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
                if (!string.IsNullOrWhiteSpace(token))
                {
                    request.Headers.Add(RestartTokenHeader, token);
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
