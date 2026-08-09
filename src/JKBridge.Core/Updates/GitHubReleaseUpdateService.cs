using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JKBridge.Core.Updates;

public interface IReleaseUpdateChecker
{
    Task<ReleaseUpdateCheckResult> CheckAsync(
        string currentVersion,
        CancellationToken cancellationToken = default);

    Task<ReleaseInstallerDownload> DownloadInstallerAsync(
        ReleaseInstallerAsset installer,
        string destinationDirectory,
        CancellationToken cancellationToken = default) =>
        Task.FromException<ReleaseInstallerDownload>(
            new NotSupportedException("This update checker does not download installers."));

    Task<ReleaseInstallerDownload> DownloadInstallerAsync(
        ReleaseInstallerAsset installer,
        string destinationDirectory,
        IProgress<ReleaseDownloadProgress>? progress,
        CancellationToken cancellationToken = default) =>
        DownloadInstallerAsync(installer, destinationDirectory, cancellationToken);
}

/// <summary>
/// Checks the fixed official JK Bridge GitHub Releases endpoints and validates
/// installer asset metadata before it is exposed to the desktop application.
/// </summary>
public sealed partial class GitHubReleaseUpdateService : IReleaseUpdateChecker, IDisposable
{
    public const int MaximumResponseBytes = 256 * 1024;
    public const int MaximumChecksumBytes = 4 * 1024;
    public const long MaximumInstallerBytes = 256L * 1024 * 1024;
    public static readonly Uri LatestReleaseMetadataUri = new(
        "https://api.github.com/repos/Mathjk/jk-bridge/releases/latest",
        UriKind.Absolute);
    public static readonly Uri PreviewReleasesMetadataUri = new(
        "https://api.github.com/repos/Mathjk/jk-bridge/releases?per_page=20",
        UriKind.Absolute);

    private static readonly MediaTypeWithQualityHeaderValue GitHubJson = new(
        "application/vnd.github+json");

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _downloadTimeout;

    public GitHubReleaseUpdateService(
        TimeSpan? requestTimeout = null,
        TimeSpan? downloadTimeout = null)
    {
        _requestTimeout = ValidateTimeout(requestTimeout ?? TimeSpan.FromSeconds(15));
        _downloadTimeout = ValidateDownloadTimeout(downloadTimeout ?? TimeSpan.FromMinutes(5));
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
        TimeSpan? requestTimeout = null,
        TimeSpan? downloadTimeout = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _requestTimeout = ValidateTimeout(requestTimeout ?? TimeSpan.FromSeconds(15));
        _downloadTimeout = ValidateDownloadTimeout(downloadTimeout ?? TimeSpan.FromMinutes(5));
    }

    public async Task<ReleaseInstallerDownload> DownloadInstallerAsync(
        ReleaseInstallerAsset installer,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        return await DownloadInstallerAsync(
                installer,
                destinationDirectory,
                progress: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ReleaseInstallerDownload> DownloadInstallerAsync(
        ReleaseInstallerAsset installer,
        string destinationDirectory,
        IProgress<ReleaseDownloadProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installer);
        var validated = ValidateInstallerAsset(installer);
        var destinationRoot = Path.GetFullPath(
            string.IsNullOrWhiteSpace(destinationDirectory)
                ? throw new ArgumentException(
                    "An update destination directory is required.",
                    nameof(destinationDirectory))
                : destinationDirectory);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_downloadTimeout);
        string? stagingDirectory = null;
        try
        {
            progress?.Report(new ReleaseDownloadProgress(
                ReleaseDownloadStage.FetchingChecksum));
            var checksumBytes = await DownloadSmallAssetAsync(
                    validated.ChecksumUri,
                    MaximumChecksumBytes,
                    timeout.Token)
                .ConfigureAwait(false);
            var expectedHash = ParseChecksum(
                checksumBytes,
                validated.FileName);

            Directory.CreateDirectory(destinationRoot);
            stagingDirectory = Path.Combine(destinationRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDirectory);
            var installerPath = Path.Combine(stagingDirectory, validated.FileName);
            var checksumPath = $"{installerPath}.sha256";
            await File.WriteAllBytesAsync(
                    checksumPath,
                    checksumBytes,
                    timeout.Token)
                .ConfigureAwait(false);

            progress?.Report(new ReleaseDownloadProgress(
                ReleaseDownloadStage.DownloadingInstaller));
            var actualHash = await DownloadInstallerFileAsync(
                    validated.DownloadUri,
                    installerPath,
                    progress,
                    timeout.Token)
                .ConfigureAwait(false);
            var installerBytes = new FileInfo(installerPath).Length;
            progress?.Report(new ReleaseDownloadProgress(
                ReleaseDownloadStage.VerifyingInstaller,
                installerBytes,
                installerBytes));
            var expectedHashBytes = Convert.FromHexString(expectedHash);
            var actualHashBytes = Convert.FromHexString(actualHash);
            if (!CryptographicOperations.FixedTimeEquals(expectedHashBytes, actualHashBytes))
            {
                throw new ReleaseUpdateException(
                    "The downloaded installer does not match its published SHA-256 checksum.");
            }

            return new(installerPath, actualHash, validated.Version);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            DeleteStagingDirectory(stagingDirectory);
            throw new ReleaseUpdateException("The installer download timed out.");
        }
        catch (HttpRequestException exception)
        {
            DeleteStagingDirectory(stagingDirectory);
            throw new ReleaseUpdateException("The installer download failed.", exception);
        }
        catch
        {
            DeleteStagingDirectory(stagingDirectory);
            throw;
        }
    }

