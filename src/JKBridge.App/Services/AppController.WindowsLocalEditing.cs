using JKBridge.App.ViewModels;
using JKBridge.Core.Models;
using JKBridge.Core.Routing;

namespace JKBridge.App.Services;

public sealed partial class AppController
{
    public async Task AddWindowsLocalServiceAsync()
    {
        var usedPorts = _config.Services.Select(service => service.LocalPort)
            .Concat(_config.WindowsLocalServices.Select(service => service.Port))
            .Concat(_lastWindowsLocalDiscovery.Select(service => service.Port))
            .ToHashSet();
        var port = Enumerable.Range(8800, 1000)
            .FirstOrDefault(candidate => !usedPorts.Contains(candidate));
        if (port == 0)
        {
            ShowControllerError("无法添加本机服务", "8800–9799 之间没有可用的建议端口。");
            return;
        }

        var id = NextWindowsLocalServiceId(_config.WindowsLocalServices);
        var added = await SaveConfigAsync(
                config => config with
                {
                    WindowsLocalServices =
                    [
                        .. config.WindowsLocalServices,
                        new WindowsLocalServiceDefinition
                        {
                            Id = id,
                            Name = "新的本机网页",
                            Description = "仅在这台 Windows 电脑上访问",
                            Enabled = false,
                            Port = port,
                        },
                    ],
                },
                "已添加本机服务草稿。",
                refreshRuntime: false)
            .ConfigureAwait(false);
        if (added)
        {
            RunOnUi(() => ViewModel.IsLinuxServicesMode = false);
        }
    }

    public async Task SaveDiscoveredWindowsLocalServiceAsync(
        DiscoveredWindowsLocalServiceUiState service)
    {
        ArgumentNullException.ThrowIfNull(service);
        var current = _lastWindowsLocalDiscovery.FirstOrDefault(result =>
            result.Key == service.Key &&
            result.CanSave &&
            string.Equals(result.OpenUrl, service.OpenUrl, StringComparison.Ordinal));
        if (current is null ||
            !WindowsLocalServiceUrl.TryParse(
                current.OpenUrl,
                current.Host,
                current.Port,
                out var uri) ||
            uri is null)
        {
            ShowControllerError("发现结果已变化", "请重新扫描后再保存该本机服务。");
            return;
        }

        var duplicate = _config.WindowsLocalServices.Any(existing =>
            existing.Port == current.Port &&
            string.Equals(existing.Host, current.Host, StringComparison.Ordinal) &&
            string.Equals(existing.Scheme, uri.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.LaunchPath, uri.AbsolutePath, StringComparison.Ordinal));
        if (duplicate)
        {
            RunOnUi(() => ViewModel.WindowsLocalScanFeedback = "该地址已经保存在本机服务中。");
            return;
        }

        var name = string.IsNullOrWhiteSpace(current.ProcessName)
            ? $"本机网页 {current.Port}"
            : current.ProcessName.Trim();
        if (name.Length > 80)
        {
            name = name[..80];
        }

        var definition = new WindowsLocalServiceDefinition
        {
            Id = NextWindowsLocalServiceId(_config.WindowsLocalServices),
            Name = name,
            Description = "由本机回环监听扫描发现",
            Enabled = true,
            Host = current.Host,
            Port = current.Port,
            Scheme = uri.Scheme,
            LaunchPath = uri.AbsolutePath,
            ProcessNameHint = SanitizeProcessName(current.ProcessName),
            HealthKind = current.CertificateUnverified
                ? HealthCheckKind.TcpConnect
                : HealthCheckKind.HttpStatus,
        };
        var added = await SaveConfigAsync(
                config => config with
                {
                    WindowsLocalServices = [.. config.WindowsLocalServices, definition],
                },
                $"已保存本机服务“{definition.Name}”。",
                refreshRuntime: false)
            .ConfigureAwait(false);
        if (added)
        {
            RunOnUi(() =>
            {
                RefreshWindowsLocalPresentation();
                ViewModel.WindowsLocalScanFeedback = $"已保存 {definition.Name}。";
            });
        }
    }

    private static string NextWindowsLocalServiceId(
        IEnumerable<WindowsLocalServiceDefinition> services)
    {
        var ids = services.Select(service => service.Id).ToHashSet(StringComparer.Ordinal);
        for (var suffix = 1; suffix <= 9999; suffix++)
        {
            var candidate = $"windows-local-{suffix}";
            if (!ids.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"windows-local-{Guid.NewGuid():N}";
    }

    private static string SanitizeProcessName(string? value)
    {
        return new string((value ?? string.Empty)
            .Trim()
            .Where(character => !char.IsControl(character))
            .Take(80)
            .ToArray());
    }
}
