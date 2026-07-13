using System.Diagnostics;
using System.Net;
using System.Text.Json;
using JKBridge.Core.Models;

namespace JKBridge.Core.Health;

public sealed class HttpServiceHealthProbe : IServiceHealthProbe, IDisposable
{
    private const int MaximumJsonBytes = 64 * 1024;

    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly TimeSpan _timeout;

    public HttpServiceHealthProbe(HttpClient? client = null, TimeSpan? timeout = null)
    {
        if (client is null)
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
            };
            _client = new HttpClient(handler);
            _ownsClient = true;
        }
        else
        {
            _client = client;
        }

        _timeout = timeout ?? TimeSpan.FromSeconds(2);
        if (_timeout <= TimeSpan.Zero && _timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Health timeout must be positive.");
        }
    }

    public async Task<ServiceHealth> CheckAsync(
        ServiceDefinition service,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);

        var startedAt = Stopwatch.GetTimestamp();
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_timeout != Timeout.InfiniteTimeSpan)
        {
            timeoutCancellation.CancelAfter(_timeout);
        }

        var effectiveToken = timeoutCancellation.Token;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildHealthUri(service));
            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, effectiveToken)
                .ConfigureAwait(false);
            var latency = Stopwatch.GetElapsedTime(startedAt);
            var status = (int)response.StatusCode;

            if (service.HealthKind == HealthCheckKind.JsonOk)
            {
                if (!response.IsSuccessStatusCode)
                {
                    return Unavailable(service, latency, status, $"HTTP {status}");
                }

                await using var stream = await response.Content
                    .ReadAsStreamAsync(effectiveToken)
                    .ConfigureAwait(false);
                var payload = await ReadBoundedAsync(stream, effectiveToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(payload);
                var ok = document.RootElement.ValueKind == JsonValueKind.Object &&
                         document.RootElement.TryGetProperty("ok", out var okValue) &&
                         okValue.ValueKind == JsonValueKind.True;
                return ok
                    ? Available(service, latency, status)
                    : Unavailable(service, latency, status, "Health response did not contain ok=true.");
            }

            return status is >= 200 and <= 499
                ? Available(service, latency, status)
                : Unavailable(service, latency, status, $"HTTP {status}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable(service, Stopwatch.GetElapsedTime(startedAt), null, "Health check timed out.");
        }
        catch (HttpRequestException exception)
        {
            return Unavailable(
                service,
                Stopwatch.GetElapsedTime(startedAt),
                exception.StatusCode is HttpStatusCode code ? (int)code : null,
                exception.Message);
        }
        catch (JsonException exception)
        {
            return Unavailable(
                service,
                Stopwatch.GetElapsedTime(startedAt),
                null,
                $"Invalid health JSON: {exception.Message}");
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private static Uri BuildHealthUri(ServiceDefinition service)
    {
        var scheme = Uri.TryCreate(service.OpenUrl, UriKind.Absolute, out var openUri)
            ? openUri.Scheme
            : Uri.UriSchemeHttp;
        return new UriBuilder(scheme, ServiceDefinition.LoopbackHost, service.LocalPort, service.HealthPath).Uri;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > MaximumJsonBytes)
            {
                throw new JsonException("Health JSON exceeds 64 KiB.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static ServiceHealth Available(
        ServiceDefinition service,
        TimeSpan latency,
        int status) =>
        new(
            service.Id,
            ServiceHealthState.Available,
            DateTimeOffset.Now,
            latency,
            status);

    private static ServiceHealth Unavailable(
        ServiceDefinition service,
        TimeSpan latency,
        int? status,
        string detail) =>
        new(
            service.Id,
            ServiceHealthState.Unavailable,
            DateTimeOffset.Now,
            latency,
            status,
            detail);
}
