using System.Net;
using System.Net.Sockets;
using JKBridge.App.Platform;
using JKBridge.Core.Models;
using JKBridge.Core.Processes;
using JKBridge.Core.Tailscale;

namespace JKBridge.App.Services;

public enum TailscalePreflightKind
{
    Ready,
    ClientMissing,
    CommandFailed,
    NotRunning,
    WrongTailnet,
    PeerMissing,
    PeerAmbiguous,
    PeerOffline,
    NameResolutionFailed,
}

public sealed record TailscalePreflightResult(
    TailscalePreflightKind Kind,
    string Message,
    TailscaleStatusSnapshot? Status = null,
    TailscaleNode? Peer = null,
    string? TargetHost = null,
    bool UsingIpFallback = false,
    string? TechnicalDetail = null)
{
    public bool IsReady => Kind == TailscalePreflightKind.Ready;

    public bool IsRetryable => Kind is
        TailscalePreflightKind.CommandFailed or
        TailscalePreflightKind.NotRunning or
        TailscalePreflightKind.PeerMissing or
        TailscalePreflightKind.PeerOffline or
        TailscalePreflightKind.NameResolutionFailed;
}

public interface ITailscaleClient
{
    Task<TailscalePreflightResult> InspectAsync(
        AppConfig config,
        CancellationToken cancellationToken = default);
}

public interface IHostNameResolver
{
    Task<bool> ResolvesAsync(string host, CancellationToken cancellationToken = default);
}

public sealed class SystemHostNameResolver : IHostNameResolver
{
    public async Task<bool> ResolvesAsync(
        string host,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            return addresses.Length > 0;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>
/// Read-only adapter over the installed Tailscale CLI. It never logs or persists raw status JSON.
/// </summary>
public sealed class TailscaleClient : ITailscaleClient
{
    private readonly ICommandRunner _commandRunner;
    private readonly Func<string?> _locateExecutable;
    private readonly IHostNameResolver _hostNameResolver;
    private readonly TimeSpan _commandTimeout;

    public TailscaleClient(
        ICommandRunner commandRunner,
        Func<string?>? locateExecutable = null,
        IHostNameResolver? hostNameResolver = null,
        TimeSpan? commandTimeout = null)
    {
        _commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
        _locateExecutable = locateExecutable ?? ExecutableLocator.FindTailscale;
        _hostNameResolver = hostNameResolver ?? new SystemHostNameResolver();
        _commandTimeout = commandTimeout ?? TimeSpan.FromSeconds(10);
    }

    public async Task<TailscalePreflightResult> InspectAsync(
        AppConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var executable = _locateExecutable();
        if (string.IsNullOrWhiteSpace(executable))
        {
            return Failure(
                TailscalePreflightKind.ClientMissing,
                "Tailscale is not installed or tailscale.exe could not be found.");
        }

        CommandResult command;
        try
        {
            command = await _commandRunner.RunAsync(
                    new CommandRequest(executable, ["status", "--json"], _commandTimeout),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or
            IOException or
            System.ComponentModel.Win32Exception)
        {
            return Failure(
                TailscalePreflightKind.CommandFailed,
                "Tailscale status could not be read.",
                exception.Message);
        }

        if (!command.IsSuccess)
        {
            var detail = command.TimedOut
                ? "tailscale status timed out."
                : FirstUsefulDetail(command.StandardError, command.ExitCode);
            return Failure(
                TailscalePreflightKind.CommandFailed,
                "Tailscale status could not be read.",
                detail);
        }

        TailscaleStatusSnapshot status;
        try
        {
            status = TailscaleStatusParser.Parse(command.StandardOutput);
        }
        catch (TailscaleStatusParseException exception)
        {
            return Failure(
                TailscalePreflightKind.CommandFailed,
                "Tailscale returned an unreadable status response.",
                exception.Message);
        }

        if (!status.IsRunning)
        {
            return new(
                TailscalePreflightKind.NotRunning,
                "Sign in to Tailscale and wait until it is running.",
                status,
                TechnicalDetail: $"BackendState={status.BackendState}");
        }

        if (!TailscalePeerMatcher.MatchesExpectedTailnet(status, config.ExpectedTailnetDnsSuffix))
        {
            return new(
                TailscalePreflightKind.WrongTailnet,
                "This Windows device is signed in to a different Tailscale network.",
                status,
                TechnicalDetail:
                    $"Expected suffix '{config.ExpectedTailnetDnsSuffix}', current suffix '{status.Tailnet.MagicDnsSuffix}'.");
        }

        var match = TailscalePeerMatcher.FindExact(status, config.Server.Host);
        switch (match.Kind)
        {
            case TailscalePeerMatchKind.NotFound:
                return new(
                    TailscalePreflightKind.PeerMissing,
                    "The configured Linux device was not found in this tailnet.",
                    status);
            case TailscalePeerMatchKind.Ambiguous:
                return new(
                    TailscalePreflightKind.PeerAmbiguous,
                    "More than one Tailscale peer matched the configured Linux device.",
                    status);
            case TailscalePeerMatchKind.Offline:
                return new(
                    TailscalePreflightKind.PeerOffline,
                    "The Linux device is currently offline in Tailscale.",
                    status,
                    match.Peer);
        }

        var peer = match.Peer!;
        var magicDnsTarget = TailscaleStatusParser.NormalizeDnsName(config.Server.Host);
        if (await _hostNameResolver.ResolvesAsync(magicDnsTarget, cancellationToken).ConfigureAwait(false))
        {
            return new(
                TailscalePreflightKind.Ready,
                "Tailscale peer is online.",
                status,
                peer,
                magicDnsTarget);
        }

        var fallback = SelectIpv4Fallback(peer.TailscaleIps);
        return fallback is not null
            ? new(
                TailscalePreflightKind.Ready,
                "MagicDNS is unavailable; using the peer's current Tailscale address for this connection only.",
                status,
                peer,
                fallback,
                UsingIpFallback: true)
            : new(
                TailscalePreflightKind.NameResolutionFailed,
                "The Linux MagicDNS name could not be resolved and no IPv4 fallback is available.",
                status,
                peer);
    }

    private static TailscalePreflightResult Failure(
        TailscalePreflightKind kind,
        string message,
        string? detail = null) =>
        new(kind, message, TechnicalDetail: detail);

    private static string? SelectIpv4Fallback(IEnumerable<string> addresses)
    {
        foreach (var value in addresses)
        {
            if (IPAddress.TryParse(value, out var address) &&
                address.AddressFamily == AddressFamily.InterNetwork)
            {
                return address.ToString();
            }
        }

        return null;
    }

    private static string FirstUsefulDetail(string? error, int exitCode)
    {
        var detail = (error ?? string.Empty).Trim();
        if (detail.Length == 0)
        {
            return $"tailscale status exited with code {exitCode}.";
        }

        return detail.Length <= 2048 ? detail : detail[..2048];
    }
}
