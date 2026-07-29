# JK Bridge Product Specification

> **Status:** The V2.1 sections describe the released product. Sections marked
> **vNext** describe approved work that is still in development;
> they are not installation or API documentation for a shipping Agent.
> The latest stable release is v2.1.0. Current App/installer builds identify as
> `v2.2.0 development`, but v2.2.0 has not been released and that label does not
> make any vNext feature supported.

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

## vNext product direction — in development

The approved vNext goal is to make a second or replacement Windows device usable
without repeated terminal work after one-time Linux preparation:

```text
install and sign in to Tailscale
-> install JK Bridge
-> choose Add this computer
-> enter a short-lived Linux-issued enrollment code
-> synchronize services and connect
```

This requires an optional Linux Agent for durable device enrollment and service
synchronization. The Agent is an ordinary deterministic program and has no AI,
model API, Codex, or cloud-service dependency. Existing one-shot pairing remains
available for portable use, recovery, and installations that do not want a
persistent Agent.

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
- An explicit native-save diagnostic ZIP containing only anonymous runtime
  status and bounded redacted recent events; no config or raw snapshot dump.
- A Settings action for a manual, read-only check of the fixed official GitHub
  Release endpoint; no background check, download, or installation.
- Explicit error categories for Tailscale, pairing expiry, peer identity, SSH authentication, host-key mismatch, forwarding denial, port conflicts, and remote HTTP health.

## vNext in-development scope

### Simple and advanced service editing

- Simple mode asks for service name, Linux service port, Windows access port,
  HTTP/HTTPS, launch path, and enabled state.
- The Windows browser URL is generated as
  `http(s)://127.0.0.1:<Windows access port>/<launch path>` and clearly labeled
  as a Windows-local address.
- The UI previews the complete mapping, for example
  `Windows 127.0.0.1:8080 -> Linux 127.0.0.1:80`.
- Changing a Windows local port or launch path does not request a new Linux
  grant. Changing the Linux destination port does.
- Local port conflicts are detected and a free alternative is suggested, but a
  stable address is never silently changed.
- Advanced mode exposes health details, a validated custom launch URL, current
  authorization state, and an administrator-only explicit Agent-managed relay
  target workflow.
- Advanced mode does not provide wildcard binds, arbitrary port ranges, SOCKS,
  shell access, exit-node behavior, or subnet routing.
- Overleaf, Sub2API, generic HTTP, and generic HTTPS templates are preview-only
  until the user confirms **Apply to draft**, and the draft still requires a
  separate save. Simple health follows the browser path without query/fragment;
  advanced mode can preserve an independent absolute `healthPath`. Generic
  HTTPS defaults to TCP health for certificate-tolerant reachability.

### Service profile and manifest

- The source includes a native Service Profile export/import flow for non-secret
  service definitions without private keys, pairing tokens, device credentials,
  server identity, or authorization receipts.
- Export uses the Windows file picker. Import is read-only until it has shown
  additions, modifications, removals, and Linux-port authorization impact; a
  separate explicit confirmation is required before atomic application.
- Applying a profile preserves the device/server/Agent state and cannot grant a
  new Linux port. The user must subsequently run Agent synchronization or
  one-shot pairing when the preview identifies a new destination port.
- With an Agent, a versioned Linux service manifest is the source of truth for
  Linux loopback destinations and recommended Windows defaults.
- Each Windows device may keep a local-port override, enabled/favorite state,
  and launch preference without changing the Linux destination.
- Manifest changes are diffed before destructive application; conflicts and
  remote ports requiring authorization are explicit.
- The first template set includes Overleaf, Sub2API, generic HTTP, and generic
  HTTPS services.

### Optional Linux Agent and devices

- The source includes a Python Agent, relay companion, systemd user-unit, and a
  non-root `jkbridge-agentctl` with preflight/install/start/stop/status/fallback.
  It runs as the ordinary Linux SSH user and listens only on the detected Linux
  Tailscale IPv4. Live lifecycle acceptance remains pending, and no explicit
  upgrade/rollback/uninstall command exists yet.
- Each Windows computer generates and keeps its own Ed25519 private key.
- The Agent enrolls, labels, lists, renames, synchronizes, and independently revokes
  device public identities.
- Each device receives only command-disabled, source-restricted, exact
  loopback `permitopen` entries for its enabled manifest services.
- Agent API V1 enrollment uses an expiring, one-use Linux-issued code and binds
  each credential to the stable Tailscale node ID and normalized login returned
  by `tailscale whois`; source IP alone is insufficient identity.
- An enrolled device can explicitly synchronize its own catalog and exact grant
  without administrator list permission. This is a manual foreground action;
  background synchronization is not implemented.
