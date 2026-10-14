using JKBridge.App.Services;
using JKBridge.App.ViewModels;
using JKBridge.Core.Models;

namespace JKBridge.Core.Tests.ViewModels;

public sealed class MainViewModelPairingTests
{
    [Fact]
    public async Task PairDeviceCommand_ForwardsOnlyThePairingCodeAndIdentityDraft()
    {
        var actions = new RecordingActions();
        var viewModel = new MainViewModel(actions)
        {
            PairingCode = "jkbridge://100.64.0.10:45123/v1?token=fixture",
            PairingIdentityFile = @"%USERPROFILE%\.ssh\jk_bridge_fixture",
        };

        await viewModel.PairDeviceCommand.ExecuteAsync(null);

        Assert.NotNull(actions.Pairing);
        Assert.Equal(viewModel.PairingCode, actions.Pairing.PairingCode);
        Assert.Equal(viewModel.PairingIdentityFile, actions.Pairing.IdentityFile);
    }

    [Fact]
    public void PairingProgress_AdvancesMonotonicallyAndClearsCodeOnSuccess()
    {
        var viewModel = new MainViewModel(new RecordingActions())
        {
            PairingCode = "one-time-secret",
        };

        viewModel.BeginPairing();
        viewModel.ApplyPairingProgress(DevicePairingStage.PreparingKey, "key");
        viewModel.ApplyPairingProgress(DevicePairingStage.AuthorizingLinux, "linux");
        viewModel.ApplyPairingProgress(DevicePairingStage.Completed, "save");
        viewModel.CompletePairing("Example Linux");

        Assert.False(viewModel.IsPairing);
        Assert.True(viewModel.SetupCompleted);
        Assert.Empty(viewModel.PairingCode);
        Assert.Equal("完成", viewModel.PairingPrerequisiteStatus);
        Assert.Equal("完成", viewModel.PairingKeyStatus);
        Assert.Equal("完成", viewModel.PairingLinuxStatus);
        Assert.Equal("完成", viewModel.PairingFinishStatus);
    }

    [Fact]
    public async Task AgentRevoke_RequiresAdminSelectionAndExplicitConfirmation()
    {
        var actions = new RecordingActions();
        var viewModel = new MainViewModel(actions);
        viewModel.ApplyAgentRegistration(true);
        viewModel.ApplyAgentDevices(
            [
                new AgentDeviceUiState(
                    "0123456789abcdef0123456789abcdef",
                    "Current",
                    "admin",
                    "owner@example.com",
                    "SHA256:" + new string('A', 43),
                    "刚刚",
                    true,
                    false),
                new AgentDeviceUiState(
                    "fedcba9876543210fedcba9876543210",
                    "Old PC",
                    "device",
                    "owner@example.com",
                    "SHA256:" + new string('B', 43),
                    "昨天",
                    false,
                    false),
            ],
            "admin",
            "ready");
        viewModel.SelectedAgentDevice = viewModel.AgentDevices[1];

        await viewModel.RevokeAgentDeviceCommand.ExecuteAsync(null);
        Assert.Null(actions.RevokedDeviceId);

        viewModel.AgentRevokeConfirmed = true;
        await viewModel.RevokeAgentDeviceCommand.ExecuteAsync(null);
        Assert.Equal("fedcba9876543210fedcba9876543210", actions.RevokedDeviceId);
        Assert.True(actions.RevokeConfirmed);
    }

    [Fact]
    public async Task ProfileImport_RequiresPreviewAndExplicitConfirmation()
    {
        var actions = new RecordingActions();
        var viewModel = new MainViewModel(actions);

        viewModel.ApplyServiceProfilePreview("新增 1 · 删除 1");
        await viewModel.ApplyServiceProfileImportCommand.ExecuteAsync(null);
        Assert.False(actions.ProfileImportApplied);

        viewModel.ServiceProfileImportConfirmed = true;
        await viewModel.ApplyServiceProfileImportCommand.ExecuteAsync(null);
        Assert.True(actions.ProfileImportApplied);
    }

    [Fact]
    public async Task AgentRelayApply_RequiresReadOnlyPreviewAndExplicitConfirmation()
    {
        var actions = new RecordingActions();
        var viewModel = new MainViewModel(actions);
        viewModel.ApplyAgentRegistration(true);

        await viewModel.ApplyAgentRelayPlanCommand.ExecuteAsync(null);
        Assert.Equal(0, actions.RelayApplyCalls);

        viewModel.ApplyAgentRelayPreview("normalized plan", canApply: true);
        await viewModel.ApplyAgentRelayPlanCommand.ExecuteAsync(null);
        Assert.Equal(0, actions.RelayApplyCalls);

        viewModel.AgentRelayConfirmed = true;
        await viewModel.ApplyAgentRelayPlanCommand.ExecuteAsync(null);
        Assert.Equal(1, actions.RelayApplyCalls);
        Assert.True(actions.RelayConfirmed);
    }

