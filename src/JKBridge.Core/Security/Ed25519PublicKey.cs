using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace JKBridge.Core.Security;

public static class Ed25519PublicKey
{
    private const string Algorithm = "ssh-ed25519";
    private const int PublicKeyLength = 32;

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !string.Equals(parts[0], Algorithm, StringComparison.Ordinal))
        {
            return false;
        }

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (blob.Length != 4 + Algorithm.Length + 4 + PublicKeyLength)
        {
            return false;
        }

        var algorithmLength = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(0, 4));
        if (algorithmLength != Algorithm.Length)
        {
            return false;
        }

        var algorithm = Encoding.ASCII.GetString(blob, 4, (int)algorithmLength);
        var keyLengthOffset = 4 + (int)algorithmLength;
        var keyLength = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(keyLengthOffset, 4));
        if (!string.Equals(algorithm, Algorithm, StringComparison.Ordinal) || keyLength != PublicKeyLength)
        {
            return false;
        }

        normalized = $"{Algorithm} {parts[1]}";
        return true;
    }

    public static string ComputeFingerprint(string value)
    {
        if (!TryNormalize(value, out var normalized))
        {
            throw new ArgumentException("A valid Ed25519 public key is required.", nameof(value));
        }

        var base64 = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
        var hash = SHA256.HashData(Convert.FromBase64String(base64));
        return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
    }
}
