# JK Bridge Architecture

> **Release boundary:** JK Bridge v2.2.0 is the latest stable desktop release
> and uses configuration schema V6. The current development branch adds schema
> V7 update-network preferences and verified one-line quick pairing. The one-shot
> Linux helper remains the recommended setup/recovery path. Stable desktop scope includes Windows-local
> discovery, service editing and profiles, diagnostic export, the installer,
> manual updater, appearance persistence, categorized navigation, and the
> first-run tutorial. The package also contains an optional persistent Linux
> Agent/API, bounded discovery/rebind, Windows enrollment, catalog and device
> management, administrator enrollment codes, managed loopback relays, and
> multi-device authorization. That optional Agent/relay lifecycle remains
> advanced/experimental until its installation, upgrade, rollback, uninstall,
> and clean-machine acceptance gates complete.

## Decision summary

| Area | Decision |
|---|---|
| Windows UI | Avalonia 12.1, .NET 10, self-contained `win-x64` EXE |
| Linux setup | Standalone Python 3 one-shot helper; no daemon or package installation |
| Network | Existing Tailscale clients; exact MagicDNS node identity |
| Pairing | Versioned HTTP request on the Linux Tailscale IPv4 interface with a short-lived 256-bit token |
| Persistent transport | One supervised Windows OpenSSH process with all enabled local forwards |
| Client auth | Dedicated Ed25519 key; no password and no private-key transfer |
| Server identity | Ed25519 host public key obtained locally during pairing, then strict app-owned pinning |
| Authorization | One managed `authorized_keys` line with no commands and exact loopback `permitopen` entries |
| Route integrity | One validated route object derives SSH, browser, health, and authorization endpoints |
| Authorization state | Schema V3 stores the paired client fingerprint and exact granted remote-port set |
| Persistence | Atomic JSON config, backup recovery, rotating redacted logs |
| Configuration | Schema V7 migrates V1–V6 profiles, persists updater network mode, Windows-local services, and tutorial completion |
| Tests | Unit/integration tests for config, protocol, runtime, view model, Linux file mutation, and packaging |
| Optional Agent configuration | Schema V4 introduced non-secret Agent registration/catalog state while preserving V3 routes and receipts |
| vNext Linux management | Development non-root Agent/API over Tailscale plus `jkbridge-agentctl` install/upgrade/rollback/uninstall/start/stop/status/fallback source; one deployed install/bootstrap/start/restart/status and first-admin enrollment completed, but full clean-machine lifecycle acceptance is still open; V1 helper remains fallback |
| vNext device identity | One dedicated Ed25519 identity and independently revocable authorization per Windows device |
| Stable service migration | Native JSON export/import UI; bounded non-secret profile, read-only diff, explicit confirmation, atomic save |
| vNext explicit relay | One exact DNS/IPv4 TCP target behind one Linux `127.0.0.1` `socat` user unit; read-only plan/probe before confirmed apply/delete |

Avalonia enables Linux-hosted development and Windows cross-publishing while retaining a native tray and child-process supervision. In stable v2.2.0 the recommended Linux side remains a short script because its only persistent output is standard OpenSSH authorization. The optional vNext Agent adds a management plane; normal service traffic continues to use the same restricted OpenSSH data plane.

## Repository structure

```text
src/JKBridge.Core/             config, pairing, Agent client, routes, authorization, Tailscale, SSH, health
src/JKBridge.App/              Avalonia UI and Windows platform adapters
scripts/jkbridge-linux.py      one-shot Linux pairing and managed-key removal
scripts/jkbridge_agent.py      development persistent Linux Agent/API and server-state CLI
scripts/jkbridge_relay.py      validated loopback relay plan/probe/apply/rollback companion
packaging/linux/               user unit/env, non-root lifecycle controller, packaging guide
scripts/setup-windows.ps1      advanced V1/manual recovery path
scripts/verify-windows.ps1     Windows environment and runtime verification
scripts/check-public-release.sh privacy/credential gate
scripts/publish-win-x64.sh     tested release packaging
tests/                         .NET and Linux tests
docs/                          install, troubleshooting, development, architecture, security, protocol, ADRs
```

