# JK Bridge installation guide

[简体中文](INSTALLATION.zh-CN.md) · **English**

This guide takes a new Windows 11 x64 client and a Linux host from an empty environment to a working JK Bridge connection. System package installation is deliberately separate from one-click pairing: JK Bridge checks dependencies but never elevates itself or changes operating-system configuration.

> [!IMPORTANT]
> These steps install the released **v2.1.0** portable ZIP with one-shot Linux
> pairing. The repository's unreleased 2.2.0/vNext installer, Service Profile,
> Windows Agent client, Linux Agent package assets, and simple route editor are development
> work. The repository has a hardened user unit and manual packaging guide. One
> deployed pair has exercised install/start/restart and first-admin enrollment,
> but there is no stable Agent artifact or complete clean-machine
> upgrade/rollback/uninstall workflow.
> Do not use an unreleased Agent screen as a substitute for the one-shot steps
> below. Any locally built artifact marked `2.2.0` is for development
> acceptance, not a stable Release.

## Before you begin

You need:

- administrator access while installing Windows optional features and software;
- `sudo` access while installing Linux packages;
- one ordinary, non-root Linux account for JK Bridge pairing and SSH;
- both devices in the same Tailscale tailnet, or an access policy that permits them to communicate;
- at least one web service already reachable on Linux `127.0.0.1`.

The email addresses used to sign in to Tailscale do not have to be identical. Both devices simply need to be members of the same tailnet and allowed by its policy.

### Released and in-development workflows

| Capability | v2.1.0 Release | Current unreleased 2.2.0/vNext repository state |
|---|---|---|
| Windows delivery | Verified portable ZIP | CI-built unsigned Inno Setup installer and preview publishing pipeline; not a stable or clean-machine-accepted installer |
| Linux setup | One-shot helper started in a terminal for each new device | No-AI Agent, user-unit assets, controlled loopback-relay helper, packaging guide, and Windows client exist in source; one deployed start/restart/enrollment passed, while the complete clean-machine lifecycle remains pending |
| Service editing | Explicit local port, remote port, loopback URL, and health fields | Simple URL generation and advanced editor are under development |
| Service transfer | Re-enter services on the new Windows computer | Native non-secret export and preview/confirm import UI exists in vNext source; not released or clean-Windows accepted |
| Recovery | Run the one-shot helper and re-pair | The same one-shot flow remains the planned fallback |

An optional future Agent will be a deterministic program, not an AI feature. It
will not need ChatGPT, Codex, a model API key, or cloud inference.

## 1. Prepare Windows 11

### 1.1 Install Tailscale

