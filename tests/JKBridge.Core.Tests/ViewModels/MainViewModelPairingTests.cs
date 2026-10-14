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
    public void LeavingSetup_InvalidatesAllTransientPreviewsAndEnrollmentCode()
    {
        var actions = new RecordingActions();
        var viewModel = new MainViewModel(actions) { SelectedPage = AppPage.Setup };
        viewModel.ApplyServiceProfilePreview("profile");

        viewModel.SelectedPage = AppPage.Overview;

        Assert.False(viewModel.HasServiceProfileImportPreview);
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

            public Task ApplyServiceProfileImportAsync(bool confirmed)
        {
            ProfileImportApplied = confirmed;
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
        public Task OpenServiceLogAsync(string serviceId) => Task.CompletedTask;
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