The fixed development Agent API V1 contract is documented separately in
[AGENT_PROTOCOL.md](AGENT_PROTOCOL.md). The server, Windows client, and endpoint
paths exist in source, but the document is not evidence of a supported Agent
installation or stable Release asset.
[AGENT_THREAT_MODEL.md](AGENT_THREAT_MODEL.md) defines the release security
gates for the development server and privileged management operations.

`JKBridge.Core` has no Avalonia dependency. External processes are invoked through argument lists, never shell interpolation. UI and platform adapters own only presentation and OS integration.

## Configuration lifecycle

Runtime data defaults to:

```text
%LOCALAPPDATA%\JKBridge\config.json
%LOCALAPPDATA%\JKBridge\config.json.bak
%LOCALAPPDATA%\JKBridge\logs\
%LOCALAPPDATA%\JKBridge\ssh\known_hosts
%USERPROFILE%\.ssh\jk_bridge_ed25519
```

The original V3 configuration shape was intentionally unconfigured:

```json
{
  "schemaVersion": 3,
  "setupCompleted": false,
  "expectedTailnetDnsSuffix": "",
  "server": {
    "displayName": "Linux device",
    "host": "",
    "user": "",
    "port": 22,
    "identityFile": "%USERPROFILE%\\.ssh\\jk_bridge_ed25519",
    "hostKeyAlias": "jk-bridge-linux",
    "pinnedHostKey": ""
  },
  "authorization": {
    "isKnown": false,
    "clientKeyFingerprint": "",
    "remotePorts": []
  }
}
```

An unconfigured app cannot enter tunnel preflight and routes to Device setup. Stable v2.2.0 loads schema V1–V5 profiles and migrates them in memory to V6 while preserving their device and service settings. Because V1/V2 did not record what Linux actually authorized, a configured profile from either version must re-pair once before SSH starts. That pairing establishes the authorization baseline. Writes use a same-directory temporary file, flush, last-valid backup, and atomic replacement.

### Schema V4–V6 migration — released in v2.2.0

Schema V4 introduced an `agent` registration object containing `isConfigured`, the
canonical endpoint, pinned Linux Tailscale stable node ID, device ID/label, an
opaque Windows Credential Manager reference, and catalog revision. The Agent
credential itself is not in JSON. Schema V5 adds persisted Windows-local service
definitions. Schema V6 adds the non-secret tutorial-completion behavior flag.
Loading any V1–V5 profile supplies safe defaults for later fields, bumps the
schema, and otherwise preserves server, services, behavior, and authorization.

### Schema V7 migration — current development branch

Schema V7 adds an isolated `updateNetwork` object. Existing V1–V6 profiles
default to Windows system-proxy mode. Manual mode stores only a loopback HTTP
proxy URL with an explicit port and no credentials or path; direct mode stores
no proxy value. The preference is applied only to future update checks and
downloads and does not change tunnel, pairing, discovery, or health traffic.

The migration and Agent catalog materialization preserve these boundaries:

- Preserve every valid V3 server identity, service, behavior setting, local
  port, browser URL, health definition, and authorization receipt.
- Treat a server catalog entry as the source of the Linux destination and its
  suggested local defaults. The materialized Windows `ServiceDefinition` keeps
  this device's local port and launch settings; a local override does not change
  Linux authorization.
- Use stable service identities and a catalog revision so an
  import or Agent sync can show additions, edits, removals, and conflicts
  before applying them.
- Preserve an existing V3 `openUrl` exactly. Migration may classify it as a
  generated simple-mode URL only when it is provably equivalent; otherwise it
  remains an explicit advanced-mode value.
- Keep the authorization receipt as observed server state. Migration must not
  manufacture Agent enrollment, a client fingerprint, or a granted port.
