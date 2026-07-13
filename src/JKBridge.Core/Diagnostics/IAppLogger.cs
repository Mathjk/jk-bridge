namespace JKBridge.Core.Diagnostics;

public interface IAppLogger
{
    event EventHandler<DiagnosticEvent>? EventWritten;

    IReadOnlyList<DiagnosticEvent> Snapshot();

    ValueTask WriteAsync(
        DiagnosticSeverity severity,
        string category,
        string message,
        string? detail = null,
        CancellationToken cancellationToken = default);
}
