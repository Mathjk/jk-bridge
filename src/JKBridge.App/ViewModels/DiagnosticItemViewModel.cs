namespace JKBridge.App.ViewModels;

public sealed class DiagnosticItemViewModel(DiagnosticUiState state) : ViewModelBase
{
    public string Time { get; } = state.Time;
    public string Level { get; } = state.Level;
    public string Title { get; } = state.Title;
    public string Detail { get; } = state.Detail;
    public bool IsConnected { get; } = state.Kind == "connected";
    public bool IsInfo { get; } = state.Kind == "info";
    public bool IsWarning { get; } = state.Kind == "warning";
    public bool IsError { get; } = state.Kind == "error";
}
