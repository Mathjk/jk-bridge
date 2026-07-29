# Windows 11 Acceptance Checklist

The checklist through **Privacy and logs** is the released v2.1 portable-client
baseline. The final **unreleased 2.2.0/vNext release gates** section covers development targets.
Unchecked vNext items are requirements, not evidence that an installer, Linux
Agent, enrollment workflow, or Service Profile UI currently ships.

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

## Unreleased 2.2.0/vNext release gates — not part of v2.1.0

Do not publish an installer or describe Agent enrollment as stable until every
applicable item below has passed on disposable, clean Windows machines and a
test Linux node. Each Windows test device must use a unique identity.

### Current source inventory — not acceptance evidence

The development tree currently contains the following implementations and
automated tests. This list explains what can be exercised; it does **not** mark
any unchecked live gate below as passed:

- Windows Agent enrollment, authenticated self/catalog synchronization,
  administrator device listing/revocation, and administrator-generated
  10-minute one-use ordinary-device codes are wired to the Setup page.
- The administrator relay panel is wired for read-only plan/probe, preview,
  separate confirmation, apply, read-only delete-plan, and confirmed delete.
  The Agent coordinates `jkbridge_relay.py`, `socat`, per-user systemd units,
  catalog revisions, and exact device grants.
- A bounded read-only Agent discovery engine, controller integration, and Setup
  candidate/selection controls exist. A single result is only preselected;
  **Use selected address** remains explicit, and discovery never enrolls.
- Linux package assets and automated package checks exist. One deployed pair
  has completed install/bootstrap/start/restart/status plus explicit discovery,
  first-admin enrollment, catalog sync, exact grants, and SSH connection. This
  privacy-safe evidence is recorded in [FIELD_ACCEPTANCE.md](FIELD_ACCEPTANCE.md),
  but the installer and all vNext Windows flows still require the disposable
  clean-Windows matrix below.

### Clean Windows installer

- [ ] Build the Inno Setup skeleton on Windows with the documented build script; verify the produced installer and detached SHA-256 before execution.
- [ ] On a clean Windows 11 x64 user profile with no .NET runtime, install per-user without requiring administrator rights for JK Bridge files and launch the self-contained app.
- [ ] With both prerequisites present, the installer reports Tailscale and `ssh.exe`/`ssh-keygen.exe` detected and performs no elevation.
- [ ] With Tailscale absent, the installer offers only the official download page; it neither downloads an unpinned executable nor attempts Tailscale login.
- [ ] With OpenSSH Client absent, accepting the optional-feature action produces one clear UAC prompt and verifies both required executables afterward.
- [ ] Canceling or denying OpenSSH UAC leaves JK Bridge installed, reports the missing prerequisite, and does not loop, roll back unrelated files, or claim readiness.
- [ ] Silent install never opens an external URL, installs a Windows capability, or triggers an elevation prompt.
- [ ] Installer startup-task selection creates only the current-user Run entry; uninstall removes only installer-owned binaries, shortcuts, and that exact entry.
- [ ] Portable ZIP and installer can coexist for release testing without sharing a running instance or overwriting an unrelated directory.
- [ ] Code-signed installer, EXE, and uninstaller verify with an accepted timestamp before the installer is promoted from development artifact to stable Release asset.

### In-app Release update

- [ ] A stable build queries only the fixed latest stable endpoint; a Preview build queries the bounded official list, selects the highest published SemVer, and does not downgrade to an older stable release.
- [ ] The install action appears only when one Release contains the exact Windows installer and matching `.sha256` asset URLs under `Mathjk/jk-bridge`.
- [ ] Explicit download accepts only the exact GitHub asset URL and one HTTPS redirect to `release-assets.githubusercontent.com`, streams no more than 256 MiB, and launches only after SHA-256 matches.
- [ ] Mismatched hashes, malformed checksum names, foreign redirects, oversized content, cancellation, and timeout leave no staged EXE and never start a process.
- [ ] Overlay-install Preview 2 over Preview 1 while JK Bridge is running; confirm setup closes/replaces the app cleanly and preserves configuration, Credential Manager entries, SSH keys, startup preference, and Agent administrator identity.
- [ ] No update check, download, setup process, UAC prompt, or browser action occurs without the corresponding explicit user action; no silent installer flags are supplied.

### Simple route editor, templates, and port conflicts