- Keep private-key contents, one-time pairing tokens, Agent enrollment secrets,
  and reusable device credentials out of `config.json`; store only the opaque
  credential reference in V4.
- Continue atomic writes and last-valid backup recovery. A failed V3-to-V4
  validation leaves the V3 file and backup recoverable rather than partially
  upgrading them.

Loading a V1 or V2 profile still follows the existing V3 safety rule first:
the service and server settings may migrate, but the unknown Linux whitelist
requires one successful authorization operation before SSH starts. V4 does not
weaken that rule.

For every service, `ServiceRouteDefinition` validates one exact IPv4-loopback route and derives four consumers from it:

```text
ServiceDefinition
  -> ssh -L 127.0.0.1:<local>:127.0.0.1:<remote>
  -> browser HTTP(S) URL on 127.0.0.1:<local>
  -> HTTP health URL or TCP probe on 127.0.0.1:<local>
  -> pairing/authorization port 127.0.0.1:<remote>
```

No consumer independently reconstructs or broadens the destination.

### Simple and advanced route experience — implemented source

The route model remains exact, while the vNext editor reduces the amount of
network terminology shown by default.

Simple mode asks for a service name, Linux service port, Windows access port,
HTTP/HTTPS scheme, and launch path. It derives and previews:

```text
http(s)://127.0.0.1:<Windows local port>/<path>
        -> SSH over Tailscale
        -> 127.0.0.1:<Linux remote port>
```

The browser always uses the Windows local port. Changing only that port, the
scheme, or the launch path does not expand Linux authorization. Adding or
changing a Linux remote port requires a new exact grant through the Agent or
the one-shot pairing fallback.

Advanced mode exposes health details and an explicit validated launch URL. For
an enrolled administrator it also exposes the managed explicit-relay workflow
described below. It does not expose wildcard listeners, arbitrary `permitopen`,
SOCKS, shell access, an exit node, or a subnet route. A Windows port collision
is reported with a suggested free port; the application does not silently
change a stable local URL.

The editor includes Overleaf, Sub2API, generic HTTP, and generic HTTPS templates.
Selecting one is preview-only; a separate confirmation and **Apply to draft**
action are required, and the user must still save the draft. In simple mode the
health path follows the browser path after dropping query and fragment. Advanced
mode can preserve an independently validated absolute `healthPath`, while the
generic HTTPS template defaults to TCP health so self-signed certificate policy
does not masquerade as tunnel failure.

## Pairing flow

```text
Linux user                            Windows app
-----------                           -----------
tailscale status --json
read local SSH host .pub
generate 256-bit token
bind Tailscale IPv4:ephemeral
print jkbridge:// code  ------------> parse/validate code
                                      check Tailscale + ssh-keygen
                                      generate/reuse Ed25519 identity
                                      enforce private-key DACL
                        <------------ POST /v1/pair + bearer token
validate Tailscale source
validate token, key, exact ports
lock + backup authorized_keys
atomic managed-line update
return MagicDNS/user/host key -------> validate response against endpoint/request
stop one-shot server                  atomically save config
                                      save fingerprint + exact granted ports
                                      build known_hosts from pinned key
                                      start strict SSH tunnel
```

The bearer token is placed only in the `Authorization` header. The pairing client disables proxies and redirects, limits request/response size, and verifies that the returned Tailscale IPv4 equals the code endpoint and that returned authorized ports exactly match the request.

The Linux helper rejects root execution, non-Tailscale sources, symlinked or foreign-owned SSH paths, malformed keys, duplicate unmanaged copies, and more than one matching entry. A managed re-pair is idempotent. An exact restricted entry emitted by the V1 helper may be migrated to V2; a marker alone is insufficient, because the complete command/source/loopback restriction shape is validated before replacement.

See [PAIRING_PROTOCOL.md](PAIRING_PROTOCOL.md) for the wire contract.

