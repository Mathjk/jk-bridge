# Windows 11 Acceptance Checklist

## Packaging

- [ ] Verify `JKBridge-win-x64.zip` against the detached `JKBridge-win-x64.zip.sha256` before extraction.
- [ ] After extraction, recompute every entry in `SHA256SUMS`; no payload is missing, extra, or mismatched.
- [ ] `JKBridge.exe` launches on a clean Windows 11 x64 machine without a separately installed .NET runtime.
- [ ] Release output contains one distributable EXE with embedded debug information and no separate PDB in the delivery folder.
- [ ] Application icon is correct at 16, 20, 24, 32, 48, and 256 px.
- [ ] Unsigned-build SmartScreen behavior is documented; a signed release verifies when signing is later enabled.
- [ ] ZIP contains `jkbridge-linux.py`, both README languages, bilingual installation/troubleshooting guides, contributor/security documents, recovery scripts, README images, and all entries are covered by `SHA256SUMS`.

## Prerequisites and identity

- [ ] Tailscale installed/running is detected from its standard location and PATH.
- [ ] Missing/stopped/logged-out Tailscale produces distinct, actionable messages.
- [ ] Windows logged into the wrong tailnet produces `WrongTailnet` and no SSH retry loop.
- [ ] `ssh.exe` and `ssh-keygen.exe` are detected from Windows OpenSSH.
- [ ] A fresh profile opens Device setup and cannot attempt an incomplete SSH connection.
- [ ] A valid one-time code completes all four setup stages and clears the token field after success.
- [ ] Pairing uses no system HTTP proxy, follows no redirects, and the token never appears in configuration or logs.
- [ ] Expired, malformed, wrong-token, wrong-IP, mismatched-port, and oversized pairing responses fail without saving configuration.
- [ ] Re-pairing with the same managed key updates its exact port whitelist idempotently and retains a Linux backup.
- [ ] Upgrading a configured schema V1/V2 profile preserves device/services, enters Needs pairing before SSH, and becomes current after one successful re-pair.
- [ ] Schema V3 persists the returned client fingerprint and exact `permitPorts`; a missing enabled port blocks before Tailscale inspection and SSH startup.
- [ ] Missing key, rejected key, and encrypted/unavailable key produce distinct actions.
- [ ] `Setup-JKBridge.ps1` rejects a private-key file or parent directory that is a symlink/junction/reparse point.
- [ ] From a non-elevated PowerShell, `Setup-JKBridge.ps1` creates or reuses the dedicated key without requesting `SeSecurityPrivilege`, changes only the DACL, and leaves any SACL unchanged.
- [ ] `Verify-JKBridge.ps1` confirms the private key owner is the current SID and its protected DACL contains exactly current-user and SYSTEM full-control ACEs.
- [ ] Correct pinned host key connects.
- [ ] Missing or stale app-owned `known_hosts` is rebuilt only from `config.json`; changing the remote server key without changing the pinned config still blocks.
- [ ] With MagicDNS resolution deliberately unavailable but one exact online peer and CGNAT IPv4 present, the verifier emits `WARN`, the app connects by runtime-only IP, and `config.json` remains unchanged.

## Tunnels and services

- [ ] One SSH process creates all three default forwards.
- [ ] Overleaf opens at `http://127.0.0.1:8080/project` and supports editor/WebSocket use.
- [ ] GPU Dashboard opens at `http://127.0.0.1:8765`.
- [ ] Investment News opens at `http://127.0.0.1:8793`.
- [ ] GPU recovery requests remain localhost-originated and function as before.
- [ ] Listeners bind only `127.0.0.1`, never `0.0.0.0`.
- [ ] A stopped Overleaf marks only Overleaf unavailable.
- [ ] A stopped GPU Dashboard marks only GPU Dashboard unavailable.
- [ ] A stopped Investment News marks only Investment News unavailable.
- [ ] Adding a fourth service through the available name/description/local-port/remote-port/URL/enabled fields validates and restarts one tunnel process once.
- [ ] The service editor persists TCP connect, HTTP status, and JSON `ok=true` health choices.
- [ ] A self-signed HTTPS route in TCP mode reports the forward reachable without disabling Open because of certificate validation.
- [ ] An HTTP/TLS health failure leaves Open available after the forward exists, while a forwarding denial keeps it disabled.
- [ ] Duplicate and externally occupied local ports block connection without killing other processes.
- [ ] With a third Linux loopback service running but absent from the key's `permitopen` list, opening it produces live SSH `administratively prohibited` stderr and the affected service is shown as Forward denied without restarting SSH.
- [ ] The automatically generated authorization permits only enabled remote ports and never emits a wildcard `permitopen`.
- [ ] SSH `-L`, browser URL, health target, and paired remote port all match the same validated loopback route after add/edit operations.
- [ ] If the same public key exists in an unmanaged Linux entry, pairing refuses it and directs the user to generate a new identity instead of adding a restricted duplicate.

## Resilience

- [ ] Linux Tailscale IP change does not require configuration changes.
- [ ] Wi-Fi disconnect/reconnect recovers automatically.
- [ ] Tailscale restart recovers automatically.
- [ ] Linux reboot recovers automatically.
- [ ] While connected, sleep for at least five minutes: the SSH PID/listeners disappear on suspend; after resume and the settle delay, one new SSH process restores all enabled services automatically.
- [ ] Retry delay increases; choosing Refresh/Recheck while reconnecting restarts preflight immediately and resets the backoff attempt.
- [ ] Authentication, host-key, configuration, and port errors do not enter infinite retries.

## Lifetime and shell integration

- [ ] Closing the window hides to tray when configured.
- [ ] Tray Open restores and activates the existing window.
- [ ] A second EXE launch activates the first instance and does not create another SSH process.
- [ ] Explicit Exit removes the SSH process and all local listeners.
- [ ] Force-killing the GUI closes the Job Object and removes its SSH process.
- [ ] Enable start-with-Windows and confirm only the HKCU Run value exists. Exit, move the portable folder, launch from the new path, and confirm the stale registration is repaired to the current quoted EXE plus `--background`.

## UI and accessibility

- [ ] Device setup, Overview, Services, Diagnostics, and Settings work with mouse and keyboard.
- [ ] Light, dark, and system themes are legible.
- [ ] 125%, 150%, and 200% scaling preserve primary actions.
- [ ] At the supported `980 x 680` minimum window size, both fixed service-card columns and all primary actions remain usable at supported scaling.
- [ ] Status uses icon, text, and color together.
- [ ] Focus order and screen-reader automation names are meaningful.
- [ ] The tray tooltip remains the documented static `JK Bridge`, and the native menu actions match the UI specification.

## Privacy and logs

- [ ] Logs contain no passwords, private keys, cookies, raw Tailscale JSON, or full environment dumps.
- [ ] User-directory paths are redacted in copied diagnostics.
- [ ] Config writes recover from interruption using the valid backup.
- [ ] Log rotation honors the size and retention caps.
- [ ] Public-release scan finds no real machine names, tailnet suffixes, user paths, generated keys, pairing tokens, credentials, runtime configuration, or logs.
