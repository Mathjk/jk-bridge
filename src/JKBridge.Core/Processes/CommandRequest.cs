namespace JKBridge.Core.Processes;

public sealed record CommandRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout,
    string? WorkingDirectory = null,
    int MaxOutputCharacters = 1_000_000);

public sealed record CommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    TimeSpan Duration)
{
    public bool IsSuccess => !TimedOut && ExitCode == 0;
}
