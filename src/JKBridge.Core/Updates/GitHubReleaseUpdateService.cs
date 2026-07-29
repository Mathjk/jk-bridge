using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JKBridge.Core.Updates;

public interface IReleaseUpdateChecker
{
    Task<ReleaseUpdateCheckResult> CheckAsync(
        string currentVersion,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Performs a read-only update check against the one official JK Bridge GitHub
/// Releases endpoint. It never downloads an asset or starts an installer.
/// </summary>
public sealed partial class GitHubReleaseUpdateService : IReleaseUpdateChecker, IDisposable
{
    public const int MaximumResponseBytes = 256 * 1024;
    public static readonly Uri LatestReleaseMetadataUri = new(
        "https://api.github.com/repos/Mathjk/jk-bridge/releases/latest",
        UriKind.Absolute);

    private static readonly MediaTypeWithQualityHeaderValue GitHubJson = new(
        "application/vnd.github+json");

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _requestTimeout;

    public GitHubReleaseUpdateService(TimeSpan? requestTimeout = null)
    {
        _requestTimeout = ValidateTimeout(requestTimeout ?? TimeSpan.FromSeconds(15));
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            Credentials = null,
            MaxConnectionsPerServer = 2,
            MaxResponseHeadersLength = 32,
            PreAuthenticate = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false,
            UseProxy = false,
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _ownsHttpClient = true;
    }

    /// <summary>
    /// Test/host injection point. Production callers should use the default
    /// constructor so proxy, redirect, cookie, and credential behavior is fixed.
    /// Response validation remains active for injected transports.
    /// </summary>
    public GitHubReleaseUpdateService(
        HttpClient httpClient,
        TimeSpan? requestTimeout = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _requestTimeout = ValidateTimeout(requestTimeout ?? TimeSpan.FromSeconds(15));
    }

    public async Task<ReleaseUpdateCheckResult> CheckAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        var current = ReleaseVersion.Parse(currentVersion, "current application version");
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseMetadataUri);
        request.Headers.Accept.Add(GitHubJson);
        request.Headers.UserAgent.ParseAdd("JKBridge-Update-Check/1");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new ReleaseUpdateException("The release update check timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new ReleaseUpdateException("The release update check failed.", exception);
        }

        using (response)
        {
            ValidateResponse(response);
            var body = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            var metadata = ParseMetadata(body);
            var latest = ReleaseVersion.Parse(metadata.TagName, "release tag");
            var releaseUri = ValidateOfficialReleaseUri(metadata.HtmlUrl, metadata.TagName);
            var updateAvailable = latest.CompareTo(current) > 0;
            return new(
                updateAvailable,
                current.WithoutPrefix,
                latest.WithoutPrefix,
                metadata.Name,
                releaseUri,
                metadata.PublishedAt);
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static TimeSpan ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "Release update timeout must be between zero and one minute.");
        }

        return timeout;
    }

    private static void ValidateResponse(HttpResponseMessage response)
    {
        if (response.RequestMessage?.RequestUri is not { } effectiveUri ||
            effectiveUri != LatestReleaseMetadataUri)
        {
            throw new ReleaseUpdateException(
                "The release update response did not come from the fixed official metadata endpoint.");
        }

        if ((int)response.StatusCode is >= 300 and < 400)
        {
            throw new ReleaseUpdateException("Release update redirects are not allowed.");
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new ReleaseUpdateException(
                $"The release update endpoint returned HTTP {(int)response.StatusCode}.");
        }

        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new ReleaseUpdateException("The release metadata response is too large.");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null ||
            !(mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
              mediaType.Equals("application/vnd.github+json", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ReleaseUpdateException(
                "The release update endpoint returned an unsupported content type.");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var stream = await content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var buffer = new byte[MaximumResponseBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream
                .ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total > MaximumResponseBytes)
        {
            throw new ReleaseUpdateException("The release metadata response is too large.");
        }

        return buffer[..total];
    }

    private static OfficialReleaseMetadata ParseMetadata(ReadOnlyMemory<byte> document)
    {
        try
        {
            using var json = JsonDocument.Parse(
                document,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16,
                });
            if (json.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ReleaseUpdateException("Release metadata must be a JSON object.");
            }

            var root = json.RootElement;
            var tagName = ReadRequiredString(root, "tag_name", 64);
            var htmlUrl = ReadRequiredString(root, "html_url", 256);
            var releaseName = ReadOptionalString(root, "name", 120) ?? tagName;
            var draft = ReadRequiredBoolean(root, "draft");
            var prerelease = ReadRequiredBoolean(root, "prerelease");
            if (draft || prerelease)
            {
                throw new ReleaseUpdateException(
                    "The official latest-release endpoint returned a draft or prerelease.");
            }

            DateTimeOffset? publishedAt = null;
            var publishedText = ReadOptionalString(root, "published_at", 64);
            if (publishedText is not null)
            {
                if (!DateTimeOffset.TryParse(
                        publishedText,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var parsedPublishedAt))
                {
                    throw new ReleaseUpdateException(
                        "Release metadata contains an invalid publication timestamp.");
                }

                publishedAt = parsedPublishedAt;
            }

            return new(
                tagName,
                SanitizeDisplayText(releaseName, 120),
                htmlUrl,
                publishedAt);
        }
        catch (JsonException exception)
        {
            throw new ReleaseUpdateException("Release metadata is not valid JSON.", exception);
        }
    }

    private static string ReadRequiredString(JsonElement root, string name, int maximumLength)
    {
        if (!root.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            property.GetString() is not { } value ||
            string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength)
        {
            throw new ReleaseUpdateException(
                $"Release metadata field '{name}' is missing or invalid.");
        }

        return value;
    }

    private static string? ReadOptionalString(JsonElement root, string name, int maximumLength)
    {
        if (!root.TryGetProperty(name, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String ||
            property.GetString() is not { } value ||
            value.Length > maximumLength)
        {
            throw new ReleaseUpdateException(
                $"Release metadata field '{name}' is invalid.");
        }

        return value;
    }

    private static bool ReadRequiredBoolean(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property) ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ReleaseUpdateException(
                $"Release metadata field '{name}' is missing or invalid.");
        }

        return property.GetBoolean();
    }

    private static Uri ValidateOfficialReleaseUri(string value, string tagName)
    {
        var expected = new Uri(
            $"https://github.com/Mathjk/jk-bridge/releases/tag/{tagName}",
            UriKind.Absolute);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var actual) ||
            actual != expected ||
            !actual.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !actual.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !actual.IsDefaultPort ||
            !string.IsNullOrEmpty(actual.UserInfo) ||
            !string.IsNullOrEmpty(actual.Query) ||
            !string.IsNullOrEmpty(actual.Fragment))
        {
            throw new ReleaseUpdateException(
                "Release metadata contains a non-official release page URL.");
        }

        return expected;
    }

    private static string SanitizeDisplayText(string value, int maximumLength)
    {
        var sanitized = ControlCharactersRegex().Replace(value, " ").Trim();
        if (sanitized.Length == 0)
        {
            throw new ReleaseUpdateException("Release metadata contains an invalid release name.");
        }

        return sanitized.Length <= maximumLength
            ? sanitized
            : sanitized[..maximumLength];
    }

    [GeneratedRegex("[\\x00-\\x1F\\x7F]", RegexOptions.NonBacktracking)]
    private static partial Regex ControlCharactersRegex();

    private sealed record OfficialReleaseMetadata(
        string TagName,
        string Name,
        string HtmlUrl,
        DateTimeOffset? PublishedAt);
}

