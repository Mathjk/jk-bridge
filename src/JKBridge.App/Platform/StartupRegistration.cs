using System.Text;
using Microsoft.Win32;

namespace JKBridge.App.Platform;

public sealed record StartupRegistrationState(
    bool IsSupported,
    bool IsEnabled,
    bool MatchesCurrentExecutable,
    string? Command);

/// <summary>
/// Manages per-user startup through HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// Calls are safe no-ops on non-Windows systems.
/// </summary>
public sealed class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public StartupRegistration(string valueName = "JK Bridge")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        if (valueName.IndexOfAny(['\\', '/']) >= 0)
        {
            throw new ArgumentException("The startup value name cannot contain path separators.", nameof(valueName));
        }

        ValueName = valueName;
    }

    public string ValueName { get; }

    public bool IsSupported => OperatingSystem.IsWindows();

    public StartupRegistrationState GetState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new StartupRegistrationState(false, false, false, null);
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var command = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        var processPath = Environment.ProcessPath;
        var expected = string.IsNullOrWhiteSpace(processPath)
            ? null
            : BuildCommand(Path.GetFullPath(processPath), ["--background"]);
        return new StartupRegistrationState(
            true,
            !string.IsNullOrWhiteSpace(command),
            expected is not null && string.Equals(command, expected, StringComparison.OrdinalIgnoreCase),
            command);
    }

    public bool IsEnabled() => GetState().IsEnabled;

    /// <summary>
    /// Registers an executable and arguments for the current Windows user.
    /// Returns false without side effects on non-Windows systems.
    /// </summary>
    public bool Enable(string executablePath, params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return Enable(executablePath, (IEnumerable<string>)arguments);
    }

    public bool Enable(string executablePath, IEnumerable<string>? arguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The startup executable does not exist.", fullPath);
        }

        var command = BuildCommand(fullPath, arguments ?? []);
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the current user's startup registry key.");
        key.SetValue(ValueName, command, RegistryValueKind.String);
        return true;
    }

    public bool EnableCurrentProcess(params string[] arguments)
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The current executable path is unavailable.");
        return Enable(executablePath, arguments);
    }

    /// <summary>
    /// Removes the startup value. Returns false on non-Windows systems, otherwise true even if absent.
    /// </summary>
    public bool Disable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
        return true;
    }

    private static string BuildCommand(string executablePath, IEnumerable<string> arguments)
    {
        var builder = new StringBuilder(QuoteWindowsArgument(executablePath));
        foreach (var argument in arguments)
        {
            ArgumentNullException.ThrowIfNull(argument);
            builder.Append(' ');
            builder.Append(QuoteWindowsArgument(argument));
        }

        return builder.ToString();
    }

    // Implements the quoting rules used by CommandLineToArgvW / the Microsoft C runtime.
    private static string QuoteWindowsArgument(string value)
    {
        if (value.Length > 0 && value.All(character => !char.IsWhiteSpace(character) && character != '"'))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        var backslashes = 0;

        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
            }
            else
            {
                builder.Append('\\', backslashes);
                builder.Append(character);
            }

            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }
}
