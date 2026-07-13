using JKBridge.App.Services;
using JKBridge.App.ViewModels;

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

    private sealed class RecordingActions : IBridgeUiActions
    {
        public PairingDraft? Pairing { get; private set; }

        public Task PairDeviceAsync(PairingDraft pairing)
        {
            Pairing = pairing;
            return Task.CompletedTask;
        }

        public Task ConnectAsync() => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task RefreshAsync() => Task.CompletedTask;
        public Task RetryAsync() => Task.CompletedTask;
        public Task OpenServiceAsync(string serviceId, string localUrl) => Task.CompletedTask;
        public Task RetryServiceAsync(string serviceId) => Task.CompletedTask;
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
