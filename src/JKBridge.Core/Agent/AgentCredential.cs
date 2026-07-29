namespace JKBridge.Core.Agent;

/// <summary>
/// A high-entropy opaque Agent credential. ToString intentionally never reveals it.
/// </summary>
public sealed class AgentCredential : IEquatable<AgentCredential>, IAgentSecretValue
{
    private readonly string _value;

    private AgentCredential(string value)
    {
        _value = value;
    }

    public static AgentCredential Parse(string value)
    {
        if (!TryParse(value, out var credential))
        {
            throw new ArgumentException(
                "Agent credentials must encode exactly 32 random bytes as unpadded base64url.",
                nameof(value));
        }

        return credential!;
    }

    public static bool TryParse(string? value, out AgentCredential? credential)
    {
        credential = null;
        if (value is null ||
            value.Length != 43 ||
            value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            return false;
        }

        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/').PadRight(44, '=');
            var bytes = Convert.FromBase64String(base64);
            var canonical = Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            if (bytes.Length != 32 || !string.Equals(canonical, value, StringComparison.Ordinal))
            {
                return false;
            }
        }
        catch (FormatException)
        {
            return false;
        }

        credential = new AgentCredential(value);
        return true;
    }

    /// <summary>
    /// Returns the credential for storage in an OS-protected secret store.
    /// Do not log the returned value or place it in a URI.
    /// </summary>
    public string Export() => _value;

    public override string ToString() => "[REDACTED]";

    public bool Equals(AgentCredential? other) =>
        other is not null &&
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(_value),
            System.Text.Encoding.ASCII.GetBytes(other._value));

    public override bool Equals(object? obj) => Equals(obj as AgentCredential);

    // Credentials must not be used as durable dictionary keys. This hash is process-local.
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_value);

    internal string RevealForAuthorizationHeader() => _value;

    string IAgentSecretValue.RevealForRedaction() => _value;
}

internal interface IAgentSecretValue
{
    string RevealForRedaction();
}
