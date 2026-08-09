using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JKBridge.Core.Updates;

namespace JKBridge.Core.Tests;

public sealed class GitHubReleaseUpdateServiceTests
{
    [Fact]
    public async Task Check_UsesOnlyFixedOfficialMetadataEndpointAndReportsUpdate()
    {
        var handler = new RecordingHandler(request => JsonResponse(new
        {
            tag_name = "v2.3.0",
            name = "JK Bridge 2.3.0",
            html_url = "https://github.com/Mathjk/jk-bridge/releases/tag/v2.3.0",
            draft = false,
            prerelease = false,
            published_at = "2026-07-29T08:30:00Z",
            body = "This untrusted release body is deliberately ignored.",
            assets = new[] { new { browser_download_url = "https://attacker.invalid/file.exe" } },
        }));
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var service = new GitHubReleaseUpdateService(httpClient);

        var result = await service.CheckAsync("2.2.0");

        Assert.True(result.UpdateAvailable);
        Assert.Equal("2.2.0", result.CurrentVersion);
        Assert.Equal("2.3.0", result.LatestVersion);
        Assert.Equal("JK Bridge 2.3.0", result.ReleaseName);
        Assert.Equal(
            "https://github.com/Mathjk/jk-bridge/releases/tag/v2.3.0",
            result.ReleasePage.AbsoluteUri);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 29, 8, 30, 0, TimeSpan.Zero),
            result.PublishedAt);
        Assert.Single(handler.Requests);
        var request = handler.Requests[0];
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(GitHubReleaseUpdateService.LatestReleaseMetadataUri, request.RequestUri);
        Assert.Null(request.Content);
        Assert.Null(request.Headers.Authorization);
        Assert.False(request.Headers.Contains("Cookie"));
        Assert.Contains(request.Headers.Accept, value =>
            value.MediaType == "application/vnd.github+json");
        Assert.Equal("JKBridge-Update-Check/1", request.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task Check_PreviewBuildSelectsNewestOfficialPreviewWithInstallerPair()
    {
        var handler = new RecordingHandler(request => JsonResponse(new[]
        {
            new
            {
                tag_name = "v2.2.0-preview.2",
                name = "JK Bridge 2.2.0 Preview 2",
                html_url = "https://github.com/Mathjk/jk-bridge/releases/tag/v2.2.0-preview.2",
                draft = false,
                prerelease = true,
                published_at = "2026-07-29T10:00:00Z",
                assets = new[]
                {
                    new
                    {
                        name = "JKBridge-Setup-win-x64-2.2.0.exe",
                        browser_download_url = "https://github.com/Mathjk/jk-bridge/releases/download/v2.2.0-preview.2/JKBridge-Setup-win-x64-2.2.0.exe",
                    },
                    new
                    {
                        name = "JKBridge-Setup-win-x64-2.2.0.exe.sha256",
                        browser_download_url = "https://github.com/Mathjk/jk-bridge/releases/download/v2.2.0-preview.2/JKBridge-Setup-win-x64-2.2.0.exe.sha256",
                    },
                },
            },
        }));
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var service = new GitHubReleaseUpdateService(httpClient);

        var result = await service.CheckAsync("2.2.0-preview.1");

        Assert.True(result.UpdateAvailable);
        Assert.True(result.IsPrerelease);
        Assert.Equal("2.2.0-preview.2", result.LatestVersion);
        Assert.NotNull(result.Installer);
        Assert.Equal(
            "JKBridge-Setup-win-x64-2.2.0.exe",
            result.Installer.FileName);
        Assert.Equal(GitHubReleaseUpdateService.PreviewReleasesMetadataUri, Assert.Single(handler.Requests).RequestUri);
    }

    [Fact]
    public async Task DownloadInstaller_VerifiesPublishedChecksumBeforeReturningFile()
    {
        var installerBytes = Encoding.UTF8.GetBytes("verified installer fixture");
        var installerName = "JKBridge-Setup-win-x64-2.2.0.exe";
        var installerUri = new Uri(
            $"https://github.com/Mathjk/jk-bridge/releases/download/v2.2.0-preview.2/{installerName}");
        var checksumUri = new Uri($"{installerUri}.sha256");
        var expectedHash = Convert.ToHexString(SHA256.HashData(installerBytes)).ToLowerInvariant();
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri == checksumUri)
            {
                return TextResponse($"{expectedHash}  {installerName}\n");
            }

            Assert.Equal(installerUri, request.RequestUri);
            return BinaryResponse(installerBytes);
        });
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var service = new GitHubReleaseUpdateService(httpClient);
        var destination = Path.Combine(Path.GetTempPath(), $"jkbridge-update-{Guid.NewGuid():N}");
        var progress = new List<ReleaseDownloadProgress>();
        var progressReporter = new RecordingProgress<ReleaseDownloadProgress>(progress.Add);

        try
        {
            var result = await service.DownloadInstallerAsync(
                new ReleaseInstallerAsset(
                    "v2.2.0-preview.2",
                    "2.2.0-preview.2",
                    installerName,
                    installerUri,
                    checksumUri),
                destination,
                progressReporter);

            Assert.Equal(installerBytes, await File.ReadAllBytesAsync(result.InstallerPath));
            Assert.Equal(expectedHash, result.Sha256);
            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal(ReleaseDownloadStage.FetchingChecksum, progress[0].Stage);
            Assert.Contains(progress, item =>
                item.Stage == ReleaseDownloadStage.DownloadingInstaller &&
                item.Percentage == 100);
            Assert.Equal(ReleaseDownloadStage.VerifyingInstaller, progress[^1].Stage);
        }
        finally
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Check_RejectsOfficialInstallerNameWithForeignDownloadUrl()
    {
        var handler = new RecordingHandler(_ => JsonResponse(new[]
        {
            new
            {
                tag_name = "v2.2.0-preview.2",
                name = "Preview 2",
                html_url = "https://github.com/Mathjk/jk-bridge/releases/tag/v2.2.0-preview.2",
                draft = false,
                prerelease = true,
                published_at = "2026-07-29T10:00:00Z",
                assets = new[]
                {
                    new
                    {
                        name = "JKBridge-Setup-win-x64-2.2.0.exe",
                        browser_download_url = "https://attacker.invalid/JKBridge-Setup-win-x64-2.2.0.exe",
                    },
                },
            },
        }));
        using var service = new GitHubReleaseUpdateService(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });

        var exception = await Assert.ThrowsAsync<ReleaseUpdateException>(() =>
            service.CheckAsync("2.2.0-preview.1"));

        Assert.Contains("non-official installer", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DownloadInstaller_ChecksumMismatchDeletesStagedExecutable()
    {
        var installerBytes = Encoding.UTF8.GetBytes("tampered installer");
        var installerName = "JKBridge-Setup-win-x64-2.2.0.exe";
        var installerUri = new Uri(
            $"https://github.com/Mathjk/jk-bridge/releases/download/v2.2.0-preview.2/{installerName}");
        var checksumUri = new Uri($"{installerUri}.sha256");
        var handler = new RecordingHandler(request => request.RequestUri == checksumUri
            ? TextResponse($"{new string('0', 64)}  {installerName}\n")
            : BinaryResponse(installerBytes));
        using var service = new GitHubReleaseUpdateService(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });
        var destination = Path.Combine(Path.GetTempPath(), $"jkbridge-update-mismatch-{Guid.NewGuid():N}");

        try
        {
            var exception = await Assert.ThrowsAsync<ReleaseUpdateException>(() =>
                service.DownloadInstallerAsync(
                    new ReleaseInstallerAsset(
                        "v2.2.0-preview.2",
                        "2.2.0-preview.2",
                        installerName,
                        installerUri,
                        checksumUri),
                    destination));

            Assert.Contains("does not match", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
        }
        finally
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DownloadInstaller_RejectsRedirectOutsideGitHubReleaseAssetCdn()
    {
        var installerName = "JKBridge-Setup-win-x64-2.2.0.exe";
        var installerUri = new Uri(
            $"https://github.com/Mathjk/jk-bridge/releases/download/v2.2.0-preview.2/{installerName}");
        var checksumUri = new Uri($"{installerUri}.sha256");
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://attacker.invalid/release") },
            Content = new ByteArrayContent([]),
        });
        using var service = new GitHubReleaseUpdateService(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });
        var destination = Path.Combine(Path.GetTempPath(), $"jkbridge-update-redirect-{Guid.NewGuid():N}");

        var exception = await Assert.ThrowsAsync<ReleaseUpdateException>(() =>
            service.DownloadInstallerAsync(
                new ReleaseInstallerAsset(
                    "v2.2.0-preview.2",
                    "2.2.0-preview.2",
                    installerName,
                    installerUri,
                    checksumUri),
                destination));

        Assert.Contains("outside GitHub", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(destination));
    }

    [Theory]
    [InlineData("2.3.0", "v2.3.0", false)]
    [InlineData("2.4.0", "v2.3.0", false)]
    [InlineData("2.3.0-preview.2", "v2.3.0", true)]
    [InlineData("2.2.9", "v2.3.0", true)]
    public async Task Check_ComparesSemanticVersions(
        string currentVersion,
        string latestTag,
        bool expectedUpdate)
    {
        var handler = new RecordingHandler(_ => currentVersion.Contains('-', StringComparison.Ordinal)
            ? ReleaseListResponse(latestTag)
            : ReleaseResponse(latestTag));
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var service = new GitHubReleaseUpdateService(httpClient);

        var result = await service.CheckAsync(currentVersion);

        Assert.Equal(expectedUpdate, result.UpdateAvailable);
    }

    [Fact]
    public async Task Check_RejectsRedirectWithoutFollowingIt()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://attacker.invalid/releases/latest") },
            Content = JsonContent("{}"),
        });
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var service = new GitHubReleaseUpdateService(httpClient);

        var exception = await Assert.ThrowsAsync<ReleaseUpdateException>(() =>
            service.CheckAsync("2.2.0"));

        Assert.Contains("redirect", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Check_RejectsNonOfficialReleasePageFromOtherwiseOfficialMetadata()
    {
        var handler = new RecordingHandler(_ => JsonResponse(new
        {
            tag_name = "v2.3.0",
            name = "JK Bridge 2.3.0",
            html_url = "https://attacker.invalid/Mathjk/jk-bridge/releases/tag/v2.3.0",
            draft = false,
            prerelease = false,
            published_at = "2026-07-29T08:30:00Z",
        }));
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var service = new GitHubReleaseUpdateService(httpClient);

        var exception = await Assert.ThrowsAsync<ReleaseUpdateException>(() =>
            service.CheckAsync("2.2.0"));

        Assert.Contains("non-official", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Check_RejectsOversizedResponseBeforeJsonParsing()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(
                new byte[GitHubReleaseUpdateService.MaximumResponseBytes + 1]),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var handler = new RecordingHandler(_ => response);
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var service = new GitHubReleaseUpdateService(httpClient);

        var exception = await Assert.ThrowsAsync<ReleaseUpdateException>(() =>
            service.CheckAsync("2.2.0"));

        Assert.Contains("too large", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/octet-stream")]
    public async Task Check_RejectsNonJsonContent(string mediaType)
    {
        var response = ReleaseResponse("v2.3.0");
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        var handler = new RecordingHandler(_ => response);
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var service = new GitHubReleaseUpdateService(httpClient);

        var exception = await Assert.ThrowsAsync<ReleaseUpdateException>(() =>
            service.CheckAsync("2.2.0"));

        Assert.Contains("content type", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("2.2")]
    [InlineData("2.02.0")]
    [InlineData("2.2.0-01")]
    [InlineData("not-a-version")]
    public async Task Check_RejectsInvalidCurrentVersionWithoutNetworkRequest(string version)
    {
        var handler = new RecordingHandler(_ => ReleaseResponse("v2.3.0"));
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var service = new GitHubReleaseUpdateService(httpClient);

        await Assert.ThrowsAsync<ReleaseUpdateException>(() => service.CheckAsync(version));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Check_RejectsDraftOrPrereleaseMetadata()
    {
        var handler = new RecordingHandler(_ => JsonResponse(new
        {
            tag_name = "v2.3.0",
            name = "JK Bridge 2.3.0 preview",
            html_url = "https://github.com/Mathjk/jk-bridge/releases/tag/v2.3.0",
            draft = false,
            prerelease = true,
            published_at = "2026-07-29T08:30:00Z",
        }));
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var service = new GitHubReleaseUpdateService(httpClient);

        var exception = await Assert.ThrowsAsync<ReleaseUpdateException>(() =>
            service.CheckAsync("2.2.0"));

        Assert.Contains("prerelease", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Check_RejectsResponseWhoseEffectiveRequestUriChanged()
    {
        var handler = new RecordingHandler(request =>
        {
            var response = ReleaseResponse("v2.3.0");
            response.RequestMessage = new HttpRequestMessage(
                HttpMethod.Get,
                "https://api.github.com/repos/attacker/project/releases/latest");
            return response;
        });
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var service = new GitHubReleaseUpdateService(httpClient);

        var exception = await Assert.ThrowsAsync<ReleaseUpdateException>(() =>
            service.CheckAsync("2.2.0"));

        Assert.Contains("fixed official", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpResponseMessage ReleaseResponse(string tag)
    {
        var isPrerelease = tag.Contains('-', StringComparison.Ordinal);
        return JsonResponse(new
        {
            tag_name = tag,
            name = $"JK Bridge {tag}",
            html_url = $"https://github.com/Mathjk/jk-bridge/releases/tag/{tag}",
            draft = false,
            prerelease = isPrerelease,
            published_at = "2026-07-29T08:30:00Z",
        });
    }

    private static HttpResponseMessage ReleaseListResponse(string tag)
    {
        var isPrerelease = tag.Contains('-', StringComparison.Ordinal);
        return JsonResponse(new[]
        {
            new
            {
                tag_name = tag,
                name = $"JK Bridge {tag}",
                html_url = $"https://github.com/Mathjk/jk-bridge/releases/tag/{tag}",
                draft = false,
                prerelease = isPrerelease,
                published_at = "2026-07-29T08:30:00Z",
            },
        });
    }

    private static HttpResponseMessage JsonResponse<T>(T payload)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent(JsonSerializer.Serialize(payload)),
        };
        return response;
    }

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private static HttpResponseMessage TextResponse(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "text/plain"),
    };

    private static HttpResponseMessage BinaryResponse(byte[] content)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return response;
    }

    private sealed class RecordingProgress<T>(Action<T> recorder) : IProgress<T>
    {
        public void Report(T value) => recorder(value);
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            var response = responseFactory(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}
