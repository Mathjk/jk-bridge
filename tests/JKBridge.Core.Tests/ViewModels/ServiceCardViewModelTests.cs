using JKBridge.App.ViewModels;
using JKBridge.Core.Models;

namespace JKBridge.Core.Tests.ViewModels;

public sealed class ServiceCardViewModelTests
{
    [Fact]
    public void TryCreateState_RejectsInvalidPortTextInsteadOfSilentlyUsingOldValue()
    {
        var viewModel = Create();
        viewModel.LocalPortText = "not-a-port";

        var valid = viewModel.TryCreateState(out var state, out var error);

        Assert.False(valid);
        Assert.Null(state);
        Assert.Contains("1–65535", error);
    }

    [Fact]
    public void TryCreateState_UsesAllEditedDraftFields()
    {
        var viewModel = Create();
        viewModel.Name = "Edited";
        viewModel.LocalPortText = "9001";
        viewModel.RemotePortText = "9002";
        viewModel.IsEnabled = false;

        var valid = viewModel.TryCreateState(out var state, out _);

        Assert.True(valid);
        Assert.Equal("Edited", state!.Name);
        Assert.Equal(9001, state.LocalPort);
        Assert.Equal(9002, state.RemotePort);
        Assert.False(state.IsEnabled);
    }

    [Fact]
    public void TryCreateState_MapsSelectedTcpHealthMode()
    {
        var viewModel = Create();
        viewModel.HealthModeIndex = 0;

        var valid = viewModel.TryCreateState(out var state, out _);

        Assert.True(valid);
        Assert.Equal(HealthCheckKind.TcpConnect, state!.HealthKind);
    }

    [Fact]
    public void ApplyRuntimeState_DoesNotOverwriteUnsavedHealthModeDraft()
    {
        var viewModel = Create();
        viewModel.HealthModeIndex = 0;

        viewModel.ApplyRuntimeState(CreateState(HealthCheckKind.JsonOk));

        Assert.Equal(0, viewModel.HealthModeIndex);
    }

    private static ServiceCardViewModel Create() => new(
        CreateState(HealthCheckKind.HttpStatus),
        _ => Task.CompletedTask,
        _ => Task.CompletedTask,
        _ => Task.CompletedTask,
        (_, _) => Task.CompletedTask,
        _ => Task.CompletedTask,
        _ => Task.CompletedTask);

    private static ServiceUiState CreateState(HealthCheckKind healthKind) =>
        new(
            "service", "WEB", "Service", "Description", "http://127.0.0.1:8080/",
            8080, 80, "等待隧道", "尚未检查", true, false, false, false,
            healthKind);
}