- [ ] Simple mode clearly labels **Windows access port** and **Linux service port** and previews `Windows 127.0.0.1:<local> -> Linux 127.0.0.1:<remote>`.
- [ ] Protocol + Windows local port + launch path generates an exact `http(s)://127.0.0.1:<local>/...` URL and preserves query and fragment components.
- [ ] Overleaf, Sub2API, generic HTTP, and generic HTTPS templates materialize valid loopback routes with documented default ports and health modes.
- [ ] Changing only Windows local port, protocol, launch path, or local preference does not request or manufacture a new Linux grant.
- [ ] Adding, enabling, or changing a Linux remote port reports the exact authorization expansion and blocks until Agent authorization or one-shot re-pairing succeeds.
- [ ] An occupied Windows local port identifies the conflict and suggests a free port, but does not terminate the owner or silently change the saved URL.
- [ ] After the user accepts a suggested port, browser launch, health target, and SSH `-L` use that same local port while `permitopen` still names only the Linux remote port.
- [ ] Advanced custom URLs accept only HTTP(S), exact `127.0.0.1`, and the selected local port; wildcard, LAN, public, credential-bearing, and mismatched-port URLs fail validation.

### Non-secret Service Profile

- [ ] Native save/open pickers export and select `.json` profiles without requiring PowerShell, and canceling either picker leaves configuration and files unchanged except for an export the user explicitly completed.
- [ ] Export contains only the explicit service allow-list and never a private-key path/content, pairing token, Agent credential/reference, server identity, tailnet identity, or authorization receipt.
- [ ] Import rejects unknown fields, unsupported versions, oversized documents, duplicate service IDs/local ports, non-loopback routes, unsafe URLs, and invalid health paths before mutation.
- [ ] The legacy-import path accepts only an exact valid v2.1/schema-v3 `config.json`, identifies it separately from a Service Profile, and projects only Services; schema 1/2/4, mixed/unknown/duplicate/missing fields, server/tailnet/behavior data, key paths, Agent state, and authorization receipts never reach the candidate configuration.
- [ ] Import previews additions, changes, removals, local conflicts, new Linux ports that require authorization, and stale ports whose grants should be contracted before enabling the apply action.
- [ ] Applying requires a separate explicit confirmation after preview; canceling or declining changes nothing, while accepted import writes atomically with a recoverable backup and preserves all device/server-specific fields.
- [ ] If local services change after preview, apply fails closed and requires a fresh preview instead of applying a stale diff.
- [ ] Importing a profile never marks Agent enrollment or Linux authorization successful; a fresh device remains blocked until a verified Agent operation or one-shot pair returns its own receipt.

### Optional no-AI Linux Agent enrollment

- [ ] Package the existing versioned Linux Agent, per-user systemd unit, environment example, and operator guide; run their non-secret/least-privilege package check against the final delivery.
- [ ] Live-test the packaged non-root systemd install, bootstrap, start, restart, login persistence, status, upgrade, rollback, disable, and uninstall paths before describing the source assets as a stable workflow.
- [ ] The Agent runs without ChatGPT, Codex, a model API key, cloud inference, or any AI runtime and never sends runtime state to such a service.
- [ ] The management listener binds only the selected Linux Tailscale IPv4, never `0.0.0.0`, ordinary LAN, public interfaces, or Tailscale Funnel.
- [ ] A clean Windows computer with Tailscale signed in and OpenSSH available can choose **Add this computer**, validate a manually entered exact tailnet endpoint, create its own Ed25519 key, enroll, synchronize services, and connect without opening PowerShell or CMD.
- [ ] On clean Windows, the explicit discovery action checks only current online peers from local Tailscale status on port `45871`, never scans a range, validates stable node ID/MagicDNS/tailnet/protocol/Agent version, preselects only one unambiguous result without using it, requires a user choice for multiple results, requires **Use selected address** in every case, and never enrolls by discovery alone. No-result, timeout, cancellation, and manual-endpoint fallback remain actionable and non-mutating.
- [ ] Enrollment rejects an Agent endpoint outside `100.64.0.0/10`, redirects, proxies, unexpected peer identity, unknown protocol/catalog version, reused or expired enrollment secret, and any receipt/catalog port mismatch.
- [ ] Windows stores the device credential only through the OS credential store; logs, diagnostics, clipboard state after completion, and `config.json` contain no reusable secret.
- [ ] Two clean Windows devices enroll with different key fingerprints and independently listed device IDs while receiving only their exact manifest-derived `permitopen` ports.
- [ ] An enrolled administrator can manually synchronize the catalog, refresh the full device list, and see stable device IDs, roles, fingerprints, service selection, revocation state, and current-device marking.
- [ ] An enrolled administrator can explicitly generate a 10-minute, one-use code that enrolls only one ordinary device; the code is shown transiently with its expiry and is absent from configuration, logs, diagnostics, and persisted UI state.
- [ ] Leaving Setup, expiry, and regeneration hide or replace the previous device code; successful consumption makes every reuse fail even if the administrator window still displays its transient copy. Clipboard copy occurs only on explicit user action, warns that Windows will not auto-clear the clipboard, and never copies an administrator recovery code.
- [ ] A first administrator can be established only with Linux bootstrap/recovery, while an ordinary device cannot generate enrollment codes through either the UI or a direct API request.
- [ ] Device-list and revoke controls remain unavailable to an ordinary device; ordinary-device synchronization uses authenticated `GET /v1/me` plus `/v1/services` and never requires or leaks the administrator-only device list.
- [ ] Revocation requires selection plus an explicit confirmation, refuses Windows self-revocation, and cannot remove the final administrator through an alternate request path.
- [ ] Revoking the second device removes only its managed key/credential; its next management request and new SSH connection fail, while the first device's tunnel and key remain usable.
- [ ] The UI states that revocation cannot terminate an already established SSH session and provides an operator action for immediate session cutoff when required.
- [ ] Stopping or uninstalling the Agent disables enrollment/synchronization but does not remove existing exact OpenSSH grants, stop application services, or break an already authorized reconnect.

