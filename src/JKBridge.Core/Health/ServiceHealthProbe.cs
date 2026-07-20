using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using JKBridge.Core.Models;
using JKBridge.Core.Routing;

namespace JKBridge.Core.Health;

public sealed class ServiceHealthProbe : IServiceHealthProbe, IDisposable
{
    private readonly HttpServiceHealthProbe _httpProbe;
    private readonly TimeSpan _timeout;

    public ServiceHealthProbe(HttpClient? httpClient = null, TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(2);
        _httpProbe = new HttpServiceHealthProbe(httpClient, _timeout);
    }

    public Task<ServiceHealth> CheckAsync(
        ServiceDefinition service,
        CancellationToken cancellationToken = default) =>
        service.HealthKind == HealthCheckKind.TcpConnect
            ? CheckTcpAsync(service, cancellationToken)
            : _httpProbe.CheckAsync(service, cancellationToken);

    public void Dispose() => _httpProbe.Dispose();

    private async Task<ServiceHealth> CheckTcpAsync(
        ServiceDefinition service,
        CancellationToken cancellationToken)
    {
        var route = ServiceRouteDefinition.Evaluate(service).Route
                    ?? throw new ArgumentException("Service route is invalid.", nameof(service));
        var startedAt = Stopwatch.GetTimestamp();
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(_timeout);
        using var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            await client.ConnectAsync(
                    IPAddress.Loopback,
                    route.LocalPort,
                    timeoutCancellation.Token)
                .ConfigureAwait(false);
            return new(
                service.Id,
                ServiceHealthState.Available,
                DateTimeOffset.Now,
                Stopwatch.GetElapsedTime(startedAt),
                Detail: "TCP connection succeeded.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(
                service.Id,
                ServiceHealthState.Unavailable,
                DateTimeOffset.Now,
                Stopwatch.GetElapsedTime(startedAt),
                Detail: "TCP health check timed out.");
        }
        catch (SocketException exception)
        {
            return new(
                service.Id,
                ServiceHealthState.Unavailable,
                DateTimeOffset.Now,
                Stopwatch.GetElapsedTime(startedAt),
                Detail: exception.Message);
        }
    }
}