    [Fact]
    public void AgentDiscovery_NeverFillsEndpointUntilCandidateIsExplicitlyUsed()
    {
        var viewModel = new MainViewModel(new RecordingActions())
        {
            AgentEndpoint = "http://100.64.0.99:45871/",
        };
        var candidate = new AgentDiscoveryCandidateUiState(
            "http://100.64.0.10:45871/",
            "Linux",
            "linux.example.ts.net",
            "1.0",
            true);

        viewModel.ApplyAgentDiscovery([candidate], "one candidate");

        Assert.Equal("http://100.64.0.99:45871/", viewModel.AgentEndpoint);
        Assert.Same(candidate, viewModel.SelectedAgentDiscoveryCandidate);

        viewModel.UseDiscoveredAgentCommand.Execute(null);

        Assert.Equal(candidate.Endpoint, viewModel.AgentEndpoint);
    }

    [Fact]
    public void AgentDiscovery_MultipleCandidatesRequireExplicitSelection()
    {
        var viewModel = new MainViewModel(new RecordingActions());

        viewModel.ApplyAgentDiscovery(
        [
            new AgentDiscoveryCandidateUiState(
                "http://100.64.0.10:45871/", "A", "a.example.ts.net", "1.0", true),
            new AgentDiscoveryCandidateUiState(
                "http://100.64.0.11:45871/", "B", "b.example.ts.net", "1.0", true),
        ],
        "multiple");

        Assert.Null(viewModel.SelectedAgentDiscoveryCandidate);
        Assert.False(viewModel.CanUseDiscoveredAgent);
        Assert.Empty(viewModel.AgentEndpoint);
    }

    [Fact]
    public void EnrollmentCode_IsClearedWhenLeavingSetup()
    {
        var viewModel = new MainViewModel(new RecordingActions())
        {
            SelectedPage = AppPage.Setup,
        };
        viewModel.ShowAgentEnrollmentCode(new string('A', 43), "soon");

        viewModel.SelectedPage = AppPage.Overview;

        Assert.False(viewModel.HasAgentEnrollmentCode);
        Assert.Empty(viewModel.AgentEnrollmentCode);
        Assert.Empty(viewModel.AgentEnrollmentCodeExpiry);
    }

    [Fact]
    public async Task AgentCatalogUpdate_RequiresPreviewAndExplicitConfirmation()
    {
        var actions = new RecordingActions();
        var viewModel = new MainViewModel(actions);
        viewModel.ApplyAgentRegistration(true);
        viewModel.ApplyAgentCatalogUpdatePreview("diff", true, "review");

        await viewModel.ApplyAgentCatalogUpdateCommand.ExecuteAsync(null);
        Assert.Equal(0, actions.CatalogApplyCalls);

        viewModel.AgentCatalogUpdateConfirmed = true;
        await viewModel.ApplyAgentCatalogUpdateCommand.ExecuteAsync(null);
        Assert.Equal(1, actions.CatalogApplyCalls);
        Assert.True(actions.CatalogConfirmed);
    }

    [Fact]
    public void AgentCatalogUpdate_ChangingGrantScopeInvalidatesPreview()
    {
        var viewModel = new MainViewModel(new RecordingActions());
        viewModel.ApplyAgentRegistration(true);
        viewModel.ApplyAgentCatalogUpdatePreview("diff", true, "review");

        viewModel.AgentCatalogApplyDefaultsToAllDevices = false;

        Assert.False(viewModel.HasAgentCatalogUpdatePreview);
        Assert.False(viewModel.CanApplyAgentCatalogUpdate);
    }

    [Fact]
    public async Task AgentRename_RequiresAdminAndChangedLabel()
    {
        var actions = new RecordingActions();
        var viewModel = new MainViewModel(actions);
        viewModel.ApplyAgentRegistration(true);
        viewModel.ApplyAgentDevices(
        [
            new AgentDeviceUiState(
                "0123456789abcdef0123456789abcdef",
                "Current",
                "admin",
                "owner@example.com",
                "SHA256:" + new string('A', 43),
                "最近记录",
                true,
                false),
        ],
        "admin",
        "ready");

        await viewModel.RenameAgentDeviceCommand.ExecuteAsync(null);
        Assert.Null(actions.RenamedDeviceId);

        viewModel.AgentRenameDeviceLabel = "Office desktop";
        await viewModel.RenameAgentDeviceCommand.ExecuteAsync(null);

        Assert.Equal("0123456789abcdef0123456789abcdef", actions.RenamedDeviceId);
        Assert.Equal("Office desktop", actions.RenamedDeviceLabel);
    }

