using System.IO.Compression;
using System.Security.Cryptography;

namespace JKBridge.Core.Pairing;

/// <summary>
/// Builds the offline, copy-paste Linux Agent installer carried by the Windows app.
/// The temporary package is verified as one SHA-256-bound archive before any file is used.
/// </summary>
public static class LinuxAgentBootstrap
{
    private sealed record EmbeddedFile(string FileName, string ResourceName, int Mode);

    private static readonly EmbeddedFile[] Files =
    [
        new("jkbridge_agent.py", "JKBridge.Core.Pairing.jkbridge_agent.py", 0x1C0),
        new("jkbridge_relay.py", "JKBridge.Core.Pairing.jkbridge_relay.py", 0x1C0),
        new("jkbridge-linux.py", "JKBridge.Core.Pairing.jkbridge-linux.py", 0x1C0),
        new("jkbridge-agentctl", "JKBridge.Core.Pairing.jkbridge-agentctl", 0x1C0),
        new("jkbridge-agent.service", "JKBridge.Core.Pairing.jkbridge-agent.service", 0x180),
    ];

    private static readonly Lazy<(string Command, string Sha256)> Bundle = new(BuildBundle);

    public static string InstallCommand => Bundle.Value.Command;

    public static string BundleSha256 => Bundle.Value.Sha256;

    internal static IReadOnlyDictionary<string, byte[]> ReadEmbeddedFiles()
    {
        var assembly = typeof(LinuxAgentBootstrap).Assembly;
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Files)
        {
            using var stream = assembly.GetManifestResourceStream(file.ResourceName) ??
                throw new InvalidOperationException(
                    $"The embedded Linux Agent file is missing: {file.FileName}");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            result.Add(file.FileName, buffer.ToArray());
        }

        return result;
    }

    private static (string Command, string Sha256) BuildBundle()
    {
        var embedded = ReadEmbeddedFiles();
        using var archiveBytes = new MemoryStream();
        using (var archive = new ZipArchive(
                   archiveBytes,
                   ZipArchiveMode.Create,
                   leaveOpen: true))
        {
            foreach (var file in Files)
            {
                var entry = archive.CreateEntry(file.FileName, CompressionLevel.SmallestSize);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var destination = entry.Open();
                destination.Write(embedded[file.FileName]);
            }
        }

        var bytes = archiveBytes.ToArray();
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var payload = Convert.ToBase64String(bytes);
        var modes = string.Join(
            ",",
            Files.Select(file => $"'{file.FileName}':{file.Mode}"));
        var python =
            "import base64,hashlib,io,pathlib,shutil,subprocess,sys,tempfile,zipfile;" +
            $"b=base64.b64decode('{payload}');" +
            $"hashlib.sha256(b).hexdigest()=='{sha256}' or sys.exit('JK Bridge Agent bundle checksum mismatch');" +
            "z=zipfile.ZipFile(io.BytesIO(b));" +
            $"a={{{modes}}};" +
            "set(z.namelist())==set(a) or sys.exit('JK Bridge Agent bundle contents mismatch');" +
            "d=tempfile.mkdtemp(prefix='jkbridge-agent-');" +
            "[(lambda p,n:(p.write_bytes(z.read(n)),p.chmod(a[n])))(pathlib.Path(d)/n,n) for n in a];" +
            "r=subprocess.run(['bash',str(pathlib.Path(d)/'jkbridge-agentctl'),'--yes','--source-dir',d,'install']);" +
            "shutil.rmtree(d);" +
            "sys.exit(r.returncode)";
        return ($"python3 -c \"{python}\"", sha256);
    }
}
