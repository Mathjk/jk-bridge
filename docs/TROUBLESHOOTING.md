# JK Bridge troubleshooting

[简体中文](TROUBLESHOOTING.zh-CN.md) · **English**

Work from the outside in: operating-system prerequisites, Tailscale reachability, pairing, SSH tunnel, then the Linux web service. JK Bridge can connect successfully even when one target web app is down, so keep tunnel failures and service-health failures separate.

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

## Still blocked?

Open a [GitHub issue](https://github.com/Mathjk/jk-bridge/issues/new/choose) with:

- JK Bridge version and Windows version;
- Linux distribution and OpenSSH version;
- the exact step that failed;
- redacted output from `Verify-JKBridge.ps1` and Linux `status`;
- whether Tailscale ping, SSH tunnel, and Linux-local `curl` succeed independently.

For suspected vulnerabilities or leaked credentials, use the private process in [SECURITY.md](../SECURITY.md) instead of a public issue.
