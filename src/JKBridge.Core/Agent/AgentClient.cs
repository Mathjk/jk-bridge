using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JKBridge.Core.Security;

namespace JKBridge.Core.Agent;

/// <summary>
/// Bounded HTTP client for the Agent API. The default transport never uses the
/// system proxy, redirects, cookies, or default credentials.
/// </summary>
public sealed class AgentClient : IDisposable
{
    private const int HardMaximumBodyBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly AgentEndpoint _endpoint;
    private readonly AgentClientOptions _options;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public AgentClient(AgentEndpoint endpoint, AgentClientOptions? options = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = ValidateOptions(options ?? new AgentClientOptions());

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            Credentials = null,
            MaxConnectionsPerServer = 4,
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
        _ownsClient = true;
    }

    /// <summary>
    /// Creates a client with an injected transport. Production callers should
    /// configure that transport with the same no-proxy/no-redirect policy as the
    /// default constructor; the request/response validation still applies.
    /// </summary>
    public AgentClient(
        AgentEndpoint endpoint,
        HttpClient httpClient,
        AgentClientOptions? options = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = ValidateOptions(options ?? new AgentClientOptions());
    }

    public async Task<AgentHealthResponse> GetHealthAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<AgentHealthResponse>(
                HttpMethod.Get,
                "v1/health",
                requestBody: null,
                authorization: null,
                cancellationToken)
            .ConfigureAwait(false);
        return AgentProtocolValidation.ValidateHealth(response, _endpoint);
    }

    public async Task<AgentBootstrapResponse> GetBootstrapAsync(
        CancellationToken cancellationToken = default)
    {
        var wireResponse = await SendAsync<AgentBootstrapWireResponse>(
                HttpMethod.Get,
                "v1/bootstrap",
                requestBody: null,
                authorization: null,
                cancellationToken)
            .ConfigureAwait(false);
        var response = MapBootstrap(wireResponse);
        return AgentProtocolValidation.ValidateBootstrap(response, _endpoint);
    }

    public async Task<AgentEnrollmentResponse> EnrollAsync(
        AgentEnrollmentRequest request,
        AgentCredential enrollmentCredential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(enrollmentCredential);
        var validatedRequest = AgentProtocolValidation.ValidateEnrollmentRequest(request);
        var wireRequest = new AgentEnrollmentWireRequest(
            AgentProtocol.Version,
            validatedRequest.DeviceLabel,
            validatedRequest.ClientPublicKey,
            validatedRequest.RequestedServiceIds);
        var wireResponse = await SendAsync<AgentEnrollmentWireResponse>(
                HttpMethod.Post,
                "v1/enroll",
                wireRequest,
                new AuthenticationHeaderValue(
                    AgentProtocol.EnrollmentAuthorizationScheme,
                    enrollmentCredential.RevealForAuthorizationHeader()),
                cancellationToken,
                enrollmentCredential)
            .ConfigureAwait(false);

        if (!AgentCredential.TryParse(wireResponse?.DeviceCredential, out var deviceCredential) ||
            deviceCredential is null ||
            deviceCredential.Equals(enrollmentCredential))
        {
            throw new AgentProtocolException(
                "Agent returned an invalid or reused device credential.",
                "invalid_response");
        }

        var server = new AgentBootstrapResponse(
            wireResponse!.ProtocolVersion,
            wireResponse.AgentVersion,
            wireResponse.ServerHost,
            wireResponse.TailnetDnsSuffix,
            wireResponse.TailscaleIpv4,
            wireResponse.TailscaleNodeId,
            wireResponse.SshUser,
            wireResponse.SshPort,
            wireResponse.HostPublicKey,
            wireResponse.DisplayName,
            EnrollmentAvailable: true,
            NormalizeRevision(wireResponse.ServicesRevision));
        var serverIdentity = new AgentServerIdentity(
            server.ProtocolVersion,
            server.AgentVersion,
            server.ServerHost,
            server.TailnetDnsSuffix,
            server.TailscaleIpv4,
            server.TailscaleNodeId,
            server.SshUser,
            server.SshPort,
            server.HostPublicKey,
            server.DisplayName,
            server.CatalogRevision);
        var response = new AgentEnrollmentResponse(
            wireResponse.ProtocolVersion,
            serverIdentity,
            wireResponse.DeviceId,
            wireResponse.DeviceLabel,
            wireResponse.Role,
            deviceCredential,
            wireResponse.ClientKeyFingerprint,
            wireResponse.PermitPorts,
            NormalizeRevision(wireResponse.ServicesRevision));
        return AgentProtocolValidation.ValidateEnrollmentResponse(
            response,
            Ed25519PublicKey.ComputeFingerprint(validatedRequest.ClientPublicKey),
            _endpoint);
    }

    public async Task<AgentDeviceListResponse> GetDevicesAsync(
        AgentCredential deviceCredential,
        CancellationToken cancellationToken = default)
    {
        var authorization = CreateDeviceAuthorization(deviceCredential);
        var response = await SendAsync<AgentDeviceListResponse>(
                HttpMethod.Get,
                "v1/devices",
                requestBody: null,
                authorization,
                cancellationToken,
                deviceCredential)
            .ConfigureAwait(false);
        return AgentProtocolValidation.ValidateDevices(response);
    }

    public async Task<AgentCurrentDeviceResponse> GetCurrentDeviceAsync(
        AgentCredential deviceCredential,
        CancellationToken cancellationToken = default)
    {
        var authorization = CreateDeviceAuthorization(deviceCredential);
        var wireResponse = await SendAsync<AgentCurrentDeviceWireResponse>(
                HttpMethod.Get,
                "v1/me",
                requestBody: null,
                authorization,
                cancellationToken,
                deviceCredential)
            .ConfigureAwait(false);
        if (wireResponse?.Device is null)
        {
            throw new AgentProtocolException(
                "JK Bridge Agent returned an empty current-device receipt.",
                "invalid_response");
        }

        return AgentProtocolValidation.ValidateCurrentDevice(new AgentCurrentDeviceResponse(
            wireResponse.ProtocolVersion,
            NormalizeRevision(wireResponse.ServicesRevision),
            wireResponse.Device));
    }

    public async Task<AgentDeviceRevocationResponse> RevokeDeviceAsync(
        string deviceId,
        AgentCredential deviceCredential,
        CancellationToken cancellationToken = default)
    {
        AgentProtocolValidation.EnsureDeviceId(deviceId);
        var authorization = CreateDeviceAuthorization(deviceCredential);
        var response = await SendAsync<AgentDeviceRevocationResponse>(
                HttpMethod.Post,
                $"v1/devices/{Uri.EscapeDataString(deviceId)}/revoke",
                new AgentProtocolVersionRequest(AgentProtocol.Version),
                authorization,
                cancellationToken,
                deviceCredential)
            .ConfigureAwait(false);
        return AgentProtocolValidation.ValidateRevocation(response, deviceId);
    }

    public async Task<AgentDeviceRenameResponse> RenameDeviceAsync(
        string deviceId,
        string deviceLabel,
        AgentCredential adminCredential,
        CancellationToken cancellationToken = default)
    {
        AgentProtocolValidation.EnsureDeviceId(deviceId);
        var validatedLabel = AgentProtocolValidation.ValidateCanonicalDeviceLabel(deviceLabel);
        var authorization = CreateDeviceAuthorization(adminCredential);
        var response = await SendAsync<AgentDeviceRenameResponse>(
                HttpMethod.Post,
                $"v1/devices/{Uri.EscapeDataString(deviceId)}/rename",
                new AgentDeviceRenameWireRequest(AgentProtocol.Version, validatedLabel),
                authorization,
                cancellationToken,
                adminCredential)
            .ConfigureAwait(false);
        return AgentProtocolValidation.ValidateRename(response, deviceId, validatedLabel);
    }

    public async Task<AgentLoopbackDiscoveryResponse> GetLoopbackDiscoveryAsync(
        AgentCredential adminCredential,
        CancellationToken cancellationToken = default)
    {
        var authorization = CreateDeviceAuthorization(adminCredential);
        var response = await SendAsync<AgentLoopbackDiscoveryResponse>(
                HttpMethod.Get,
                "v1/discovery",
                requestBody: null,
                authorization,
                cancellationToken,
                adminCredential)
            .ConfigureAwait(false);
        return ValidateLoopbackDiscovery(response);
    }

    public async Task<AgentServiceCatalogResponse> GetServiceCatalogAsync(
        AgentCredential deviceCredential,
        CancellationToken cancellationToken = default)
    {
        var authorization = CreateDeviceAuthorization(deviceCredential);
        var wireResponse = await SendAsync<AgentServiceCatalogWireResponse>(
                HttpMethod.Get,
                "v1/services",
                requestBody: null,
                authorization,
                cancellationToken,
                deviceCredential)
            .ConfigureAwait(false);
        var response = MapCatalog(wireResponse);
        return AgentProtocolValidation.ValidateCatalog(response);
    }

    public async Task<AgentServiceCatalogResponse> UpdateServiceCatalogAsync(
        string expectedRevision,
        IReadOnlyList<AgentServiceDefinition> services,
        bool applyDefaultsToAllDevices,
        AgentCredential deviceCredential,
        CancellationToken cancellationToken = default)
    {
        if (!long.TryParse(
                expectedRevision,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var numericRevision) ||
            numericRevision < 0)
        {
            throw new AgentProtocolException(
                "Agent protocol v1 requires a non-negative numeric expected services revision.",
                "invalid_request");
        }

        var validated = AgentProtocolValidation.ValidateCatalog(
            new AgentServiceCatalogResponse(AgentProtocol.Version, expectedRevision, services));
        var wireServices = validated.Services.Select(service => new AgentServiceWireDefinition(
            service.Id,
            service.Name,
            service.Description,
            service.Icon,
            service.RemotePort,
            service.SuggestedLocalPort,
            service.Scheme,
            service.OpenPath,
            service.HealthPath,
            service.HealthKind,
            service.EnabledByDefault)).ToArray();
        var authorization = CreateDeviceAuthorization(deviceCredential);
        var wireResponse = await SendAsync<AgentServiceCatalogWireResponse>(
                HttpMethod.Put,
                "v1/services",
                new AgentServiceCatalogUpdateWireRequest(
                    AgentProtocol.Version,
                    numericRevision,
                    wireServices,
                    applyDefaultsToAllDevices),
                authorization,
                cancellationToken,
                deviceCredential)
            .ConfigureAwait(false);
        return AgentProtocolValidation.ValidateCatalog(MapCatalog(wireResponse));
    }

    public async Task<AgentEnrollmentCodeResponse> CreateEnrollmentCodeAsync(
        int ttlSeconds,
        AgentCredential adminCredential,
        CancellationToken cancellationToken = default)
    {
        if (ttlSeconds is < 60 or > 3600)
        {
            throw new AgentProtocolException(
                "Enrollment-code lifetime must be between 60 and 3600 seconds.",
                "invalid_request");
        }

        var authorization = CreateDeviceAuthorization(adminCredential);
        var wireResponse = await SendAsync<AgentEnrollmentCodeWireResponse>(
                HttpMethod.Post,
                "v1/enrollment-codes",
                new AgentEnrollmentCodeWireRequest(AgentProtocol.Version, ttlSeconds),
                authorization,
                cancellationToken,
                adminCredential)
            .ConfigureAwait(false);
        if (wireResponse is null || wireResponse.ProtocolVersion != AgentProtocol.Version ||
            !AgentCredential.TryParse(wireResponse.EnrollmentCode, out var enrollmentCode) ||
            enrollmentCode is null || enrollmentCode.Equals(adminCredential) ||
            !DateTimeOffset.TryParseExact(
                wireResponse.ExpiresAt,
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var expiresAt))
        {
            throw new AgentProtocolException(
                "JK Bridge Agent returned an invalid enrollment code receipt.",
                "invalid_response");
        }

        return new AgentEnrollmentCodeResponse(
            wireResponse.ProtocolVersion,
            enrollmentCode,
            expiresAt);
    }

    public async Task<AgentRelayPlanResponse> PlanRelayAsync(
        AgentRelayPlanRequest request,
        AgentCredential adminCredential,
        CancellationToken cancellationToken = default)
    {
        var validated = AgentRelayValidation.ValidatePlanRequest(request);
        var authorization = CreateDeviceAuthorization(adminCredential);
        var wireResponse = await SendAsync<AgentRelayPlanWireResponse>(
                HttpMethod.Post,
                "v1/relays/plan",
                new AgentRelayPlanWireRequest(
                    AgentProtocol.Version,
                    validated.Spec,
                    validated.Service,
                    validated.GrantToAllDevices),
                authorization,
                cancellationToken,
                adminCredential)
            .ConfigureAwait(false);
        return AgentRelayValidation.ValidatePlanResponse(
            MapRelayPlan(wireResponse),
            "install",
            validated.Spec.Slug,
            validated.Spec);
    }

    public async Task<AgentRelayProbeResponse> ProbeRelayAsync(
        AgentRelaySpec spec,
        AgentCredential adminCredential,
        CancellationToken cancellationToken = default)
    {
        var validated = AgentRelayValidation.ValidateSpec(spec);
        var authorization = CreateDeviceAuthorization(adminCredential);
        var response = await SendAsync<AgentRelayProbeResponse>(
                HttpMethod.Post,
                "v1/relays/probe",
                new AgentRelayProbeWireRequest(AgentProtocol.Version, validated),
                authorization,
                cancellationToken,
                adminCredential)
            .ConfigureAwait(false);
        return AgentRelayValidation.ValidateProbeResponse(response, validated);
    }

    public async Task<AgentRelayApplyResponse> ApplyRelayAsync(
        AgentRelayApplyRequest request,
        AgentCredential adminCredential,
        CancellationToken cancellationToken = default)
    {
        if (request?.PlanToken is null)
        {
            throw new AgentProtocolException("Relay apply request is incomplete.", "invalid_request");
        }

        AgentRelayValidation.EnsureConfirmed(request.Confirmed);
        var validated = AgentRelayValidation.ValidatePlanRequest(request.Plan);
        var authorization = CreateDeviceAuthorization(adminCredential);
        var wireResponse = await SendAsync<AgentRelayApplyWireResponse>(
                HttpMethod.Post,
                "v1/relays/apply",
                new AgentRelayApplyWireRequest(
                    AgentProtocol.Version,
                    validated.Spec,
                    validated.Service,
                    validated.GrantToAllDevices,
                    request.PlanToken.Export(),
                    Confirmed: true),
                authorization,
                cancellationToken,
                adminCredential,
                request.PlanToken)
            .ConfigureAwait(false);
        return AgentRelayValidation.ValidateApplyResponse(
            MapRelayApply(wireResponse),
            "install",
            validated.Spec.Slug,
            validated.Spec,
            request.PlanToken);
    }

    public async Task<AgentRelayPlanResponse> PlanRelayDeletionAsync(
        AgentRelayDeletionPlanRequest request,
        AgentCredential adminCredential,
        CancellationToken cancellationToken = default)
    {
        var slug = AgentRelayValidation.ValidateSlug(request?.Slug);
        var authorization = CreateDeviceAuthorization(adminCredential);
        var wireResponse = await SendAsync<AgentRelayPlanWireResponse>(
                HttpMethod.Post,
                "v1/relays/delete-plan",
                new AgentRelayDeletePlanWireRequest(AgentProtocol.Version, slug),
                authorization,
                cancellationToken,
                adminCredential)
            .ConfigureAwait(false);
        return AgentRelayValidation.ValidatePlanResponse(
            MapRelayPlan(wireResponse),
            "delete",
            slug,
            expectedSpec: null);
    }

    public async Task<AgentRelayApplyResponse> DeleteRelayAsync(
        AgentRelayDeletionRequest request,
        AgentCredential adminCredential,
        CancellationToken cancellationToken = default)
    {
        if (request?.PlanToken is null)
        {
            throw new AgentProtocolException("Relay deletion request is incomplete.", "invalid_request");
        }

        AgentRelayValidation.EnsureConfirmed(request.Confirmed);
        var slug = AgentRelayValidation.ValidateSlug(request.Slug);
        var authorization = CreateDeviceAuthorization(adminCredential);
        var wireResponse = await SendAsync<AgentRelayApplyWireResponse>(
                HttpMethod.Post,
                "v1/relays/delete",
                new AgentRelayDeleteWireRequest(
                    AgentProtocol.Version,
                    slug,
                    request.PlanToken.Export(),
                    Confirmed: true),
                authorization,
                cancellationToken,
                adminCredential,
                request.PlanToken)
            .ConfigureAwait(false);
        return AgentRelayValidation.ValidateApplyResponse(
            MapRelayApply(wireResponse),
            "delete",
            slug,
            expectedSpec: null,
            request.PlanToken);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private static AuthenticationHeaderValue CreateDeviceAuthorization(AgentCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return new AuthenticationHeaderValue("Bearer", credential.RevealForAuthorizationHeader());
    }

    private async Task<TResponse?> SendAsync<TResponse>(
        HttpMethod method,
        string relativePath,
        object? requestBody,
        AuthenticationHeaderValue? authorization,
        CancellationToken cancellationToken,
        params IAgentSecretValue[] credentialsToRedact)
    {
        var requestUri = _endpoint.Resolve(relativePath);
        using var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        request.Headers.TryAddWithoutValidation(
            AgentProtocol.ProtocolVersionHeader,
            AgentProtocol.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Authorization = authorization;

        if (requestBody is not null)
        {
            byte[] bytes;
            try
            {
                bytes = JsonSerializer.SerializeToUtf8Bytes(requestBody, requestBody.GetType(), JsonOptions);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                throw new AgentProtocolException(
                    "Agent request could not be serialized.",
                    "invalid_request",
                    innerException: exception);
            }

            if (bytes.Length > _options.MaximumRequestBytes)
            {
                throw new AgentProtocolException(
                    "Agent request exceeds the allowed size.",
                    "request_too_large");
            }

            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AgentProtocolException(
                "Timed out while contacting JK Bridge Agent.",
                "timeout",
                innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            throw new AgentProtocolException(
                "Could not connect to JK Bridge Agent over Tailscale.",
                "connection_failed",
                innerException: exception);
        }

        using (response)
        {
            EnsureResponseOrigin(response, requestUri);
            byte[] bytes;
            try
            {
                bytes = await ReadLimitedAsync(response, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AgentProtocolException(
                    "Timed out while reading the JK Bridge Agent response.",
                    "timeout",
                    response.StatusCode,
                    exception);
            }

            if (!response.IsSuccessStatusCode)
            {
                var (code, detail) = TryReadError(bytes, credentialsToRedact);
                throw new AgentProtocolException(
                    detail ?? $"JK Bridge Agent returned HTTP {(int)response.StatusCode}.",
                    code ?? "http_error",
                    response.StatusCode);
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                throw new AgentProtocolException(
                    "JK Bridge Agent response is not application/json.",
                    "invalid_response");
            }

            try
            {
                return JsonSerializer.Deserialize<TResponse>(bytes, JsonOptions);
            }
            catch (JsonException exception)
            {
                throw new AgentProtocolException(
                    "JK Bridge Agent returned invalid or unexpected JSON.",
                    "invalid_response",
                    innerException: exception);
            }
        }
    }

    private static void EnsureResponseOrigin(HttpResponseMessage response, Uri requestedUri)
    {
        var actualUri = response.RequestMessage?.RequestUri;
        if (actualUri is null || !actualUri.Equals(requestedUri))
        {
            throw new AgentProtocolException(
                "JK Bridge Agent redirected the request; redirects are not permitted.",
                "redirect_forbidden",
                response.StatusCode);
        }
    }

    private async Task<byte[]> ReadLimitedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > 0 &&
            response.Content.Headers.ContentLength > _options.MaximumResponseBytes)
        {
            throw new AgentProtocolException(
                "JK Bridge Agent response exceeds the allowed size.",
                "response_too_large");
        }

        await using var source = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return destination.ToArray();
            }

            if (destination.Length + read > _options.MaximumResponseBytes)
            {
                throw new AgentProtocolException(
                    "JK Bridge Agent response exceeds the allowed size.",
                    "response_too_large");
            }

            destination.Write(buffer, 0, read);
        }
    }

    private static (string? Code, string? Detail) TryReadError(
        byte[] bytes,
        IReadOnlyList<IAgentSecretValue> credentialsToRedact)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("error", out var error))
            {
                return (null, null);
            }

            string? code = null;
            string? detail = null;
            if (error.ValueKind == JsonValueKind.String)
            {
                detail = error.GetString();
            }
            else if (error.ValueKind == JsonValueKind.Object)
            {
                if (error.TryGetProperty("code", out var codeElement) &&
                    codeElement.ValueKind == JsonValueKind.String)
                {
                    code = SanitizeErrorText(codeElement.GetString(), credentialsToRedact, 64);
                }

                if (error.TryGetProperty("message", out var messageElement) &&
                    messageElement.ValueKind == JsonValueKind.String)
                {
                    detail = messageElement.GetString();
                }
            }

            return (code, SanitizeErrorText(detail, credentialsToRedact, 300));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? SanitizeErrorText(
        string? value,
        IReadOnlyList<IAgentSecretValue> credentialsToRedact,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var result = new string(value
            .Where(character => !char.IsControl(character) || character == ' ')
            .ToArray());
        foreach (var credential in credentialsToRedact)
        {
            result = result.Replace(
                credential.RevealForRedaction(),
                "[REDACTED]",
                StringComparison.Ordinal);
        }

        result = result.Trim();
        return result.Length switch
        {
            0 => null,
            _ when result.Length <= maximumLength => result,
            _ => result[..maximumLength],
        };
    }

    private static AgentClientOptions ValidateOptions(AgentClientOptions options)
    {
        if (options.RequestTimeout < TimeSpan.FromSeconds(1) ||
            options.RequestTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Agent request timeout must be between 1 and 60 seconds.");
        }

        if (options.MaximumRequestBytes is < 1024 or > HardMaximumBodyBytes ||
            options.MaximumResponseBytes is < 1024 or > HardMaximumBodyBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"Agent body limits must be between 1024 and {HardMaximumBodyBytes} bytes.");
        }

        return options;
    }

    private static AgentBootstrapResponse MapBootstrap(AgentBootstrapWireResponse? response)
    {
        if (response is null)
        {
            throw new AgentProtocolException(
                "JK Bridge Agent returned an empty bootstrap response.",
                "invalid_response");
        }

        return new AgentBootstrapResponse(
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
            response.EnrollmentAvailable,
            NormalizeRevision(response.ServicesRevision));
    }

    private static AgentServiceCatalogResponse MapCatalog(AgentServiceCatalogWireResponse? response)
    {
        if (response?.Services is null)
        {
            throw new AgentProtocolException(
                "JK Bridge Agent returned an empty service catalog.",
                "invalid_response");
        }

        var services = response.Services.Select(service => new AgentServiceDefinition(
            service.Id,
            service.Name,
            service.Description,
            service.Icon,
            service.EnabledByDefault,
            FavoriteByDefault: false,
            service.SuggestedLocalPort,
            Models.ServiceDefinition.LoopbackHost,
            service.RemotePort,
            service.Scheme,
            service.OpenPath,
            service.HealthPath,
            service.HealthKind)).ToArray();
        return new AgentServiceCatalogResponse(
            response.ProtocolVersion,
            NormalizeRevision(response.ServicesRevision),
            services);
    }

    private static AgentRelayPlanResponse MapRelayPlan(AgentRelayPlanWireResponse? response)
    {
        if (response is null || !AgentRelayPlanToken.TryParse(response.PlanToken, out var planToken) ||
            planToken is null || response.RelayPlan is null || response.ServiceChange is null)
        {
            throw new AgentProtocolException(
                "JK Bridge Agent returned an invalid relay plan.",
                "invalid_response");
        }

        return new AgentRelayPlanResponse(
            response.ProtocolVersion,
            planToken,
            response.RelayPlan,
            response.ServiceChange,
            NormalizeRevision(response.NextServicesRevision));
    }

    private static AgentRelayApplyResponse MapRelayApply(AgentRelayApplyWireResponse? response)
    {
        if (response is null)
        {
            throw new AgentProtocolException(
                "JK Bridge Agent returned an empty relay mutation receipt.",
                "invalid_response");
        }

        return new AgentRelayApplyResponse(
            MapRelayPlan(new AgentRelayPlanWireResponse(
                response.ProtocolVersion,
                response.PlanToken,
                response.RelayPlan,
                response.ServiceChange,
                response.NextServicesRevision)),
            response.Applied,
            response.BackupCreated);
    }

    private static AgentLoopbackDiscoveryResponse ValidateLoopbackDiscovery(
        AgentLoopbackDiscoveryResponse? response)
    {
        if (response is null ||
            response.ProtocolVersion != AgentProtocol.Version ||
            response.Scope != "current-127.0.0.1-listeners" ||
            response.Candidates is null ||
            response.Candidates.Count > AgentProtocol.MaximumServices ||
            response.ExcludedKnownServiceCount < 0 ||
            string.IsNullOrWhiteSpace(response.Note) ||
            response.Note.Length > 256)
        {
            throw new AgentProtocolException(
                "JK Bridge Agent returned an invalid loopback discovery response.",
                "invalid_response");
        }

        var ports = new HashSet<int>();
        foreach (var candidate in response.Candidates)
        {
            if (candidate.Port is < 1 or > 65535 ||
                !ports.Add(candidate.Port) ||
                candidate.BindAddress != "127.0.0.1" ||
                candidate.ProcessName.Length > 64 ||
                candidate.ProcessName.Any(character =>
                    !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or '+')) ||
                candidate.Scheme is not ("http" or "https" or "tcp") ||
                candidate.StatusCode is not null and (< 100 or > 599) ||
                string.IsNullOrWhiteSpace(candidate.Title) ||
                candidate.Title != candidate.Title.Trim() ||
                candidate.Title.Length > 120 ||
                candidate.Title.Any(char.IsControl) ||
                candidate.HealthPath != "/" ||
                candidate.SuggestedTemplateId.Length is < 1 or > 48 ||
                candidate.SuggestedTemplateId[0] is < 'a' or > 'z' ||
                candidate.SuggestedTemplateId.Any(character =>
                    character is not (>= 'a' and <= 'z') and
                    not (>= '0' and <= '9') and
                    not '-'))
            {
                throw new AgentProtocolException(
                    "JK Bridge Agent returned an unsafe loopback discovery candidate.",
                    "invalid_response");
            }

            if (candidate.WebDetected != !candidate.Scheme.Equals("tcp", StringComparison.Ordinal) ||
                candidate.Scheme == "https" && candidate.TlsCertificateVerified is not false ||
                candidate.Scheme != "https" && candidate.TlsCertificateVerified is not null)
            {
                throw new AgentProtocolException(
                    "JK Bridge Agent returned inconsistent loopback probe metadata.",
                    "invalid_response");
            }
        }

        return response;
    }

    private static string NormalizeRevision(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out var numeric) && numeric >= 0)
        {
            return numeric.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? string.Empty;
            if (text.Length is >= 1 and <= 128 &&
                char.IsAsciiLetterOrDigit(text[0]) &&
                text.All(character =>
                    char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-'))
            {
                return text;
            }
        }

        throw new AgentProtocolException(
            "JK Bridge Agent returned an invalid services revision.",
            "invalid_response");
    }

    private sealed record AgentProtocolVersionRequest(int ProtocolVersion);

    private sealed record AgentDeviceRenameWireRequest(
        int ProtocolVersion,
        string DeviceLabel);

    private sealed record AgentEnrollmentWireRequest(
        int ProtocolVersion,
        string DeviceLabel,
        string ClientPublicKey,
        IReadOnlyList<string> RequestedServiceIds);

    private sealed record AgentEnrollmentWireResponse(
        int ProtocolVersion,
        string AgentVersion,
        string ServerHost,
        string TailnetDnsSuffix,
        string TailscaleIpv4,
        string TailscaleNodeId,
        string SshUser,
        int SshPort,
        string HostPublicKey,
        string DisplayName,
        string DeviceId,
        string DeviceLabel,
        string Role,
        string DeviceCredential,
        string ClientKeyFingerprint,
        IReadOnlyList<int> PermitPorts,
        JsonElement ServicesRevision);

    private sealed record AgentBootstrapWireResponse(
        int ProtocolVersion,
        string AgentVersion,
        string ServerHost,
        string TailnetDnsSuffix,
        string TailscaleIpv4,
        string TailscaleNodeId,
        string SshUser,
        int SshPort,
        string HostPublicKey,
        string DisplayName,
        bool EnrollmentAvailable,
        JsonElement ServicesRevision);

    private sealed record AgentServiceCatalogWireResponse(
        int ProtocolVersion,
        JsonElement ServicesRevision,
        IReadOnlyList<AgentServiceWireDefinition> Services);

    private sealed record AgentCurrentDeviceWireResponse(
        int ProtocolVersion,
        JsonElement ServicesRevision,
        AgentDeviceInfo Device);

    private sealed record AgentServiceCatalogUpdateWireRequest(
        int ProtocolVersion,
        long ExpectedRevision,
        IReadOnlyList<AgentServiceWireDefinition> Services,
        bool ApplyDefaultsToAllDevices);

    private sealed record AgentServiceWireDefinition(
        string Id,
        string Name,
        string Description,
        string Icon,
        int RemotePort,
        int SuggestedLocalPort,
        string Scheme,
        string OpenPath,
        string HealthPath,
        string HealthKind,
        bool EnabledByDefault);

    private sealed record AgentEnrollmentCodeWireRequest(int ProtocolVersion, int TtlSeconds);

    private sealed record AgentEnrollmentCodeWireResponse(
        int ProtocolVersion,
        string EnrollmentCode,
        string ExpiresAt);

    private sealed record AgentRelayPlanWireRequest(
        int ProtocolVersion,
        AgentRelaySpec Spec,
        AgentRelayServicePresentation Service,
        bool GrantToAllDevices);

    private sealed record AgentRelayApplyWireRequest(
        int ProtocolVersion,
        AgentRelaySpec Spec,
        AgentRelayServicePresentation Service,
        bool GrantToAllDevices,
        string PlanToken,
        bool Confirmed);

    private sealed record AgentRelayProbeWireRequest(
        int ProtocolVersion,
        AgentRelaySpec Spec);

    private sealed record AgentRelayDeletePlanWireRequest(int ProtocolVersion, string Slug);

    private sealed record AgentRelayDeleteWireRequest(
        int ProtocolVersion,
        string Slug,
        string PlanToken,
        bool Confirmed);

    private sealed record AgentRelayPlanWireResponse(
        int ProtocolVersion,
        string PlanToken,
        AgentRelayPlan RelayPlan,
        AgentRelayServiceChange ServiceChange,
        JsonElement NextServicesRevision);

    private sealed record AgentRelayApplyWireResponse(
        int ProtocolVersion,
        string PlanToken,
        AgentRelayPlan RelayPlan,
        AgentRelayServiceChange ServiceChange,
        JsonElement NextServicesRevision,
        bool Applied,
        bool BackupCreated);
}
