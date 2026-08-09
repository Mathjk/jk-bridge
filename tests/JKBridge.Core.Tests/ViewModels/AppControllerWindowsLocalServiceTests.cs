using JKBridge.App.Platform;
using JKBridge.App.Services;
using JKBridge.App.ViewModels;
using JKBridge.Core.Configuration;
using JKBridge.Core.Diagnostics;
using JKBridge.Core.Models;
using JKBridge.Core.Pairing;
using JKBridge.Core.Tunneling;

namespace JKBridge.Core.Tests.ViewModels;

public sealed class AppControllerWindowsLocalServiceTests
{
    [Fact]
    public async Task ScanAndSaveDiscoveredService_PersistsWithoutRefreshingLinuxRuntime()
    {
        var result = WebResult(8384, "syncthing", "/");
        await using var fixture = ControllerFixture.Create([result]);

        await fixture.Controller.ScanWindowsLocalServicesAsync();

        Assert.Contains(8080, fixture.Discovery.LastTunnelPorts);
        var discovered = Assert.Single(fixture.Controller.ViewModel.DiscoveredWindowsLocalServices);
        Assert.Equal("syncthing", discovered.ProcessName);
        Assert.True(discovered.CanSave);

        await fixture.Controller.SaveDiscoveredWindowsLocalServiceAsync(discovered.State);

        var saved = Assert.Single(fixture.Controller.CurrentConfig.WindowsLocalServices);
        Assert.Equal("syncthing", saved.Name);
        Assert.Equal(8384, saved.Port);
        Assert.Equal("127.0.0.1", saved.Host);
        Assert.Equal("/", saved.LaunchPath);
        Assert.Equal(HealthCheckKind.HttpStatus, saved.HealthKind);
        Assert.Equal(0, fixture.Runtime.RefreshCalls);
        Assert.Equal(0, fixture.Runtime.StopCalls);
        Assert.Single(fixture.Controller.ViewModel.WindowsLocalServices);
    }

    [Fact]
    public async Task AddWindowsLocalServiceAsync_AvoidsCurrentlyDiscoveredPort()
    {
        await using var fixture = ControllerFixture.Create([WebResult(8800, "demo", "/")]);
        await fixture.Controller.ScanWindowsLocalServicesAsync();

        await fixture.Controller.AddWindowsLocalServiceAsync();

        var saved = Assert.Single(fixture.Controller.CurrentConfig.WindowsLocalServices);
        Assert.Equal(8801, saved.Port);
        Assert.False(saved.Enabled);
        Assert.False(fixture.Controller.ViewModel.IsLinuxServicesMode);
        Assert.Equal(0, fixture.Runtime.RefreshCalls);
        Assert.Equal(0, fixture.Runtime.StopCalls);
    }

    [Fact]
    public async Task ScanWindowsLocalServicesAsync_PreservesUnsavedCardEdits()
    {
        await using var fixture = ControllerFixture.Create([]);
        await fixture.Controller.AddWindowsLocalServiceAsync();
        var card = Assert.Single(fixture.Controller.ViewModel.WindowsLocalServices);
        card.Name = "尚未保存的名称";
        card.Description = "尚未保存的说明";

        await fixture.Controller.ScanWindowsLocalServicesAsync();

        Assert.Equal("尚未保存的名称", card.Name);
        Assert.Equal("尚未保存的说明", card.Description);
        Assert.Same(card, Assert.Single(fixture.Controller.ViewModel.WindowsLocalServices));
    }

    [Fact]
    public async Task SaveDiscoveredService_RejectsStateThatWasNotReturnedByCurrentScan()
    {
        await using var fixture = ControllerFixture.Create([]);
        var fabricated = new DiscoveredWindowsLocalServiceUiState(
            "fabricated",
            "127.0.0.1",
            9123,
            "unknown",
            "http",
            "http://127.0.0.1:9123/",
            "已确认网页",
            "fabricated",
            true,
            true,
            false,
            false);

        await fixture.Controller.SaveDiscoveredWindowsLocalServiceAsync(fabricated);

        Assert.Empty(fixture.Controller.CurrentConfig.WindowsLocalServices);
    }

    private static DiscoveredWindowsLocalService WebResult(
        int port,
        string processName,
        string path) =>
        new(
            $"127.0.0.1:{port}:42",
            "127.0.0.1",
            port,
            42,
            processName,
            LocalEndpointProtocol.Http,
            $"http://127.0.0.1:{port}{path}",
            200,
            false,
            false,
            "HTTP 200");

    private sealed class ControllerFixture : IAsyncDisposable
    {
        private readonly string _directory;

        private ControllerFixture(
            string directory,
            AppController controller,
            FakeRuntime runtime,
            FakeWindowsDiscovery discovery)
        {
            _directory = directory;
            Controller = controller;
            Runtime = runtime;
            Discovery = discovery;
        }

        public AppController Controller { get; }
        public FakeRuntime Runtime { get; }
        public FakeWindowsDiscovery Discovery { get; }

        public static ControllerFixture Create(
            IReadOnlyList<DiscoveredWindowsLocalService> results)
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                $"jkbridge-windows-local-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var runtime = new FakeRuntime();
            var discovery = new FakeWindowsDiscovery(results);
            var controller = new AppController(
                new ConfigStore(Path.Combine(directory, "config.json")),
                new RollingFileLogger(Path.Combine(directory, "logs")),
                runtime,
                new FakePairingService(),
                new StartupRegistration($"JK Bridge Test {Guid.NewGuid():N}"),
                Path.Combine(directory, "logs"),
                () => null,
                dispatchUi: action => action(),
                windowsLocalDiscovery: discovery);
            return new ControllerFixture(directory, controller, runtime, discovery);
        }

        public async ValueTask DisposeAsync()
        {
            await Controller.DisposeAsync();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }

    private sealed class FakeWindowsDiscovery(
        IReadOnlyList<DiscoveredWindowsLocalService> results)
        : IWindowsLocalServiceDiscovery
    {
        public IReadOnlySet<int> LastTunnelPorts { get; private set; } = new HashSet<int>();

        public Task<IReadOnlyList<DiscoveredWindowsLocalService>> DiscoverAsync(
            IReadOnlySet<int> existingTunnelPorts,
            CancellationToken cancellationToken = default)
        {
            LastTunnelPorts = existingTunnelPorts.ToHashSet();
            return Task.FromResult(results);
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
        public int RefreshCalls { get; private set; }
        public int StopCalls { get; private set; }

        public event EventHandler<TunnelSnapshot>? SnapshotChanged
        {
            add { }
            remove { }
        }

        public TunnelSnapshot Snapshot { get; } = TunnelSnapshot.Stopped();

        public Task StartAsync(AppConfig config, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCalls++;
            return Task.CompletedTask;
        }

        public Task RestartAsync(AppConfig config, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RefreshAsync(AppConfig config, CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
