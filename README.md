# JK Bridge

[简体中文](README.zh-CN.md) | English

JK Bridge is a Windows desktop app that opens Linux localhost-only web services through an existing Tailscale network and one supervised OpenSSH tunnel. It uses the Linux node's MagicDNS identity, so a changed `100.x.y.z` address does not require editing launchers or configuration.

The normal first run is deliberately short:

1. Run one temporary helper command on Linux.
2. Paste its one-time pairing code into the Windows app.
3. Select **Pair and connect**.

The app creates a dedicated Ed25519 key, applies a restrictive Windows ACL, pins the Linux SSH host key, and asks the Linux helper to install an exact loopback-only `authorized_keys` rule. The helper then exits; no Linux daemon or JK Bridge package installation is required.

## What it provides

- One-click Windows-to-Linux pairing over Tailscale.
- No Linux password collection and no private-key transfer.
- Stable MagicDNS identity instead of a persisted Tailscale IP.
- One supervised `ssh.exe` process for all enabled local forwards.
- Loopback-only Windows listeners and Linux targets.
- Per-service health checks, bounded reconnects, tray mode, and optional Windows startup.
- Editable services for future localhost web apps.
- Atomic configuration, pinned host keys, redacted diagnostics, and restricted server authorization.
- A self-contained Windows x64 EXE plus a standalone Python 3 Linux helper.

Default services:

| Service | Windows URL | Forward |
|---|---|---|
| Overleaf | `http://127.0.0.1:8080/project` | Windows `8080` → Linux `127.0.0.1:80` |
| GPU Dashboard | `http://127.0.0.1:8765/` | Windows `8765` → Linux `127.0.0.1:8765` |

These are editable examples. JK Bridge never binds them to the public internet or ordinary LAN.

## Requirements

Windows 11 x64:

- Tailscale installed, running, and logged in.
- Windows OpenSSH Client (`ssh.exe` and `ssh-keygen.exe`).
- The Windows and Linux devices must be members of the same tailnet, or otherwise permitted to communicate by its access policy.

Linux:

- Tailscale installed, running, and logged in.
- Python 3.9 or later.
- OpenSSH Server with an Ed25519 host key.
- The intended non-root SSH user must be able to edit its own `~/.ssh/authorized_keys`.
- Each target web service must listen on Linux loopback, such as `127.0.0.1:80`.

JK Bridge detects these prerequisites and fails safely, but it does not use administrator privileges to install operating-system packages or change `sshd_config`.

## Quick start

### 1. Verify the release

Download the Windows ZIP and Linux helper from the same GitHub Release. Verify the ZIP using the detached checksum before extracting it:

```powershell
Get-FileHash .\JKBridge-win-x64.zip -Algorithm SHA256
Get-Content .\JKBridge-win-x64.zip.sha256
```

After extraction, verify every packaged file against `SHA256SUMS`. Release executables are currently unsigned, so Windows may show an unknown-publisher warning.

### 2. Start one-shot pairing on Linux

Run this as the Linux account JK Bridge will use for SSH. Do not use `sudo`:

```bash
chmod +x ./jkbridge-linux.py
./jkbridge-linux.py status
./jkbridge-linux.py pair
```

The helper prints a code beginning with `jkbridge://`. It listens only on the Linux Tailscale IPv4 address, accepts one successful request, expires after 10 minutes, and then exits.

### 3. Pair on Windows

1. Start `JKBridge.exe`.
2. Open **Device setup** if it is not already shown.
3. Paste the complete `jkbridge://...` code.
4. Select **Pair and connect**.

The token is sent only in an authorization header and is never stored in configuration. On success the pairing field is cleared, the Linux helper exits, and JK Bridge opens one SSH tunnel containing all enabled forwards.

### 4. Open a service

Use the Overview page or tray menu. Browser addresses remain under Windows `127.0.0.1`; do not replace them with the Linux Tailscale IP.

## Daily use and reboots

After a successful pairing, no username, password, IP address, or pairing code is normally needed again.

- If **Start with Windows** is enabled in Settings, JK Bridge starts for the current Windows user and reconnects automatically after Tailscale becomes ready.
- Otherwise, start `JKBridge.exe` manually.
- A Linux Tailscale IP change requires no reconfiguration because the app resolves the saved MagicDNS identity.
- Re-pair only after a Linux reinstall/SSH host-key rotation, deliberate key revocation, moving to a different Linux user or node, or adding remote ports not already authorized.

## Adding another service

Add the service on the Services page with a unique Windows local port, the Linux loopback port, and a Windows loopback URL. JK Bridge always fixes the remote host to `127.0.0.1`.

The Linux authorization contains an exact `permitopen` list. If the new remote port was not authorized during the last pairing:

1. Stop the active bridge.
2. Run `./jkbridge-linux.py pair` again on Linux.
3. Open Device setup in JK Bridge and pair again using the existing Windows identity path.

The managed key entry is updated atomically and a backup is retained.

An exact restricted entry produced by the V1 setup helper is recognized and automatically migrated to the V2 managed format. A personal, unrestricted, malformed, or merely comment-lookalike entry using the same public key is still refused; choose a new Windows identity path in that case.

## Linux helper operations

```bash
# Check Tailscale identity and list JK Bridge-managed keys
./jkbridge-linux.py status

# Start a 10-minute one-shot pairing endpoint
./jkbridge-linux.py pair

# Remove one managed authorization by the displayed fingerprint
./jkbridge-linux.py remove --fingerprint 'SHA256:...'
```

Backups are stored under `~/.ssh/jkbridge-backups/`. The helper refuses symlinked or incorrectly owned SSH paths, unmanaged duplicate public keys, malformed keys, non-Tailscale clients, invalid tokens, and non-loopback authorization requests.

## Troubleshooting

| Symptom | Action |
|---|---|
| Tailscale is missing or logged out | Start Tailscale on both devices and confirm they can see each other. |
| Pairing times out | Confirm both devices are in the same tailnet and its ACL allows Windows to reach the Linux ephemeral pairing port. Generate a new code. |
| OpenSSH is missing on Windows | Install the Windows **OpenSSH Client** optional feature. |
| SSH authentication is rejected | Run Linux `status`, confirm the managed key exists, then re-pair with a new Windows identity path if the same key exists in an unmanaged entry. |
| Host key mismatch | Stop. Verify whether Linux was reinstalled or its SSH host key was intentionally rotated, then re-pair locally. Never disable strict host-key checking. |
| Local port is occupied | Choose another local port or close the known owning program. JK Bridge never kills it automatically. |
| Tunnel works but one page does not | Check that the service is listening on the configured Linux `127.0.0.1:<port>`. |
| A new service says forwarding is prohibited | Re-pair after enabling that service so its exact remote port is added to `permitopen`. |

Windows fallback diagnostics remain available in `Verify-JKBridge.ps1`. `Setup-JKBridge.ps1` is retained for advanced manual recovery, but is not part of the normal V2 setup.

## Security model

- Pairing HTTP is carried inside Tailscale's encrypted network and additionally requires a 256-bit, short-lived bearer token.
- The helper binds only to the Linux Tailscale IPv4 interface and stops after one success, expiry, cancellation, or too many failures.
- The token never appears in an HTTP URL, app configuration, or application logs.
- The Linux SSH host public key is read locally by the helper and pinned by Windows; SSH uses strict host-key checking afterward.
- The Windows private key is protected by a DACL containing only the current user and SYSTEM.
- The Linux authorization disables commands and shell access, limits sources to Tailscale ranges, and permits only exact enabled Linux loopback ports.
- All local forwards bind to Windows `127.0.0.1`; all destinations are Linux `127.0.0.1`.

See [Security](docs/SECURITY.md), [Architecture](docs/ARCHITECTURE.md), and [Pairing protocol](docs/PAIRING_PROTOCOL.md) for the full design.

## Build and test

The repository targets .NET 10 and Avalonia 12.1:

```bash
dotnet test JKBridge.slnx -c Release
python3 -m unittest discover -s tests/linux -p 'test_*.py'
./scripts/check-public-release.sh
./scripts/publish-win-x64.sh
```

The publish script creates:

- `artifacts/JKBridge-win-x64.zip`
- `artifacts/JKBridge-win-x64.zip.sha256`
- `artifacts/delivery/JKBridge.exe`
- `artifacts/delivery/jkbridge-linux.py`
- bilingual documentation and `SHA256SUMS`

Windows-specific tray, startup, sleep/resume, Job Object, and browser behavior should also be checked using [the Windows acceptance checklist](docs/WINDOWS_ACCEPTANCE.md).

## Repository map

```text
src/                    .NET core and Avalonia desktop app
scripts/                Linux pairing, Windows fallback, release tools
tests/                  .NET and Linux helper tests
docs/                   product, architecture, security, protocol, decisions
.github/workflows/       continuous integration and release packaging
```

## Privacy and public releases

This repository ships no configured host, tailnet, Tailscale IP, username, home-directory path, client public key, host public key, token, or runtime log. Defaults are intentionally unconfigured, tests use generated example identities, and packaging fails if the public-release scanner detects common credentials or machine-specific paths.

Before publishing a fork, run `./scripts/check-public-release.sh`, inspect `git diff --cached`, and never add runtime files from `%LOCALAPPDATA%\JKBridge`, `~/.ssh`, or `artifacts/`.

## License

MIT. See [LICENSE](LICENSE).
