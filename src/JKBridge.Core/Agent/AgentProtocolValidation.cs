using System.Net;
using System.Text.RegularExpressions;
using JKBridge.Core.Configuration;
using JKBridge.Core.Models;
using JKBridge.Core.Security;

namespace JKBridge.Core.Agent;

internal static partial class AgentProtocolValidation
{
    [GeneratedRegex("^[a-z][a-z0-9-]{0,47}$", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceIdPattern();

    [GeneratedRegex("^[a-f0-9]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceIdPattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SshUserPattern();

    [GeneratedRegex("^SHA256:[A-Za-z0-9+/]{43}$", RegexOptions.CultureInvariant)]
    private static partial Regex FingerprintPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex RevisionPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex TailscaleNodeIdPattern();

    internal static AgentHealthResponse ValidateHealth(
        AgentHealthResponse? response,
        AgentEndpoint endpoint)
    {
        EnsureProtocol(response?.ProtocolVersion);
        if (response!.Status is not ("ok" or "needsBootstrap") ||
            !IsSafeRequiredText(response.AgentVersion, 64))
        {
            throw Invalid("Agent health response is invalid.");
        }
        return response;
    }

    internal static AgentBootstrapResponse ValidateBootstrap(
        AgentBootstrapResponse? response,
        AgentEndpoint endpoint)
    {
        if (response is null)
        {
            throw Invalid("Agent bootstrap response is missing.");
        }

        var identity = ValidateServerIdentity(new AgentServerIdentity(
            response.ProtocolVersion,
            response.AgentVersion,
            response.ServerHost,
            response.TailnetDnsSuffix,
            response.TailscaleIpv4,
            response.TailscaleNodeId,
            response.SshUser,
            response.SshPort,
            response.HostPublicKey,
            response.DisplayName,
            response.CatalogRevision), endpoint);
        return response with
        {
            ServerHost = identity.ServerHost,
            TailnetDnsSuffix = identity.TailnetDnsSuffix,
            HostPublicKey = identity.HostPublicKey,
            DisplayName = identity.DisplayName,
            CatalogRevision = identity.ServicesRevision,
        };
    }

    internal static AgentEnrollmentRequest ValidateEnrollmentRequest(AgentEnrollmentRequest? request)
    {
        if (request is null || !IsSafeRequiredText(request.DeviceLabel, 80) ||
            !Ed25519PublicKey.TryNormalize(request.ClientPublicKey, out var publicKey) ||
            request.RequestedServiceIds is null ||
            request.RequestedServiceIds.Count > AgentProtocol.MaximumServices)
        {
            throw Invalid("Agent enrollment request is invalid.");
        }

        var serviceIds = request.RequestedServiceIds
            .Select(id => id?.Trim() ?? string.Empty)
            .ToArray();
        if (serviceIds.Any(id => !ServiceIdPattern().IsMatch(id)) ||
            serviceIds.Distinct(StringComparer.Ordinal).Count() != serviceIds.Length)
        {
            throw Invalid("Agent enrollment request contains invalid or duplicate service IDs.");
        }

        return request with
        {
            DeviceLabel = request.DeviceLabel.Trim(),
            ClientPublicKey = publicKey,
            RequestedServiceIds = serviceIds,
        };
    }

    internal static AgentEnrollmentResponse ValidateEnrollmentResponse(
        AgentEnrollmentResponse? response,
        string expectedClientFingerprint,
        AgentEndpoint endpoint)
    {
        EnsureProtocol(response?.ProtocolVersion);
        if (response?.Server is null)
        {
            throw Invalid("Agent enrollment receipt does not include a server identity.");
        }

        var server = ValidateServerIdentity(response.Server, endpoint);
        if (!IsDeviceId(response!.DeviceId) ||
            !IsSafeRequiredText(response.DeviceLabel, 80) ||
            response.Role is not ("admin" or "device") ||
            response.DeviceCredential is null ||
            !string.Equals(
                response.ClientKeyFingerprint,
                expectedClientFingerprint,
                StringComparison.Ordinal) ||
            !IsRevision(response.ServicesRevision) ||
            !string.Equals(response.ServicesRevision, server.ServicesRevision, StringComparison.Ordinal))
        {
            throw Invalid("Agent enrollment receipt does not match this Windows device.");
        }

        var ports = NormalizePorts(response.PermitPorts, requireAtLeastOne: true);
        return response with { Server = server, PermitPorts = ports };
    }

    internal static AgentServerIdentity ValidateServerIdentity(
        AgentServerIdentity? response,
        AgentEndpoint endpoint)
    {
        EnsureProtocol(response?.ProtocolVersion);
        EnsureEndpointAddress(response!.TailscaleIpv4, endpoint);

        var host = NormalizeDnsName(response.ServerHost);
        var suffix = NormalizeDnsName(response.TailnetDnsSuffix);
        if (!IsDnsName(host) || !IsDnsName(suffix) ||
            !host.EndsWith('.' + suffix, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("Agent returned an invalid MagicDNS host or tailnet suffix.");
        }

        if (!IsSafeRequiredText(response.AgentVersion, 64) ||
            !IsSafeRequiredText(response.DisplayName, 128) ||
            !IsTailscaleNodeId(response.TailscaleNodeId) ||
            !SshUserPattern().IsMatch(response.SshUser ?? string.Empty) ||
            response.SshPort is < 1 or > 65535 ||
            !Ed25519PublicKey.TryNormalize(response.HostPublicKey, out var normalizedHostKey) ||
            !IsRevision(response.ServicesRevision))
        {
            throw Invalid("Agent response contains invalid server identity data.");
        }

        return response with
        {
            ServerHost = host,
            TailnetDnsSuffix = suffix,
            HostPublicKey = normalizedHostKey,
            DisplayName = response.DisplayName.Trim(),
        };
    }

    internal static AgentDeviceListResponse ValidateDevices(AgentDeviceListResponse? response)
    {
        EnsureProtocol(response?.ProtocolVersion);
        if (response!.Devices is null || response.Devices.Count > AgentProtocol.MaximumDevices)
        {
            throw Invalid("Agent device list is invalid.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in response.Devices)
        {
            if (device is null || !ids.Add(device.DeviceId) || !IsDeviceId(device.DeviceId) ||
                !IsSafeRequiredText(device.DeviceLabel, 80) ||
                !FingerprintPattern().IsMatch(device.ClientKeyFingerprint ?? string.Empty) ||
                device.Role is not ("admin" or "device") ||
                !IsTailscaleNodeId(device.TailscaleNodeId) ||
                !IsSafeRequiredText(device.TailscaleLogin, 254) ||
                device.ServiceIds is null ||
                device.ServiceIds.Count > AgentProtocol.MaximumServices ||
                device.ServiceIds.Any(id => !ServiceIdPattern().IsMatch(id ?? string.Empty)) ||
                device.ServiceIds.Distinct(StringComparer.Ordinal).Count() != device.ServiceIds.Count ||
                device.CreatedAt == default ||
                device.LastSeenAt is { } lastSeen && lastSeen < device.CreatedAt ||
                device.RevokedAt is { } revokedAt && revokedAt < device.CreatedAt)
            {
                throw Invalid("Agent device list contains an invalid device record.");
            }

            var ports = NormalizePorts(device.PermitPorts, requireAtLeastOne: !device.Revoked);
            if (device.Revoked && ports.Count != 0)
            {
                throw Invalid("A revoked Agent device cannot retain permitted ports.");
            }
        }

        return response;
    }

    internal static AgentCurrentDeviceResponse ValidateCurrentDevice(
        AgentCurrentDeviceResponse? response)
    {
        EnsureProtocol(response?.ProtocolVersion);
        if (response?.Device is null || !IsRevision(response.ServicesRevision))
        {
            throw Invalid("Agent current-device receipt is invalid.");
        }

        ValidateDevices(new AgentDeviceListResponse(
            response.ProtocolVersion,
            [response.Device]));
        if (response.Device.Revoked || response.Device.ServiceIds.Count == 0)
        {
            throw Invalid("Agent current-device receipt is not active.");
        }

        return response;
    }

    internal static AgentDeviceRevocationResponse ValidateRevocation(
        AgentDeviceRevocationResponse? response,
        string requestedDeviceId)
    {
        EnsureProtocol(response?.ProtocolVersion);
        if (!response!.Revoked || response.RevokedAt == default ||
            !string.Equals(response.DeviceId, requestedDeviceId, StringComparison.Ordinal) ||
            !IsSafeOptionalText(response.Note, 160))
        {
            throw Invalid("Agent revocation receipt does not match the requested device.");
        }

        return response;
    }

    internal static AgentDeviceRenameResponse ValidateRename(
        AgentDeviceRenameResponse? response,
        string requestedDeviceId,
        string requestedDeviceLabel)
    {
        EnsureProtocol(response?.ProtocolVersion);
        if (response?.Device is null || !response.Renamed ||
            !IsCanonicalDeviceLabel(response.PreviousDeviceLabel) ||
            string.Equals(response.PreviousDeviceLabel, requestedDeviceLabel, StringComparison.Ordinal) ||
            !string.Equals(response.Device.DeviceId, requestedDeviceId, StringComparison.Ordinal) ||
            !string.Equals(response.Device.DeviceLabel, requestedDeviceLabel, StringComparison.Ordinal))
        {
            throw Invalid("Agent device-rename receipt does not match the requested change.");
        }

        ValidateDevices(new AgentDeviceListResponse(
            response.ProtocolVersion,
            [response.Device]));
        return response;
    }

    internal static string ValidateCanonicalDeviceLabel(string? deviceLabel)
    {
        if (!IsCanonicalDeviceLabel(deviceLabel))
        {
            throw new AgentProtocolException(
                "Agent device label must be 1–80 safe characters without surrounding whitespace.",
                "invalid_request");
        }

        return deviceLabel!;
    }

    internal static AgentServiceCatalogResponse ValidateCatalog(AgentServiceCatalogResponse? response)
    {
        EnsureProtocol(response?.ProtocolVersion);
        if (!IsRevision(response!.Revision) || response.Services is null ||
            response.Services.Count is < 1 or > AgentProtocol.MaximumServices)
        {
            throw Invalid("Agent service catalog is invalid.");
        }

        var serviceIds = new HashSet<string>(StringComparer.Ordinal);
        var suggestedPorts = new HashSet<int>();
        var validated = new List<AgentServiceDefinition>(response.Services.Count);
        foreach (var service in response.Services)
        {
            var serviceId = service?.Id ?? string.Empty;
            if (service is null || !ServiceIdPattern().IsMatch(serviceId) ||
                !serviceIds.Add(serviceId) ||
                !IsSafeRequiredText(service.Name, 80) ||
                !IsSafeOptionalText(service.Description, 160) ||
                !IsSafeRequiredText(service.Icon, 32) ||
                service.SuggestedLocalPort is < 1 or > 65535 ||
                !suggestedPorts.Add(service.SuggestedLocalPort) ||
                !string.Equals(service.RemoteHost, ServiceDefinition.LoopbackHost, StringComparison.Ordinal) ||
                service.RemotePort is < 1 or > 65535 ||
                (!string.Equals(service.Scheme, "http", StringComparison.Ordinal) &&
                 !string.Equals(service.Scheme, "https", StringComparison.Ordinal)) ||
                !IsSafeOpenPath(service.OpenPath) ||
                !IsSafeHealthPath(service.HealthPath) ||
                service.HealthKind is not (
                    AgentHealthKinds.HttpStatus or AgentHealthKinds.JsonOk or AgentHealthKinds.TcpConnect))
            {
                throw Invalid("Agent service catalog contains an invalid service definition.");
            }

            ServiceDefinition localDefinition;
            try
            {
                localDefinition = service.ToServiceDefinition();
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                throw Invalid("Agent service catalog cannot be converted to a safe local route.", exception);
            }

            var result = ConfigValidator.Validate(new AppConfig { Services = [localDefinition] });
            if (!result.IsValid)
            {
                throw Invalid("Agent service catalog cannot be converted to a safe local route.");
            }

            validated.Add(service);
        }

        return response with { Services = validated };
    }

    internal static void EnsureDeviceId(string? deviceId)
    {
        if (!IsDeviceId(deviceId))
        {
            throw Invalid("Agent device ID is invalid.");
        }
    }

    internal static bool TryParseTailscaleAddress(string? value, out IPAddress? address)
    {
        address = null;
        if (!IPAddress.TryParse(value, out var parsed) || !AgentEndpoint.IsTailscaleIpv4(parsed))
        {
            return false;
        }

        address = parsed;
        return true;
    }

    internal static IReadOnlyList<int> NormalizePorts(
        IReadOnlyList<int>? ports,
        bool requireAtLeastOne)
    {
        if (ports is null ||
            ports.Count > AgentProtocol.MaximumPermitPorts ||
            requireAtLeastOne && ports.Count == 0 ||
            ports.Any(port => port is < 1 or > 65535) ||
            ports.Distinct().Count() != ports.Count)
        {
            throw Invalid("Agent response contains invalid permitted ports.");
        }

        return ports.Order().ToArray();
    }

    private static void EnsureProtocol(int? protocolVersion)
    {
        if (protocolVersion != AgentProtocol.Version)
        {
            throw Invalid("JK Bridge Agent protocol version is not supported.");
        }
    }

    private static void EnsureEndpointAddress(string? value, AgentEndpoint endpoint)
    {
        if (!TryParseTailscaleAddress(value, out var address) || !endpoint.Address.Equals(address))
        {
            throw Invalid("Agent response does not match the requested Tailscale IPv4 endpoint.");
        }
    }

    private static bool IsDeviceId(string? value) =>
        value is not null && DeviceIdPattern().IsMatch(value);

    private static bool IsCanonicalDeviceLabel(string? value) =>
        IsSafeRequiredText(value, 80) && string.Equals(value, value!.Trim(), StringComparison.Ordinal);

    private static bool IsRevision(string? value) =>
        value is not null && RevisionPattern().IsMatch(value);

    private static bool IsTailscaleNodeId(string? value) =>
        value is not null && TailscaleNodeIdPattern().IsMatch(value);

    private static bool IsSafeRequiredText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        !value.Any(char.IsControl);

    private static bool IsSafeOptionalText(string? value, int maximumLength) =>
        value is not null && value.Length <= maximumLength && !value.Any(char.IsControl);

    private static bool IsSafeOpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2048 ||
            !path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) ||
            path.Any(char.IsControl))
        {
            return false;
        }

        return Uri.TryCreate($"http://127.0.0.1:1{path}", UriKind.Absolute, out var uri) &&
               string.Equals(uri.Host, ServiceDefinition.LoopbackHost, StringComparison.Ordinal) &&
               uri.Port == 1 &&
               string.IsNullOrEmpty(uri.UserInfo);
    }

    private static bool IsSafeHealthPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.Length <= 2048 &&
        path.StartsWith('/') &&
        !path.StartsWith("//", StringComparison.Ordinal) &&
        !path.Contains('#') &&
        !path.Any(char.IsControl) &&
        Uri.IsWellFormedUriString(path, UriKind.Relative);

    private static string NormalizeDnsName(string? value) =>
        (value ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

    private static bool IsDnsName(string value) =>
        value.Length is > 0 and <= 253 && Uri.CheckHostName(value) == UriHostNameType.Dns;

    private static AgentProtocolException Invalid(string message, Exception? inner = null) =>
        new(message, "invalid_response", innerException: inner);
}
