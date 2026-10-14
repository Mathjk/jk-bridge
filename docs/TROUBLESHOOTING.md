# JK Bridge troubleshooting

[简体中文](TROUBLESHOOTING.zh-CN.md) · **English**

Work from the outside in: operating-system prerequisites, Tailscale reachability, pairing, SSH tunnel, then the Linux web service. JK Bridge can connect successfully even when one target web app is down, so keep tunnel failures and service-health failures separate.

> [!NOTE]
> The current release provides both the unsigned installer and portable ZIP,
> plus Service Profiles, Windows-local discovery, restart actions, the optional
> `jkbridge-ctl` control daemon, desktop alerts, the manual updater, and
> first-run guidance. One-shot Linux pairing remains the supported
> setup/recovery path.

## Collect a safe baseline

On Windows, from the extracted JK Bridge folder:

```powershell
& "$env:ProgramFiles\Tailscale\tailscale.exe" status
Get-Command ssh, ssh-keygen
.\Verify-JKBridge.ps1 -PortExpectation Any
```

On Linux, as the ordinary JK Bridge user:

```bash
tailscale status
python3 ~/jkbridge-linux.py status
sudo sshd -t
```

Do not post raw pairing codes, `config.json`, private keys, Tailscale JSON, or `authorized_keys`. Before sharing diagnostics, remove hostnames, tailnet names, usernames, IPs, fingerprints, and personal paths.

## The Windows app does not open

1. Confirm the ZIP hash using the commands in [Installation](INSTALLATION.md).
2. Extract the ZIP fully; do not run the EXE from inside File Explorer's archive preview.
3. If Windows marks files downloaded from the internet, use **Properties → Unblock** on the ZIP before extracting, or run:

   ```powershell
   Unblock-File .\JKBridge.exe
   ```

4. Start it once from PowerShell and then inspect `%LOCALAPPDATA%\JKBridge\logs` if it exits unexpectedly.

The executable is currently unsigned, so SmartScreen may show an unknown publisher. A verified Release hash confirms file integrity but does not suppress SmartScreen.

## The Settings update action is unavailable or fails

- Preview 1 has no direct-update control. If the old one-step action in Preview
  2/3 stalls, manually download the official Preview 4 installer and
  `.sha256`, or explicitly exit the old tray process before running its already
  verified installer.
- Preview 4/5 force direct GitHub connections. If Release checks work but asset
  download times out on a proxy-dependent network, manually install Preview 6
  once. Preview 6 uses the Windows/.NET system proxy while retaining exact-host,
  redirect, size, and SHA-256 validation.
- In **Settings → Update network**, select Windows system proxy (default), a
  manual loopback HTTP proxy, or direct GitHub access, then save. A manual value
  such as `http://127.0.0.1:7897` must not contain credentials or a path and
  cannot point to a LAN or public host.
- Run **Check official Release** first. **Download and verify update** appears
  only when the selected Release has the exact EXE and `.sha256` pair.
- Download bytes, percentage, average speed, estimated remaining time, and
  verification stage are visible, and the operation can be cancelled. A checksum
  mismatch, untrusted redirect, cancellation, or timeout never starts setup or
  exits JK Bridge.
- If the in-app route remains slow, use **Open in browser** to download from the
  already validated official Release page through the browser's own network path.
- After verification, select **Start installation (JK Bridge will exit)**.
  JK Bridge exits only after the normal visible setup wizard starts
  successfully; the wizard displays real installation progress. A launch
  failure leaves JK Bridge running so the action can be retried.

## Tailscale is missing, signed out, or cannot find Linux

Run `tailscale status` on both devices. Confirm:

- `BackendState` is running;
- each device appears in the same tailnet;
- the Linux device is online;
- tailnet ACLs/grants allow the Windows device to reach Linux;
- device approval, key expiry, and host firewall rules are not blocking the connection.

The login accounts can differ. If the devices landed in different tailnets, sign out of the incorrect one or invite the other account/device into the intended tailnet.

From Windows:

```powershell
& "$env:ProgramFiles\Tailscale\tailscale.exe" ping <linux-magicdns-name>
```

JK Bridge saves the exact MagicDNS name. A changed Tailscale IP alone is not a reason to re-pair.

## “OpenSSH needs attention” or OpenSSH is not found

JK Bridge needs two Windows executables: `ssh.exe` and `ssh-keygen.exe`.

