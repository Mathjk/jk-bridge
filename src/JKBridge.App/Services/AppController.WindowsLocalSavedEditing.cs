using JKBridge.App.ViewModels;
using JKBridge.Core.Models;

namespace JKBridge.App.Services;

public sealed partial class AppController
{
    public async Task SaveWindowsLocalServiceAsync(WindowsLocalServiceUiState service)
    {
        ArgumentNullException.ThrowIfNull(service);
        var existing = _config.WindowsLocalServices.FirstOrDefault(item => item.Id == service.Id);
        if (existing is null)
        {
            ShowControllerError("本机服务不存在", "请刷新页面后重试。");
            return;
        }

        var processHint = string.IsNullOrWhiteSpace(service.ProcessName)
            ? existing.ProcessNameHint
            : SanitizeProcessName(service.ProcessName);
        var updated = existing with
        {
            Name = service.Name,
            Description = service.Description,
            Host = service.Host,
            Port = service.Port,
            Scheme = service.Scheme,
            LaunchPath = service.LaunchPath,
            ProcessNameHint = processHint,
            HealthKind = service.HealthKind,
            Enabled = service.IsEnabled,
        };
        var saved = await SaveConfigAsync(
                config => config with
                {
                    WindowsLocalServices = config.WindowsLocalServices
                        .Select(item => item.Id == service.Id ? updated : item)
                        .ToList(),
                },
                $"已保存本机服务“{updated.Name}”。",
                refreshRuntime: false)
            .ConfigureAwait(false);
        if (saved)
        {
            RunOnUi(() => RefreshWindowsLocalPresentation());
        }
    }

    public async Task RemoveWindowsLocalServiceAsync(string serviceId)
    {
        var existing = _config.WindowsLocalServices.FirstOrDefault(service => service.Id == serviceId);
        if (existing is null)
        {
            return;
        }

        var removed = await SaveConfigAsync(
                config => config with
                {
                    WindowsLocalServices = config.WindowsLocalServices
                        .Where(service => service.Id != serviceId)
                        .ToList(),
                },
                $"已移除本机服务“{existing.Name}”；没有停止对应的 Windows 程序。",
                refreshRuntime: false)
            .ConfigureAwait(false);
        if (removed)
        {
            RunOnUi(() => RefreshWindowsLocalPresentation());
        }
    }
}
