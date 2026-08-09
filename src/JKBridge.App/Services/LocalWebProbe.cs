using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using JKBridge.Core.Routing;

namespace JKBridge.App.Services;

/// <summary>
/// Sends header-only, proxy-free probes to already-listening loopback ports.
/// Redirects are never followed and response bodies are never read.
/// </summary>
public sealed class LocalWebProbe : ILocalWebProbe, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly HttpClient _httpsClient;
    private readonly TimeSpan _attemptTimeout;
    private bool _disposed;

    public LocalWebProbe(TimeSpan? attemptTimeout = null)
    {
        _attemptTimeout = attemptTimeout ?? TimeSpan.FromMilliseconds(600);
        if (_attemptTimeout <= TimeSpan.Zero || _attemptTimeout > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(attemptTimeout),
                "Probe timeout must be greater than zero and no more than five seconds.");
        }

        _httpClient = CreateClient(allowUntrustedCertificate: false);
        _httpsClient = CreateClient(allowUntrustedCertificate: true);
    }

    public async Task<LocalWebProbeResult> ProbeAsync(
        LocalTcpListener listener,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(listener);
        if (!WindowsLocalServiceUrl.IsLoopbackHost(listener.Host) ||
            listener.Port is < 1 or > 65535)
        {
            throw new ArgumentException("Probe target must be a valid literal loopback listener.");
        }

        var http = await TryProbeAsync(
                _httpClient,
                listener,
                Uri.UriSchemeHttp,
                certificateUnverified: false,
                cancellationToken)
            .ConfigureAwait(false);
        if (http is not null)
        {
            return http;
        }

        var https = await TryProbeAsync(
                _httpsClient,
                listener,
                Uri.UriSchemeHttps,
                certificateUnverified: true,
                cancellationToken)
            .ConfigureAwait(false);
        return https ?? LocalWebProbeResult.TcpOnly();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
        _httpsClient.Dispose();
    }

    private async Task<LocalWebProbeResult?> TryProbeAsync(
        HttpClient client,
        LocalTcpListener listener,
        string scheme,
        bool certificateUnverified,
        CancellationToken cancellationToken)
    {
        var url = WindowsLocalServiceUrl.Build(listener.Host, scheme, listener.Port, "/");
        using var request = new HttpRequestMessage(HttpMethod.Head, url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_attemptTimeout);
        try
        {
            using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);
            var suggestedPath = SuggestedPath(response.Headers.Location, request.RequestUri!);
            var protocol = string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                ? LocalEndpointProtocol.Https
                : LocalEndpointProtocol.Http;
            var detail = protocol == LocalEndpointProtocol.Https
                ? "已确认 HTTPS；发现阶段未验证本机证书。"
                : "已确认 HTTP 网页服务。";
            return new(
                protocol,
                (int)response.StatusCode,
                suggestedPath,
                certificateUnverified,
                detail);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static HttpClient CreateClient(bool allowUntrustedCertificate)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(1),
            MaxConnectionsPerServer = 8,
            PooledConnectionLifetime = TimeSpan.FromSeconds(10),
            UseCookies = false,
            UseProxy = false,
        };
        if (allowUntrustedCertificate)
        {
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            };
        }

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private static string SuggestedPath(Uri? location, Uri requestUri)
    {
        if (location is null)
        {
            return "/";
        }

        Uri resolved;
        try
        {
            resolved = location.IsAbsoluteUri ? location : new Uri(requestUri, location);
        }
        catch (UriFormatException)
        {
            return "/";
        }

        var host = resolved.Host.Trim('[', ']');
        if (!WindowsLocalServiceUrl.IsLoopbackHost(host) ||
            resolved.Port != requestUri.Port ||
            !string.Equals(resolved.Scheme, requestUri.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return "/";
        }

        var path = string.IsNullOrEmpty(resolved.AbsolutePath) ? "/" : resolved.AbsolutePath;
        return ServiceOpenUrl.IsSafeLaunchPath(path) ? path : "/";
    }
}
