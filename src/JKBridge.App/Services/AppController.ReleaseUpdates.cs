using System.ComponentModel;
using JKBridge.Core.Diagnostics;
using JKBridge.Core.Updates;

namespace JKBridge.App.Services;

public sealed partial class AppController
{
    public void CancelReleaseUpdate()
    {
        var cancellation = Volatile.Read(ref _releaseUpdateCancellation);
        if (cancellation is null)
        {
            return;
        }

        RunOnUi(() =>
        {
            ViewModel.UpdateStageText = "正在取消";
            ViewModel.UpdateProgressText = "正在停止当前网络操作…";
        });
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The operation completed between the read and cancellation request.
        }
    }

    public async Task LaunchDownloadedReleaseInstallerAsync()
    {
        var download = _downloadedReleaseInstaller;
        var installer = _pendingReleaseInstaller;
        if (download is null ||
            installer is null ||
            !string.Equals(download.Version, installer.Version, StringComparison.OrdinalIgnoreCase) ||
            Interlocked.CompareExchange(ref _releaseUpdateActive, 1, 0) != 0)
        {
            return;
        }

        RunOnUi(() =>
        {
            ViewModel.IsUpdateChecking = true;
            ViewModel.IsUpdateProgressVisible = true;
            ViewModel.IsUpdateProgressIndeterminate = true;
            ViewModel.UpdateStageText = "正在启动安装向导";
            ViewModel.UpdateProgressText = "安装向导启动后，JK Bridge 才会安全退出";
            ViewModel.UpdateCheckFeedback =
                "正在打开已校验的 Windows 安装向导；在确认启动成功前，JK Bridge 不会退出。";
        });

        try
        {
            var installerPath = ValidateDownloadedInstallerPath(download.InstallerPath);
            _installerLauncher(installerPath);
            RunOnUi(() =>
            {
                ViewModel.UpdateStageText = "安装向导已启动";
                ViewModel.UpdateProgressText = "正在安全退出 JK Bridge；后续进度请查看安装向导";
                ViewModel.UpdateCheckFeedback =
                    "Windows 安装向导已启动。JK Bridge 正在安全退出，安装进度将在安装向导中显示。";
            });
            await SafeLogAsync(
                    DiagnosticSeverity.Information,
                    "Updates",
                    "A verified official installer was explicitly launched before application exit.",
                    $"Release: {installer.Version}; SHA-256: {download.Sha256}")
                .ConfigureAwait(false);
            await _applicationExitRequester().ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ReleaseUpdateException or IOException or UnauthorizedAccessException or
                InvalidOperationException or Win32Exception)
        {
            await SafeLogAsync(
                    DiagnosticSeverity.Warning,
                    "Updates",
                    "The verified installer could not be launched safely.",
                    exception.GetType().FullName)
                .ConfigureAwait(false);
            RunOnUi(() =>
            {
                ViewModel.IsUpdateProgressIndeterminate = false;
                ViewModel.UpdateStageText = "安装向导启动失败";
                ViewModel.UpdateProgressText = "JK Bridge 仍在运行";
                ViewModel.UpdateCheckFeedback =
                    "无法启动已校验的安装器；JK Bridge 没有退出。可重试，或使用上方官方 Release 地址手动安装。";
            });
        }
        finally
        {
            Interlocked.Exchange(ref _releaseUpdateActive, 0);
            RunOnUi(() => ViewModel.IsUpdateChecking = false);
        }
    }

    private void ReportReleaseDownloadProgress(ReleaseDownloadProgress progress)
    {
        RunOnUi(() =>
        {
            ViewModel.IsUpdateProgressVisible = true;
            switch (progress.Stage)
            {
                case ReleaseDownloadStage.FetchingChecksum:
                    ViewModel.IsUpdateProgressIndeterminate = true;
                    ViewModel.UpdateProgressPercent = 0;
                    ViewModel.UpdateStageText = "正在获取官方校验文件";
                    ViewModel.UpdateProgressText = "正在读取 SHA-256 清单…";
                    break;
                case ReleaseDownloadStage.DownloadingInstaller:
                    ViewModel.UpdateStageText = "正在下载安装包";
                    if (progress.Percentage is { } percentage &&
                        progress.TotalBytes is { } totalBytes)
                    {
                        ViewModel.IsUpdateProgressIndeterminate = false;
                        ViewModel.UpdateProgressPercent = percentage;
                        ViewModel.UpdateProgressText =
                            $"{FormatByteCount(progress.BytesReceived)} / {FormatByteCount(totalBytes)} · {percentage:0}%";
                    }
                    else
                    {
                        ViewModel.IsUpdateProgressIndeterminate = true;
                        ViewModel.UpdateProgressText =
                            $"{FormatByteCount(progress.BytesReceived)} 已下载";
                    }
                    break;
                case ReleaseDownloadStage.VerifyingInstaller:
                    ViewModel.IsUpdateProgressIndeterminate = true;
                    ViewModel.UpdateProgressPercent = 100;
                    ViewModel.UpdateStageText = "正在校验安装包";
                    ViewModel.UpdateProgressText = "下载完成，正在核验 SHA-256…";
                    break;
            }
        });
    }

    private static string FormatByteCount(long bytes)
    {
        if (bytes >= 1024L * 1024L)
        {
            return $"{bytes / (1024d * 1024d):0.0} MB";
        }

        if (bytes >= 1024L)
        {
            return $"{bytes / 1024d:0.0} KB";
        }

        return $"{bytes} B";
    }
}
