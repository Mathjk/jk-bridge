using JKBridge.App.ViewModels;
using JKBridge.Core.Models;

namespace JKBridge.App.Services;

public sealed partial class AppController
{
    private void RefreshWindowsLocalPresentation(bool preserveDrafts = false)
    {
        var states = _config.WindowsLocalServices
            .Select(ToWindowsLocalServiceUiState)
            .ToArray();
        if (preserveDrafts)
        {
            ViewModel.UpdateWindowsLocalServices(states);
        }
        else
        {
            ViewModel.SetWindowsLocalServices(states);
        }
    }

    private DiscoveredWindowsLocalServiceUiState ToDiscoveredWindowsLocalServiceUiState(
        DiscoveredWindowsLocalService service)
    {
        var protocol = service.Protocol switch
        {
            LocalEndpointProtocol.Http => "http",
            LocalEndpointProtocol.Https => "https",
            _ => "tcp",
        };
        var status = service.IsExistingTunnel
            ? "现有 Linux 隧道"
            : service.IsWeb
                ? "已确认网页"
                : "仅 TCP";
        var detail = service.CertificateUnverified
            ? $"{service.Detail} HTTPS 使用了未受信任的本机证书。"
            : service.Detail;
        return new(
            service.Key,
            service.Host,
            service.Port,
            service.ProcessName,
            protocol,
            service.OpenUrl,
            status,
            detail,
            service.IsWeb,
            service.CanSave,
            service.IsExistingTunnel,
            service.CertificateUnverified);
    }

    private WindowsLocalServiceUiState ToWindowsLocalServiceUiState(
        WindowsLocalServiceDefinition service)
    {
        var current = _lastWindowsLocalDiscovery.FirstOrDefault(result =>
            result.Port == service.Port &&
            string.Equals(result.Host, service.Host, StringComparison.Ordinal));
        var processName = string.IsNullOrWhiteSpace(current?.ProcessName)
            ? service.ProcessNameHint
            : current.ProcessName;
        if (!service.Enabled)
        {
            return CreateWindowsLocalUiState(
                service,
                processName,
                "已停用",
                "不会参与扫描状态判断；对应的 Windows 程序不会被停止。",
                isAvailable: false,
                hasWarning: false,
                canOpen: false);
        }

        if (current is null)
        {
            return CreateWindowsLocalUiState(
                service,
                processName,
                _hasCompletedWindowsLocalDiscovery ? "离线" : "尚未扫描",
                _hasCompletedWindowsLocalDiscovery
                    ? "当前没有发现对应的回环监听。"
                    : "点击“扫描本机”检查当前回环监听。",
                isAvailable: false,
                hasWarning: _hasCompletedWindowsLocalDiscovery,
                canOpen: false);
        }

        if (current.IsExistingTunnel)
        {
            return CreateWindowsLocalUiState(
                service,
                processName,
                "端口冲突",
                "该端口目前属于已配置的 Linux 隧道，不能作为 Windows 本机服务使用。",
                isAvailable: false,
                hasWarning: true,
                canOpen: false);
        }

        var processChanged =
            !string.IsNullOrWhiteSpace(service.ProcessNameHint) &&
            !string.IsNullOrWhiteSpace(current.ProcessName) &&
            !string.Equals(
                service.ProcessNameHint,
                current.ProcessName,
                StringComparison.OrdinalIgnoreCase);
        var available = current.IsWeb || service.HealthKind == HealthCheckKind.TcpConnect;
        var status = processChanged
            ? "进程已变化"
            : available
                ? "可用"
                : "有监听，未确认网页";
        var detail = processChanged
            ? $"保存时为 {service.ProcessNameHint}，当前为 {current.ProcessName}。"
            : current.CertificateUnverified
                ? $"{current.Detail} 本机 HTTPS 证书未受信任。"
                : current.Detail;
        return CreateWindowsLocalUiState(
            service,
            processName,
            status,
            detail,
            available,
            processChanged || !available || current.CertificateUnverified,
            canOpen: true);
    }

    private static WindowsLocalServiceUiState CreateWindowsLocalUiState(
        WindowsLocalServiceDefinition service,
        string processName,
        string status,
        string detail,
        bool isAvailable,
        bool hasWarning,
        bool canOpen) =>
        new(
            service.Id,
            service.Name,
            service.Description,
            service.Host,
            service.Port,
            service.Scheme,
            service.LaunchPath,
            processName,
            status,
            detail,
            service.Enabled,
            isAvailable,
            hasWarning,
            canOpen,
            service.HealthKind);
}