    public async Task<ReleaseUpdateCheckResult> CheckAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        var current = ReleaseVersion.Parse(currentVersion, "current application version");
        var metadataUri = current.Prerelease.Count == 0
            ? LatestReleaseMetadataUri
            : PreviewReleasesMetadataUri;
        using var request = new HttpRequestMessage(HttpMethod.Get, metadataUri);
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
            ValidateResponse(response, metadataUri);
            var body = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            var metadata = ParseMetadata(body, includePrereleases: current.Prerelease.Count > 0);
            var latest = metadata.Version;
            var releaseUri = ValidateOfficialReleaseUri(metadata.HtmlUrl, metadata.TagName);
            var updateAvailable = latest.CompareTo(current) > 0;
            return new(
                updateAvailable,
                current.WithoutPrefix,
                latest.WithoutPrefix,
                metadata.Name,
                releaseUri,
                metadata.PublishedAt,
                metadata.Prerelease,
                metadata.Installer);
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

    private static TimeSpan ValidateDownloadTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "Release download timeout must be between zero and ten minutes.");
        }

        return timeout;
    }

    private async Task<byte[]> DownloadSmallAssetAsync(
        Uri uri,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var response = await SendAssetRequestAsync(uri, cancellationToken).ConfigureAwait(false);
        ValidateAssetResponse(response, maximumBytes);
        return await ReadBoundedAssetAsync(response.Content, maximumBytes, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string> DownloadInstallerFileAsync(
        Uri uri,
        string destinationPath,
        IProgress<ReleaseDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await SendAssetRequestAsync(uri, cancellationToken).ConfigureAwait(false);
        ValidateAssetResponse(response, MaximumInstallerBytes);
        var expectedBytes = response.Content.Headers.ContentLength;
        progress?.Report(new ReleaseDownloadProgress(
            ReleaseDownloadStage.DownloadingInstaller, 0, expectedBytes));
        await using var input = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        long nextProgressReport = 256 * 1024;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > MaximumInstallerBytes)
            {
                throw new ReleaseUpdateException("The installer asset is too large.");
            }

            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            if (total >= nextProgressReport ||
                (expectedBytes.HasValue && total >= expectedBytes.Value))
            {
                progress?.Report(new ReleaseDownloadProgress(
                    ReleaseDownloadStage.DownloadingInstaller,
                    total,
                    expectedBytes));
                nextProgressReport = total + (256 * 1024);
            }
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(new ReleaseDownloadProgress(
            ReleaseDownloadStage.DownloadingInstaller,
            total,
            expectedBytes));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private async Task<HttpResponseMessage> SendAssetRequestAsync(
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        var currentUri = initialUri;
        for (var redirect = 0; redirect <= 1; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            request.Headers.UserAgent.ParseAdd("JKBridge-Update-Download/1");
            var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri != currentUri)
            {
                response.Dispose();
                throw new ReleaseUpdateException(
                    "The installer response did not come from the requested official URL.");
            }

            if ((int)response.StatusCode is < 300 or >= 400)
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (redirect != 0 || location is null)
            {
                throw new ReleaseUpdateException("The installer download used an invalid redirect chain.");
            }

            var redirected = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
            if (!IsApprovedReleaseAssetCdnUri(redirected))
            {
                throw new ReleaseUpdateException(
                    "The installer download redirected outside GitHub's release asset host.");
            }

            currentUri = redirected;
        }

        throw new ReleaseUpdateException("The installer download used too many redirects.");
    }

    private static bool IsApprovedReleaseAssetCdnUri(Uri uri) =>
        uri.IsAbsoluteUri &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.Ordinal) &&
        uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Fragment);

    private static void ValidateAssetResponse(HttpResponseMessage response, long maximumBytes)
    {
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new ReleaseUpdateException(
                $"The installer asset endpoint returned HTTP {(int)response.StatusCode}.");
        }

        if (response.Content.Headers.ContentLength is long contentLength &&
            contentLength > maximumBytes)
        {
            throw new ReleaseUpdateException("The installer asset is too large.");
        }
    }

    private static async Task<byte[]> ReadBoundedAssetAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[maximumBytes + 1];
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

        if (total > maximumBytes)
        {
            throw new ReleaseUpdateException("The installer asset is too large.");
        }

        return buffer[..total];
    }

    private static string ParseChecksum(ReadOnlySpan<byte> content, string expectedFileName)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(content).TrimEnd('\r', '\n');
        }
        catch (DecoderFallbackException exception)
        {
            throw new ReleaseUpdateException("The installer checksum file is not valid UTF-8.", exception);
        }

        var separator = text.IndexOf("  ", StringComparison.Ordinal);
        if (separator != 64 ||
            text.Length != 66 + expectedFileName.Length ||
            !IsSha256Hex(text.AsSpan(0, 64)) ||
            !string.Equals(text[66..], expectedFileName, StringComparison.Ordinal))
        {
            throw new ReleaseUpdateException("The installer checksum file has an invalid format.");
        }

        return text[..64].ToLowerInvariant();
    }

    private static bool IsSha256Hex(ReadOnlySpan<char> value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static ReleaseInstallerAsset ValidateInstallerAsset(ReleaseInstallerAsset installer)
    {
        var version = ReleaseVersion.Parse(installer.Version, "installer release version");
        if (!string.Equals(installer.TagName, $"v{version.WithoutPrefix}", StringComparison.Ordinal))
        {
            throw new ReleaseUpdateException("The installer release tag is invalid.");
        }

        var expectedFileName =
            $"JKBridge-Setup-win-x64-{version.Major}.{version.Minor}.{version.Patch}.exe";
        var expectedDownload = new Uri(
            $"https://github.com/Mathjk/jk-bridge/releases/download/{installer.TagName}/{expectedFileName}");
        if (!string.Equals(installer.FileName, expectedFileName, StringComparison.Ordinal) ||
            installer.DownloadUri != expectedDownload ||
            installer.ChecksumUri != new Uri($"{expectedDownload}.sha256"))
        {
            throw new ReleaseUpdateException("The installer asset metadata is invalid.");
        }

        return installer;
    }

    private static void DeleteStagingDirectory(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void ValidateResponse(HttpResponseMessage response, Uri expectedUri)
    {
        if (response.RequestMessage?.RequestUri is not { } effectiveUri ||
            effectiveUri != expectedUri)
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

    private static OfficialReleaseMetadata ParseMetadata(
        ReadOnlyMemory<byte> document,
        bool includePrereleases)
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
            if (!includePrereleases)
            {
                if (json.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new ReleaseUpdateException("Release metadata must be a JSON object.");
                }

                return ParseMetadataObject(json.RootElement, allowPrerelease: false);
            }

            if (json.RootElement.ValueKind != JsonValueKind.Array ||
                json.RootElement.GetArrayLength() is 0 or > 20)
            {
                throw new ReleaseUpdateException(
                    "Preview release metadata must be a non-empty bounded JSON array.");
            }

            OfficialReleaseMetadata? selected = null;
            foreach (var item in json.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw new ReleaseUpdateException("Release metadata entries must be JSON objects.");
                }

                if (ReadRequiredBoolean(item, "draft"))
                {
                    continue;
                }

                var candidate = ParseMetadataObject(item, allowPrerelease: true);
                if (selected is null || candidate.Version.CompareTo(selected.Version) > 0)
                {
                    selected = candidate;
                }
            }

            return selected ?? throw new ReleaseUpdateException(
                "The official preview release list contains no published release.");
        }
        catch (JsonException exception)
        {
            throw new ReleaseUpdateException("Release metadata is not valid JSON.", exception);
        }
    }

    private static OfficialReleaseMetadata ParseMetadataObject(
        JsonElement root,
        bool allowPrerelease)
    {
        var tagName = ReadRequiredString(root, "tag_name", 64);
        var version = ReleaseVersion.Parse(tagName, "release tag");
        var htmlUrl = ReadRequiredString(root, "html_url", 256);
        var releaseName = ReadOptionalString(root, "name", 120) ?? tagName;
        var draft = ReadRequiredBoolean(root, "draft");
        var prerelease = ReadRequiredBoolean(root, "prerelease");
        if (draft || (!allowPrerelease && prerelease))
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
            version,
            SanitizeDisplayText(releaseName, 120),
            htmlUrl,
            publishedAt,
            prerelease,
            ParseInstallerAssets(root, tagName, version));
    }

    private static ReleaseInstallerAsset? ParseInstallerAssets(
        JsonElement root,
        string tagName,
        ReleaseVersion version)
    {
        if (!root.TryGetProperty("assets", out var assets) ||
            assets.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() > 32)
        {
            throw new ReleaseUpdateException("Release asset metadata is invalid or too large.");
        }

        var installerName = $"JKBridge-Setup-win-x64-{version.Major}.{version.Minor}.{version.Patch}.exe";
        var checksumName = $"{installerName}.sha256";
        Uri? installerUri = null;
        Uri? checksumUri = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object)
            {
                throw new ReleaseUpdateException("Release asset entries must be JSON objects.");
            }

            if (!asset.TryGetProperty("name", out var nameProperty) ||
                nameProperty.ValueKind != JsonValueKind.String ||
                nameProperty.GetString() is not { } name ||
                name.Length > 160)
            {
                continue;
            }

            if (!string.Equals(name, installerName, StringComparison.Ordinal) &&
                !string.Equals(name, checksumName, StringComparison.Ordinal))
            {
                continue;
            }

            var downloadUrl = ReadRequiredString(asset, "browser_download_url", 512);
            var expected = new Uri(
                $"https://github.com/Mathjk/jk-bridge/releases/download/{tagName}/{name}",
                UriKind.Absolute);
            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var actual) || actual != expected)
            {
                throw new ReleaseUpdateException(
                    "Release metadata contains a non-official installer asset URL.");
            }

            if (string.Equals(name, installerName, StringComparison.Ordinal))
            {
                if (installerUri is not null)
                {
                    throw new ReleaseUpdateException("Release metadata contains duplicate installer assets.");
                }

                installerUri = expected;
            }
            else
            {
                if (checksumUri is not null)
                {
                    throw new ReleaseUpdateException("Release metadata contains duplicate checksum assets.");
                }

                checksumUri = expected;
            }
        }

        return installerUri is not null && checksumUri is not null
            ? new ReleaseInstallerAsset(
                tagName,
                version.WithoutPrefix,
                installerName,
                installerUri,
                checksumUri)
            : null;
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
        ReleaseVersion Version,
        string Name,
        string HtmlUrl,
        DateTimeOffset? PublishedAt,
        bool Prerelease,
        ReleaseInstallerAsset? Installer);
}

public sealed record ReleaseInstallerAsset(
    string TagName,
    string Version,
    string FileName,
    Uri DownloadUri,
    Uri ChecksumUri);

public sealed record ReleaseInstallerDownload(
    string InstallerPath,
    string Sha256,
    string Version);

public enum ReleaseDownloadStage
{
    FetchingChecksum,
    DownloadingInstaller,
    VerifyingInstaller,
}

public sealed record ReleaseDownloadProgress(
    ReleaseDownloadStage Stage,
    long BytesReceived = 0,
    long? TotalBytes = null)
{
    public double? Percentage =>
        TotalBytes is > 0
            ? Math.Clamp(BytesReceived * 100d / TotalBytes.Value, 0d, 100d)
            : null;
}

public sealed record ReleaseUpdateCheckResult(
    bool UpdateAvailable,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseName,
    Uri ReleasePage,
    DateTimeOffset? PublishedAt,
    bool IsPrerelease = false,
    ReleaseInstallerAsset? Installer = null);

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