## Tailscale preflight

1. Run `tailscale status --json` with a short timeout.
2. Require `BackendState == Running`.
3. Compare the configured tailnet suffix.
4. Match one exact normalized MagicDNS FQDN.
5. Distinguish missing and offline peers.
6. Prefer MagicDNS for SSH.
7. If DNS resolution fails but the exact online peer exposes one CGNAT IPv4, use it only for the current process.

The expected tailnet is a diagnostic guard. The pinned SSH host key remains the persistent server-identity boundary.

## SSH process

The command model is equivalent to:

```text
ssh -n -N -T
  -i <dedicated-identity> -p <port>
  -o IdentitiesOnly=yes -o BatchMode=yes
  -o PasswordAuthentication=no -o KbdInteractiveAuthentication=no
  -o HostKeyAlias=jk-bridge-linux
  -o UserKnownHostsFile=<app-data>\ssh\known_hosts
  -o StrictHostKeyChecking=yes -o CheckHostIP=no -o UpdateHostKeys=no
  -o ExitOnForwardFailure=yes
  -o ServerAliveInterval=15 -o ServerAliveCountMax=3
  -L 127.0.0.1:<local>:127.0.0.1:<remote>
  <user>@<resolved-target>
```

The dedicated `known_hosts` file uses the alias and is regenerated only from the already pinned config value, never from network discovery. A host-key mismatch is blocking.

On Windows the process is assigned to a Job Object with kill-on-close. A named mutex enforces a single app instance and a named pipe asks the first instance to show its window. Suspend stops the tunnel; resume re-enters preflight after a short settle period.

## Runtime state and health

Only the controller mutates runtime state. Operations are serialized so connect, stop, config changes, process exit, and resume cannot race. Before Tailscale inspection or SSH startup, the runtime compares enabled route ports with the V3 authorization receipt. Missing or unknown grants enter `NeedsPairing`; unused extra grants are non-blocking but recommend re-pairing to restore least privilege.

```text
Stopped -> Preflight -> Starting -> Connected -> Degraded
             |                         |
             +-> NeedsAction           +-> Reconnecting -> Preflight
             +-> NeedsPairing
Any active state -> Stopping -> Stopped
```

Transient retry uses bounded exponential backoff. Authentication, host-key, invalid configuration, and local bind failures do not retry forever.

`ExitOnForwardFailure` proves listener creation, not application availability. Every enabled service therefore has an independent short-timeout health mode: strict HTTP status, JSON `ok=true`, or TCP connect. TCP mode is appropriate for self-signed HTTPS when certificate trust should remain a browser decision. A failed health check changes the service and aggregate status but does not invalidate a proven SSH listener or block the browser-open action. A forwarding denial remains blocking.

## Authorization line

For enabled remote ports `<p1>...<pn>`, Linux writes the equivalent of:

```text
restrict,port-forwarding,command="/usr/bin/false",from="100.64.0.0/10,fd7a:115c:a1e0::/48",permitopen="127.0.0.1:<p1>",permitopen="127.0.0.1:<pn>" ssh-ed25519 <PUBLIC_KEY> JK Bridge managed SHA256:<FINGERPRINT>
```

The key cannot run commands or obtain a shell. Only exact IPv4 loopback targets requested for currently enabled services are authorized. `authorized_keys` is locked, backed up, written with mode `0600`, fsynced, and atomically replaced.

After a successful pair, Windows stores only the public client fingerprint and the exact returned port set. It never assumes that editing JSON also changed Linux. Adding or changing a remote port therefore produces an explicit re-pair state before a new SSH process is attempted.

## Optional Linux Agent — implemented source, unsupported deployment

The vNext Agent is an optional, deterministic Linux program. It has no AI,
model, cloud API, or Codex dependency. The `jkbridge` Skill remains a separate
operator/developer aid for planning unusual routes, diagnosis, and audited
recovery; it is not part of the runtime dependency chain.

