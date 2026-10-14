using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JKBridge.Core.Models;
using JKBridge.Core.Routing;

namespace JKBridge.Core.Configuration;

public static class ServiceProfileSerializer
{
    public const int MaximumDocumentBytes = 1024 * 1024;

    private const int LegacyConfigSchemaVersion = 3;

    private const string LegacyProfileDisplayName = "JK Bridge v2.1 services";

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static ServiceProfile Create(
        AppConfig config,
        string displayName = "JK Bridge services")
    {
        ArgumentNullException.ThrowIfNull(config);

        var profile = new ServiceProfile
        {
            DisplayName = displayName,
            Services = config.Services
                .Select(ServiceProfileMapping.FromServiceDefinition)
                .ToList(),
        };
        ServiceProfileValidator.EnsureValid(profile);
        return profile;
    }

    public static string Serialize(ServiceProfile profile)
    {
        ServiceProfileValidator.EnsureValid(profile);
        return JsonSerializer.Serialize(profile, SerializerOptions);
    }

    public static byte[] SerializeToUtf8Bytes(ServiceProfile profile)
    {
        ServiceProfileValidator.EnsureValid(profile);
        return JsonSerializer.SerializeToUtf8Bytes(profile, SerializerOptions);
    }

    public static ServiceProfile Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        EnsureSize(Encoding.UTF8.GetByteCount(json));
        return Deserialize(Encoding.UTF8.GetBytes(json));
    }

    public static ServiceProfile Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        EnsureSize(utf8Json.Length);
        var source = ClassifyAndValidateJsonShape(utf8Json);
        if (source != ServiceProfileImportSource.ServiceProfile)
        {
            throw new ServiceProfileSerializationException(
                "Expected a JK Bridge Service Profile, but the document is a legacy configuration.");
        }

        return DeserializeProfile(utf8Json);
    }

    public static ServiceProfileImportDocument DeserializeImportDocument(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        EnsureSize(Encoding.UTF8.GetByteCount(json));
        return DeserializeImportDocument(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>
    /// Reads either the portable Service Profile format or an exact public
    /// v2.1/schema-v3 config.json. Legacy device state is validated and then
    /// discarded; only the explicit service allow-list reaches the result.
    /// </summary>
    public static ServiceProfileImportDocument DeserializeImportDocument(
        ReadOnlySpan<byte> utf8Json)
    {
        EnsureSize(utf8Json.Length);
        var source = ClassifyAndValidateJsonShape(utf8Json);
        var profile = source switch
        {
            ServiceProfileImportSource.ServiceProfile => DeserializeProfile(utf8Json),
            ServiceProfileImportSource.LegacyConfigV3 => ExtractLegacyConfigServices(utf8Json),
            _ => throw new ServiceProfileSerializationException(
                "Service import document format is not supported."),
        };
        return new(profile, source);
    }

    private static ServiceProfile DeserializeProfile(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var profile = JsonSerializer.Deserialize<ServiceProfile>(utf8Json, SerializerOptions);
            ServiceProfileValidator.EnsureValid(profile);
            return profile!;
        }
        catch (JsonException exception)
        {
            throw new ServiceProfileSerializationException(
                "Service profile JSON is malformed or contains unsupported fields.",
                exception);
        }
    }

    private static ServiceProfile ExtractLegacyConfigServices(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var legacy = JsonSerializer.Deserialize<LegacyConfigV3Document>(
                             utf8Json,
                             SerializerOptions)
                         ?? throw new JsonException("Legacy configuration is required.");
            if (legacy.SchemaVersion != LegacyConfigSchemaVersion)
            {
                throw new ServiceProfileSerializationException(
                    $"Only JK Bridge v2.1 schema {LegacyConfigSchemaVersion} configuration can be imported as a legacy service list.");
            }

            var current = legacy.ToCurrentConfig();
            try
            {
                ConfigValidator.EnsureValid(current);
            }
            catch (ConfigValidationException exception)
            {
                throw new ServiceProfileSerializationException(
                    "Legacy JK Bridge configuration is not valid and cannot be imported.",
                    exception);
            }

            // This projection is the security boundary: none of the validated
            // legacy server, key-path, or authorization fields are returned.
            return Create(current, LegacyProfileDisplayName);
        }
        catch (JsonException exception)
        {
            throw new ServiceProfileSerializationException(
                "Legacy JK Bridge configuration is malformed or contains unsupported fields.",
                exception);
        }
    }

    private static ServiceProfileImportSource ClassifyAndValidateJsonShape(
        ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            using var document = JsonDocument.Parse(
                utf8Json.ToArray(),
                new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ServiceProfileSerializationException(
                    "Service import document must be a JSON object.");
            }

            EnsureNoDuplicateProperties(document.RootElement, "$");
            var root = document.RootElement;
            var hasProfileFormat = root.TryGetProperty("format", out _);
            var hasLegacySchema = root.TryGetProperty("schemaVersion", out var schema);
            if (hasProfileFormat && hasLegacySchema)
            {
                throw new ServiceProfileSerializationException(
                    "Service import document ambiguously mixes Service Profile and legacy configuration fields.");
            }

            if (hasProfileFormat)
            {
                EnsureRequiredProperties(
                    root,
                    ["format", "formatVersion", "displayName", "services"],
                    "Service Profile");
                return ServiceProfileImportSource.ServiceProfile;
            }

            if (hasLegacySchema)
            {
                if (schema.ValueKind != JsonValueKind.Number ||
                    !schema.TryGetInt32(out var schemaVersion) ||
                    schemaVersion != LegacyConfigSchemaVersion)
                {
                    throw new ServiceProfileSerializationException(
                        $"Only JK Bridge v2.1 schema {LegacyConfigSchemaVersion} configuration is supported for legacy import.");
                }

                EnsureRequiredProperties(
                    root,
                    [
                        "schemaVersion",
                        "setupCompleted",
                        "expectedTailnetDnsSuffix",
                        "server",
                        "behavior",
                        "authorization",
                        "services",
                    ],
                    "legacy configuration");
                return ServiceProfileImportSource.LegacyConfigV3;
            }

            throw new ServiceProfileSerializationException(
                "Document is neither a JK Bridge Service Profile nor a supported legacy configuration.");
        }
        catch (JsonException exception)
        {
            throw new ServiceProfileSerializationException(
                "Service import document is malformed JSON.",
                exception);
        }
    }

    private static void EnsureRequiredProperties(
        JsonElement root,
        IEnumerable<string> requiredProperties,
        string description)
    {
        var missing = requiredProperties
            .Where(property => !root.TryGetProperty(property, out _))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new ServiceProfileSerializationException(
                $"{description} is missing required fields: {string.Join(", ", missing)}.");
        }
    }

    private static void EnsureNoDuplicateProperties(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new ServiceProfileSerializationException(
                        $"Duplicate JSON property '{property.Name}' at {path} is not allowed.");
                }

                EnsureNoDuplicateProperties(property.Value, $"{path}.{property.Name}");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                EnsureNoDuplicateProperties(item, $"{path}[{index++}]");
            }
        }
    }

    private static void EnsureSize(int documentBytes)
    {
        if (documentBytes > MaximumDocumentBytes)
        {
            throw new ServiceProfileSerializationException(
                $"Service profile exceeds the {MaximumDocumentBytes}-byte size limit.");
        }
    }

    /// <summary>
    /// Prepares a replacement of the portable service list while retaining every
    /// device- and server-specific field from the current configuration.
    /// Callers can inspect Changes before persisting CandidateConfig with ConfigStore.
    /// </summary>
    public static ServiceProfileImportPlan PrepareImport(
        AppConfig current,
        ServiceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(current);
        ConfigValidator.EnsureValid(current);
        ServiceProfileValidator.EnsureValid(profile);

        var proposedServices = profile.Services
            .Select(ServiceProfileMapping.ToServiceDefinition)
            .ToList();
        var candidate = current with { Services = proposedServices };
        ConfigValidator.EnsureValid(candidate);
        var changes = ServiceChangeAnalyzer.Analyze(current.Services, proposedServices);
        return new(candidate, changes);
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true,
            MaxDepth = 32,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public enum ServiceProfileImportSource
{
    ServiceProfile,
    LegacyConfigV3,
}

public sealed record ServiceProfileImportDocument(
    ServiceProfile Profile,
    ServiceProfileImportSource Source);

public sealed record ServiceProfileImportPlan(
    AppConfig CandidateConfig,
    ServiceChangeAssessment Changes);

public sealed class ServiceProfileSerializationException : Exception
{
    public ServiceProfileSerializationException(string message)
        : base(message)
    {
    }

    public ServiceProfileSerializationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