```powershell
Get-Command ssh, ssh-keygen
Get-WindowsCapability -Online | Where-Object Name -Like 'OpenSSH.Client*'
```

If the capability is absent, install **OpenSSH Client** from Optional features or from an administrator PowerShell:

```powershell
Add-WindowsCapability -Online -Name OpenSSH.Client~~~~0.0.1.0
```

Reopen PowerShell and JK Bridge. Do not install Windows OpenSSH Server for this problem.

## The overview says “tunnel needs attention: not started”

This is a state, not proof that `ssh.exe` crashed. Check in order:

1. Device setup shows a completed Linux profile, user, identity path, and pinned host key.
2. Tailscale and OpenSSH checks pass.
3. Select **Connect** or complete **Pair and connect**.
4. Confirm every enabled Windows local port is free.
5. Open Diagnostics and read the newest blocking item.

For an already configured profile:

```powershell
.\Verify-JKBridge.ps1 -PortExpectation Free
```

Run that before connecting. After JK Bridge reports connected, use:

```powershell
.\Verify-JKBridge.ps1 -PortExpectation Listening -TestServices
```

## Pairing code is rejected or times out

- Run `pair` without `sudo`, using the same ordinary Linux user that SSH should use.
- Keep the Linux terminal open until Windows finishes.
- Copy the complete `jkbridge://...` value without spaces or line breaks.
- Codes expire after 10 minutes and one success; generate a new one instead of reusing it.
- Confirm Windows can reach the Linux MagicDNS node through Tailscale.
- Check whether a host firewall blocks the temporary port shown by the helper.
- Confirm the client system clock is reasonably correct.

The temporary server binds only the Linux Tailscale IPv4, not the LAN or public interfaces.

## A Linux relay plan is blocked or apply is unavailable

The stable v2.2.1 app exposes the optional experimental **Advanced: Linux
For HTTPS, the relay is transparent TCP. A successful TCP probe does not prove
that a certificate, SNI, cookie domain, redirect, or application host check will
accept a Windows URL whose host is `127.0.0.1`.

## “Re-pair required” after upgrading

Schema V3 in v2.1 compares the currently enabled Linux destination ports with the exact ports returned by the last pair. V1/V2 did not store that receipt, so an upgraded profile must pair once. Device and service settings are preserved; start `pair` on Linux and keep the existing dedicated identity path in Windows.

After that baseline exists, only a new or changed Linux destination port blocks connection. The app lists the missing ports. Changing only a Windows local port does not require new authorization.

## A duplicate public key or managed-entry warning appears

JK Bridge refuses to add a restricted copy of a key that already exists in an unmanaged `authorized_keys` entry, because the older entry may still grant broader access.

```bash
python3 ~/jkbridge-linux.py status
```

V2 automatically migrates an exact, fully restricted V1 entry. It refuses a personal unrestricted key, malformed line, duplicate, or comment lookalike. In that case, choose a new dedicated identity path in Windows and pair again. Do not delete unrelated personal SSH keys merely to silence the warning.

Back up and inspect `~/.ssh/authorized_keys` locally if manual cleanup is required. JK Bridge backups are under `~/.ssh/jkbridge-backups/`.

## SSH authentication is denied

On Linux:

```bash
python3 ~/jkbridge-linux.py status
ls -ld ~/.ssh
ls -l ~/.ssh/authorized_keys
sudo sshd -T | grep -E '^(pubkeyauthentication|allowtcpforwarding)'
```

Expected ownership is the ordinary Linux user, with `~/.ssh` normally `0700` and `authorized_keys` `0600`. Public-key authentication must be enabled and TCP forwarding must be `yes` or `local`.

Do not loosen permissions to `0777`, enable password fallback, or replace the managed line with an unrestricted key. Re-pairing safely recreates the managed entry.

## SSH host key mismatch

Stop immediately. The pinned key changed independently of the Tailscale IP. Legitimate causes include a Linux reinstall or intentional SSH host-key rotation; an unverified mismatch may indicate the wrong machine or interception.

Verify the change on the Linux console. If intentional, run a fresh one-shot pairing locally and update the Windows profile. Never solve this by enabling `StrictHostKeyChecking=no`, using `accept-new`, or deleting validation without re-pairing.

## A Windows local port is occupied

Find the owner without terminating it automatically:

```powershell
Get-NetTCPConnection -State Listen -LocalPort 8080 | Select-Object LocalAddress,LocalPort,OwningProcess
Get-Process -Id <OwningProcess>
```

