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
| Persistence | Atomic JSON config, backup recovery, rotating redacted logs |
| Tests | Unit/integration tests for config, protocol, runtime, view model, Linux file mutation, and packaging |

Avalonia enables Linux-hosted development and Windows cross-publishing while retaining a native tray and child-process supervision. The Linux side remains a short script because its only persistent output is standard OpenSSH authorization.

## Repository structure

```text
src/JKBridge.Core/             config, pairing, process, Tailscale, SSH, health
src/JKBridge.App/              Avalonia UI and Windows platform adapters
scripts/jkbridge-linux.py      one-shot Linux pairing and managed-key removal
scripts/setup-windows.ps1      advanced V1/manual recovery path
scripts/verify-windows.ps1     Windows environment and runtime verification
scripts/check-public-release.sh privacy/credential gate
scripts/publish-win-x64.sh     tested release packaging
tests/                         .NET and Linux tests
docs/                          product, architecture, security, protocol, ADRs
```

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

Fresh V2 configuration is intentionally unconfigured:

```json
{
  "schemaVersion": 2,
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
  }
}
```

An unconfigured app cannot enter tunnel preflight and routes to Device setup. Schema V1 profiles migrate in memory to V2 with `setupCompleted=true`, then persist atomically. Writes use a same-directory temporary file, flush, last-valid backup, and atomic replacement.

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

Only the controller mutates runtime state. Operations are serialized so connect, stop, config changes, process exit, and resume cannot race.

```text
Stopped -> Preflight -> Starting -> Connected -> Degraded
             |                         |
             +-> NeedsAction           +-> Reconnecting -> Preflight
Any active state -> Stopping -> Stopped
```

Transient retry uses bounded exponential backoff. Authentication, host-key, invalid configuration, and local bind failures do not retry forever.

`ExitOnForwardFailure` proves listener creation, not web-service availability. Every enabled service therefore has an independent short-timeout loopback HTTP health check. A failed service changes only its own status and the aggregate state.

## Authorization line

For enabled remote ports `<p1>...<pn>`, Linux writes the equivalent of:

```text
restrict,port-forwarding,command="/usr/bin/false",from="100.64.0.0/10,fd7a:115c:a1e0::/48",permitopen="127.0.0.1:<p1>",permitopen="127.0.0.1:<pn>" ssh-ed25519 <PUBLIC_KEY> JK Bridge managed SHA256:<FINGERPRINT>
```

The key cannot run commands or obtain a shell. Only exact IPv4 loopback targets requested for currently enabled services are authorized. `authorized_keys` is locked, backed up, written with mode `0600`, fsynced, and atomically replaced.

## Logging and privacy

- Structured runtime events, capped rolling files, and an in-memory recent-event list.
- User profile paths are redacted in displayed or copied diagnostics.
- No private-key material, password, cookie, pairing token, raw Tailscale JSON, environment dump, or HTTP response body is logged.
- Pairing failures use safe messages; public client fingerprints may be shown for revocation.
- Public source defaults contain no deployed identity or generated public key.

## Publishing

The release script runs privacy checks, Python tests, .NET Release tests, and self-contained Windows publish before producing a staging directory. It packages the EXE, Linux helper, bilingual documents, recovery scripts, security notes, and checksums. A detached SHA-256 file covers the ZIP.