public sealed record ReleaseUpdateCheckResult(
    bool UpdateAvailable,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseName,
    Uri ReleasePage,
    DateTimeOffset? PublishedAt);

public sealed class ReleaseUpdateException(string message, Exception? innerException = null)
    : Exception(message, innerException);

internal sealed partial record ReleaseVersion(
    ulong Major,
    ulong Minor,
    ulong Patch,
    IReadOnlyList<string> Prerelease,
    string WithoutPrefix) : IComparable<ReleaseVersion>
{
    public static ReleaseVersion Parse(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
        {
            throw new ReleaseUpdateException($"The {description} is invalid.");
        }

        var match = SemanticVersionRegex().Match(value);
        if (!match.Success ||
            !ulong.TryParse(match.Groups["major"].Value, out var major) ||
            !ulong.TryParse(match.Groups["minor"].Value, out var minor) ||
            !ulong.TryParse(match.Groups["patch"].Value, out var patch))
        {
            throw new ReleaseUpdateException($"The {description} is invalid.");
        }

        var prereleaseText = match.Groups["prerelease"].Value;
        var prerelease = prereleaseText.Length == 0
            ? Array.Empty<string>()
            : prereleaseText.Split('.');
        if (prerelease.Any(identifier =>
                identifier.Length == 0 ||
                (identifier.Length > 1 && identifier[0] == '0' &&
                 identifier.All(char.IsAsciiDigit))))
        {
            throw new ReleaseUpdateException($"The {description} is invalid.");
        }

        var withoutPrefix = value[0] is 'v' or 'V' ? value[1..] : value;
        return new(major, minor, patch, prerelease, withoutPrefix);
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = Major.CompareTo(other.Major);
        if (core == 0)
        {
            core = Minor.CompareTo(other.Minor);
        }

        if (core == 0)
        {
            core = Patch.CompareTo(other.Patch);
        }

        if (core != 0)
        {
            return core;
        }

        if (Prerelease.Count == 0 || other.Prerelease.Count == 0)
        {
            return Prerelease.Count == other.Prerelease.Count
                ? 0
                : Prerelease.Count == 0 ? 1 : -1;
        }

        var comparedIdentifiers = Math.Min(Prerelease.Count, other.Prerelease.Count);
        for (var index = 0; index < comparedIdentifiers; index++)
        {
            var left = Prerelease[index];
            var right = other.Prerelease[index];
            var leftIsNumeric = ulong.TryParse(left, out var leftNumber);
            var rightIsNumeric = ulong.TryParse(right, out var rightNumber);
            int comparison;
            if (leftIsNumeric && rightIsNumeric)
            {
                comparison = leftNumber.CompareTo(rightNumber);
            }
            else if (leftIsNumeric != rightIsNumeric)
            {
                comparison = leftIsNumeric ? -1 : 1;
            }
            else
            {
                comparison = string.Compare(left, right, StringComparison.Ordinal);
            }

            if (comparison != 0)
            {
                return comparison;
            }
        }

        return Prerelease.Count.CompareTo(other.Prerelease.Count);
    }

    [GeneratedRegex(
        "\\A[vV]?(?<major>0|[1-9][0-9]*)\\.(?<minor>0|[1-9][0-9]*)\\.(?<patch>0|[1-9][0-9]*)(?:-(?<prerelease>[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SemanticVersionRegex();
}
