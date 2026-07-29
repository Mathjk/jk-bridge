using JKBridge.App.Platform;
using JKBridge.App.Services;
using JKBridge.Core.Configuration;
using JKBridge.Core.Diagnostics;
using JKBridge.Core.Models;
using JKBridge.Core.Pairing;
using JKBridge.Core.Tunneling;
using JKBridge.Core.Updates;

namespace JKBridge.Core.Tests.ViewModels;

public sealed class AppControllerSupportActionsTests
{
    [Fact]
    public async Task SupportActions_AreManualAndDiagnosticExportUsesSafeService()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jkbridge-support-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var configStore = new ConfigStore(Path.Combine(directory, "config.json"));
        var config = TestConfigs.CreateConfigured() with
        {
            Behavior = TestConfigs.CreateConfigured().Behavior with { AutoConnect = false },
        };
        await configStore.SaveAsync(config);
        var picker = new RecordingDiagnosticPathPicker(
            Path.Combine(directory, "jkbridge-diagnostics.zip"));
        var storage = new RecordingDiagnosticStorage();
        var updateChecker = new RecordingUpdateChecker();
        var controller = new AppController(
            configStore,
            new RollingFileLogger(Path.Combine(directory, "logs")),
            new FakeRuntime(),
            new FakePairingService(),
            new StartupRegistration($"JK Bridge Test {Guid.NewGuid():N}"),
            Path.Combine(directory, "logs"),
            () => null,
            dispatchUi: action => action(),
            diagnosticBundleService: new DiagnosticBundleService(picker, storage),
            releaseUpdateChecker: updateChecker);

