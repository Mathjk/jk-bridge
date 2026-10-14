using System.Net;
using JKBridge.App.Platform;
using JKBridge.App.Services;
using JKBridge.App.ViewModels;
using JKBridge.Core.Configuration;
using JKBridge.Core.Diagnostics;
using JKBridge.Core.Models;
using JKBridge.Core.Tunneling;

namespace JKBridge.Core.Tests.Configuration;

public sealed class ServiceRestartActionTests
{
    [Fact]
    public void DefaultConfiguration_GpuDashboardCarriesRestartPath()
    {
        var config = AppConfig.CreateDefault();

        var result = ConfigValidator.Validate(config);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Issues));
        var dashboard = config.Services.Single(service => service.Id == "gpu-dashboard");
        Assert.Equal("/api/restart", dashboard.RestartPath);
        Assert.Empty(dashboard.RestartToken);
    }

    [Theory]
    [InlineData("/api/restart")]
    [InlineData("/api/v1/restart")]
    public void Validate_AcceptsSafeRestartPathWithOptionalToken(string restartPath)
    {
        var config = Configured(restartPath, "unit-test-token");

        var result = ConfigValidator.Validate(config);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Issues));
    }

    [Theory]
    [InlineData("api/restart")]
    [InlineData("/api/restart?force=1")]
    [InlineData("/api/restart#anchor")]
    [InlineData("/api\\restart")]
    [InlineData("/api/restart\u0001")]
    [InlineData("//api/restart")]
    public void Validate_RejectsUnsafeRestartPath(string restartPath)
    {
        var config = Configured(restartPath, "unit-test-token");

        var result = ConfigValidator.Validate(config);

        AssertIssue(result, "service.invalidRestartPath", "services[1].restartPath");
    }

    [Fact]
    public void Validate_RejectsRestartTokenWithoutPath()
    {
        var config = AppConfig.CreateDefault() with
        {
            Services =
            [
                .. AppConfig.CreateDefault().Services.Select(service =>
                    service.Id == "gpu-dashboard"
                        ? service with { RestartPath = string.Empty, RestartToken = "unit-test-token" }
                        : service with { }),
            ],
        };

        var result = ConfigValidator.Validate(config);

        AssertIssue(result, "service.restartTokenWithoutPath", "services[1].restartToken");
    }

    [Fact]
    public void Validate_RejectsOverLongRestartToken()
    {
        var config = Configured("/api/restart", new string('x', 257));

        var result = ConfigValidator.Validate(config);

        AssertIssue(result, "text.invalid", "services[1].restartToken");
    }

    [Fact]
    public void ServiceProfile_CarriesRestartPathButNeverTheToken()
    {
        var config = Configured("/api/restart", "super-secret-token");

        var profile = ServiceProfileSerializer.Create(config, "Portable services");
        var json = ServiceProfileSerializer.Serialize(profile);
        var roundTripped = ServiceProfileSerializer.Deserialize(json);

        var entry = profile.Services.Single(service => service.Id == "gpu-dashboard");
        Assert.Equal("/api/restart", entry.RestartPath);
        Assert.DoesNotContain("super-secret-token", json, StringComparison.Ordinal);
        Assert.Equal(
            entry.RestartPath,
            roundTripped.Services.Single(service => service.Id == "gpu-dashboard").RestartPath);

        var imported = ServiceProfileMapping.ToServiceDefinition(entry);
        Assert.Equal("/api/restart", imported.RestartPath);
        Assert.Empty(imported.RestartToken);
        Assert.True(ServiceProfileValidator.Validate(roundTripped).IsValid);
    }

    [Fact]
    public async Task SaveServiceAsync_PersistsRestartActionAndRefreshesDraft()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"jkbridge-restart-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var store = new InMemoryRestartTokenStore();
        var controller = new AppController(
            new ConfigStore(Path.Combine(directory, "config.json")),
            new RollingFileLogger(Path.Combine(directory, "logs")),
            new FakeRuntime(),
            new FakePairingService(),
            new StartupRegistration($"JK Bridge Test {Guid.NewGuid():N}"),
            Path.Combine(directory, "logs"),
            () => null,
            dispatchUi: action => action(),
            restartTokenStore: store);

        try
        {
            var existing = controller.CurrentConfig.Services.Single(service => service.Id == "gpu-dashboard");
            await controller.SaveServiceAsync(new ServiceUiState(
                existing.Id,
                "GD",
                existing.Name,
                existing.Description,
                existing.OpenUrl,
                existing.LocalPort,
                existing.RemotePort,
                "可用",
                "ok",
                existing.Enabled,
                true,
                false,
                true,
                existing.HealthKind,
                existing.HealthPath,
                "/api/restart",
                "super-secret-token"));

            var saved = controller.CurrentConfig.Services.Single(service => service.Id == "gpu-dashboard");
            Assert.Equal("/api/restart", saved.RestartPath);
            // The secret lives in the credential store; config keeps only the reference.
            Assert.Empty(saved.RestartToken);
            Assert.Equal("restart-gpu-dashboard", saved.RestartTokenReference);
            Assert.Equal("super-secret-token", await store.LoadAsync(saved.RestartTokenReference));
            Assert.True(saved.HasRestartAction);

            var draft = controller.ViewModel.Services.Single(service => service.Id == "gpu-dashboard");
            Assert.True(draft.HasRestartAction);
            Assert.True(draft.UseRestartAction);
            Assert.Equal("/api/restart", draft.RestartPathText);
            Assert.Equal("super-secret-token", draft.RestartTokenText);
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
    public void ServiceCardViewModel_RejectsUnsafeRestartPathInDraft()
    {
        var draft = CreateDraft(restartPath: "/api/restart?force=1", restartToken: "token");

        var valid = draft.TryCreateState(out var state, out var error);

        Assert.False(valid);
        Assert.Null(state);
        Assert.Contains("重启路径", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceCardViewModel_DisabledToggleClearsActionButKeepsConfigSilent()
    {
        var draft = CreateDraft(restartPath: "/api/restart", restartToken: "token");
        draft.UseRestartAction = false;

        Assert.False(draft.HasRestartAction);
        var valid = draft.TryCreateState(out var state, out _);

        Assert.True(valid);
        Assert.NotNull(state);
        Assert.Empty(state!.RestartPath);
        Assert.Empty(state.RestartToken);
    }

    [Fact]
    public async Task RestartServiceAsync_SendsJsonPostWithTokenThroughTunnelPort()
    {
        var handler = new CapturingHandler();
        using var client = new HttpClient(handler);
        var directory = CreateControllerDirectory();
        var controller = CreateController(client, directory);

        try
        {
            var existing = controller.CurrentConfig.Services.Single(service => service.Id == "gpu-dashboard");
            await controller.SaveServiceAsync(new ServiceUiState(
                existing.Id,
                "GD",
                existing.Name,
                existing.Description,
                existing.OpenUrl,
                existing.LocalPort,
                existing.RemotePort,
                "可用",
                "ok",
                existing.Enabled,
                true,
                false,
                true,
                existing.HealthKind,
                existing.HealthPath,
                "/api/restart",
                "unit-test-token"));

            await controller.RestartServiceAsync("gpu-dashboard");

            Assert.True(handler.Called, "The restart request was never sent.");
            Assert.Equal(HttpMethod.Post, handler.Method);
            Assert.Equal(
                $"http://127.0.0.1:{existing.LocalPort}/api/restart",
                handler.RequestUri);
            Assert.Contains("application/json", handler.ContentType ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal("{}", handler.Body);
            Assert.Equal("unit-test-token", handler.Token);
            Assert.Contains("重启指令已发送", controller.ViewModel.IssueTitle, StringComparison.Ordinal);
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
    public async Task RestartServiceAsync_SurfacesRejectionWithoutClaimingSuccess()
    {
        var handler = new CapturingHandler(HttpStatusCode.Unauthorized);
        using var client = new HttpClient(handler);
        var directory = CreateControllerDirectory();
        var controller = CreateController(client, directory);

        try
        {
            var existing = controller.CurrentConfig.Services.Single(service => service.Id == "gpu-dashboard");
            await controller.SaveServiceAsync(new ServiceUiState(
                existing.Id,
                "GD",
                existing.Name,
                existing.Description,
                existing.OpenUrl,
                existing.LocalPort,
                existing.RemotePort,
                "可用",
                "ok",
                existing.Enabled,
                true,
                false,
                true,
                existing.HealthKind,
                existing.HealthPath,
                "/api/restart",
                "unit-test-token"));

            await controller.RestartServiceAsync("gpu-dashboard");

            Assert.True(handler.Called, "The restart request was never sent.");
            Assert.Contains("重启被拒绝", controller.ViewModel.IssueTitle, StringComparison.Ordinal);
            Assert.Contains("令牌", controller.ViewModel.IssueDetail, StringComparison.Ordinal);
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
    public void ConfirmAsync_WithoutOwnerWindowFailsSafe()
    {
        var directory = CreateControllerDirectory();
        var controller = CreateController(new HttpClient(new CapturingHandler()), directory);

        try
        {
            // The test host has no main window, so a destructive action must not proceed.
            var confirmed = controller.ConfirmAsync(new ConfirmRequest("t", "m")).GetAwaiter().GetResult();

            Assert.False(confirmed);
        }
        finally
        {
            controller.DisposeAsync().GetAwaiter().GetResult();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RestartServiceAsync_DoesNothingWithoutConfiguredAction()
    {
        var handler = new CapturingHandler();
        using var client = new HttpClient(handler);
        var directory = CreateControllerDirectory();
        var controller = CreateController(client, directory);

        try
        {
            await controller.RestartServiceAsync("overleaf");

            Assert.False(handler.Called);
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
    public void Validate_AcceptsRestartPortThatMatchesAnExistingTunnel()
    {
        var config = Configured("/api/restart/investment-news", "unit-test-token", restartLocalPort: 8765);

        var result = ConfigValidator.Validate(config);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Issues));
    }

    [Fact]
    public void Validate_RejectsRestartPortWithoutMatchingTunnel()
    {
        var config = Configured("/api/restart/investment-news", "unit-test-token", restartLocalPort: 9123);

        var result = ConfigValidator.Validate(config);

        AssertIssue(result, "service.restartPortWithoutTunnel", "services[1].restartLocalPort");
    }

    [Fact]
    public void Validate_RejectsOutOfRangeRestartPort()
    {
        var config = Configured("/api/restart", "unit-test-token", restartLocalPort: 70000);

        var result = ConfigValidator.Validate(config);

        AssertIssue(result, "service.invalidRestartLocalPort", "services[1].restartLocalPort");
    }

    [Fact]
    public async Task RestartServiceAsync_TargetsSharedControlPortWhenConfigured()
    {
        var handler = new CapturingHandler();
        using var client = new HttpClient(handler);
        var directory = CreateControllerDirectory();
        var controller = CreateController(client, directory);

        try
        {
            var existing = controller.CurrentConfig.Services.Single(service => service.Id == "investment-news");
            await controller.SaveServiceAsync(new ServiceUiState(
                existing.Id,
                "IN",
                existing.Name,
                existing.Description,
                existing.OpenUrl,
                existing.LocalPort,
                existing.RemotePort,
                "可用",
                "ok",
                existing.Enabled,
                true,
                false,
                true,
                existing.HealthKind,
                existing.HealthPath,
                "/api/restart/investment-news",
                "unit-test-token",
                RestartLocalPort: 8765));

            await controller.RestartServiceAsync("investment-news");

            Assert.True(handler.Called, "The restart request was never sent.");
            Assert.Equal("http://127.0.0.1:8765/api/restart/investment-news", handler.RequestUri);
            Assert.Equal("unit-test-token", handler.Token);
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

    private static string CreateControllerDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"jkbridge-restart-http-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static AppController CreateController(
        HttpClient client,
        string directory,
        IRestartTokenStore? tokenStore = null) =>
        new(
            new ConfigStore(Path.Combine(directory, "config.json")),
            new RollingFileLogger(Path.Combine(directory, "logs")),
            new ConnectedRuntime(),
            new FakePairingService(),
            new StartupRegistration($"JK Bridge Test {Guid.NewGuid():N}"),
            Path.Combine(directory, "logs"),
            () => null,
            dispatchUi: action => action(),
            restartHttpClient: client,
            restartTokenStore: tokenStore ?? new InMemoryRestartTokenStore());

    private sealed class CapturingHandler(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public bool Called { get; private set; }

        public HttpMethod? Method { get; private set; }

        public string? RequestUri { get; private set; }

        public string? ContentType { get; private set; }

        public string? Body { get; private set; }

        public string? Token { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Called = true;
            Method = request.Method;
            RequestUri = request.RequestUri?.AbsoluteUri;
            ContentType = request.Content?.Headers.ContentType?.ToString();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Token = request.Headers.TryGetValues("X-JKBridge-Restart-Token", out var values)
                ? values.Single()
                : null;
            return new HttpResponseMessage(status);
        }
    }

    private sealed class ConnectedRuntime : IBridgeRuntime
    {
        public event EventHandler<TunnelSnapshot>? SnapshotChanged
        {
            add { }
            remove { }
        }

        public TunnelSnapshot Snapshot { get; } = new(
            TunnelPhase.Connected,
            DesiredConnected: true,
            "Connected.",
            DateTimeOffset.Now,
            [
                new ServiceSnapshot("gpu-dashboard", "GPU Dashboard", ServicePhase.Available),
                new ServiceSnapshot("investment-news", "Investment News", ServicePhase.Available),
            ]);

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

    private static ServiceCardViewModel CreateDraft(string restartPath, string restartToken)
    {
        var state = new ServiceUiState(
            "gpu-dashboard",
            "GD",
            "GPU Dashboard",
            "显卡状态与任务监控",
            "http://127.0.0.1:8765/",
            8765,
            8765,
            "可用",
            "ok",
            true,
            true,
            false,
            true,
            HealthCheckKind.JsonOk,
            "/api/health",
            restartPath,
            restartToken);
        return new ServiceCardViewModel(
            state,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask);
    }

    private static AppConfig Configured(
        string restartPath,
        string restartToken,
        int restartLocalPort = 0) =>
        AppConfig.CreateDefault() with
        {
            Services =
            [
                .. AppConfig.CreateDefault().Services.Select(service =>
                    service.Id == "gpu-dashboard"
                        ? service with
                        {
                            RestartPath = restartPath,
                            RestartToken = restartToken,
                            RestartLocalPort = restartLocalPort,
                        }
                        : service with { }),
            ],
        };

    private static void AssertIssue(
        ConfigValidationResult result,
        string code,
        string path)
    {
        Assert.Contains(result.Issues, issue => issue.Code == code && issue.Path == path);
    }

    private sealed class InMemoryRestartTokenStore : IRestartTokenStore
    {
        private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);

        public Task<string> SaveAsync(
            string serviceId,
            string token,
            CancellationToken cancellationToken = default)
        {
            var reference = "restart-" + serviceId;
            _tokens[reference] = token;
            return Task.FromResult(reference);
        }

        public Task<string?> LoadAsync(string reference, CancellationToken cancellationToken = default) =>
            Task.FromResult(_tokens.TryGetValue(reference, out var token) ? token : null);

        public Task DeleteAsync(string reference, CancellationToken cancellationToken = default)
        {
            _tokens.Remove(reference);
            return Task.CompletedTask;
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
