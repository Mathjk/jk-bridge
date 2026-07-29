namespace JKBridge.Core.Routing;

/// <summary>
/// Builds and decomposes browser URLs that stay on the Windows loopback listener.
/// </summary>
public static class ServiceOpenUrl
{
    public const int MaximumLaunchPathLength = 4096;

    public static string Build(string scheme, int localPort, string launchPath)
    {
        if (!IsSupportedScheme(scheme))
        {
            throw new ArgumentException("Scheme must be HTTP or HTTPS.", nameof(scheme));
        }

        if (localPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(localPort),
                localPort,
                "Port must be between 1 and 65535.");
        }

        if (!IsSafeLaunchPath(launchPath))
        {
            throw new ArgumentException(
                "Launch path must begin with a single slash and be a safe URI path, query, and fragment.",
                nameof(launchPath));
        }

        var normalizedScheme = scheme.ToLowerInvariant();
        var openUrl = $"{normalizedScheme}://127.0.0.1:{localPort}{launchPath}";
        if (!TryParse(openUrl, localPort, out _))
        {
            throw new ArgumentException(
                "Launch path does not form a valid loopback HTTP(S) URL.",
                nameof(launchPath));
        }

        return openUrl;
    }

    public static bool TryBuild(
        string? scheme,
        int localPort,
        string? launchPath,
        out string openUrl)
    {
        openUrl = string.Empty;
        if (!IsSupportedScheme(scheme) ||
            localPort is < 1 or > 65535 ||
            !IsSafeLaunchPath(launchPath))
        {
            return false;
        }

        var candidate = $"{scheme!.ToLowerInvariant()}://127.0.0.1:{localPort}{launchPath}";
        if (!TryParse(candidate, localPort, out _))
        {
            return false;
        }

        openUrl = candidate;
        return true;
    }

    public static bool TryParse(
        string? openUrl,
        int expectedLocalPort,
        out ServiceOpenUrlParts? parts)
    {
        parts = null;
        if (expectedLocalPort is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(openUrl) ||
            openUrl.Length > MaximumLaunchPathLength + 128 ||
            openUrl.Any(char.IsControl) ||
            openUrl.Contains('\\') ||
            !Uri.IsWellFormedUriString(openUrl, UriKind.Absolute) ||
            !Uri.TryCreate(openUrl, UriKind.Absolute, out var uri) ||
            !IsSupportedScheme(uri.Scheme) ||
            !string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.Port != expectedLocalPort)
        {
            return false;
        }

        var launchPath = uri.AbsolutePath + uri.Query + uri.Fragment;
        if (!IsSafeLaunchPath(launchPath))
        {
            return false;
        }

        parts = new(
            uri.Scheme.ToLowerInvariant(),
            expectedLocalPort,
            launchPath,
            uri);
        return true;
    }

    public static bool IsSafeLaunchPath(string? launchPath) =>
        !string.IsNullOrWhiteSpace(launchPath) &&
        launchPath.Length <= MaximumLaunchPathLength &&
        launchPath.StartsWith("/", StringComparison.Ordinal) &&
        !launchPath.StartsWith("//", StringComparison.Ordinal) &&
        !launchPath.Contains('\\') &&
        !launchPath.Any(char.IsControl) &&
        Uri.IsWellFormedUriString(
            $"http://127.0.0.1:1{launchPath}",
            UriKind.Absolute);

    private static bool IsSupportedScheme(string? scheme) =>
        string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
}

public sealed record ServiceOpenUrlParts(
    string Scheme,
    int LocalPort,
    string LaunchPath,
    Uri Uri);
