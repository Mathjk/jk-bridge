using System.ComponentModel;
using System.Diagnostics;
using JKBridge.Core.Diagnostics;
using JKBridge.Core.Routing;

namespace JKBridge.App.Services;

public sealed partial class AppController
{
    public async Task OpenWindowsLocalServiceAsync(string serviceId)
    {
        var service = _config.WindowsLocalServices.FirstOrDefault(item =>
            item.Id == serviceId && item.Enabled);
        if (service is null)
        {
            return;
        }

        var current = _lastWindowsLocalDiscovery.FirstOrDefault(result =>
            !result.IsExistingTunnel &&
            result.Port == service.Port &&
            string.Equals(result.Host, service.Host, StringComparison.Ordinal));
        if (current is null)
        {
            ShowControllerError(
                "本机服务当前离线",
                "当前扫描结果中没有该回环监听。请启动对应程序，再点击“检查”。");
            return;
        }

        var url = WindowsLocalServiceUrl.Build(
            service.Host,
            service.Scheme,
            service.Port,
            service.LaunchPath);
        await OpenWindowsLocalUriAsync(url, $"本机端口 {service.Port}").ConfigureAwait(false);
    }

    private async Task OpenWindowsLocalUriAsync(string url, string diagnosticLabel)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var candidate) ||
            !WindowsLocalServiceUrl.TryParse(
                url,
                candidate.Host.Trim('[', ']'),
                candidate.Port,
                out var validated) ||
            validated is null)
        {
            ShowControllerError("无法打开本机地址", "该地址不是受支持的回环 HTTP(S) 地址。");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = validated.AbsoluteUri,
                UseShellExecute = true,
            });
            await SafeLogAsync(
                    DiagnosticSeverity.Information,
                    "Windows local service",
                    $"Opened {diagnosticLabel}.")
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or
                                          Win32Exception or
                                          PlatformNotSupportedException)
        {
            ShowControllerError("无法打开浏览器", "Windows 未能启动默认浏览器。");
            await SafeLogAsync(
                    DiagnosticSeverity.Warning,
                    "Windows local service",
                    $"Could not open {diagnosticLabel}.",
                    exception.Message)
                .ConfigureAwait(false);
        }
    }
}
