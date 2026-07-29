using JKBridge.App.Platform;
using JKBridge.App.ViewModels;
using JKBridge.Core.Models;

namespace JKBridge.Core.Tests.ViewModels;

public sealed class ServiceRouteEditorViewModelTests
{
    [Fact]
    public void Constructor_DecomposesStoredLoopbackUrlIntoSimpleFields()
    {
        var viewModel = Create(localUrl: "https://127.0.0.1:8443/project?q=1#editor", localPort: 8443);

        Assert.Equal(1, viewModel.ProtocolIndex);
        Assert.Equal("/project?q=1#editor", viewModel.LaunchPath);
        Assert.False(viewModel.UseCustomUrl);
        Assert.False(viewModel.IsAdvanced);
    }

    [Fact]
    public void SimpleFields_GenerateLiveLoopbackBrowserUrl()
    {
        var viewModel = Create();

        viewModel.LocalPortText = "9001";
        viewModel.ProtocolIndex = 1;
        viewModel.LaunchPath = "#/login";

        Assert.Equal("https://127.0.0.1:9001/#/login", viewModel.LocalUrl);
        Assert.Equal(viewModel.LocalUrl, viewModel.BrowserUrlPreview);
        Assert.Contains("Windows 127.0.0.1:9001", viewModel.RoutePreview);
    }

    [Fact]
    public void LocalOnlyChanges_DoNotRequireLinuxReauthorization()
    {
        var viewModel = Create();

        viewModel.LocalPortText = "9001";
        viewModel.LaunchPath = "/new-entry";

        Assert.False(viewModel.RequiresLinuxReauthorization);
        Assert.Contains("不新增授权", viewModel.ReauthorizationHint);
    }

    [Fact]
    public void RemotePortChange_ExplainsLinuxReauthorization()
    {
        var viewModel = Create(remotePort: 80);

        viewModel.RemotePortText = "8081";

        Assert.True(viewModel.RequiresLinuxReauthorization);
        Assert.Contains("80", viewModel.ReauthorizationHint);
        Assert.Contains("8081", viewModel.ReauthorizationHint);
        Assert.Contains("重新配对", viewModel.ReauthorizationHint);
    }

    [Fact]
    public void DisabledService_DefersAuthorizationUntilItIsEnabled()
    {
        var viewModel = Create(remotePort: 80);
        viewModel.IsEnabled = false;
        viewModel.RemotePortText = "8081";

        Assert.False(viewModel.RequiresLinuxReauthorization);
        Assert.Contains("暂不申请权限", viewModel.ReauthorizationHint);

        viewModel.IsEnabled = true;

        Assert.True(viewModel.RequiresLinuxReauthorization);
    }

    [Fact]
    public void CustomUrl_MustRemainLoopbackAndMatchWindowsPort()
    {
        var viewModel = Create();
        viewModel.UseCustomUrl = true;
        viewModel.LocalUrl = "http://example.com:8080/";

        var valid = viewModel.TryCreateState(out var state, out var error);

        Assert.False(valid);
        Assert.Null(state);
        Assert.Contains("127.0.0.1", error);
    }

    [Fact]
    public void CustomUrl_IsPreservedWhenValid()
    {
        var viewModel = Create();
        viewModel.UseCustomUrl = true;
        viewModel.LocalUrl = "http://127.0.0.1:8080/custom?q=1#result";

        var valid = viewModel.TryCreateState(out var state, out _);

        Assert.True(valid);
        Assert.Equal("http://127.0.0.1:8080/custom?q=1#result", state!.LocalUrl);
    }