`scripts/jkbridge_agent.py`, its sibling `scripts/jkbridge_relay.py`, a hardened
systemd user-unit template, and `packaging/linux/jkbridge-agentctl` exist.
When manually deployed, the Agent runs as the ordinary SSH user, listens only
on that Linux node's detected Tailscale IPv4, and owns a small management plane:

- maintain a versioned Linux service manifest;
- enroll, label, list, rename, and revoke independent Windows device identities;
- let an enrolled administrator issue an expiring, one-use ordinary-device
  enrollment code without returning to the Linux terminal;
- derive each device's exact `permitopen` set from enabled manifest services;
- plan, probe, create/update, and delete one explicitly named loopback relay and
  converge its catalog entry plus affected exact grants;
- serialize Agent and one-shot writers through the same
  `~/.ssh/.jkbridge.lock`, then reuse locked, backed-up, atomic
  `authorized_keys` mutation;
- report sanitized health/bootstrap state without starting or stopping
  application services.
- inspect only current exact Linux `127.0.0.1` listeners through a bounded,
  administrator-only read and return disabled-draft suggestions without
  mutating the catalog or grants.

The non-root controller implements prerequisite preflight, one-confirmation
install, manifest-verified upgrade, controller-snapshot rollback,
state-preserving uninstall, start, stop, status, and fallback. Install copies
all three Python helpers, environment, and user unit, bootstraps private state
only when absent, verifies that `authorized_keys` did not change, and enables
the user service. Upgrade preserves private state, credentials, SSH grants, and
a custom port. Uninstall removes managed runtime files while preserving state
and grants. It never invokes `sudo` or installs OS packages. These paths have
not passed full live clean-machine acceptance. The relay source requires the companion file beside
`jkbridge_agent.py`, executable
`/usr/bin/socat`, a working systemd user instance, and write access to the
reviewed per-user unit directory. One deployed pair has exercised
install/bootstrap/start/restart/status and first-admin enrollment; see the
[field acceptance record](FIELD_ACCEPTANCE.md). That evidence does not close
the remaining clean-machine upgrade, rollback, or uninstall acceptance gates.

Every Windows device generates its own Ed25519 key. The Agent stores the public
key, fingerprint, device metadata, and authorization state, never the private
key. Revoking one device removes only its managed authorization. Sharing a
private key between Windows devices is not a supported migration mechanism.
Authenticated read operations refresh `lastSeenAt` at most once every five
minutes using a best-effort atomic metadata write. The UI therefore labels it
as a recent Agent record, not real-time online presence.

The Linux manifest is the source of truth for Linux destinations and recommended
Windows defaults. A Windows device stores only local choices such as an
overridden local port, enabled/favorite state, and launch preference. A local
port override cannot cause the Agent to broaden a Linux grant.

The Windows setup page now exposes an explicit, foreground synchronization
operation. It re-verifies the Agent endpoint's stable Tailscale node and pinned
SSH/server identity, loads the per-device credential from Windows Credential
Manager, fetches the current device grant and catalog at one revision, and only
then atomically replaces the local service list and observed exact-port receipt.
It preserves an existing Windows local port for a service with the same stable
ID. Synchronization is not a background watcher: catalog changes do not reach a
Windows device until its user requests synchronization.

An enrolled administrator can also preview the complete local-to-Linux service
catalog diff, including added/modified/removed services, exact remote ports, and
affected device IDs. A separate confirmation sends `PUT /v1/services` with the
observed revision; the Agent rejects stale state, empty grants, and attempts to
bypass managed-relay transactions. The controller then reads the committed
state before replacing local services.

An administrator can additionally refresh a sanitized device list and revoke a
selected non-current device from the setup page. Revocation requires an
explicit confirmation and does not terminate an already authenticated SSH
session. Administrators can rename a selected device label without changing its
role, key, Tailscale identity, or grant. The same administrator can generate a
600-second, one-use code that
can enroll only a normal `device`; the code is shown transiently, is not saved
to config/logs, and must be cleared or overwritten from the clipboard after
transfer. Approval of an existing unknown/old device is not implemented.

