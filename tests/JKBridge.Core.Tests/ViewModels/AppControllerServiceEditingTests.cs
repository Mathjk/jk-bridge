using JKBridge.App.Platform;
using JKBridge.App.Services;
using JKBridge.App.ViewModels;
using JKBridge.Core.Configuration;
using JKBridge.Core.Diagnostics;
using JKBridge.Core.Models;
using JKBridge.Core.Tunneling;

namespace JKBridge.Core.Tests.ViewModels;

public sealed class AppControllerServiceEditingTests
{
    [Fact]
    public async Task SaveServiceAsync_PersistsIndependentHealthPathWithoutDerivingItFromOpenUrl()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"jkbridge-service-edit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var controller = new AppController(
            new ConfigStore(Path.Combine(directory, "config.json")),
            new RollingFileLogger(Path.Combine(directory, "logs")),
            new FakeRuntime(),
            new FakePairingService(),
            new StartupRegistration($"JK Bridge Test {Guid.NewGuid():N}"),
            Path.Combine(directory, "logs"),
            () => null,
            dispatchUi: action => action());

        try
        {
            var existing = controller.CurrentConfig.Services.Single(service => service.Id == "overleaf");
            await controller.SaveServiceAsync(new ServiceUiState(
                existing.Id,
                "OL",
                existing.Name,
                existing.Description,
                "http://127.0.0.1:8080/project",
                8080,
                80,
                "等待隧道",
                "尚未检查",
                existing.Enabled,
                false,
                false,
                false,
                HealthCheckKind.JsonOk,
                "/api/health"));

            var saved = controller.CurrentConfig.Services.Single(service => service.Id == "overleaf");
            Assert.Equal("/api/health", saved.HealthPath);
            Assert.Equal(HealthCheckKind.JsonOk, saved.HealthKind);
            Assert.Equal("http://127.0.0.1:8080/project", saved.OpenUrl);
            var refreshedDraft = controller.ViewModel.Services.Single(service => service.Id == "overleaf");
            Assert.Equal("/api/health", refreshedDraft.HealthPathText);
            Assert.True(refreshedDraft.UseCustomHealthPath);
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

        public Task StartAsync(AppConfig config, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RestartAsync(AppConfig config, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RefreshAsync(AppConfig config, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void SetHealthCheckInterval(TimeSpan interval)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
