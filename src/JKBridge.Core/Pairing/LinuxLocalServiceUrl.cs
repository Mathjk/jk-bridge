namespace JKBridge.Core.Pairing;

/// <summary>
/// A Linux-local web address normalized for a disabled service draft.
/// Query strings and fragments are deliberately never retained.
/// </summary>
public sealed record LinuxLocalServiceUrl(
    string Scheme,
    int Port,
    string LaunchPath,
    bool RemovedQueryOrFragment)
{
    public string SanitizedUrl =>
        $"{Scheme}://127.0.0.1:{Port}{LaunchPath}";

    public static bool TryParse(
        string? value,
        out LinuxLocalServiceUrl? parsed,
        out string error)
    {
        parsed = null;
        error = string.Empty;
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) ||
            trimmed.Length > 2048 ||
            trimmed.Any(char.IsControl) ||
            !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            error = "Enter one valid absolute Linux-local URL.";
            return false;
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 ||
            !string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Port is < 1 or > 65535)
        {
            error = "Only http(s)://127.0.0.1:port or localhost URLs are accepted.";
            return false;
        }

        var path = string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
        if (!path.StartsWith('/') ||
            path.Length > 1024 ||
            path.Any(char.IsControl) ||
            path.Contains('\\'))
        {
            error = "The Linux-local URL path is invalid.";
            return false;
        }

        parsed = new LinuxLocalServiceUrl(
            scheme,
            uri.Port,
            path,
            uri.Query.Length != 0 || uri.Fragment.Length != 0);
        return true;
    }
}