    [Fact]
    public async Task AgentEndpointRebind_RequiresExplicitDiscoverySelectionAndConfirmation()
    {
        var actions = new RecordingActions();
        var viewModel = new MainViewModel(actions);
        viewModel.ApplyAgentRegistration(true);
        var candidate = new AgentDiscoveryCandidateUiState(
            "http://100.64.0.11:45871/",
            "Linux",
            "linux.example.ts.net",
            "1.0",
            true,
            "node-linux");
        viewModel.ApplyAgentDiscovery([candidate], "found");

        await viewModel.RebindAgentEndpointCommand.ExecuteAsync(null);
        Assert.Null(actions.ReboundEndpoint);

        viewModel.AgentEndpointRebindConfirmed = true;
        await viewModel.RebindAgentEndpointCommand.ExecuteAsync(null);

        Assert.Equal(candidate.Endpoint, actions.ReboundEndpoint);
        Assert.True(actions.RebindConfirmed);
    }

    [Fact]
    public void LeavingSetup_InvalidatesAllTransientPreviewsAndEnrollmentCode()
    {
        var actions = new RecordingActions();
        var viewModel = new MainViewModel(actions) { SelectedPage = AppPage.Setup };
        viewModel.ApplyAgentRelayPreview("relay", true);
        viewModel.ApplyAgentCatalogUpdatePreview("catalog", true, "review");
        viewModel.ApplyServiceProfilePreview("profile");
        viewModel.ShowAgentEnrollmentCode(new string('E', 43), "soon");

        viewModel.SelectedPage = AppPage.Overview;

        Assert.False(viewModel.HasAgentRelayPreview);
        Assert.False(viewModel.HasAgentCatalogUpdatePreview);
        Assert.False(viewModel.HasServiceProfileImportPreview);
        Assert.False(viewModel.HasAgentEnrollmentCode);
        Assert.Equal(1, actions.InvalidateTransientCalls);
    }

    [Fact]
    public async Task WelcomeTutorial_CanBeDismissedReopenedNavigatedAndCompleted()
    {
        var actions = new RecordingActions();
        var viewModel = new MainViewModel(actions);

        viewModel.ShowWelcomeTutorialIfNeeded(completed: false);

        Assert.True(viewModel.IsWelcomeTutorialVisible);
        Assert.Equal(0, viewModel.WelcomeTutorialStepIndex);
        Assert.Equal("步骤 1 / 4", viewModel.WelcomeTutorialProgressText);
        Assert.Contains("本机", viewModel.WelcomeTutorialStep.Title);

        viewModel.DismissWelcomeTutorialCommand.Execute(null);
        Assert.False(viewModel.IsWelcomeTutorialVisible);

        viewModel.ShowWelcomeTutorialCommand.Execute(null);
        await viewModel.AdvanceWelcomeTutorialCommand.ExecuteAsync(null);
        await viewModel.AdvanceWelcomeTutorialCommand.ExecuteAsync(null);
        await viewModel.AdvanceWelcomeTutorialCommand.ExecuteAsync(null);

        Assert.Equal(3, viewModel.WelcomeTutorialStepIndex);
        Assert.True(viewModel.IsLastWelcomeTutorialStep);
        Assert.Equal("完成教程", viewModel.WelcomeTutorialNextButtonText);
        Assert.Equal(0, actions.TutorialCompletionCalls);

        await viewModel.AdvanceWelcomeTutorialCommand.ExecuteAsync(null);

        Assert.Equal(1, actions.TutorialCompletionCalls);
        Assert.False(viewModel.IsWelcomeTutorialCompleting);

        viewModel.MarkWelcomeTutorialCompleted();
        Assert.False(viewModel.IsWelcomeTutorialVisible);
        viewModel.ShowWelcomeTutorialIfNeeded(completed: true);
        Assert.False(viewModel.IsWelcomeTutorialVisible);
    }

