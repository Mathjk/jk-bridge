using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using JKBridge.Core.Configuration;
using JKBridge.Core.Models;
using JKBridge.Core.Routing;

namespace JKBridge.Core.Tests.Configuration;

public sealed class ServiceProfileTests
{
    [Fact]
    public void CreateAndSerialize_UsesAnExplicitNonSecretAllowList()
    {
        var config = ConfiguredWithAgent();

        var profile = ServiceProfileSerializer.Create(config, "Portable services");
        var json = ServiceProfileSerializer.Serialize(profile);

        Assert.Equal("Portable services", profile.DisplayName);
        Assert.Equal(config.Services.Count, profile.Services.Count);
        Assert.Contains("\"windowsLocalPort\"", json, StringComparison.Ordinal);
        Assert.Contains("\"linuxLoopbackPort\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("server", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("identityFile", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pinnedHostKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fingerprint", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(config.Agent.Endpoint, json, StringComparison.Ordinal);
        Assert.DoesNotContain(config.Agent.TailscaleNodeId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(config.Agent.DeviceId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(config.Agent.CredentialReference, json, StringComparison.Ordinal);
        Assert.DoesNotContain(config.Server.IdentityFile, json, StringComparison.Ordinal);
        Assert.DoesNotContain(config.Server.PinnedHostKey, json, StringComparison.Ordinal);
        Assert.DoesNotContain(config.Authorization.ClientKeyFingerprint, json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeAndDeserialize_RoundTripsPortableServicePropertiesAndEnums()
    {
        var expected = ServiceProfileSerializer.Create(TestConfigs.CreateConfigured());

        var json = ServiceProfileSerializer.Serialize(expected);
        var actual = ServiceProfileSerializer.Deserialize(json);

        Assert.Equal(expected.Format, actual.Format);
        Assert.Equal(expected.FormatVersion, actual.FormatVersion);
        Assert.Equal(expected.DisplayName, actual.DisplayName);
        Assert.Equal(expected.Services, actual.Services);
        Assert.Contains("\"healthKind\": \"httpStatus\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_CanExtractServicesFromAVersionThreeConfiguration()
    {
        var legacy = TestConfigs.CreateConfigured() with { SchemaVersion = 3 };

        var profile = ServiceProfileSerializer.Create(legacy);

        Assert.Equal(legacy.Services.Count, profile.Services.Count);
        Assert.Equal(legacy.Services[0].OpenUrl, profile.Services[0].OpenUrl);
    }

    [Fact]
    public void DeserializeImportDocument_ExtractsOnlyServicesFromLegalV21Configuration()
    {
        var legacy = ConfiguredWithAgent() with
        {
            SchemaVersion = 3,
            Server = ConfiguredWithAgent().Server with
            {
                IdentityFile = @"C:\private\legacy-jk-bridge-key",
            },
        };
        var json = SerializeLegacyV3(legacy);

        var import = ServiceProfileSerializer.DeserializeImportDocument(json);
        var portableJson = ServiceProfileSerializer.Serialize(import.Profile);

        Assert.Equal(ServiceProfileImportSource.LegacyConfigV3, import.Source);
        Assert.Equal("JK Bridge v2.1 services", import.Profile.DisplayName);
        Assert.Equal(legacy.Services.Count, import.Profile.Services.Count);
        Assert.Equal(legacy.Services[0].Id, import.Profile.Services[0].Id);
        Assert.Equal(legacy.Services[0].LocalPort, import.Profile.Services[0].WindowsLocalPort);
        Assert.Equal(legacy.Services[0].RemotePort, import.Profile.Services[0].LinuxLoopbackPort);
        Assert.DoesNotContain(legacy.Server.Host, portableJson, StringComparison.Ordinal);
        Assert.DoesNotContain(legacy.Server.IdentityFile, portableJson, StringComparison.Ordinal);
        Assert.DoesNotContain(legacy.Server.PinnedHostKey, portableJson, StringComparison.Ordinal);
        Assert.DoesNotContain(legacy.Authorization.ClientKeyFingerprint, portableJson, StringComparison.Ordinal);
        Assert.DoesNotContain("server", portableJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", portableJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("agent", portableJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeserializeImportDocument_ExplicitlyIdentifiesPortableProfile()
    {
        var profile = ServiceProfileSerializer.Create(AppConfig.CreateDefault());

        var import = ServiceProfileSerializer.DeserializeImportDocument(
            ServiceProfileSerializer.Serialize(profile));

        Assert.Equal(ServiceProfileImportSource.ServiceProfile, import.Source);
        Assert.Equal(profile.Services, import.Profile.Services);
    }

    [Fact]
    public void Deserialize_PortableOnlyApiRejectsLegacyConfigInsteadOfGuessing()
    {
        var legacyJson = SerializeLegacyV3(
            TestConfigs.CreateConfigured() with { SchemaVersion = 3 });

        var exception = Assert.Throws<ServiceProfileSerializationException>(() =>
            ServiceProfileSerializer.Deserialize(legacyJson));

        Assert.Contains("legacy configuration", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(999)]
    public void DeserializeImportDocument_RejectsUnsupportedLegacySchemas(int schemaVersion)
    {
        var json = JsonNode.Parse(SerializeLegacyV3(
            TestConfigs.CreateConfigured() with { SchemaVersion = 3 }))!.AsObject();
        json["schemaVersion"] = schemaVersion;

        var exception = Assert.Throws<ServiceProfileSerializationException>(() =>
            ServiceProfileSerializer.DeserializeImportDocument(json.ToJsonString()));

        Assert.Contains("schema 3", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeserializeImportDocument_RejectsAmbiguousOrUnknownLegacyFields()
    {
        var original = JsonNode.Parse(SerializeLegacyV3(
            TestConfigs.CreateConfigured() with { SchemaVersion = 3 }))!.AsObject();
        var ambiguous = original.DeepClone().AsObject();
        ambiguous["format"] = ServiceProfile.ExpectedFormat;
        var unknownRoot = original.DeepClone().AsObject();
        unknownRoot["agent"] = new JsonObject { ["credential"] = "secret" };
        var unknownNested = original.DeepClone().AsObject();
        unknownNested["server"]!.AsObject()["pairingToken"] = "secret";

        Assert.Throws<ServiceProfileSerializationException>(() =>
            ServiceProfileSerializer.DeserializeImportDocument(ambiguous.ToJsonString()));
        Assert.Throws<ServiceProfileSerializationException>(() =>
            ServiceProfileSerializer.DeserializeImportDocument(unknownRoot.ToJsonString()));
        Assert.Throws<ServiceProfileSerializationException>(() =>
            ServiceProfileSerializer.DeserializeImportDocument(unknownNested.ToJsonString()));
    }

    [Fact]
    public void DeserializeImportDocument_RejectsMissingDuplicateAndUnsafeLegacyFields()
    {
        var originalText = SerializeLegacyV3(
            TestConfigs.CreateConfigured() with { SchemaVersion = 3 });
        var missing = JsonNode.Parse(originalText)!.AsObject();
        Assert.True(missing.Remove("authorization"));
        var unsafeRoute = JsonNode.Parse(originalText)!.AsObject();
        unsafeRoute["services"]![0]!["remoteHost"] = "0.0.0.0";
        var duplicate = originalText.Replace(
            "\"schemaVersion\":3",
            "\"schemaVersion\":3,\"schemaVersion\":3",
            StringComparison.Ordinal);

        Assert.Throws<ServiceProfileSerializationException>(() =>
            ServiceProfileSerializer.DeserializeImportDocument(missing.ToJsonString()));
        Assert.Throws<ServiceProfileSerializationException>(() =>
            ServiceProfileSerializer.DeserializeImportDocument(unsafeRoute.ToJsonString()));
        var duplicateException = Assert.Throws<ServiceProfileSerializationException>(() =>
            ServiceProfileSerializer.DeserializeImportDocument(duplicate));
        Assert.Contains("Duplicate", duplicateException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_RejectsUnknownFieldsThatCouldSmuggleCredentials()
    {
        var profile = ServiceProfileSerializer.Create(AppConfig.CreateDefault());
        var json = ServiceProfileSerializer.Serialize(profile);
        var withToken = json.TrimEnd('}') + ",\"pairingToken\":\"secret\"}";

        var exception = Assert.Throws<ServiceProfileSerializationException>(() =>
            ServiceProfileSerializer.Deserialize(withToken));

        Assert.IsType<JsonException>(exception.InnerException);
    }

    [Fact]
    public void Deserialize_RejectsOversizedDocumentsBeforeParsing()
    {
        var json = new string(' ', ServiceProfileSerializer.MaximumDocumentBytes + 1);

        var exception = Assert.Throws<ServiceProfileSerializationException>(() =>
            ServiceProfileSerializer.Deserialize(json));

        Assert.Contains("size limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsUnsafeOrConflictingPortableRoutes()
    {
        var profile = ServiceProfileSerializer.Create(AppConfig.CreateDefault());
        profile.Services[0] = profile.Services[0] with
        {
            OpenUrl = "http://example.com/project",
        };
        profile.Services[1] = profile.Services[1] with
        {
            WindowsLocalPort = profile.Services[0].WindowsLocalPort,
            OpenUrl = "http://127.0.0.1:8080/",
        };

        var result = ServiceProfileValidator.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "service.invalidOpenUrl");
        Assert.Contains(result.Issues, issue => issue.Code == "service.duplicateLocalPort");
    }

    [Fact]
    public void PrepareImport_ReplacesOnlyServicesAndReportsNewRemoteAuthorization()
    {
        var current = ConfiguredWithAgent();
        var sub2Api = ServiceTemplateCatalog.Create(ServiceTemplateCatalog.Sub2Api);
        var profile = ServiceProfileSerializer.Create(current) with
        {
            Services = current.Services
                .Select(ServiceProfileMappingForTest)
                .Append(ServiceProfileMappingForTest(sub2Api))
                .ToList(),
        };

        var plan = ServiceProfileSerializer.PrepareImport(current, profile);

        Assert.Equal(current.Server, plan.CandidateConfig.Server);
        Assert.Equal(current.Authorization, plan.CandidateConfig.Authorization);
        Assert.Equal(current.Agent, plan.CandidateConfig.Agent);
        Assert.Equal(current.Behavior, plan.CandidateConfig.Behavior);
        Assert.Equal(current.ExpectedTailnetDnsSuffix, plan.CandidateConfig.ExpectedTailnetDnsSuffix);
        Assert.Equal(ServiceDefinition.LoopbackHost, plan.CandidateConfig.Services[^1].RemoteHost);
        Assert.Equal(8081, plan.CandidateConfig.Services[^1].RemotePort);
        Assert.Equal(RemoteAuthorizationImpact.ReauthorizationRequired, plan.Changes.AuthorizationImpact);
        Assert.Equal([8081], plan.Changes.AddedRemotePorts);
    }

    [Fact]
    public void PrepareImport_LocalUrlEditPreservesAuthorizationAndNeedsNoRepair()
    {
        var current = ConfiguredWithAgent();
        var profile = ServiceProfileSerializer.Create(current);
        profile.Services[0] = profile.Services[0] with
        {
            WindowsLocalPort = 18080,
            OpenUrl = ServiceOpenUrl.Build("http", 18080, "/project?mode=edit#/document"),
        };

        var plan = ServiceProfileSerializer.PrepareImport(current, profile);

        Assert.Equal(current.Authorization, plan.CandidateConfig.Authorization);
        Assert.Equal(RemoteAuthorizationImpact.None, plan.Changes.AuthorizationImpact);
        Assert.False(plan.Changes.RequiresRemoteReauthorization);
    }

    [Fact]
    public void Validate_RejectsNullServiceEntryWithoutThrowing()
    {
        var profile = ServiceProfileSerializer.Create(AppConfig.CreateDefault()) with
        {
            Services = [null!],
        };

        var result = ServiceProfileValidator.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue =>
            issue.Path == "services[0]" && issue.Code == "service.required");
    }

    private static AppConfig ConfiguredWithAgent() => TestConfigs.CreateConfigured() with
    {
        Agent = new AgentRegistration
        {
            IsConfigured = true,
            Endpoint = "http://100.73.7.51:45821/",
            TailscaleNodeId = "nJKBridgeLinux123",
            DeviceId = "device_A1b2c3d4",
            DeviceLabel = "Office Windows",
            CredentialReference = "agent-" + new string('a', 64),
            CatalogRevision = "sha256:abcdef123456",
        },
    };

    private static string SerializeLegacyV3(AppConfig config)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 3,
            config.SetupCompleted,
            config.ExpectedTailnetDnsSuffix,
            config.Server,
            config.Behavior,
            config.Authorization,
            config.Services,
        }, options);
    }

    private static ServiceProfileEntry ServiceProfileMappingForTest(ServiceDefinition service) => new()
    {
        Id = service.Id,
        Name = service.Name,
        Description = service.Description,
        Icon = service.Icon,
        Enabled = service.Enabled,
        Favorite = service.Favorite,
        WindowsLocalPort = service.LocalPort,
        LinuxLoopbackPort = service.RemotePort,
        OpenUrl = service.OpenUrl,
        HealthPath = service.HealthPath,
        HealthKind = service.HealthKind,
    };
}