The Setup page's explicit discovery probes only known online Tailscale peers at
port `45871`, validates their public Agent identity against the local peer list,
and requires **Use selected address** before enrollment. For an existing
registration, a second explicit confirmation may rebind a changed endpoint only
when the discovered stable node is the already pinned node and authenticated
`/v1/me`, server identity, key/grant, and catalog revision all still agree. It
changes only endpoint/revision metadata, never the device ID, credential, SSH
key, or pinned host key.

### Managed explicit relay transaction — implemented source

The advanced setup path models one exact route as:

```text
Windows 127.0.0.1:<local>
  -> restricted SSH permitopen 127.0.0.1:<relay>
  -> Linux systemd user unit / socat TCP relay on 127.0.0.1:<relay>
  -> one normalized DNS name or canonical IPv4:<target>
```

The administrator supplies a stable service slug, Linux relay port, exact
target host/port, HTTP(S) presentation, launch path, Windows suggested port,
health policy, and whether to grant the new service to all active devices. The
Agent accepts neither a URL as the target, IPv6, wildcard/CIDR, UDP, arbitrary
unit text, nor a caller-selected unit path. Unit identity is derived as
`jkbridge-relay-<slug>.service`, and the listener is rendered as literal
`TCP4-LISTEN:<relay>,bind=127.0.0.1`.

Planning and probing are read-only. The plan reports create/update/no-change or
blocked status, the normalized mapping, observed managed-unit digest, next
catalog revision, affected devices, and exact added/removed ports. The probe
checks `socat`, Linux relay-port occupancy, and TCP reachability of the exact
target. Windows enables apply only after both are acceptable and the user has
reviewed the summary and checked explicit confirmation.

Apply repeats administrator/node/server checks and recomputes a 64-hex plan
token bound to the current managed unit, manifest revision, service request,
device grants, and grant policy. Stale state fails closed. A create/update uses
a recognized, generated user unit only; it refuses to overwrite an unmanaged
unit, writes atomically, backs up updates, and rolls the unit back if the
catalog/authorization commit fails outside a pending recovery transaction.
Deletion has its own read-only plan and confirmation token. It contracts the
catalog and every affected exact SSH grant before disabling, backing up, and
removing only the recognized unit; a unit failure attempts to restore the
previous catalog/grants. After a successful receipt Windows reads the committed
catalog revision and atomically refreshes its local materialized services.

This relay is a transparent TCP hop, not an HTTP reverse proxy. HTTPS sites may
still fail in a browser when certificates, SNI, redirects, cookies, or origin
policy require the original target host. Implemented source and deterministic
tests do not replace clean Linux/Windows, systemd, `socat`, DNS, TLS, and rollback
acceptance.

The relay companion also implements a strict manager-layer adoption transaction
for one exact legacy Skill-created unit. It allow-lists the legacy/canonical
unit name and exact user-unit path, rejects drop-ins and any unit that cannot be
proven equivalent, shows a full read-only diff, and requires explicit
confirmation plus a state-bound token before backup and canonical replacement.
This migration primitive is not connected to the Agent endpoints or Windows UI;
it must not be described as part of the end-user relay workflow yet.

The native Service Profile path is separate from Agent authority. Export uses a
platform file picker and contains only bounded service definitions. Import
first performs a read-only validation and addition/change/removal plus Linux
port-impact preview, then requires a separate confirmation before atomic save.
It preserves the server, SSH/Agent identity, authorization receipt, behavior,
and credential reference. Consequently a profile that introduces a Linux port
still requires Agent synchronization or one-shot pairing before SSH can use it.