- Explicit discovery checks only known online Tailscale peers on port `45871`,
  validates stable-node/MagicDNS/tailnet/protocol/version claims, and requires a
  separate **Use selected address** action before enrollment. A selected result
  never consumes a code or enrolls by itself.
- An existing registration can explicitly rebind only to a discovered endpoint
  for the same pinned stable node after a second confirmation and complete
  authenticated identity/grant/revision verification. Device ID, credential,
  SSH key, and pinned server identity remain unchanged.
- The Windows setup page provides an administrator-only sanitized device list
  and explicit-confirmation revoke action. It prevents self-revocation in the
  normal UI and reminds the user that an existing SSH session must be ended
  separately.
- Administrators can rename a device label without changing its role, key,
  Tailscale identity, or exact grant. `lastSeenAt` is shown as a throttled recent
  Agent record, not real-time presence.
- Administrators can preview additions, modifications, removals, exact remote
  ports, and affected devices, confirm separately, then submit the complete
  catalog with its expected revision. Managed-relay services cannot be changed
  through this generic write path.
- An administrator can generate a transient 10-minute, one-use enrollment code
  for one additional ordinary `device`, copy it to the new Windows installation,
  and complete independent-key enrollment without returning to Linux. It never
  creates another administrator; Linux-local recovery remains the admin path.
- Stopping the development Agent disables Agent management but does not remove
  unrelated SSH entries or break already running application services. A future
  supported uninstall must preserve the same boundary.
- The one-shot V1 helper remains a supported fallback.
- Development fallback stops only the Agent and retains state, helpers, and SSH
  entries. Same-key Agent-to-one-shot conversion requires one exact canonical
  restricted entry; port expansion still requires a fresh pair, an existing SSH
  session continues until Windows stops it, and a later Agent transaction can
  reconcile only that exact one-shot entry.
- The source includes an advanced administrator-only explicit relay flow. It
  accepts one exact DNS/IPv4 TCP target, reserves one Linux loopback port,
  generates and probes a read-only plan, reports exact grant/device effects,
  and requires a state-bound plan token plus explicit confirmation before
  create/update or delete.
- Relay application creates only a recognized `jkbridge-relay-<slug>.service`
  systemd user unit backed by `socat`, binds literal `127.0.0.1`, converges the
  service catalog and per-device exact grants, and uses backup/rollback paths.
  Arbitrary unit text, URI targets, IPv6, UDP, wildcards, CIDRs, general proxying,
  and public listeners remain out of scope.
- Strict adoption of one exact legacy Skill-created relay exists only as a
  relay-manager plan/confirm/apply primitive. It is not exposed by Agent API V1
  or the Windows UI and is not yet a normal migration feature.

### Configuration V4

- V3 configurations migrate without losing server identity, services, browser
  URLs, local ports, behavior, health settings, or observed authorization.
- The in-development V4 implementation adds non-secret Agent registration
  metadata: endpoint, pinned Linux Tailscale node ID, device ID/label, opaque
  credential-store reference, and catalog revision. It does not put the Agent
  credential in `config.json` and does not make Agent deployment supported by
  itself.
- Existing custom URLs remain custom unless they can be proven equivalent to a
  generated simple-mode URL.
- A synchronized catalog uses stable service IDs and server-owned Linux
  destinations, then materializes each device's Windows-local route and
  preferences in its V4 configuration.
- Migration never fabricates an Agent enrollment or granted port and never
  stores private-key contents or reusable enrollment secrets in `config.json`.
- V1/V2 profiles retain the existing safe requirement to establish a known
  authorization before SSH starts.

## Non-goals

- Installing or logging into Tailscale automatically.
- Installing OS packages, changing package repositories, creating Linux users, or rewriting `sshd_config` with elevation.
- Capturing Linux passwords or importing personal SSH private keys.
- Public exposure through Tailscale Funnel, LAN binds, or internet reverse proxying.
- Starting or stopping the Linux web applications themselves.
- SFTP/FileZilla management, multi-server synchronization, cloud accounts, or an extension marketplace.
- Automatic software updates or signed installer delivery in V2.

The following remain non-goals for vNext:

- Depending on AI for pairing, synchronization, tunneling, health, or recovery.
- Sharing or synchronizing one Windows SSH private key across devices.
- Treating Tailscale membership or a `100.x` source address as sufficient
  authorization for a management operation.
- Public Agent exposure, Tailscale Funnel, LAN listeners, wildcard
  `permitopen`, arbitrary website proxying, UDP, or whole-subnet access.
- Silently logging into Tailscale or storing a Tailscale password.

## Page tree

```text
JK Bridge
├── Device setup
│   ├── Agent discovery/manual address and explicit enrollment
│   ├── Same-node endpoint rebind and Agent device/catalog controls
│   ├── Linux one-shot helper, pairing code, and dedicated identity path
│   └── Pairing/enrollment progress and security summary
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
│   ├── Sanitized activity
│   └── Explicit safe diagnostic ZIP export
└── Settings
    ├── Paired Linux profile and re-pair action
    ├── Startup/reconnect/tray behavior
    ├── Appearance
    ├── Paths and version
    └── Manual official Release check
```

