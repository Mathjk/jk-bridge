using System.Net;
using System.Text;
using System.Text.Json;
using JKBridge.Core.Agent;

namespace JKBridge.Core.Tests;

public sealed class AgentRelayClientTests
{
    private const string AdminCredentialText =
        "EEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE";

    private const string EnrollmentCodeText =
        "IIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIIII";

    private const string PlanTokenText =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private const string OtherPlanTokenText =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private const string DeviceId = "0123456789abcdef0123456789abcdef";

    private static readonly AgentEndpoint Endpoint =
        AgentEndpoint.Create(IPAddress.Parse("100.64.0.10"), 45871);

    [Fact]
    public async Task PlanRelayAsync_NormalizesExactTargetAndReturnsRedactedBoundToken()
    {
        var expectedSpec = Spec(targetHost: "campus.example");
        var handler = new RecordingHandler(_ => JsonResponse(PlanPayload(
            "install",
            "create",
            expectedSpec,
            PlanTokenText,
            canApply: true)));
        using var client = CreateClient(handler);

        var response = await client.PlanRelayAsync(
            new AgentRelayPlanRequest(
                Spec(targetHost: "Campus.Example."),
                Presentation(),
                GrantToAllDevices: true),
            AgentCredential.Parse(AdminCredentialText));

        Assert.Equal("[REDACTED]", response.PlanToken.ToString());
        Assert.Equal(PlanTokenText, response.PlanToken.Export());
        Assert.Equal("campus.example", response.RelayPlan.Spec!.TargetHost);
        Assert.Equal("Bearer", handler.Request!.Headers.Authorization?.Scheme);
        Assert.DoesNotContain(AdminCredentialText, handler.Request.RequestUri!.AbsoluteUri);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("campus.example", body.RootElement.GetProperty("spec").GetProperty("targetHost").GetString());
        Assert.True(body.RootElement.GetProperty("grantToAllDevices").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("planToken", out _));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("2001:db8::1")]
    [InlineData("*")]
    [InlineData("target/path")]
    [InlineData("10.00.0.1")]
    public async Task ProbeRelayAsync_RejectsUnsafeOrAmbiguousTargetsBeforeHttp(string targetHost)
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("HTTP must not be called"));
        using var client = CreateClient(handler);

        await Assert.ThrowsAsync<AgentProtocolException>(() => client.ProbeRelayAsync(
            Spec(targetHost),
            AgentCredential.Parse(AdminCredentialText)));

        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task ProbeRelayAsync_RejectsSelfLoopAndInconsistentReadiness()
    {
        var handler = new RecordingHandler(_ => JsonResponse(new
        {
            protocolVersion = 1,
            spec = Spec(),
            dependencyAvailable = true,
            relayPortInUse = false,
            targetReachable = true,
            readyToInstall = false,
            targetError = "",
        }));
        using var client = CreateClient(handler);

        await Assert.ThrowsAsync<AgentProtocolException>(() => client.ProbeRelayAsync(
            Spec(),
            AgentCredential.Parse(AdminCredentialText)));

        var selfLoop = Spec(targetHost: "127.0.0.1") with { TargetPort = 19000 };
        await Assert.ThrowsAsync<AgentProtocolException>(() => client.ProbeRelayAsync(
            selfLoop,
            AgentCredential.Parse(AdminCredentialText)));
    }

    [Fact]
    public async Task PlanRelayAsync_RejectsHealthPathThatCatalogCouldNotImport()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("HTTP must not be called"));
        using var client = CreateClient(handler);
        var presentation = Presentation() with { HealthPath = "/a b" };

        await Assert.ThrowsAsync<AgentProtocolException>(() => client.PlanRelayAsync(
            new AgentRelayPlanRequest(Spec(), presentation, GrantToAllDevices: false),
            AgentCredential.Parse(AdminCredentialText)));

        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task ApplyRelayAsync_RequiresConfirmationAndRejectsDifferentPlanToken()
    {
        var calls = 0;
        var handler = new RecordingHandler(_ =>
        {
            calls++;
            return JsonResponse(ApplyPayload(
                "install",
                "create",
                Spec(),
                OtherPlanTokenText,
                applied: true));
        });
        using var client = CreateClient(handler);
        var request = new AgentRelayPlanRequest(Spec(), Presentation(), GrantToAllDevices: false);

        await Assert.ThrowsAsync<AgentProtocolException>(() => client.ApplyRelayAsync(
            new AgentRelayApplyRequest(request, AgentRelayPlanToken.Parse(PlanTokenText), Confirmed: false),
            AgentCredential.Parse(AdminCredentialText)));
        Assert.Equal(0, calls);

        var exception = await Assert.ThrowsAsync<AgentProtocolException>(() => client.ApplyRelayAsync(
            new AgentRelayApplyRequest(request, AgentRelayPlanToken.Parse(PlanTokenText), Confirmed: true),
            AgentCredential.Parse(AdminCredentialText)));

        Assert.Equal("invalid_response", exception.ErrorCode);
        Assert.Equal(1, calls);
        Assert.DoesNotContain(PlanTokenText, handler.Request!.RequestUri!.AbsoluteUri);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.True(body.RootElement.GetProperty("confirmed").GetBoolean());
        Assert.Equal(PlanTokenText, body.RootElement.GetProperty("planToken").GetString());
    }

    [Fact]
    public async Task ApplyRelayAsync_RedactsPlanTokenEchoedByServerError()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = JsonContent(new
            {
                error = new
                {
                    code = "stale_plan",
                    message = $"plan {PlanTokenText} is stale",
                },
            }),
        });
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<AgentProtocolException>(() => client.ApplyRelayAsync(
            new AgentRelayApplyRequest(
                new AgentRelayPlanRequest(Spec(), Presentation(), GrantToAllDevices: false),
                AgentRelayPlanToken.Parse(PlanTokenText),
                Confirmed: true),
            AgentCredential.Parse(AdminCredentialText)));

        Assert.DoesNotContain(PlanTokenText, exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanAndApplyRelayDeletion_UseExactSlugAndConfirmation()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new RecordingHandler(request =>
        {
            requests.Add(request);
            return request.RequestUri!.AbsolutePath.EndsWith("delete-plan", StringComparison.Ordinal)
                ? JsonResponse(PlanPayload(
                    "delete",
                    "delete",
                    Spec(),
                    PlanTokenText,
                    canApply: true))
                : JsonResponse(ApplyPayload(
                    "delete",
                    "delete",
                    Spec(),
                    PlanTokenText,
                    applied: true));
        });
        using var client = CreateClient(handler);
        var credential = AgentCredential.Parse(AdminCredentialText);

        var plan = await client.PlanRelayDeletionAsync(
            new AgentRelayDeletionPlanRequest("campus-auth"),
            credential);
        var applied = await client.DeleteRelayAsync(
            new AgentRelayDeletionRequest("campus-auth", plan.PlanToken, Confirmed: true),
            credential);

        Assert.True(applied.Applied);
        Assert.Equal("delete", applied.Plan.RelayPlan.Operation);
        Assert.Equal("/v1/relays/delete-plan", requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("/v1/relays/delete", requests[1].RequestUri!.AbsolutePath);
        using var deleteBody = JsonDocument.Parse(handler.Body!);
        Assert.Equal("campus-auth", deleteBody.RootElement.GetProperty("slug").GetString());
        Assert.True(deleteBody.RootElement.GetProperty("confirmed").GetBoolean());
    }

    [Fact]
    public async Task CreateEnrollmentCodeAsync_ReturnsOneTimeRedactedSecretAndValidatesTtl()
    {
        var calls = 0;
        var handler = new RecordingHandler(_ =>
        {
            calls++;
            return JsonResponse(new
            {
                protocolVersion = 1,
                enrollmentCode = EnrollmentCodeText,
                expiresAt = "2099-01-02T03:04:05Z",
            }, HttpStatusCode.Created);
        });
        using var client = CreateClient(handler);
        var credential = AgentCredential.Parse(AdminCredentialText);

        await Assert.ThrowsAsync<AgentProtocolException>(() =>
            client.CreateEnrollmentCodeAsync(59, credential));
        Assert.Equal(0, calls);

        var response = await client.CreateEnrollmentCodeAsync(600, credential);

        Assert.Equal("[REDACTED]", response.EnrollmentCode.ToString());
        Assert.Equal(EnrollmentCodeText, response.EnrollmentCode.Export());
        Assert.Equal(TimeSpan.Zero, response.ExpiresAt.Offset);
        Assert.Equal("Bearer", handler.Request!.Headers.Authorization?.Scheme);
        Assert.DoesNotContain(EnrollmentCodeText, handler.Request.RequestUri!.AbsoluteUri);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(600, body.RootElement.GetProperty("ttlSeconds").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("enrollmentCode", out _));
    }

    private static AgentRelaySpec Spec(string targetHost = "10.20.30.40") =>
        new("campus-auth", 19000, targetHost, 443, "https", "/login?from=jk#account");

    private static AgentRelayServicePresentation Presentation() =>
        new("Campus Auth", "Explicit campus target", "Globe", 19000, "/", "tcpConnect", true);

    private static object PlanPayload(
        string operation,
        string action,
        AgentRelaySpec? spec,
        string planToken,
        bool canApply) => new
        {
            protocolVersion = 1,
            planToken,
            relayPlan = new
            {
                operation,
                action,
                slug = "campus-auth",
                unitName = "jkbridge-relay-campus-auth.service",
                spec,
                observedSha256 = (string?)null,
                reason = "ready",
                requiresConfirmation = true,
                canApply,
            },
            serviceChange = new
            {
                addedRemotePorts = operation == "install" ? new[] { 19000 } : Array.Empty<int>(),
                removedRemotePorts = operation == "delete" ? new[] { 19000 } : Array.Empty<int>(),
                affectedDeviceIds = new[] { DeviceId },
            },
            nextServicesRevision = 4,
        };

    private static object ApplyPayload(
        string operation,
        string action,
        AgentRelaySpec? spec,
        string planToken,
        bool applied) => new
        {
            protocolVersion = 1,
            planToken,
            relayPlan = new
            {
                operation,
                action,
                slug = "campus-auth",
                unitName = "jkbridge-relay-campus-auth.service",
                spec,
                observedSha256 = (string?)null,
                reason = "ready",
                requiresConfirmation = true,
                canApply = true,
            },
            serviceChange = new
            {
                addedRemotePorts = operation == "install" ? new[] { 19000 } : Array.Empty<int>(),
                removedRemotePorts = operation == "delete" ? new[] { 19000 } : Array.Empty<int>(),
                affectedDeviceIds = new[] { DeviceId },
            },
            nextServicesRevision = 4,
            applied,
            backupCreated = false,
        };

    private static AgentClient CreateClient(RecordingHandler handler) =>
        new(Endpoint, new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });

    private static HttpResponseMessage JsonResponse(
        object value,
        HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
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