    [Fact]
    public void OccupiedLocalPort_IsReportedWithoutSilentlyChangingTheDraft()
    {
        var probe = new FakeLocalPortProbe(port => port == 8080
            ? new(LocalPortAvailabilityStatus.InUse, 8082)
            : new(LocalPortAvailabilityStatus.Available));

        var viewModel = Create(localPortProbe: probe);

        Assert.True(viewModel.HasLocalPortConflict);
        Assert.True(viewModel.HasSuggestedLocalPort);
        Assert.Equal("使用 8082", viewModel.SuggestedLocalPortText);
        Assert.Equal("8080", viewModel.LocalPortText);
        Assert.Equal("http://127.0.0.1:8080/", viewModel.LocalUrl);
        Assert.Contains("只有点击", viewModel.LocalPortAvailabilityHint);
    }

    [Fact]
    public void SuggestedLocalPort_IsAppliedOnlyAfterExplicitCommand()
    {
        var probe = new FakeLocalPortProbe(port => port == 8080
            ? new(LocalPortAvailabilityStatus.InUse, 8082)
            : new(LocalPortAvailabilityStatus.Available));
        var viewModel = Create(localPortProbe: probe);

        viewModel.ApplySuggestedLocalPortCommand.Execute(null);

        Assert.Equal("8082", viewModel.LocalPortText);
        Assert.Equal("http://127.0.0.1:8082/", viewModel.LocalUrl);
        Assert.False(viewModel.HasLocalPortConflict);
        Assert.False(viewModel.HasSuggestedLocalPort);
    }

    [Fact]
    public void Save_RechecksPortAndRejectsANewConflict()
    {
        var checkCount = 0;
        var probe = new FakeLocalPortProbe(_ => ++checkCount == 1
            ? new(LocalPortAvailabilityStatus.Available)
            : new(LocalPortAvailabilityStatus.InUse, 8082));
        var viewModel = Create(localPortProbe: probe);

        var valid = viewModel.TryCreateState(out var state, out var error);

        Assert.False(valid);
        Assert.Null(state);
        Assert.Contains("已被占用", error);
        Assert.Contains("8082", error);
        Assert.Equal("8080", viewModel.LocalPortText);
    }

    [Fact]
    public void ActiveSavedRoute_IsNotReportedAsConflictingWithItself()
    {
        var probe = new FakeLocalPortProbe(_ => new(LocalPortAvailabilityStatus.InUse, 8082));
        var viewModel = Create(canOpen: true, localPortProbe: probe);

        Assert.False(viewModel.HasLocalPortConflict);
        Assert.Contains("当前 JK Bridge", viewModel.LocalPortAvailabilityHint);
        Assert.Empty(probe.CheckedPorts);
    }

    [Fact]
    public void SelectingTemplate_OnlyPreviewsAndDoesNotOverwriteDraft()
    {
        var viewModel = Create();
        var originalName = viewModel.Name;
        var originalLocalPort = viewModel.LocalPortText;

        viewModel.SelectedTemplate = viewModel.ServiceTemplates.Single(template =>
            template.TemplateId == "sub2api");

        Assert.Equal(originalName, viewModel.Name);
        Assert.Equal(originalLocalPort, viewModel.LocalPortText);
        Assert.False(viewModel.CanApplySelectedTemplate);
        Assert.Contains("尚未修改", viewModel.TemplateApplyFeedback);
    }

    [Fact]
    public void TemplateRequiresConfirmationBeforeExplicitApply()
    {
        var viewModel = Create();
        viewModel.SelectedTemplate = viewModel.ServiceTemplates.Single(template =>
            template.TemplateId == "sub2api");

        viewModel.ApplySelectedTemplateCommand.Execute(null);

        Assert.Equal("Service", viewModel.Name);
        Assert.Equal("8080", viewModel.LocalPortText);

        viewModel.TemplateApplyConfirmed = true;
        viewModel.ApplySelectedTemplateCommand.Execute(null);

        Assert.Equal("Sub2API", viewModel.Name);
        Assert.Equal("8081", viewModel.LocalPortText);
        Assert.Equal("8081", viewModel.RemotePortText);
        Assert.Equal("http://127.0.0.1:8081/", viewModel.LocalUrl);
        Assert.Equal("/", viewModel.HealthPathText);
        Assert.Equal(1, viewModel.HealthModeIndex);
        Assert.False(viewModel.TemplateApplyConfirmed);
        Assert.Contains("尚未保存", viewModel.TemplateApplyFeedback);
    }