The vNext source adds a simple/advanced service editor and explicit Agent
enrollment to Device setup. It also includes manual Agent catalog sync,
administrator device list/revoke and ordinary-device code controls, managed
explicit-relay plan/probe/confirm/delete, and native Service Profile
export/import with preview and confirmation. Explicit Agent discovery,
same-stable-node endpoint rebind, direct catalog preview/confirm/write, device
rename, diagnostic export, and manual Release checking are also wired in source.
Background sync, old-device approval, supported Linux lifecycle, explicit
upgrade/uninstall, and clean Windows 11 acceptance remain pending.

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

V4 adds `AgentRegistration` for a non-secret endpoint, pinned Linux Tailscale
node ID, device identity, Windows credential-store reference, and catalog
revision. Agent API V1 adds catalog service definitions and device summaries
with stable Tailscale node/login fields. The Linux server and Windows enrollment
client exist. The Windows source also contains explicit manual sync,
administrator catalog write/list/rename/revoke and ordinary-device code
generation, discovery/rebind, managed relay planning/application, diagnostic
export/manual Release check, and native non-secret Service Profile transfer;
supported installation and complete clean-machine interoperability acceptance
remain pending. Relay plan tokens and enrollment codes are transient secrets, never
catalog, manifest, config, or Service Profile objects.

## Risks and chosen controls

- A passwordless client key enables unattended reconnect. Its permissions are constrained on Windows and its Linux authorization cannot execute commands or reach unlisted targets.
- HTTP pairing has no TLS certificate. It runs only inside Tailscale's encrypted network, requires a high-entropy short-lived token, binds only to the Tailscale interface, and immediately pins SSH for all persistent transport.
- A tailnet peer with a stolen pairing code could race the intended client. The code is treated as a temporary secret, expires quickly, and the Linux console shows the paired client fingerprint.
- Adding a new remote service requires re-pairing when its port is not already authorized. This is intentional least privilege.
- Legacy configurations cannot prove their historic Linux whitelist, so V3 requires one explicit re-pair instead of guessing.
- A self-signed HTTPS certificate may fail strict HTTP health. TCP mode checks only forward reachability, and browser launch remains available so the user can make the certificate decision there.
- One SSH process means forwarding changes briefly reconnect all services; this avoids ControlMaster complexity and hidden sessions.

The development Agent adds these risks and boundaries:

- A persistent management listener increases attack surface. It is optional,
  Tailscale-only, non-root, rate/size/concurrency limited, and cannot execute
  arbitrary commands or broaden a service beyond the manifest.
- A compromised device credential affects one device. Per-device identities
  and independent server-side revocation prevent that credential from becoming
  a shared fleet identity.
- A malicious or stale manifest could expand destination ports. Manifest
  validation, revision checks, explicit diffs, exact `permitopen`, atomic
  writes, and backups make expansion visible and recoverable.
- Agent unavailability must degrade management rather than tunneling: existing
  SSH grants continue, while enrollment and synchronization use a clear fallback
  or wait state.
- Endpoint drift cannot become server substitution: rebind requires the same
  pinned stable node plus full authenticated identity/grant verification and
  explicit confirmation.
- A relay can become an SSRF or privilege-expansion mechanism if its target or
  grant is broad. The implementation accepts one normalized DNS/IPv4 host and
  port, uses a generated loopback-only unit, requires administrator role plus a
  fresh state-bound plan and confirmation, and refuses unmanaged-unit overwrite.
- Transparent HTTPS relays do not rewrite certificates, SNI, redirects, cookies,
  or origins. The UI warns that TCP reachability does not prove the target web
  application will work through a `127.0.0.1` browser URL.

## Release requirements

- Repository contains no personal machine identity, runtime data, credentials, pairing tokens, or real public keys.
- Public-release scanner, .NET tests, Python tests, and Release build pass.
- Windows package contains the EXE, Linux helper, bilingual README/installation/troubleshooting guides, fallback diagnostics, contributor and security documents, README images, and checksums.
- Windows-specific acceptance is completed before marking a GitHub Release stable.

Before any vNext Agent feature is described as stable, the release additionally
requires V3-to-V4 migration and rollback tests, multiple-device isolation and
revocation tests, Agent-unavailable fallback tests, manifest/relay conflict and
rollback tests, and proof that neither Windows nor Linux binds a managed service
or relay to a wildcard address. Real systemd-user, `socat`, DNS/TLS, clean
Windows/Linux and update/uninstall acceptance remain separate release gates
from the implemented protocol and UI.
