using System.Net;
using System.Text;
using System.Text.Json;
using JKBridge.Core.Agent;
using JKBridge.Core.Security;

namespace JKBridge.Core.Tests;

public sealed class AgentClientTests
{
    private const string DeviceId = "0123456789abcdef0123456789abcdef";

    private const string EnrollmentCredentialText =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private const string DeviceCredentialText =
        "EEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE";

    private static readonly AgentEndpoint Endpoint =
        AgentEndpoint.Create(IPAddress.Parse("100.64.0.10"), 45871);

    [Theory]
    [InlineData("https://100.64.0.10:45871/")]
    [InlineData("http://linux.example.ts.net:45871/")]
    [InlineData("http://192.168.1.2:45871/")]
    [InlineData("http://user@100.64.0.10:45871/")]
    [InlineData("http://100.64.0.10:45871/v1")]
    [InlineData("http://100.64.0.10:45871/?token=secret")]
    [InlineData("http://100.64.0.10:45871/#fragment")]
    public void AgentEndpoint_RejectsAnythingExceptExactTailscaleHttpOrigin(string value)
    {
        Assert.False(AgentEndpoint.TryParse(value, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void AgentCredential_ToStringIsAlwaysRedacted()
    {
        var credential = AgentCredential.Parse(EnrollmentCredentialText);

        Assert.Equal("[REDACTED]", credential.ToString());
        Assert.Equal(EnrollmentCredentialText, credential.Export());
    }

    [Theory]
    [InlineData("1starts-with-digit")]
    [InlineData("UPPERCASE")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task EnrollAsync_RejectsServiceIdsOutsideLinuxCanonicalPattern(string serviceId)
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("HTTP must not be called"));
        using var client = CreateClient(handler);

        await Assert.ThrowsAsync<AgentProtocolException>(() => client.EnrollAsync(
            new AgentEnrollmentRequest("Office PC", TestConfigs.TestHostKey, [serviceId]),
            AgentCredential.Parse(EnrollmentCredentialText)));

        Assert.Null(handler.Request);
    }

    [Theory]
    [InlineData("device_12345678")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcde")]
    public async Task RevokeDeviceAsync_RejectsNonCanonicalDeviceId(string deviceId)
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("HTTP must not be called"));
        using var client = CreateClient(handler);

        await Assert.ThrowsAsync<AgentProtocolException>(() => client.RevokeDeviceAsync(
            deviceId,
            AgentCredential.Parse(DeviceCredentialText)));

        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task GetBootstrapAsync_ValidatesMagicDnsNodeIdentityAndNumericRevision()
    {
        var handler = new RecordingHandler(_ => JsonResponse(BootstrapPayload(servicesRevision: 12)));
        using var client = CreateClient(handler);

        var response = await client.GetBootstrapAsync();

        Assert.Equal(TestConfigs.LinuxHost, response.ServerHost);
        Assert.Equal("nodeLinux123", response.TailscaleNodeId);
        Assert.Equal("12", response.CatalogRevision);
        Assert.Equal(TestConfigs.TestHostKey, response.HostPublicKey);
        Assert.Equal("http://100.64.0.10:45871/v1/bootstrap", handler.Request!.RequestUri!.AbsoluteUri);
        Assert.Null(handler.Request.Headers.Authorization);
    }

    [Fact]
    public async Task EnrollAsync_SendsEnrollmentSecretOnlyInAuthorizationHeader()
    {
        var fingerprint = Ed25519PublicKey.ComputeFingerprint(TestConfigs.TestHostKey);
        var handler = new RecordingHandler(_ => JsonResponse(new
        {
            protocolVersion = 1,
            agentVersion = "0.1.0",
            serverHost = TestConfigs.LinuxHost,
            tailnetDnsSuffix = TestConfigs.TailnetSuffix,
            tailscaleIpv4 = "100.64.0.10",
            tailscaleNodeId = "nodeLinux123",
            sshUser = "alice",
            sshPort = 22,
            hostPublicKey = TestConfigs.TestHostKey,
            displayName = "Research Linux",
            deviceId = DeviceId,
            deviceLabel = "Office PC",
            role = "admin",
            deviceCredential = DeviceCredentialText,
            clientKeyFingerprint = fingerprint,
            permitPorts = new[] { 8081, 80 },
            servicesRevision = 5,
        }));
        using var client = CreateClient(handler);
        var enrollment = AgentCredential.Parse(EnrollmentCredentialText);

        var response = await client.EnrollAsync(
            new AgentEnrollmentRequest("Office PC", TestConfigs.TestHostKey, []),
            enrollment);

        Assert.Equal("JKBridge-Enrollment", handler.Request!.Headers.Authorization?.Scheme);
        Assert.Equal(EnrollmentCredentialText, handler.Request.Headers.Authorization?.Parameter);
        Assert.DoesNotContain(EnrollmentCredentialText, handler.Request.RequestUri!.AbsoluteUri);
        Assert.DoesNotContain(EnrollmentCredentialText, Encoding.UTF8.GetString(handler.Body!));
        Assert.Equal(DeviceCredentialText, response.DeviceCredential.Export());
        Assert.Equal([80, 8081], response.PermitPorts);
        Assert.Equal("5", response.ServicesRevision);
        using var requestJson = JsonDocument.Parse(handler.Body!);
        Assert.Equal("Office PC", requestJson.RootElement.GetProperty("deviceLabel").GetString());
        Assert.False(requestJson.RootElement.TryGetProperty("deviceName", out _));
        Assert.Empty(requestJson.RootElement.GetProperty("requestedServiceIds").EnumerateArray());
    }

    [Fact]
    public async Task GetServiceCatalogAsync_MapsImplicitLoopbackAndBuildsWindowsUrl()
    {
        var handler = new RecordingHandler(_ => JsonResponse(new
        {
            protocolVersion = 1,
            servicesRevision = 7,
            services = new[]
            {
                new
                {
                    id = "sub2api",
                    name = "Sub2API",
                    description = "API bridge",
                    icon = "Globe",
                    remotePort = 8081,
                    suggestedLocalPort = 18081,
                    scheme = "http",
                    openPath = "/dashboard?source=jk#accounts",
                    healthPath = "/health",
                    healthKind = "httpStatus",
                    enabledByDefault = true,
                },
            },
        }));
        using var client = CreateClient(handler);

        var response = await client.GetServiceCatalogAsync(
            AgentCredential.Parse(DeviceCredentialText));
        var service = Assert.Single(response.Services);
        var local = service.ToServiceDefinition(localPortOverride: 28081);

        Assert.Equal("7", response.Revision);
        Assert.Equal("127.0.0.1", service.RemoteHost);
        Assert.Equal(8081, service.RemotePort);
        Assert.Equal("http://127.0.0.1:28081/dashboard?source=jk#accounts", local.OpenUrl);
        Assert.Equal("Bearer", handler.Request!.Headers.Authorization?.Scheme);
        Assert.DoesNotContain(DeviceCredentialText, handler.Request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task GetLoopbackDiscoveryAsync_RequiresBearerAndValidatesBoundedCandidates()
    {
        var handler = new RecordingHandler(_ => JsonResponse(new
        {
            protocolVersion = 1,
            discoveredAt = DateTimeOffset.Parse("2026-08-09T12:00:00Z"),
            scope = "current-127.0.0.1-listeners",
            candidates = new[]
            {
                new
                {
                    port = 8384,
                    bindAddress = "127.0.0.1",
                    processName = "syncthing",
                    scheme = "http",
                    webDetected = true,
                    statusCode = (int?)200,
                    title = "Syncthing",
                    healthPath = "/",
                    suggestedTemplateId = "syncthing",
                    tlsCertificateVerified = (bool?)null,
                },
            },
            excludedKnownServiceCount = 2,
            note = "No disks or port ranges were scanned.",
        }));
        using var client = CreateClient(handler);

        var response = await client.GetLoopbackDiscoveryAsync(
            AgentCredential.Parse(DeviceCredentialText));

        var candidate = Assert.Single(response.Candidates);
        Assert.Equal(8384, candidate.Port);
        Assert.Equal("syncthing", candidate.SuggestedTemplateId);
        Assert.Equal("/v1/discovery", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", handler.Request.Headers.Authorization?.Scheme);
    }

    [Fact]
    public async Task GetLoopbackDiscoveryAsync_RejectsWildcardOrInconsistentMetadata()
    {
        var handler = new RecordingHandler(_ => JsonResponse(new
        {
            protocolVersion = 1,
            discoveredAt = DateTimeOffset.Parse("2026-08-09T12:00:00Z"),
            scope = "current-127.0.0.1-listeners",
            candidates = new[]
            {
                new
                {
                    port = 8384,
                    bindAddress = "0.0.0.0",
                    processName = "syncthing",
                    scheme = "http",
                    webDetected = false,
                    statusCode = (int?)200,
                    title = "Syncthing",
                    healthPath = "/",
                    suggestedTemplateId = "syncthing",
                    tlsCertificateVerified = (bool?)null,
                },
            },
            excludedKnownServiceCount = 0,
            note = "invalid fixture",
        }));
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<AgentProtocolException>(() =>
            client.GetLoopbackDiscoveryAsync(AgentCredential.Parse(DeviceCredentialText)));

        Assert.Equal("invalid_response", exception.ErrorCode);
    }

    [Fact]
    public async Task UpdateServiceCatalogAsync_SendsExpectedRevisionAndCanonicalLoopbackShape()
    {
        var handler = new RecordingHandler(_ => JsonResponse(new
        {
            protocolVersion = 1,
            servicesRevision = 8,
            services = new[]
            {
                new
                {
                    id = "sub2api",
                    name = "Sub2API",
                    description = "API bridge",
                    icon = "Globe",
                    remotePort = 8081,
                    suggestedLocalPort = 18081,
                    scheme = "http",
                    openPath = "/",
                    healthPath = "/health",
                    healthKind = "httpStatus",
                    enabledByDefault = true,
                },
            },
        }));
        using var client = CreateClient(handler);
        var service = new AgentServiceDefinition(
            "sub2api",
            "Sub2API",
            "API bridge",
            "Globe",
            EnabledByDefault: true,
            FavoriteByDefault: false,
            SuggestedLocalPort: 18081,
            RemoteHost: "127.0.0.1",
            RemotePort: 8081,
            Scheme: "http",
            OpenPath: "/",
            HealthPath: "/health",
            HealthKind: "httpStatus");

        var response = await client.UpdateServiceCatalogAsync(
            "7",
            [service],
            applyDefaultsToAllDevices: false,
            AgentCredential.Parse(DeviceCredentialText));

        Assert.Equal("8", response.Revision);
        Assert.Equal(HttpMethod.Put, handler.Request!.Method);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(7, body.RootElement.GetProperty("expectedRevision").GetInt64());
        Assert.False(body.RootElement.GetProperty("applyDefaultsToAllDevices").GetBoolean());
        var wireService = body.RootElement.GetProperty("services")[0];
        Assert.False(wireService.TryGetProperty("remoteHost", out _));
        Assert.False(wireService.TryGetProperty("favoriteByDefault", out _));
    }

    [Fact]
    public async Task GetServiceCatalogAsync_RejectsServerSuppliedNonLoopbackHost()
    {
        var handler = new RecordingHandler(_ => JsonResponse(new
        {
            protocolVersion = 1,
            servicesRevision = 1,
            services = new[]
            {
                new
                {
                    id = "unsafe",
                    name = "Unsafe",
                    description = "",
                    icon = "Globe",
                    remoteHost = "10.0.0.8",
                    remotePort = 80,
                    suggestedLocalPort = 8080,
                    scheme = "http",
                    openPath = "/",
                    healthPath = "/",
                    healthKind = "httpStatus",
                    enabledByDefault = true,
                },
            },
        }));
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<AgentProtocolException>(() =>
            client.GetServiceCatalogAsync(AgentCredential.Parse(DeviceCredentialText)));

        Assert.Equal("invalid_response", exception.ErrorCode);
    }

    [Fact]
    public async Task GetDevicesAsync_AndRevokeDeviceAsync_UseBearerAndSafePath()
    {
        var fingerprint = Ed25519PublicKey.ComputeFingerprint(TestConfigs.TestHostKey);
        var requests = new List<HttpRequestMessage>();
        var handler = new RecordingHandler(request =>
        {
            requests.Add(request);
            return request.RequestUri!.AbsolutePath.EndsWith("/revoke", StringComparison.Ordinal)
                ? JsonResponse(new
                {
                    protocolVersion = 1,
                    deviceId = DeviceId,
                    revoked = true,
                    revokedAt = DateTimeOffset.Parse("2026-07-29T12:00:00Z"),
                    note = "revoked by administrator",
                })
                : JsonResponse(new
                {
                    protocolVersion = 1,
                    devices = new[]
                    {
                        new
                        {
                            deviceId = DeviceId,
                            deviceLabel = "Office PC",
                            clientKeyFingerprint = fingerprint,
                            role = "admin",
                            tailscaleNodeId = "nodeWindows456",
                            tailscaleLogin = "owner@example.com",
                            serviceIds = new[] { "overleaf", "sub2api" },
                            permitPorts = new[] { 80, 8081 },
                            createdAt = DateTimeOffset.Parse("2026-07-28T12:00:00Z"),
                            lastSeenAt = DateTimeOffset.Parse("2026-07-29T11:59:00Z"),
                            revokedAt = (DateTimeOffset?)null,
                        },
                    },
                });
        });
        using var client = CreateClient(handler);
        var credential = AgentCredential.Parse(DeviceCredentialText);

        var devices = await client.GetDevicesAsync(credential);
        var revoked = await client.RevokeDeviceAsync(DeviceId, credential);

        Assert.Single(devices.Devices);
        Assert.True(revoked.Revoked);
        Assert.Equal($"/v1/devices/{DeviceId}/revoke", requests[1].RequestUri!.AbsolutePath);
        Assert.All(requests, request => Assert.Equal("Bearer", request.Headers.Authorization?.Scheme));
    }

    [Fact]
    public async Task GetCurrentDeviceAsync_ReturnsActiveSelfReceiptForNonAdminSync()
    {
        var fingerprint = Ed25519PublicKey.ComputeFingerprint(TestConfigs.TestHostKey);
        var handler = new RecordingHandler(_ => JsonResponse(new
        {
            protocolVersion = 1,
            servicesRevision = 9,
            device = new
            {
                deviceId = DeviceId,
                deviceLabel = "Laptop",
                clientKeyFingerprint = fingerprint,
                role = "device",
                tailscaleNodeId = "nodeWindows456",
                tailscaleLogin = "owner@example.com",
                serviceIds = new[] { "overleaf", "sub2api" },
                permitPorts = new[] { 80, 8081 },
                createdAt = DateTimeOffset.Parse("2026-07-28T12:00:00Z"),
                lastSeenAt = DateTimeOffset.Parse("2026-07-29T11:59:00Z"),
                revokedAt = (DateTimeOffset?)null,
            },
        }));
        using var client = CreateClient(handler);

        var response = await client.GetCurrentDeviceAsync(
            AgentCredential.Parse(DeviceCredentialText));

        Assert.Equal("9", response.ServicesRevision);
        Assert.Equal(DeviceId, response.Device.DeviceId);
        Assert.Equal("device", response.Device.Role);
        Assert.False(response.Device.Revoked);
        Assert.Equal("/v1/me", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", handler.Request.Headers.Authorization?.Scheme);
    }

    [Fact]
    public async Task GetCurrentDeviceAsync_RejectsRevokedSelfReceipt()
    {
        var fingerprint = Ed25519PublicKey.ComputeFingerprint(TestConfigs.TestHostKey);
        var handler = new RecordingHandler(_ => JsonResponse(new
        {
            protocolVersion = 1,
            servicesRevision = 9,
            device = new
            {
                deviceId = DeviceId,
                deviceLabel = "Laptop",
                clientKeyFingerprint = fingerprint,
                role = "device",
                tailscaleNodeId = "nodeWindows456",
                tailscaleLogin = "owner@example.com",
                serviceIds = Array.Empty<string>(),
                permitPorts = Array.Empty<int>(),
                createdAt = DateTimeOffset.Parse("2026-07-28T12:00:00Z"),
                lastSeenAt = DateTimeOffset.Parse("2026-07-29T11:59:00Z"),
                revokedAt = DateTimeOffset.Parse("2026-07-29T12:00:00Z"),
            },
        }));
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<AgentProtocolException>(() =>
            client.GetCurrentDeviceAsync(AgentCredential.Parse(DeviceCredentialText)));

        Assert.Equal("invalid_response", exception.ErrorCode);
    }

    [Fact]
    public async Task RenameDeviceAsync_SendsCanonicalLabelAndValidatesReceipt()
    {
        var fingerprint = Ed25519PublicKey.ComputeFingerprint(TestConfigs.TestHostKey);
        var handler = new RecordingHandler(_ => JsonResponse(new
        {
            protocolVersion = 1,
            renamed = true,
            previousDeviceLabel = "Old Laptop",
            device = new
            {
                deviceId = DeviceId,
                deviceLabel = "Travel Laptop",
                clientKeyFingerprint = fingerprint,
                role = "device",
                tailscaleNodeId = "nodeWindows456",
                tailscaleLogin = "owner@example.com",
                serviceIds = new[] { "overleaf", "sub2api" },
                permitPorts = new[] { 80, 8081 },
                createdAt = DateTimeOffset.Parse("2026-07-28T12:00:00Z"),
                lastSeenAt = DateTimeOffset.Parse("2026-07-29T11:59:00Z"),
                revokedAt = (DateTimeOffset?)null,
            },
        }));
        using var client = CreateClient(handler);

        var response = await client.RenameDeviceAsync(
            DeviceId,
            "Travel Laptop",
            AgentCredential.Parse(DeviceCredentialText));

        Assert.True(response.Renamed);
        Assert.Equal("Old Laptop", response.PreviousDeviceLabel);
        Assert.Equal("Travel Laptop", response.Device.DeviceLabel);
        Assert.Equal($"/v1/devices/{DeviceId}/rename", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", handler.Request.Headers.Authorization?.Scheme);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("Travel Laptop", body.RootElement.GetProperty("deviceLabel").GetString());
        Assert.Equal(2, body.RootElement.EnumerateObject().Count());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("line\nbreak")]
    public async Task RenameDeviceAsync_RejectsNonCanonicalLabelBeforeHttp(string label)
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("HTTP must not be called"));
        using var client = CreateClient(handler);

        await Assert.ThrowsAsync<AgentProtocolException>(() => client.RenameDeviceAsync(
            DeviceId,
            label,
            AgentCredential.Parse(DeviceCredentialText)));

        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task RenameDeviceAsync_RejectsReceiptForDifferentDeviceOrLabel()
    {
        var fingerprint = Ed25519PublicKey.ComputeFingerprint(TestConfigs.TestHostKey);
        var handler = new RecordingHandler(_ => JsonResponse(new
        {
            protocolVersion = 1,
            renamed = true,
            previousDeviceLabel = "Old Laptop",
            device = new
            {
                deviceId = "fedcba9876543210fedcba9876543210",
                deviceLabel = "Unexpected Label",
                clientKeyFingerprint = fingerprint,
                role = "device",
                tailscaleNodeId = "nodeWindows456",
                tailscaleLogin = "owner@example.com",
                serviceIds = new[] { "overleaf" },
                permitPorts = new[] { 80 },
                createdAt = DateTimeOffset.Parse("2026-07-28T12:00:00Z"),
                lastSeenAt = DateTimeOffset.Parse("2026-07-29T11:59:00Z"),
                revokedAt = (DateTimeOffset?)null,
            },
        }));
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<AgentProtocolException>(() => client.RenameDeviceAsync(
            DeviceId,
            "Travel Laptop",
            AgentCredential.Parse(DeviceCredentialText)));

        Assert.Equal("invalid_response", exception.ErrorCode);
    }

    [Fact]
    public async Task Client_RejectsProtocolMismatchAndChunkedOversizedBody()
    {
        var wrongVersion = new RecordingHandler(_ => JsonResponse(new
        {
            protocolVersion = 2,
            status = "ok",
            agentVersion = "0.1.0",
        }));
        using (var client = CreateClient(wrongVersion))
        {
            await Assert.ThrowsAsync<AgentProtocolException>(() => client.GetHealthAsync());
        }

        var oversized = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(new byte[65 * 1024])),
        });
        using (var client = CreateClient(oversized))
        {
            var exception = await Assert.ThrowsAsync<AgentProtocolException>(() => client.GetHealthAsync());
            Assert.Equal("response_too_large", exception.ErrorCode);
        }
    }

    [Fact]
    public async Task Client_RedactsCredentialFromErrorAndRejectsChangedResponseOrigin()
    {
        var errorHandler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = JsonContent(new
            {
                error = new
                {
                    code = "bad_token",
                    message = $"credential {DeviceCredentialText} rejected",
                },
            }),
        });
        using (var client = CreateClient(errorHandler))
        {
            var exception = await Assert.ThrowsAsync<AgentProtocolException>(() =>
                client.GetServiceCatalogAsync(AgentCredential.Parse(DeviceCredentialText)));
            Assert.DoesNotContain(DeviceCredentialText, exception.ToString(), StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", exception.Message, StringComparison.Ordinal);
        }

        var redirectHandler = new RecordingHandler(request =>
        {
            var response = JsonResponse(new { protocolVersion = 1 });
            response.RequestMessage = new HttpRequestMessage(
                request.Method,
                "http://100.64.0.11:45871/v1/services");
            return response;
        });
        using (var client = CreateClient(redirectHandler))
        {
            var exception = await Assert.ThrowsAsync<AgentProtocolException>(() =>
                client.GetServiceCatalogAsync(AgentCredential.Parse(DeviceCredentialText)));
            Assert.Equal("redirect_forbidden", exception.ErrorCode);
        }
    }

    private static AgentClient CreateClient(RecordingHandler handler)
    {
        var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return new AgentClient(Endpoint, httpClient);
    }

    private static object BootstrapPayload(object servicesRevision) => new
    {
        protocolVersion = 1,
        agentVersion = "0.1.0",
        serverHost = TestConfigs.LinuxHost,
        tailnetDnsSuffix = TestConfigs.TailnetSuffix,
        tailscaleIpv4 = "100.64.0.10",
        tailscaleNodeId = "nodeLinux123",
        sshUser = "alice",
        sshPort = 22,
        hostPublicKey = TestConfigs.TestHostKey + " host comment",
        displayName = "Research Linux",
        enrollmentAvailable = true,
        servicesRevision,
    };

    private static HttpResponseMessage JsonResponse(object value) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent(value),
        };

    private static HttpContent JsonContent(object value) =>
        new StringContent(
            JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Encoding.UTF8,
            "application/json");

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public byte[]? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var response = responseFactory(request);
            response.RequestMessage ??= request;
            return response;
        }
    }
}