    [Fact]
    public void SimpleHealthPath_FollowsBrowserPathButDropsQueryAndFragment()
    {
        var viewModel = Create(localUrl: "http://127.0.0.1:8080/project?q=1#editor");

        Assert.False(viewModel.UseCustomHealthPath);
        Assert.Equal("/project", viewModel.HealthPathText);

        viewModel.LaunchPath = "/next?tab=2#pane";

        Assert.Equal("/next", viewModel.HealthPathText);
    }

    [Fact]
    public void CustomHealthPath_RemainsIndependentFromBrowserPath()
    {
        var viewModel = Create();
        viewModel.UseCustomHealthPath = true;
        viewModel.HealthPathText = "/api/health";

        viewModel.LaunchPath = "/dashboard";

        Assert.Equal("/api/health", viewModel.HealthPathText);
        Assert.Contains("独立路径", viewModel.HealthPathValidationHint);
    }

    [Fact]
    public void ExistingIndependentHealthPath_IsLoadedAndPreservedInDraftState()
    {
        var viewModel = Create(
            localUrl: "http://127.0.0.1:8080/dashboard",
            healthPath: "/api/health");

        Assert.True(viewModel.UseCustomHealthPath);
        Assert.Equal("/api/health", viewModel.HealthPathText);

        var valid = viewModel.TryCreateState(out var state, out _);

        Assert.True(valid);
        Assert.Equal("/api/health", state!.HealthPath);
    }

    [Fact]
    public void InvalidHealthPath_IsRejectedBeforeSave()
    {
        var viewModel = Create();
        viewModel.UseCustomHealthPath = true;
        viewModel.HealthPathText = "/#/browser-only";

        var valid = viewModel.TryCreateState(out var state, out var error);

        Assert.False(valid);
        Assert.Null(state);
        Assert.Contains("健康检查路径", error);
    }

    [Fact]
    public void GenericHttpsTemplate_ExplainsTcpHealthAndCertificateLimits()
    {
        var viewModel = Create();
        viewModel.SelectedTemplate = viewModel.ServiceTemplates.Single(template =>
            template.TemplateId == "generic-https");
        viewModel.TemplateApplyConfirmed = true;

        viewModel.ApplySelectedTemplateCommand.Execute(null);

        Assert.True(viewModel.IsHttps);
        Assert.Equal(0, viewModel.HealthModeIndex);
        Assert.Contains("自签名", viewModel.HealthModeExplanation);
        Assert.Contains("浏览器证书警告", viewModel.HttpsCompatibilityHint);
    }

    private static ServiceCardViewModel Create(
        string localUrl = "http://127.0.0.1:8080/",
        int localPort = 8080,
        int remotePort = 80,
        bool canOpen = false,
        string? healthPath = null,
        ILocalPortAvailabilityProbe? localPortProbe = null) =>
        new(
            new ServiceUiState(
                "service", "WEB", "Service", "Description", localUrl,
                localPort, remotePort, "等待隧道", "尚未检查",
                true, false, false, canOpen, HealthCheckKind.HttpStatus,
                healthPath ?? new Uri(localUrl).AbsolutePath),
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            localPortProbe ?? new FakeLocalPortProbe(_ => new(LocalPortAvailabilityStatus.Available)));

    private sealed class FakeLocalPortProbe(
        Func<int, LocalPortAvailabilityResult> check) : ILocalPortAvailabilityProbe
    {
        public List<int> CheckedPorts { get; } = [];

        public LocalPortAvailabilityResult Check(int port)
        {
            CheckedPorts.Add(port);
            return check(port);
        }
    }
}
