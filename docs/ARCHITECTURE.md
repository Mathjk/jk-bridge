# JK Bridge Architecture

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
| vNext device identity | One dedicated Ed25519 identity and independently revocable authorization per Windows device |
| Stable service migration | Native JSON export/import UI; bounded non-secret profile, read-only diff, explicit confirmation, atomic save |
| vNext explicit relay | One exact DNS/IPv4 TCP target behind one Linux `127.0.0.1` `socat` user unit; read-only plan/probe before confirmed apply/delete |

Avalonia enables Linux-hosted development and Windows cross-publishing while retaining a native tray and child-process supervision. The recommended Linux side remains a short script because its only persistent output is standard OpenSSH authorization; the optional jkbridge-ctl control daemon adds restart/log/status actions, and normal service traffic continues to use the same restricted OpenSSH data plane.

## Repository structure

```text
src/JKBridge.Core/             config, pairing, routes, authorization, Tailscale, SSH, health
src/JKBridge.App/              Avalonia UI and Windows platform adapters
scripts/jkbridge-linux.py      one-shot Linux pairing and managed-key removal
scripts/jkbridge_relay.py      validated loopback relay plan/probe/apply/rollback companion
packaging/linux/               user unit/env, non-root lifecycle controller, packaging guide
scripts/setup-windows.ps1      advanced V1/manual recovery path
scripts/verify-windows.ps1     Windows environment and runtime verification
scripts/check-public-release.sh privacy/credential gate
scripts/publish-win-x64.sh     tested release packaging
tests/                         .NET and Linux tests
docs/                          install, troubleshooting, development, architecture, security, protocol, ADRs
```

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

An unconfigured app cannot enter tunnel preflight and routes to Device setup. Stable v2.2.1 loads schema V1–V6 profiles and migrates them in memory to V7 while preserving their device, service, and behavior settings. Because V1/V2 did not record what Linux actually authorized, a configured profile from either version must re-pair once before SSH starts. That pairing establishes the authorization baseline. Writes use a same-directory temporary file, flush, last-valid backup, and atomic replacement.

### Schema V4–V6 migration — released in v2.2.0

### Schema V7 migration — released in v2.2.1

Schema V7 adds an isolated `updateNetwork` object. Existing V1–V6 profiles
default to Windows system-proxy mode. Manual mode stores only a loopback HTTP
proxy URL with an explicit port and no credentials or path; direct mode stores
no proxy value. The preference is applied only to future update checks and
downloads and does not change tunnel, pairing, discovery, or health traffic.

The migration preserves these boundaries:

- Preserve every valid V3 server identity, service, behavior setting, local
  port, browser URL, health definition, and authorization receipt.
- Treat a server catalog entry as the source of the Linux destination and its
  suggested local defaults. The materialized Windows `ServiceDefinition` keeps
  this device's local port and launch settings; a local override does not change
  Linux authorization.
- Use stable service identities and a catalog revision so an
  import can show additions, edits, removals, and conflicts
  before applying them.
- Preserve an existing V3 `openUrl` exactly. Migration may classify it as a
  generated simple-mode URL only when it is provably equivalent; otherwise it
  remains an explicit advanced-mode value.
- Keep the authorization receipt as observed server state. Migration must not
  manufacture a client fingerprint or a granted port.
- Keep private-key contents, one-time pairing tokens,
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
changing a Linux remote port requires a new exact grant through
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

The release script runs privacy checks, Python tests, .NET Release tests, and self-contained Windows publish before producing a staging directory. One payload array is the source of truth for both `SHA256SUMS` and ZIP creation. The staging package also carries the optional `jkbridge-ctl` script and its systemd user unit. Detached SHA-256 files cover the ZIP and installer. The explicit in-app overlay-install workflow does not promote the `v2.2.0` Preview line to stable.
