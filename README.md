<p align="right"><strong>English</strong> · <a href="README.zh-CN.md">简体中文</a></p>

<p align="center">
  <img src="docs/images/app-icon.svg" width="112" alt="JK Bridge logo">
</p>

<h1 align="center">JK Bridge</h1>

<p align="center">
  Open Linux localhost web apps from Windows through Tailscale and a locked-down OpenSSH tunnel.<br>
  No fixed Tailscale IPs, hand-edited SSH keys, or Linux-side daemon.
</p>

<p align="center">
  <a href="https://github.com/Mathjk/jk-bridge/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/Mathjk/jk-bridge/actions/workflows/ci.yml/badge.svg"></a>
  <a href="https://github.com/Mathjk/jk-bridge/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/Mathjk/jk-bridge?style=flat-square"></a>
  <a href="LICENSE"><img alt="MIT license" src="https://img.shields.io/github/license/Mathjk/jk-bridge?style=flat-square"></a>
  <img alt="Windows 11 x64" src="https://img.shields.io/badge/client-Windows%2011%20x64-0078D4?style=flat-square&logo=windows11">
</p>

<p align="center">
  <a href="https://github.com/Mathjk/jk-bridge/releases/latest"><strong>Download</strong></a> ·
  <a href="#five-minute-setup"><strong>Quick start</strong></a> ·
  <a href="docs/INSTALLATION.md"><strong>Installation guide</strong></a> ·
  <a href="docs/TROUBLESHOOTING.md"><strong>Troubleshooting</strong></a> ·
  <a href="docs/SECURITY.md"><strong>Security model</strong></a>
</p>

<p align="center">
  <img src="docs/images/device-setup.png" width="900" alt="JK Bridge device setup screen">
</p>

## Why JK Bridge?

Overleaf, GPU dashboards, Jupyter, and other self-hosted tools are often safest when they listen only on Linux `127.0.0.1`. That also makes them inconvenient to open from a Windows computer.

JK Bridge turns those services into ordinary Windows-local URLs while preserving the loopback boundary. It saves the Linux node's stable Tailscale MagicDNS name—not its changeable `100.x.y.z` address—then supervises one strict SSH tunnel for every enabled service.

```text
Windows browser                                                   Linux host
http://127.0.0.1:8080/project                                    127.0.0.1:80
          │                                                             ▲
          └── JK Bridge ── OpenSSH local forward ── Tailscale ──────────┘
```

The normal first run is: start the temporary Linux helper, paste its one-time code into the Windows app, and select **Pair and connect**. The helper exits after pairing; daily use happens entirely from `JKBridge.exe`.

## What runs where?

| File or component | Run it on | Purpose |
|---|---|---|
| `JKBridge.exe` | Windows 11 x64 | Desktop UI, setup, tunnel supervision, health checks, tray, and startup |
| `jkbridge-linux.py` | Linux | One-shot pairing, managed SSH-key status, and revocation |
| Tailscale | Both devices | Encrypted device-to-device network and stable MagicDNS identity |
| OpenSSH Client | Windows only | Creates the dedicated key and runs the local-forward tunnel |
| OpenSSH Server | Linux only | Accepts the restricted key and terminates the tunnel |

> [!IMPORTANT]
> Windows does **not** need OpenSSH Server, and Linux does **not** need the JK Bridge desktop app.

## Requirements and official downloads