        try
        {
            await controller.InitializeAsync();

            Assert.Equal(0, picker.Calls);
            Assert.Equal(0, updateChecker.Calls);

            await controller.ExportDiagnosticBundleAsync();

            Assert.Equal(1, picker.Calls);
            Assert.Equal(1, storage.Calls);
            Assert.NotEmpty(storage.Content);
            Assert.Contains("已导出安全诊断包", controller.ViewModel.DiagnosticBundleFeedback);
            Assert.False(controller.ViewModel.IsDiagnosticBundleBusy);

            await controller.CheckForUpdatesAsync();

            Assert.Equal(1, updateChecker.Calls);
            Assert.Equal("2.2.0-preview.2", updateChecker.CurrentVersion);
            Assert.True(controller.ViewModel.HasUpdateReleaseInfo);
            Assert.Equal(
                "https://github.com/Mathjk/jk-bridge/releases/tag/v2.3.0",
                controller.ViewModel.UpdateReleaseUrl);
            Assert.Contains("没有成对的 Windows 安装器和 SHA-256", controller.ViewModel.UpdateCheckFeedback);
            Assert.False(controller.ViewModel.IsUpdateChecking);
        }
        finally
        {
            await controller.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UpdateCheck_WhenCurrentBuildIsAhead_DoesNotCallItLatestStable()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jkbridge-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var configStore = new ConfigStore(Path.Combine(directory, "config.json"));
        var config = TestConfigs.CreateConfigured() with
        {
            Behavior = TestConfigs.CreateConfigured().Behavior with { AutoConnect = false },
        };
        await configStore.SaveAsync(config);
        var updateChecker = new RecordingUpdateChecker(updateAvailable: false, latestVersion: "2.1.0");
        var controller = new AppController(
            configStore,
            new RollingFileLogger(Path.Combine(directory, "logs")),
            new FakeRuntime(),
            new FakePairingService(),
            new StartupRegistration($"JK Bridge Test {Guid.NewGuid():N}"),
            Path.Combine(directory, "logs"),
            () => null,
            dispatchUi: action => action(),
            releaseUpdateChecker: updateChecker);

        try
        {
            await controller.InitializeAsync();
            await controller.CheckForUpdatesAsync();

            Assert.DoesNotContain("已是最新正式版", controller.ViewModel.UpdateCheckFeedback);
            Assert.Contains("可能是未发布开发版", controller.ViewModel.UpdateCheckFeedback);
            Assert.Contains("官方最新正式版：2.1.0", controller.ViewModel.UpdateCheckFeedback);
        }
        finally
        {
            await controller.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReinstallCheckedRelease_DownloadsVerifiesAndLaunchesExactInstallerPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jkbridge-update-install-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var configStore = new ConfigStore(Path.Combine(directory, "config.json"));
        await configStore.SaveAsync(TestConfigs.CreateConfigured() with
        {
            Behavior = TestConfigs.CreateConfigured().Behavior with { AutoConnect = false },
        });
        var installerPath = Path.Combine(
            directory,
            "Updates",
            "verified",
            "JKBridge-Setup-win-x64-2.2.0.exe");
        var updateChecker = new RecordingUpdateChecker(
            updateAvailable: false,
            latestVersion: "2.2.0-preview.2",
            installer: InstallerAsset(),
            downloadedInstallerPath: installerPath);
        string? launchedPath = null;
        var controller = new AppController(
            configStore,
            new RollingFileLogger(Path.Combine(directory, "logs")),
            new FakeRuntime(),
            new FakePairingService(),
            new StartupRegistration($"JK Bridge Test {Guid.NewGuid():N}"),
            Path.Combine(directory, "logs"),
            () => null,
            dispatchUi: action => action(),
            releaseUpdateChecker: updateChecker,
            installerLauncher: path => launchedPath = path);

        try
        {
            await controller.InitializeAsync();
            await controller.CheckForUpdatesAsync();

            Assert.True(controller.ViewModel.CanInstallUpdate);
            Assert.Contains("重新下载", controller.ViewModel.UpdateCheckFeedback);

            await controller.InstallCheckedReleaseAsync();

            Assert.Equal(1, updateChecker.DownloadCalls);
            Assert.Equal(installerPath, launchedPath);
            Assert.Contains("安装器已启动", controller.ViewModel.UpdateCheckFeedback);
        }
        finally
        {
            await controller.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static ReleaseInstallerAsset InstallerAsset()
    {
        const string name = "JKBridge-Setup-win-x64-2.2.0.exe";
        var download = new Uri(
            $"https://github.com/Mathjk/jk-bridge/releases/download/v2.2.0-preview.2/{name}");
        return new(
            "v2.2.0-preview.2",
            "2.2.0-preview.2",
            name,
            download,
            new Uri($"{download}.sha256"));
    }

    private sealed class RecordingDiagnosticPathPicker(string path) : IDiagnosticBundlePathPicker
    {
        public int Calls { get; private set; }

        public Task<string?> PickExportPathAsync(
            string suggestedFileName,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<string?>(path);
        }
    }

    private sealed class RecordingDiagnosticStorage : IDiagnosticBundleStorage
    {
        public int Calls { get; private set; }
        public byte[] Content { get; private set; } = [];

        public Task WriteAtomicallyAsync(
            string destinationPath,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Content = content.ToArray();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingUpdateChecker(
        bool updateAvailable = true,
        string latestVersion = "2.3.0",
        ReleaseInstallerAsset? installer = null,
        string? downloadedInstallerPath = null) : IReleaseUpdateChecker
    {
        public int Calls { get; private set; }
        public int DownloadCalls { get; private set; }
        public string? CurrentVersion { get; private set; }

        public Task<ReleaseUpdateCheckResult> CheckAsync(
            string currentVersion,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            CurrentVersion = currentVersion;
            return Task.FromResult(new ReleaseUpdateCheckResult(
                updateAvailable,
                currentVersion,
                latestVersion,
                $"JK Bridge {latestVersion}",
                new Uri($"https://github.com/Mathjk/jk-bridge/releases/tag/v{latestVersion}"),
                DateTimeOffset.Parse("2026-07-29T00:00:00Z"),
                Installer: installer));
        }

        public Task<ReleaseInstallerDownload> DownloadInstallerAsync(
            ReleaseInstallerAsset requestedInstaller,
            string destinationDirectory,
            CancellationToken cancellationToken = default)
        {
            DownloadCalls++;
            Assert.Same(installer, requestedInstaller);
            var path = downloadedInstallerPath ?? throw new InvalidOperationException();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "verified fixture");
            return Task.FromResult(new ReleaseInstallerDownload(path, new string('a', 64), latestVersion));
        }
    }

    private sealed class FakePairingService : IDevicePairingService
    {
        public Task<DevicePairingResult> PairAsync(
            DevicePairingRequest request,
            IProgress<DevicePairingProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<DevicePairingResult>(new NotSupportedException());
    }

    private sealed class FakeRuntime : IBridgeRuntime
    {
        public event EventHandler<TunnelSnapshot>? SnapshotChanged
        {
            add { }
            remove { }
        }

        public TunnelSnapshot Snapshot { get; } = TunnelSnapshot.Stopped();
        public Task StartAsync(AppConfig config, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RestartAsync(AppConfig config, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RefreshAsync(AppConfig config, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
