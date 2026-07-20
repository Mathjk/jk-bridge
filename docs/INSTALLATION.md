# JK Bridge installation guide

[简体中文](INSTALLATION.zh-CN.md) · **English**

This guide takes a new Windows 11 x64 client and a Linux host from an empty environment to a working JK Bridge connection. System package installation is deliberately separate from one-click pairing: JK Bridge checks dependencies but never elevates itself or changes operating-system configuration.

## Before you begin

You need:

- administrator access while installing Windows optional features and software;
- `sudo` access while installing Linux packages;
- one ordinary, non-root Linux account for JK Bridge pairing and SSH;
- both devices in the same Tailscale tailnet, or an access policy that permits them to communicate;
- at least one web service already reachable on Linux `127.0.0.1`.

The email addresses used to sign in to Tailscale do not have to be identical. Both devices simply need to be members of the same tailnet and allowed by its policy.

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

## 8. Configure daily startup

In JK Bridge Settings, enable **Start with Windows** if desired. This registers the app only for the current Windows user. At the next login, JK Bridge waits for Tailscale and reconnects automatically.

Without that option, simply start `JKBridge.exe` after a reboot. No pairing code, username, password, or IP address is normally required again.

Keep the extracted directory in place: moving or deleting `JKBridge.exe` after enabling startup leaves the startup entry pointing to the old path.

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
