using JKBridge.Core.Models;

namespace JKBridge.Core.Tests;

internal static class TestConfigs
{
    public const string TailnetSuffix = "example-tailnet.ts.net";
    public const string LinuxHost = "linux-box.example-tailnet.ts.net";
    public const string WindowsHost = "windows-client.example-tailnet.ts.net";
    public const string TestHostKey =
        "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAII5PDcg58G9EbEkF2WSaZfPg2gmYobklFKxJt19PREep";

    public static AppConfig CreateConfigured()
    {
        var defaults = AppConfig.CreateDefault();
        return defaults with
        {
            SetupCompleted = true,
            ExpectedTailnetDnsSuffix = TailnetSuffix,
            Server = defaults.Server with
            {
                DisplayName = "Example Linux workstation",
                Host = LinuxHost,
                User = "alice",
                PinnedHostKey = TestHostKey,
            },
            Authorization = new ForwardAuthorizationGrant
            {
                IsKnown = true,
                ClientKeyFingerprint = "SHA256:" + new string('A', 43),
                RemotePorts = [80, 8765, 8793],
            },
        };
    }
}