| Device | Requirement | Official install/download | Quick verification |
|---|---|---|---|
| Windows | Windows 11 x64 | Built into the operating system | `winver` |
| Windows | Tailscale, running and signed in | [Windows download](https://tailscale.com/download/windows) · [install guide](https://tailscale.com/docs/install/windows) | `tailscale status` |
| Windows | OpenSSH Client (`ssh.exe` and `ssh-keygen.exe`) | [Microsoft installation guide](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_install_firstuse) | `Get-Command ssh, ssh-keygen` |
| Linux | Tailscale, running and signed in | [Linux install guide](https://tailscale.com/docs/install/linux) · [manual packages](https://pkgs.tailscale.com/stable/) | `tailscale status` |
| Linux | Python 3.9 or later | Your distribution package, or [python.org](https://www.python.org/downloads/) | `python3 --version` |
| Linux | OpenSSH Server with an Ed25519 host key | [Ubuntu Server guide](https://documentation.ubuntu.com/server/how-to/security/openssh-server/) | `sudo sshd -t` |
| Linux | Each target web app on loopback | The web app's own documentation | `curl -I http://127.0.0.1:<port>/` |

Both devices must appear in the same tailnet, or the tailnet policy must explicitly allow them to communicate. The login email addresses may differ; tailnet membership is what matters.

The app checks these prerequisites but intentionally does not install operating-system packages, elevate itself, change `sshd_config`, or start your web apps. See the [step-by-step installation guide](docs/INSTALLATION.md) for GUI and command-line installation methods.

## Five-minute setup

### 1. Install and verify the prerequisites

Complete the table above on both devices. On Windows, install **OpenSSH Client**, not OpenSSH Server. On Linux, confirm SSH and the target web service are already running.

### 2. Download and verify JK Bridge on Windows

From the [latest Release](https://github.com/Mathjk/jk-bridge/releases/latest), download these two assets from the same version:

- `JKBridge-win-x64.zip`
- `JKBridge-win-x64.zip.sha256`

In PowerShell, from the download directory:

```powershell
Get-FileHash .\JKBridge-win-x64.zip -Algorithm SHA256
Get-Content .\JKBridge-win-x64.zip.sha256
```

The two hashes must match. Extract the ZIP to a permanent folder. Releases are not currently code-signed, so Windows may show an unknown-publisher warning; verify the hash before choosing to run the app.

### 3. Copy the Linux helper to Linux

The extracted ZIP already contains `jkbridge-linux.py`; there is no separate installer. Copy that one file to the Linux user's home directory using Tailscale file transfer, SCP, a shared folder, or removable media.

Run it as the ordinary Linux account that JK Bridge should use for SSH—never with `sudo`:

```bash
chmod 700 ~/jkbridge-linux.py
python3 ~/jkbridge-linux.py status
python3 ~/jkbridge-linux.py pair
```

Keep this terminal open. The helper prints a `jkbridge://...` code that expires after 10 minutes and after one successful use.

### 4. Pair from Windows

1. Start `JKBridge.exe`.
2. Open **Device setup** if it is not already visible.
3. Paste the complete `jkbridge://...` code.
4. Select **Pair and connect**.

The app creates a dedicated Ed25519 key, restricts its Windows file permissions, pins the Linux SSH host key, and asks the helper to install one exact loopback-only authorization. It never asks for the Linux password and never sends a private key.

### 5. Open a service

Use the Overview page or tray menu. The browser URL remains a Windows URL such as `http://127.0.0.1:8080/project`; do not replace it with the Linux Tailscale IP.

Default examples:

| Service | Open on Windows | Forwarded to Linux |
|---|---|---|
| Overleaf | `http://127.0.0.1:8080/project` | `127.0.0.1:80` |
| GPU Dashboard | `http://127.0.0.1:8765/` | `127.0.0.1:8765` |

These entries are editable. Nothing is bound to the public internet or ordinary LAN.

## After the first setup

| Situation | What to do |
|---|---|
| Normal Windows reboot | Start `JKBridge.exe`, or enable **Start with Windows** once |
| Linux Tailscale IP changes | Nothing; JK Bridge resolves the saved MagicDNS identity |
| Add a web app using an already-authorized Linux port | Add or edit it on the Services page |
| Add a new Linux destination port | Enable the service, run `pair` again on Linux, and re-pair with the existing Windows key |
| Linux is reinstalled or its SSH host key is intentionally rotated | Verify the change locally, then re-pair |
| A Windows device/key is lost | Run Linux `status`, then `remove --fingerprint 'SHA256:...'` |

Pairing updates only the JK Bridge-managed line in `~/.ssh/authorized_keys` and first backs up the file under `~/.ssh/jkbridge-backups/`.

## Package contents

| File | Use |
|---|---|
| `JKBridge.exe` | Portable, self-contained Windows x64 app |
| `jkbridge-linux.py` | Standalone Python 3 Linux helper |
| `Setup-JKBridge.ps1` | Advanced V1/manual recovery path; not needed for normal V2 setup |
| `Verify-JKBridge.ps1` | Windows environment and runtime diagnostics |
| `README*.md`, `docs/` | English and Chinese setup, troubleshooting, security, and architecture |
| `SHA256SUMS`, `*.sha256` | Release integrity records |

## Troubleshooting

| Symptom | First check |
|---|---|
| Linux device is not found | Run `tailscale status` on both devices and confirm they share a tailnet |
| Pairing code times out | Keep `pair` running, generate a fresh code, and check tailnet ACLs/firewall |
| “OpenSSH needs attention” | Install Windows **OpenSSH Client**, then verify both `ssh` and `ssh-keygen` |
| Tunnel starts but a page fails | On Linux, `curl` the exact configured `127.0.0.1:<port>` URL |
| “Host key mismatch” | Stop; verify a legitimate Linux reinstall/key rotation, then re-pair—never disable checking |
| Local port is occupied | Close the known owner or choose another Windows local port |
| New service says forwarding is prohibited | Re-pair after enabling its new Linux destination port |

See [Troubleshooting](docs/TROUBLESHOOTING.md) for exact commands, restart behavior, duplicate-key migration, and safe diagnostics.

## Security in brief

- Pairing is reachable only on the Linux Tailscale IPv4 and additionally requires a random, short-lived 256-bit token.
- The Linux host key is read locally during pairing and pinned; subsequent SSH connections use strict verification.
- The dedicated Windows private key is limited to the current user and SYSTEM.
- The Linux key cannot open a shell or run commands and can reach only explicitly enabled Linux `127.0.0.1` ports.
- Every Windows listener and Linux destination is fixed to `127.0.0.1`.
- Configuration writes are atomic; diagnostics are redacted; the public-release scanner blocks common credentials and machine identities.

Read the full [security design](docs/SECURITY.md) and [pairing protocol](docs/PAIRING_PROTOCOL.md). To report a vulnerability privately, follow [SECURITY.md](SECURITY.md).

## Documentation

| Document | Audience |
|---|---|
| [Installation](docs/INSTALLATION.md) · [中文](docs/INSTALLATION.zh-CN.md) | First-time users and administrators |
| [Troubleshooting](docs/TROUBLESHOOTING.md) · [中文](docs/TROUBLESHOOTING.zh-CN.md) | Users diagnosing setup, tunnel, or web-service failures |
| [Architecture](docs/ARCHITECTURE.md) | Maintainers and security reviewers |
| [Security design](docs/SECURITY.md) | Administrators and reviewers |
| [Pairing protocol](docs/PAIRING_PROTOCOL.md) | Implementers and protocol reviewers |
| [Development guide](docs/DEVELOPMENT.md) | Contributors building and releasing the project |
| [Windows acceptance checklist](docs/WINDOWS_ACCEPTANCE.md) | Release testers |

## Build from source

The desktop app targets .NET 10 and Avalonia 12.1. With the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) installed:

```bash
dotnet test JKBridge.slnx -c Release
python3 -m unittest discover -s tests/linux -p 'test_*.py'
./scripts/check-public-release.sh
./scripts/check-doc-links.sh
./scripts/publish-win-x64.sh
```

The last command creates the tested Windows package under `artifacts/`. Start with the [development guide](docs/DEVELOPMENT.md) before changing pairing, authorization, process supervision, or release code.

## Support and contributing

- For a reproducible bug or feature request, [open an issue](https://github.com/Mathjk/jk-bridge/issues/new/choose). Remove hostnames, tailnet names, usernames, public keys, tokens, and personal paths first.
- For code or documentation changes, read [CONTRIBUTING.md](CONTRIBUTING.md).
- For vulnerabilities, use the private process in [SECURITY.md](SECURITY.md), not a public issue.

JK Bridge is currently tested as a portable Windows 11 x64 client. The executable is not yet commercially code-signed, and the project does not install Tailscale/OpenSSH, manage tailnet policy, expose public URLs, or start the underlying Linux web services.

## License

[MIT](LICENSE)
