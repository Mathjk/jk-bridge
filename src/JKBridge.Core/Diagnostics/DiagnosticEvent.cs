namespace JKBridge.Core.Diagnostics;

public enum DiagnosticSeverity
{
    Debug,
    Information,
    Warning,
    Error,
}

public sealed record DiagnosticEvent(
    DateTimeOffset Timestamp,
    DiagnosticSeverity Severity,
    string Category,
    string Message,
    string? Detail = null);
