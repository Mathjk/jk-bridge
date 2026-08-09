using System.IO.Compression;
using System.Security.Cryptography;

namespace JKBridge.Core.Pairing;

/// <summary>
/// Builds the copy-paste Linux bootstrap command shown by the Windows app.
/// The helper is embedded in the signed/release-hashed application payload,
/// verified in .NET, compressed into the command, and verified again by Python.
/// </summary>
public static class LinuxPairingBootstrap
{
    internal const string HelperResourceName = "JKBridge.Core.Pairing.jkbridge-linux.py";

    public const string SourceCommit = "f5f5695e4e6e0016321d0e59fb811a20cf1989ef";

    public const string HelperSha256 =
        "e0d56bdff7272af173b3f23b78d2de63d773d0a98ed2b64cb96b89b85ff134b0";

    public static readonly Uri HelperSourceUri = new(
        $"https://github.com/Mathjk/jk-bridge/blob/{SourceCommit}/scripts/jkbridge-linux.py",
        UriKind.Absolute);

    private static readonly Lazy<string> PairCommandValue = new(BuildPairCommand);

    public static string PairCommand => PairCommandValue.Value;

    public const string ExistingFileCommand = "python3 jkbridge-linux.py pair";

    internal static byte[] ReadEmbeddedHelper()
    {
        using var stream = typeof(LinuxPairingBootstrap).Assembly
            .GetManifestResourceStream(HelperResourceName) ??
            throw new InvalidOperationException("The embedded Linux pairing helper is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var helper = buffer.ToArray();
        var actualHash = SHA256.HashData(helper);
        var expectedHash = Convert.FromHexString(HelperSha256);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
        {
            throw new InvalidOperationException(
                "The embedded Linux pairing helper failed its SHA-256 integrity check.");
        }

        return helper;
    }

    private static string BuildPairCommand()
    {
        var helper = ReadEmbeddedHelper();
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(
                   compressed,
                   CompressionLevel.SmallestSize,
                   leaveOpen: true))
        {
            gzip.Write(helper);
        }

        var payload = Convert.ToBase64String(compressed.ToArray());
        return "python3 -c \"import base64,gzip,hashlib,sys;" +
               $"x=gzip.decompress(base64.b64decode('{payload}'));" +
               "h=hashlib.sha256(x).hexdigest();" +
               $"h=='{HelperSha256}' or sys.exit('JK Bridge helper checksum mismatch');" +
               "exec(compile(x,'jkbridge-linux.py','exec'))\" pair";
    }
}