Close the known application or assign JK Bridge a different Windows local port. Each enabled service needs a unique local port, and the listener must remain on `127.0.0.1`.

The new browser address must use the replacement **Windows** port. For example,
if Windows `8080` was occupied and you deliberately choose `18080` while Linux
still listens on `80`, use:

```text
http://127.0.0.1:18080/project -> Linux 127.0.0.1:80
```

Changing only `8080` to `18080` and updating the loopback browser URL does not
require Linux reauthorization. Do not change the Linux remote port merely to
solve a Windows collision. A vNext simple editor is intended to regenerate this
URL and suggest a free port, but it must not silently change a stable address;
v2.1.0 users should update and verify the explicit URL themselves.

## Tunnel connects, but one page does not load

Test the exact destination on Linux:

```bash
curl -v http://127.0.0.1:<remote-port>/<path>
ss -ltnp | grep ':<remote-port>\b'
```

Common causes are:

- the web app is stopped;
- its port differs from the JK Bridge remote port;
- it listens only on another container/network namespace;
- the Windows URL contains the wrong path;
- the app requires HTTPS while the configured URL uses HTTP;
- the app returns a slow or unusual health response even though it is usable.

JK Bridge forwards TCP; it does not repair application routing, authentication, TLS, containers, or reverse-proxy configuration.

For a self-signed HTTPS service whose port and browser access are otherwise correct, select **TCP connect** in the Services editor. That checks TCP reachability without deciding whether the browser should trust the certificate. An HTTP/TLS health failure no longer disables **Open** after the SSH forward exists; you still make the certificate decision in the browser.

## A new service says forwarding is prohibited

The Linux key has an exact `permitopen` whitelist. Enable the service in Windows, stop the current tunnel, start `pair` on Linux, and pair again using the existing identity. The updated managed line will include the new Linux destination port.

Changing only the Windows local port does not require new Linux authorization. Changing or adding the Linux remote port does.

## Reboot or resume does not reconnect

- Verify Tailscale itself is running and signed in after login.
- If **Start with Windows** is enabled, do not move `JKBridge.exe`; the startup entry records its absolute path.
- Start the app manually once and inspect Diagnostics.
- Sleep/resume deliberately stops and re-enters tunnel preflight; allow Tailscale a short time to restore connectivity.
- A Linux Tailscale IP change is normal and should not require re-pairing.

## Reset or revoke access

List and remove only JK Bridge-managed Linux authorizations:

```bash
python3 ~/jkbridge-linux.py status
python3 ~/jkbridge-linux.py remove --fingerprint 'SHA256:...'
```

For a full Windows reset, exit JK Bridge, revoke the Linux key first, disable startup, then remove `%LOCALAPPDATA%\JKBridge` and the dedicated identity shown in Device setup. Never delete an identity unless you have confirmed it is dedicated to JK Bridge.

## Copying configuration or importing services did not authorize a new computer

Do not migrate a whole `config.json` or a private key to another Windows
computer. The authorization receipt belongs to the old key, and sharing one
private key prevents independent device revocation. Pair the new computer with
its own key, then revoke the old fingerprint only after successful validation.

The stable v2.2.1 app has a native Service Profile file picker: export writes the
service-only allow-list, while import first performs a read-only validation and
change preview. Applying the preview requires a separate explicit confirmation.

The v2.2.1 importer can also read an exact v2.1/schema-v3
`config.json` as a legacy input when no Service Profile export is available. It
validates the old document but projects only Services into the preview; the old
server, tailnet, behavior, private-key path, authorization receipt, and device
state are discarded. Do not overwrite the new computer's config with the old
file, and continue treating that legacy source as sensitive.

A profile intentionally contains no private key, pairing token, device
credential, server identity, or authorization receipt. Importing service
definitions therefore never authorizes Linux. If the preview lists a new Linux
remote port, complete one-shot pairing again. Canceling the picker or refusing confirmation makes no change;
if services changed after preview, select and preview the file again.

## Still blocked?

Open a [GitHub issue](https://github.com/Mathjk/jk-bridge/issues/new/choose) with:

- JK Bridge version and Windows version;
- Linux distribution and OpenSSH version;
- the exact step that failed;
- redacted output from `Verify-JKBridge.ps1` and Linux `status`;
- whether Tailscale ping, SSH tunnel, and Linux-local `curl` succeed independently.

For suspected vulnerabilities or leaked credentials, use the private process in [SECURITY.md](../SECURITY.md) instead of a public issue.
