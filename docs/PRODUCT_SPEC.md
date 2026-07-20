# JK Bridge Product Specification

## Product statement

JK Bridge is a quiet Windows 11 desktop utility that pairs with one Linux workstation over Tailscale and opens selected Linux localhost web services through one supervised SSH tunnel, without requiring the user to maintain IP addresses, SSH commands, or authorization files.

## Primary user and job

The product is for a person who owns or administers both devices, already uses Tailscale, and wants private access to Overleaf, monitoring dashboards, or similar Linux loopback applications from Windows.

The key user moment is:

> Run one Linux command, paste one code into Windows, select one button, then open every authorized service from a stable local address—even after a Tailscale IP change.

## Complete core loop

```text
Linux one-shot helper -> Windows one-click pairing -> restricted SSH authorization
-> pinned server identity -> supervised forwards -> per-service health -> browser
```

After pairing, normal use is `start app -> automatic preflight -> connect -> open service`. A Windows reboot needs no credential entry.

## V2.1 scope

### Automated setup

- An unconfigured app opens directly to Device setup.
- Linux runs a standalone Python 3 helper as the intended non-root SSH user.
- The helper detects Tailscale identity and reads the local Ed25519 SSH host public key.
- It exposes a random, 10-minute, one-success pairing endpoint only on the Tailscale IPv4 interface.
- Windows validates the pairing code, Tailscale state, and required OpenSSH tools.
- Windows generates or safely reuses a dedicated Ed25519 key and applies an exact current-user/SYSTEM DACL.
- Linux atomically installs or updates one managed, command-disabled, Tailscale-source, exact-`permitopen` authorization.
- Windows pins the returned host public key and persists the MagicDNS identity, never the pairing token or Tailscale IP.
- Schema V1/V2 configurations preserve their device and service settings, then require one re-pair to establish a trustworthy V3 authorization record.

### Connection and services

- Resolve the configured Linux peer by exact MagicDNS identity.
- Use current Tailscale IP only as a runtime fallback; never persist it.
- Run one hidden, public-key-only `ssh.exe` process containing all enabled `-L` forwards.
- Bind every Windows listener and Linux destination to `127.0.0.1`.
- Reconnect transient failures with bounded backoff, but stop for host-key, authentication, invalid-config, or port-conflict errors.
- Ship editable Overleaf, GPU Dashboard, and Investment News examples.
- Add, edit, enable, disable, and remove future HTTP/HTTPS loopback services.
- Derive SSH forwarding, browser opening, health probing, and Linux authorization from one validated loopback route definition.
- Record the paired client fingerprint and exact Linux destination ports; block before SSH when an enabled route lacks a known grant.
- Probe services independently with TCP connect, HTTP status, or JSON `ok=true`; one failed app does not restart a healthy SSH transport.
- Keep health results advisory for browser launch once the SSH forward exists; forwarding denial remains blocking.

### Windows experience

- Device setup, Overview, Services, Diagnostics, and Settings pages.
- Four-stage pairing progress: network, identity key, Linux authorization, secure connection.
- Native tray menu, close-to-tray, single instance, optional current-user startup, and sleep/resume handling.
- Chinese-first UI with readable technical diagnostics.
- Health-mode selection in the service editor, including TCP mode for self-signed HTTPS.
- System, light, and dark appearance modes.

### Persistence and diagnostics

- Atomic JSON configuration writes and last-valid backup recovery.
- Dedicated app-owned `known_hosts` derived only from the pinned configuration.
- Rolling redacted logs and a copyable diagnostic summary.
- Explicit error categories for Tailscale, pairing expiry, peer identity, SSH authentication, host-key mismatch, forwarding denial, port conflicts, and remote HTTP health.

## Non-goals

- Installing or logging into Tailscale automatically.
- Installing OS packages, changing package repositories, creating Linux users, or rewriting `sshd_config` with elevation.
- Capturing Linux passwords or importing personal SSH private keys.
- Public exposure through Tailscale Funnel, LAN binds, or internet reverse proxying.
- Starting or stopping the Linux web applications themselves.
- SFTP/FileZilla management, multi-server synchronization, cloud accounts, or an extension marketplace.
- Automatic software updates or signed installer delivery in V2.

## Page tree

```text
JK Bridge
├── Device setup
│   ├── Linux helper command
│   ├── One-time pairing code
│   ├── Dedicated identity path
│   └── Pairing progress and security summary
├── Overview
│   ├── Dependency rail
│   ├── Current issue/action
│   ├── Service cards
│   └── Recent activity
├── Services
│   ├── Service list
│   └── Add/edit panel
├── Diagnostics
│   ├── Tailscale and peer identity
│   ├── SSH trust and process
│   ├── Local forwards and health
│   └── Sanitized activity
└── Settings
    ├── Paired Linux profile and re-pair action
    ├── Startup/reconnect/tray behavior
    ├── Appearance
    └── Paths and version
```

## Product state

```text
Unconfigured -> Pairing -> Configured -> Authorization check -> Preflight -> Starting -> Connected
                    |                |              |                              |
                    +-> Safe failure +-> Re-pair    +-> Needs action               +-> Degraded
Connected -> Reconnecting -> Preflight
Any running state -> Stopping -> Stopped
```

## Data objects

- `AppConfig`: schema version, setup completion, expected tailnet, server, behavior, authorization receipt, services.
- `ServerProfile`: display name, MagicDNS host, SSH user/port, identity path, pinned Ed25519 host key.
- `ServiceDefinition`: name, local port, Linux loopback port, browser URL, health mode, enabled state.
- `ServiceRoute`: the single validated SSH forward, browser URI, health URI, and authorization target derived from a service.
- `ForwardAuthorizationGrant`: whether the receipt is known, paired client fingerprint, and exact granted Linux ports.
- `PairingCode`: Tailscale IPv4 endpoint, ephemeral port, version, 256-bit token.
- `PairingRequest/Response`: protocol version, client public key, exact ports, server identity and host public key.
- `TunnelSnapshot`, `ServiceSnapshot`, `DiagnosticEvent`: runtime and sanitized presentation state.

## Risks and chosen controls

- A passwordless client key enables unattended reconnect. Its permissions are constrained on Windows and its Linux authorization cannot execute commands or reach unlisted targets.
- HTTP pairing has no TLS certificate. It runs only inside Tailscale's encrypted network, requires a high-entropy short-lived token, binds only to the Tailscale interface, and immediately pins SSH for all persistent transport.
- A tailnet peer with a stolen pairing code could race the intended client. The code is treated as a temporary secret, expires quickly, and the Linux console shows the paired client fingerprint.
- Adding a new remote service requires re-pairing when its port is not already authorized. This is intentional least privilege.
- Legacy configurations cannot prove their historic Linux whitelist, so V3 requires one explicit re-pair instead of guessing.
- A self-signed HTTPS certificate may fail strict HTTP health. TCP mode checks only forward reachability, and browser launch remains available so the user can make the certificate decision there.
- One SSH process means forwarding changes briefly reconnect all services; this avoids ControlMaster complexity and hidden sessions.

## Release requirements

- Repository contains no personal machine identity, runtime data, credentials, pairing tokens, or real public keys.
- Public-release scanner, .NET tests, Python tests, and Release build pass.
- Windows package contains the EXE, Linux helper, bilingual README/installation/troubleshooting guides, fallback diagnostics, contributor and security documents, README images, and checksums.
- Windows-specific acceptance is completed before marking a GitHub Release stable.