### Administrator-controlled Linux relays

- [ ] Final Linux delivery includes executable sibling `jkbridge_relay.py`, verifies `/usr/bin/socat`, and installs the Agent with write access only to its state, managed SSH entries, and the current user's systemd unit directory; no root service is introduced.
- [ ] A clean administrator Windows GUI can enter one service ID, Linux relay port, exact DNS-or-IPv4 target and TCP port, Windows local port, HTTP(S) choice, and launch path without using PowerShell/CMD.
- [ ] **Generate plan and probe** is read-only: it creates no unit, catalog change, grant, backup, or recovery journal, and previews the normalized three-hop mapping, dependency result, port occupancy, target reachability, catalog revision, added/removed ports, affected devices, and create/update/no-change/blocked action.
- [ ] Apply remains disabled until the current preview is applicable and the user separately confirms it. Editing any field or changing Linux/catalog/device state invalidates the plan; a stale or altered plan token fails closed.
- [ ] Create writes only an Agent-marked per-user `jkbridge-relay-<slug>.service`, binds `socat` only to Linux `127.0.0.1:<relayPort>`, enables/starts it, updates the catalog, and expands only the exact catalog-derived `permitopen` grants disclosed by the plan.
- [ ] Update backs up the prior managed unit and restarts it; injected unit, systemctl, catalog, grant, or persistence failures restore or converge safely without overwriting an unrelated file or leaving broader authorization than the committed catalog.
- [ ] Delete requires a separate read-only delete plan plus explicit confirmation, contracts catalog/device grants before removing only the matching Agent-managed unit, and refuses an absent, modified, conflicting, or unmanaged unit without deleting it.
- [ ] Target validation rejects URLs, credentials, wildcard/CIDR, IPv6, broadcast/multicast, self-loop, UDP, SOCKS/general proxy, Funnel, exit-node, and subnet-route forms; Linux relay and application listeners remain loopback-only.
- [ ] Windows `localPort` may differ from Linux `relayPort`; browser URL, health check, and SSH `-L` use the Windows port while Linux authorization names only `127.0.0.1:<relayPort>`.
- [ ] HTTPS acceptance covers certificate hostname, SNI, cookie-domain, redirect, and application-host behavior; a TCP-ready probe is not presented as proof that `https://127.0.0.1:<port>` is application-compatible.
- [ ] Perform real Linux create, update, reboot/login persistence, Agent restart, rollback, delete, and uninstall tests plus clean-Windows GUI tests before marking the relay workflow released.

### One-shot fallback and recovery

- [ ] With no Agent installed, a vNext client clearly offers `jkbridge-linux.py pair` and completes Pairing Protocol V1 without trying an Agent endpoint repeatedly.
- [ ] With the Agent stopped, incompatible, or unreachable, choosing one-shot fallback uses a fresh temporary code, preserves host-key pinning and per-device key isolation, and never creates a wildcard grant.
- [ ] Canceling fallback returns to an actionable setup screen and does not save partial server, credential, enrollment, or authorization state.
- [ ] A device enrolled through the Agent can recover through explicit one-shot pairing with the same dedicated public key while preserving exact routes and a verifiable authorization receipt.
- [ ] Linux `status` and `remove --fingerprint` remain functional and safe alongside Agent-managed entries, backups, and locks.
