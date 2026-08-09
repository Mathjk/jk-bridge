using System.Text.Json;

namespace JKBridge.Core.Agent;

/// <summary>
/// One opaque, transient copy-paste value containing an Agent origin and one-time credential.
/// It must never be logged, persisted in configuration, or placed in a URL.
/// </summary>
public sealed class AgentSetupCode
{
    public const string Prefix = "JKBA1.";
    private const int MaximumEncodedLength = 2048;

    private AgentSetupCode(AgentEndpoint endpoint, AgentCredential enrollmentCredential)
    {
        Endpoint = endpoint;
        EnrollmentCredential = enrollmentCredential;
    }

    public AgentEndpoint Endpoint { get; }

    public AgentCredential EnrollmentCredential { get; }

    public static bool TryParse(
        string? value,
        out AgentSetupCode? setupCode,
        out string error)
    {
        setupCode = null;
        error = string.Empty;
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) ||
            !trimmed.StartsWith(Prefix, StringComparison.Ordinal))
        {
            error = "Agent setup codes must start with JKBA1.";
            return false;
        }

        var encoded = trimmed[Prefix.Length..];
        if (encoded.Length is < 1 or > MaximumEncodedLength ||
            encoded.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            error = "The Agent setup code format is invalid.";
            return false;
        }

        byte[] payload;
        try
        {
            var padded = encoded.Replace('-', '+').Replace('_', '/')
                .PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            payload = Convert.FromBase64String(padded);
            var canonical = Convert.ToBase64String(payload)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            if (!string.Equals(canonical, encoded, StringComparison.Ordinal))
            {
                error = "The Agent setup code is not canonical base64url.";
                return false;
            }
        }
        catch (FormatException)
        {
            error = "The Agent setup code format is invalid.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "The Agent setup code payload is invalid.";
                return false;
            }

            int? version = null;
            string? endpointValue = null;
            string? enrollmentValue = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                {
                    error = "The Agent setup code contains a duplicate field.";
                    return false;
                }

                switch (property.Name)
                {
                    case "version" when property.Value.ValueKind == JsonValueKind.Number &&
                                             property.Value.TryGetInt32(out var parsedVersion):
                        version = parsedVersion;
                        break;
                    case "endpoint" when property.Value.ValueKind == JsonValueKind.String:
                        endpointValue = property.Value.GetString();
                        break;
                    case "enrollmentCode" when property.Value.ValueKind == JsonValueKind.String:
                        enrollmentValue = property.Value.GetString();
                        break;
                    default:
                        error = "The Agent setup code contains an unknown field or invalid field type.";
                        return false;
                }
            }

            if (version != 1 ||
                seen.Count != 3 ||
                !AgentEndpoint.TryParse(endpointValue, out var endpoint, out _) ||
                !AgentCredential.TryParse(enrollmentValue, out var credential))
            {
                error = "The Agent setup code version, endpoint, or credential is invalid.";
                return false;
            }

            setupCode = new AgentSetupCode(endpoint!, credential!);
            return true;
        }
        catch (JsonException)
        {
            error = "The Agent setup code payload is not valid JSON.";
            return false;
        }
    }

    public override string ToString() => "[REDACTED AGENT SETUP CODE]";
}