1. Download Tailscale from the [official Windows page](https://tailscale.com/download/windows). The official direct stable installer is also available as [`tailscale-setup-latest.exe`](https://pkgs.tailscale.com/stable/tailscale-setup-latest.exe).
2. Run the installer.
3. Open Tailscale from the system tray and sign in to the intended tailnet.
4. Confirm the Linux device eventually appears in the device list.

Verify in PowerShell:

```powershell
& "$env:ProgramFiles\Tailscale\tailscale.exe" status
```

If Tailscale was installed elsewhere, open the tray app or locate it with:

```powershell
Get-ChildItem "$env:ProgramFiles\Tailscale" -Filter tailscale.exe
```

For current operating-system support and managed deployment options, use the [official Windows install documentation](https://tailscale.com/docs/install/windows).

### 1.2 Install Windows OpenSSH Client

JK Bridge needs **OpenSSH Client** on Windows. It does not need Windows OpenSSH Server.

GUI method:

1. Open **Settings** and search for **Optional features**.
2. Choose **View features** or **Add an optional feature**.
3. Search for **OpenSSH Client**, install it, and reopen PowerShell.

Administrator PowerShell method:

```powershell
Get-WindowsCapability -Online | Where-Object Name -Like 'OpenSSH.Client*'
Add-WindowsCapability -Online -Name OpenSSH.Client~~~~0.0.1.0
```

Verify both required executables:

```powershell
Get-Command ssh, ssh-keygen
ssh -V
```

Both commands should resolve under `C:\Windows\System32\OpenSSH\`. If they do not, restart PowerShell or Windows and check the optional feature again. See [Microsoft's official OpenSSH installation guide](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_install_firstuse).

## 2. Prepare Linux

Run package-installation commands with `sudo`, but later run `jkbridge-linux.py` as the ordinary account JK Bridge should use for SSH.

### 2.1 Install Python 3

Verify first:

```bash
python3 --version
```

JK Bridge requires Python 3.9 or later and uses only the standard library. On Ubuntu or Debian, install it if necessary:

```bash
sudo apt update
sudo apt install python3
```

Other distributions should use their normal package manager. [Python.org downloads](https://www.python.org/downloads/) are available when a distribution package is unsuitable.

### 2.2 Install and sign in to Tailscale

Tailscale's official automated Linux installation is:

```bash
curl -fsSL https://tailscale.com/install.sh | sh
sudo tailscale up
```

If your policy does not allow piping a downloaded script to a shell, follow the distribution-specific instructions in the [official Linux guide](https://tailscale.com/docs/install/linux) or use the [official stable package repositories](https://pkgs.tailscale.com/stable/).

Verify:

```bash
tailscale status
tailscale ip -4
```

The Windows device should appear in `tailscale status`. If the two devices use different login emails, verify in the Tailscale admin console that they still belong to the same tailnet.

### 2.3 Install OpenSSH Server

Ubuntu or Debian:

```bash
sudo apt update
sudo apt install openssh-server
sudo systemctl enable --now ssh
```

Fedora or RHEL-family systems:

```bash
sudo dnf install openssh-server
sudo systemctl enable --now sshd
```

Arch Linux:

```bash
sudo pacman -S openssh
sudo systemctl enable --now sshd
```

Validate the server configuration before continuing:

```bash
sudo sshd -t
sudo sshd -T | grep -E '^(pubkeyauthentication|allowtcpforwarding)'
sudo test -s /etc/ssh/ssh_host_ed25519_key.pub && echo 'Ed25519 host key: present'
```

JK Bridge needs `pubkeyauthentication yes` and `allowtcpforwarding yes` or `allowtcpforwarding local`. A standard OpenSSH Server installation normally generates the Ed25519 host key automatically. If the key is missing, generate standard host keys and restart the correct service for your distribution:

```bash
sudo ssh-keygen -A
sudo systemctl restart ssh     # Ubuntu/Debian
# sudo systemctl restart sshd  # Fedora/RHEL/Arch
```

If SSH policy changes are required, edit the server configuration deliberately, run `sudo sshd -t`, and only then reload or restart SSH. JK Bridge will not change `sshd_config`. Ubuntu administrators should also read the [official OpenSSH Server guide](https://documentation.ubuntu.com/server/how-to/security/openssh-server/).

### 2.4 Verify each local web service

The destination must listen on Linux loopback. Examples:

```bash
curl -I http://127.0.0.1:80/project
curl -I http://127.0.0.1:8765/
curl -I http://127.0.0.1:8793/
ss -ltn | grep -E '127\.0\.0\.1:(80|8765|8793)\b'
```

An HTTP redirect, authentication response, or application page can all prove that a service is alive. A connection-refused error means the application itself must be started or its configured port corrected before JK Bridge can make it reachable.

## 3. Confirm Tailscale connectivity

On both devices, `tailscale status` should list the other device. From Windows you can also run:

```powershell
& "$env:ProgramFiles\Tailscale\tailscale.exe" ping <linux-magicdns-name>
```

If the ping is denied, fix tailnet membership, ACLs/grants, device approval, or the host firewall before pairing. Do not work around the problem by exposing the Linux web service to `0.0.0.0`.

## 4. Download and verify JK Bridge

Open the [latest GitHub Release](https://github.com/Mathjk/jk-bridge/releases/latest) and download:

- `JKBridge-win-x64.zip`
- `JKBridge-win-x64.zip.sha256`

From the Windows download directory:

```powershell
$actual = (Get-FileHash .\JKBridge-win-x64.zip -Algorithm SHA256).Hash.ToLowerInvariant()
$expected = (Get-Content .\JKBridge-win-x64.zip.sha256).Split()[0].ToLowerInvariant()
$actual
$expected
$actual -eq $expected
```

The last line must be `True`. Extract the ZIP to a stable folder such as one under the current user's applications directory; do not run it permanently from a temporary archive preview.

The executable is currently unsigned. A SmartScreen warning is therefore possible. The checksum proves that the download matches the published Release asset; it does not replace normal trust in the repository and release process.

The repository contains a vNext Inno Setup definition and build script, but the
stable v2.1.0 instructions use the ZIP. Explicitly labelled Preview installers
are development-acceptance artifacts. Do not treat an installer built by an
unrelated third party as an official Release. The installer can detect
Tailscale, open its official download page, and offer a UAC-confirmed OpenSSH
Client installation; it cannot sign in to Tailscale for you.

Starting with Preview 4, an installed build can use **Settings → Version check
→ Check official Release**, followed by **Download and verify update**. Preview
builds remain on the Preview channel; stable builds use the stable channel. JK
Bridge accepts only a matching installer and `.sha256` pair from the same
official Release. The first phase shows real byte/percentage progress and
verification stages, can be cancelled, and leaves JK Bridge running. Only the
separate **Start installation (JK Bridge will exit)** action starts the normal
visible Windows setup wizard. JK Bridge requests a controlled shutdown only
after setup process creation succeeds; the setup wizard then displays the real
installation progress.
Preview 6 uses the Windows/.NET system proxy for GitHub metadata and assets
while retaining exact-host, redirect, size, and SHA-256 validation. Preview 4/5
forced direct connections; a proxy-dependent machine must manually install
Preview 6 once before later in-app downloads can use its system proxy.

The flow does not check in the background, install
silently, or bypass SmartScreen. Configuration, device credentials, and SSH keys
live outside the installation directory and are retained by a normal same-user
overlay install. Preview 1 has no update control. If the old one-step action in
Preview 2/3 stalls, manually download Preview 4, or explicitly exit the old tray
process before running its already verified installer.

## 5. Copy and run the Linux helper

The extracted Windows package contains `jkbridge-linux.py`. Copy it to the ordinary Linux user's home directory by Tailscale file transfer, SCP, a shared folder, or removable media.

On Linux:

```bash
chmod 700 ~/jkbridge-linux.py
python3 ~/jkbridge-linux.py status
python3 ~/jkbridge-linux.py pair
```

Do not use `sudo`. Keep the terminal open and copy the complete `jkbridge://...` value. Treat it like a temporary password: do not publish it or save it in notes. It expires after 10 minutes, one successful request, cancellation, or too many failed requests.

## 6. Pair and connect on Windows

1. Start `JKBridge.exe`.
2. Open **Device setup**.
3. Paste the complete pairing code.
4. Review the Linux node, user, and requested destination ports shown by the app.
5. Select **Pair and connect**.

Success means:

- the Linux `pair` process exits;
- Windows configuration is saved under `%LOCALAPPDATA%\JKBridge`;
- a dedicated identity exists under the current user's `.ssh` directory;
- the Overview shows the tunnel connected or shows a service-specific health warning;
- the enabled Windows `127.0.0.1` ports are listening.

No Linux password or fixed Tailscale IP is stored.

## 7. Verify the first connection

Open each service from the JK Bridge Overview. For deeper Windows checks, run PowerShell from the extracted folder:

```powershell
.\Verify-JKBridge.ps1 -PortExpectation Listening -TestServices
```

If the tunnel is connected but a service health check fails, return to Linux and test the exact target with `curl`. See [Troubleshooting](TROUBLESHOOTING.md).

### How Windows and Linux ports relate

JK Bridge creates a Windows-local listener and asks SSH to connect that listener
to one exact Linux-loopback destination:

```text
http://127.0.0.1:<Windows localPort>/<launch path>
                       -> 127.0.0.1:<Linux remotePort>
```

For example, Windows `8080` can forward to Linux `80`. The browser must open
`http://127.0.0.1:8080/...`, not port `80` and not the Linux Tailscale address.
Changing only the Windows port, `http`/`https` choice, or launch path does not
add a Linux destination. Adding or changing the Linux remote port requires a new
exact grant through one-shot pairing today and through the optional Agent in the
future.

The launch path controls which page JK Bridge opens. It does not rewrite HTTP
redirects returned by the application. A custom URL must remain an HTTP(S) URL
on exact host `127.0.0.1` and use the configured Windows local port. TLS
certificates and application hostname requirements may still need application
or reverse-proxy configuration.

## 8. Configure daily startup

In JK Bridge Settings, enable **Start with Windows** if desired. This registers the app only for the current Windows user. At the next login, JK Bridge waits for Tailscale and reconnects automatically.

Without that option, simply start `JKBridge.exe` after a reboot. No pairing code, username, password, or IP address is normally required again.

Keep the extracted directory in place: moving or deleting `JKBridge.exe` after enabling startup leaves the startup entry pointing to the old path.

## Add or replace a Windows computer

Released v2.1.0 treats every Windows computer as an independent device:

1. Install and sign in to Tailscale on the new computer.
2. Install Windows OpenSSH Client and extract the verified JK Bridge ZIP.
3. Start `python3 ~/jkbridge-linux.py pair` on Linux.
4. Pair the new Windows computer so it creates its own Ed25519 identity.
5. Recreate the required service definitions and verify each local port.
6. If this replaces an old computer, revoke the old fingerprint on Linux only
   after the new connection passes acceptance.

Do not copy the old computer's private key, authorization receipt, or complete
`config.json`. A copied private key makes two computers indistinguishable and
prevents independent revocation. A copied receipt only describes the old key's
observed Linux authorization and cannot authorize a new key.

The vNext Service Profile format and native file-picker workflow are deliberately non-secret: they may carry
service names, Windows and Linux ports, launch URLs, and health choices, but not
private keys, pairing tokens, device credentials, server identity, or
authorization receipts. The vNext UI can export this allow-list, validate and
preview an import without mutation, and apply it only after explicit
confirmation. This workflow is implemented in source but not released or
clean-Windows accepted. Import replaces only local service definitions; a new
device must still enroll through the Agent or complete one-shot pairing.

If the old computer can no longer export a Service Profile, the 2.2.0
development import picker also accepts an exact v2.1/schema-v3 `config.json` as
a legacy source. It validates the complete old shape, then projects only
Services into the same read-only preview. Server identity, tailnet identity,
behavior settings, private-key path, authorization receipt, and all other
device-specific fields are discarded. This does **not** make whole-config copy a
migration method: do not overwrite the target configuration with the old file,
handle that source as sensitive, and prefer a Service Profile when possible.

The vNext source also contains the Agent and relay scripts, a hardened per-user
systemd unit, an environment example, package checks, and a manual packaging
guide. One deployed pair has completed installation, restart, discovery, and
first-admin enrollment, but the assets have not completed login persistence,
upgrade, rollback, uninstall, and clean-machine acceptance, so they are not stable
installation instructions. A bounded discovery engine is present and tested:
on explicit invocation it checks only known online Tailscale peers on the fixed
Agent port and validates their node and tailnet identity. The vNext Setup page
is wired to invoke it only after **Discover Agent** is selected. One result is
preselected but not copied into the endpoint field; multiple results require an
explicit choice. In either case **Use selected address** is a separate action,
and enrollment still requires a one-time code plus an explicit enrollment
click. This GUI flow is implemented but not clean-Windows accepted.

After enrollment, any vNext device can manually synchronize its own service
selection through authenticated `/v1/me` and `/v1/services`. An administrator
can additionally refresh the complete Agent device list and explicitly revoke a
selected device other than the current computer. Revocation requires a
confirmation checkbox, removes future SSH authentication for only that device,
and cannot terminate an already established SSH session. An ordinary device
receives only its own device receipt and cannot list or revoke other devices.

### vNext development reference: add another Agent device

This is implemented in source but is not a stable installation path yet:

1. On an already enrolled administrator Windows computer, open **Device setup**,
   synchronize/refresh Agent state, and select **Generate new device code**.
2. The UI displays one ordinary-device code with a 10-minute expiry. It can be
   consumed once. The code is not written to JK Bridge configuration or logs.
3. Copy it directly to the target Windows computer. After copying, overwrite
   the clipboard yourself; the operating system clipboard is not auto-cleared.
4. On the target, select **Discover Agent**, review the sanitized name/MagicDNS
   identity, select the intended candidate if more than one appears, and select
   **Use selected address**. If none is found, enter the exact Linux Tailscale
   IPv4 Agent endpoint manually. Then enter the code and a device label and
   explicitly select **Enroll and connect**. Discovery alone never enrolls.

The first administrator cannot be created by this button. It must use the
administrator code printed by Linux Agent bootstrap, or a new administrator
recovery code issued locally on Linux. An ordinary device cannot generate
codes, list peers, revoke peers, or manage relays.

For administrator replacement, lost-device recovery, revocation boundaries,
and the rule that administrator roles are not transferred in place, follow the
[vNext Agent multi-device operations guide](AGENT_OPERATIONS.md).

### vNext development reference: controlled Linux relay

The **Advanced: Linux reachable target** panel is for one exact TCP service that
Linux can reach but Windows cannot. The two local ports remain independent:

```text
Windows http(s)://127.0.0.1:<Windows port>/<path>
  -> SSH -> Linux 127.0.0.1:<relay port>
  -> one exact <target DNS-or-IPv4>:<target port>
```

Only an Agent administrator can operate it. Enter a service ID, the Linux relay
port, one DNS name or IPv4 target and port, the Windows local port, protocol,
and launch path. Select **Generate plan and probe** first. Planning and probing
are read-only: the preview reports normalized mapping, dependency, port and
target reachability, catalog revision, exact added/removed `permitopen` ports,
and affected device IDs. Review that preview, separately tick its confirmation,
then apply. Deletion has the same read-only delete-plan and explicit-confirm
boundary. Changing a field invalidates the prior plan, and a plan bound to stale
Linux/catalog/device state must be regenerated.

The Linux side requires the packaged sibling `jkbridge_relay.py`,
`/usr/bin/socat`, and a working per-user systemd session. A managed relay unit is
created under the user's systemd unit directory and listens only on
`127.0.0.1`; create/update/delete is coordinated with the Agent catalog and
exact per-device grants. The Agent refuses unmanaged/conflicting units and is
not a general proxy: there is no wildcard, CIDR, URI target, UDP, SOCKS, Funnel,
exit-node, or subnet-route mode. HTTPS is transparent TCP forwarding, so the
target may still reject `127.0.0.1` because of certificate hostname, SNI,
cookies, or redirects.

Although this relay workflow and its automated tests exist in the current
source tree, it still requires a real Linux install/lifecycle run and clean
Windows GUI acceptance before it can be described as released.

If the Agent is absent, stopped, incompatible, or deliberately disabled, use
the one-shot helper. Existing restricted SSH tunnels do not depend on the Agent
remaining online.

## Upgrade from v2.0.x to v2.1

Replace the old extracted application with the new one. Device, service, and behavior settings migrate to schema V3. Earlier versions did not record which Linux ports were actually authorized, so the first v2.1 connection asks you to pair once. This is not configuration loss: run `python3 ~/jkbridge-linux.py pair` again, paste the new code into Device setup, and keep the existing dedicated identity path.

The completed pair records the returned client fingerprint and exact port set. Later Linux destination-port changes are listed before SSH starts; changing only a Windows local port does not require new Linux authorization.

## Add Investment News to an existing installation

Exit JK Bridge from the tray, then run this from PowerShell in the updated extracted directory:

```powershell
.\Add-Investment-News.ps1
```

The script backs up `%LOCALAPPDATA%\JKBridge\config.json` and adds one exact mapping: Windows `127.0.0.1:8793` → Linux `127.0.0.1:8793`. Restart JK Bridge. If the current Linux tunnel key has not been authorized for port `8793`, re-pair once as described below.

## Add another Linux web service

1. Add the service in JK Bridge with a unique Windows local port, the Linux loopback destination port, and a Windows loopback URL.
2. Choose a health mode: **HTTP status** for ordinary pages, **JSON ok=true** for that response shape, or **TCP connect** for self-signed HTTPS and port-only readiness.
3. Enable the service.
4. If the Linux destination port was not part of the previous authorization, the app lists it before connecting; run `python3 ~/jkbridge-linux.py pair` again.
5. Re-pair from Device setup while keeping the existing Windows identity path.

Health is advisory rather than browser access control. Once the SSH forward exists, an HTTP/TLS health failure still leaves **Open** available. A confirmed forwarding denial, disconnected tunnel, or disabled service remains blocking.

Re-pairing updates the exact `permitopen` list and retains a backup. It does not grant arbitrary access to every Linux port.

## Uninstall or revoke access

Disable **Start with Windows**, exit JK Bridge, and remove the extracted application folder if desired. Runtime configuration is under `%LOCALAPPDATA%\JKBridge`; the dedicated private key is under the path shown in Device setup.

Removing Windows files does not revoke a key that may have been copied. Revoke it on Linux first:

```bash
python3 ~/jkbridge-linux.py status
python3 ~/jkbridge-linux.py remove --fingerprint 'SHA256:...'
```

Then remove the Windows device from the Tailscale admin console if it should no longer belong to the tailnet.