Agent API V1 uses fixed `/v1` HTTP endpoints over Tailscale. Linux issues an
expiring one-use `admin` or `device` recovery code. Enrollment records the peer's
stable node ID and normalized login from `tailscale whois --json`, then issues a
per-device Bearer credential bound to both values. Windows independently maps
the endpoint to one online Tailscale peer and matches its stable node ID and
MagicDNS identity before saving. Request and response bodies are both bounded
to 64 KiB. An active device reads its own grant through `/v1/me`; administrator
list/revoke, device-code creation, catalog writes, and relay operations remain
separately role-gated. See
[AGENT_PROTOCOL.md](AGENT_PROTOCOL.md).

### Compatibility and failure boundaries

- A v2.1 client and `jkbridge-linux.py pair` continue to work without an Agent.
- The vNext client may use one-shot Pairing Protocol V1 to configure or recover a
  server when the Agent is absent or deliberately disabled.
- Manually starting or stopping the Agent must not invalidate an already authorized
  SSH tunnel. Agent-dependent management actions become unavailable, while
  existing exact OpenSSH grants remain effective until explicitly changed.
- Schema V4 must not claim an Agent enrollment when migrating V3 state.
- Unknown Agent protocol versions, manifest versions, or authorization states
  fail closed; they never fall back to wildcard access.
- The Agent does not replace Tailscale login, SSH host-key pinning, or the
  OpenSSH transport. It only reduces repeated terminal work around management.
- The fixed development API and packaged templates remain unsupported until
  Linux lifecycle and clean-machine interoperability acceptance pass.
- Explicit discovery, same-node endpoint rebind, device rename, and confirmed
  direct catalog writes exist in source and tests but still require clean-machine
  acceptance. Approval of a replacement/old device, credential rotation, and
  background catalog synchronization are not present.
- Managed relay source exists, but supported dependency installation, per-user
  systemd lifecycle acceptance, signed package provenance, legacy-relay adoption
  integration, and clean-machine/TLS acceptance remain release gates.

## Logging and privacy

- Structured runtime events, capped rolling files, and an in-memory recent-event list.
- User profile paths are redacted in displayed or copied diagnostics.
- No private-key material, password, cookie, pairing token, raw Tailscale JSON, environment dump, or HTTP response body is logged.
- Pairing failures use safe messages; public client fingerprints may be shown for revocation.
- Public source defaults contain no deployed identity or generated public key.
- The Diagnostics page can explicitly export an allow-listed ZIP containing
  format/app version, anonymous tunnel/service phases, and at most 200 bounded,
  redacted recent events. It never serializes the config or runtime snapshot,
  rejects reparse-point destinations, and writes by durable atomic replacement.
- The Settings page checks for updates only on user action. Stable builds use
  the fixed latest-release endpoint; prerelease builds use a bounded official
  release list and retain their Preview channel. A second explicit action
  downloads an exact official installer/checksum pair using system proxy,
  loopback manual HTTP proxy, or direct mode. It accepts only GitHub's
  release-asset CDN redirect, uses HTTP/2 when negotiated, streams a bounded EXE
  into the managed update directory, reports bytes, percentage, average speed,
  estimated remaining time and verification progress, supports cancellation,
  and verifies SHA-256. Success only stages the EXE. A third
  explicit action revalidates the managed path, starts the normal visible
  installer without silent flags, and requests controlled shutdown only after
  process creation succeeds. It never performs a background check, download, or
  installation.

## Publishing

The release script runs privacy checks, Python tests, .NET Release tests, and self-contained Windows publish before producing a staging directory. One payload array is the source of truth for both `SHA256SUMS` and ZIP creation. The vNext staging package also carries the development Agent and relay companion scripts, Linux user-unit/environment/controller assets, and their manual review notes. A package-contract check proves those assets are present, executable, colocatable, lifecycle-command complete, and non-secret; it does not install `socat`, provide signed Agent provenance, or replace clean-machine Agent/relay acceptance. Detached SHA-256 files cover the ZIP and installer. The explicit in-app overlay-install workflow does not promote the `v2.2.0` Preview line to stable.
