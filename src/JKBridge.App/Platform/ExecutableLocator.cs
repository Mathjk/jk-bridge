namespace JKBridge.App.Platform;

/// <summary>
/// Locates the native command-line tools used by JK Bridge without invoking a shell.
/// </summary>
public static class ExecutableLocator
{
    public static string? FindTailscale()
    {
        var candidates = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            AddIfSet(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Tailscale", "tailscale.exe");
            AddIfSet(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Tailscale", "tailscale.exe");

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            AddIfSet(candidates, localAppData, "Tailscale", "tailscale.exe");
        }

        return Find("tailscale", candidates);
    }

    public static string? FindOpenSsh()
    {
        var candidates = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            AddIfSet(candidates, windowsDirectory, "System32", "OpenSSH", "ssh.exe");

            // A 32-bit process uses Sysnative to bypass WOW64 file-system redirection.
            if (!Environment.Is64BitProcess && Environment.Is64BitOperatingSystem)
            {
                AddIfSet(candidates, windowsDirectory, "Sysnative", "OpenSSH", "ssh.exe");
            }

            AddIfSet(candidates, Environment.SystemDirectory, "OpenSSH", "ssh.exe");
        }
        else
        {
            candidates.Add("/usr/bin/ssh");
            candidates.Add("/usr/local/bin/ssh");
        }

        return Find("ssh", candidates);
    }

    public static string? FindSshKeygen()
    {
        var candidates = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            AddIfSet(candidates, windowsDirectory, "System32", "OpenSSH", "ssh-keygen.exe");
            if (!Environment.Is64BitProcess && Environment.Is64BitOperatingSystem)
            {
                AddIfSet(candidates, windowsDirectory, "Sysnative", "OpenSSH", "ssh-keygen.exe");
            }

            AddIfSet(candidates, Environment.SystemDirectory, "OpenSSH", "ssh-keygen.exe");
        }
        else
        {
            candidates.Add("/usr/bin/ssh-keygen");
            candidates.Add("/usr/local/bin/ssh-keygen");
        }

        return Find("ssh-keygen", candidates);
    }

    /// <summary>
    /// Searches preferred absolute paths first, followed by the current process PATH.
    /// Returns a normalized absolute path, or <see langword="null"/> when not found.
    /// </summary>
    public static string? Find(string executableName, IEnumerable<string>? preferredPaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);

        if (preferredPaths is not null)
        {
            foreach (var candidate in preferredPaths)
            {
                var resolved = ResolveCandidate(candidate);
                if (resolved is not null && seen.Add(resolved))
                {
                    return resolved;
                }
            }
        }

        foreach (var directory in EnumeratePathDirectories())
        {
            foreach (var fileName in EnumerateExecutableNames(executableName))
            {
                var resolved = ResolveCandidate(Path.Combine(directory, fileName));
                if (resolved is not null && seen.Add(resolved))
                {
                    return resolved;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumeratePathDirectories()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = entry.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(directory))
            {
                yield return directory;
            }
        }
    }

    private static IEnumerable<string> EnumerateExecutableNames(string executableName)
    {
        if (!OperatingSystem.IsWindows() || Path.HasExtension(executableName))
        {
            yield return executableName;
            yield break;
        }

        var pathExtensions = Environment.GetEnvironmentVariable("PATHEXT");
        if (string.IsNullOrWhiteSpace(pathExtensions))
        {
            pathExtensions = ".EXE;.COM;.BAT;.CMD";
        }

        foreach (var extension in pathExtensions.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = extension.Trim();
            if (!normalized.StartsWith('.'))
            {
                normalized = "." + normalized;
            }

            yield return executableName + normalized;
        }
    }

    private static string? ResolveCandidate(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"')));
            return File.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static void AddIfSet(List<string> candidates, string? root, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var segments = new string[parts.Length + 1];
        segments[0] = root;
        Array.Copy(parts, 0, segments, 1, parts.Length);
        candidates.Add(Path.Combine(segments));
    }
}