    [Fact]
    public void ThemeSelection_PreviewsImmediatelyAndDistinguishesUnsavedState()
    {
        var actions = new RecordingActions();
        var viewModel = new MainViewModel(actions);

        viewModel.ThemeIndex = 1;
        viewModel.ThemeIndex = 2;
        viewModel.ThemeIndex = 0;

        Assert.Equal(
            [ThemePreference.Light, ThemePreference.Dark, ThemePreference.System],
            actions.ThemePreviews);
        Assert.Contains("跟随系统", viewModel.ThemePreviewText);
        Assert.Contains("尚未保存", viewModel.ThemePreviewText);

        viewModel.MarkThemeSaved(ThemePreference.System);

        Assert.Contains("已保存", viewModel.ThemePreviewText);
        Assert.DoesNotContain("尚未保存", viewModel.ThemePreviewText);
    }

    private sealed class RecordingActions : IBridgeUiActions
    {
        public PairingDraft? Pairing { get; private set; }
        public string? RevokedDeviceId { get; private set; }
        public bool RevokeConfirmed { get; private set; }
        public bool ProfileImportApplied { get; private set; }
        public int RelayApplyCalls { get; private set; }
        public bool RelayConfirmed { get; private set; }
        public int CatalogApplyCalls { get; private set; }
        public bool CatalogConfirmed { get; private set; }
        public string? RenamedDeviceId { get; private set; }
        public string? RenamedDeviceLabel { get; private set; }
        public string? ReboundEndpoint { get; private set; }
        public bool RebindConfirmed { get; private set; }
        public int InvalidateTransientCalls { get; private set; }
        public int TutorialCompletionCalls { get; private set; }
        public List<ThemePreference> ThemePreviews { get; } = [];

        public Task PairDeviceAsync(PairingDraft pairing)
        {
            Pairing = pairing;
            return Task.CompletedTask;
        }

        public Task RevokeAgentDeviceAsync(string deviceId, bool confirmed)
        {
            RevokedDeviceId = deviceId;
            RevokeConfirmed = confirmed;
            return Task.CompletedTask;
        }

        public Task ApplyServiceProfileImportAsync(bool confirmed)
        {
            ProfileImportApplied = confirmed;
            return Task.CompletedTask;
        }

        public Task ApplyAgentRelayPlanAsync(bool confirmed)
        {
            RelayApplyCalls++;
            RelayConfirmed = confirmed;
            return Task.CompletedTask;
        }

        public Task ApplyAgentCatalogUpdateAsync(bool confirmed)
        {
            CatalogApplyCalls++;
            CatalogConfirmed = confirmed;
            return Task.CompletedTask;
        }

        public Task RenameAgentDeviceAsync(string deviceId, string deviceLabel)
        {
            RenamedDeviceId = deviceId;
            RenamedDeviceLabel = deviceLabel;
            return Task.CompletedTask;
        }

        public Task RebindAgentEndpointAsync(
            AgentDiscoveryCandidateUiState candidate,
            bool confirmed)
        {
            ReboundEndpoint = candidate.Endpoint;
            RebindConfirmed = confirmed;
            return Task.CompletedTask;
        }

        public void InvalidateTransientSetupState() => InvalidateTransientCalls++;

        public void PreviewTheme(ThemePreference theme) => ThemePreviews.Add(theme);

        public Task CompleteWelcomeTutorialAsync()
        {
            TutorialCompletionCalls++;
            return Task.CompletedTask;
        }

        public Task ConnectAsync() => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task RefreshAsync() => Task.CompletedTask;
        public Task RetryAsync() => Task.CompletedTask;
        public Task OpenServiceAsync(string serviceId, string localUrl) => Task.CompletedTask;
        public Task RetryServiceAsync(string serviceId) => Task.CompletedTask;
        public Task RestartServiceAsync(string serviceId) => Task.CompletedTask;
        public Task<bool> ConfirmAsync(ConfirmRequest request) => Task.FromResult(false);
        public Task CopyAddressAsync(string localUrl) => Task.CompletedTask;
        public Task SetServiceEnabledAsync(string serviceId, bool enabled) => Task.CompletedTask;
        public Task AddServiceAsync() => Task.CompletedTask;
        public Task SaveServiceAsync(ServiceUiState service) => Task.CompletedTask;
        public Task RemoveServiceAsync(string serviceId) => Task.CompletedTask;
        public Task RunDiagnosticsAsync() => Task.CompletedTask;
        public Task CopyDiagnosticSummaryAsync() => Task.CompletedTask;
        public Task OpenLogFolderAsync() => Task.CompletedTask;
        public Task SaveSettingsAsync(SettingsDraft settings) => Task.CompletedTask;
    }
}
