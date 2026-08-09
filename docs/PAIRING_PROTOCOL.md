# JK Bridge Pairing Protocol V1

This document describes the local one-shot setup protocol between the Linux helper and Windows application. It is not used for normal tunnel traffic.

> **Status:** This remains the released one-shot setup and recovery protocol in
> stable v2.2.0. The optional persistent Linux
> Agent and its multi-device management contract are a separate vNext design in
> [AGENT_PROTOCOL.md](AGENT_PROTOCOL.md). No Agent endpoint described there is
> required or implied by Pairing Protocol V1.

## Pairing code

```text
jkbridge://<tailscale-ipv4>:<ephemeral-port>/v1?token=<base64url-token>
```

Constraints:

- Scheme, path, and protocol version are exact.
- Host must be an IPv4 address inside `100.64.0.0/10`.
- Port must be `1..65535`.
- Token decodes to exactly 32 random bytes and has no Base64 padding.
- The code is a temporary secret and must not be saved or shared.

The Windows client converts this to `http://<ip>:<port>/v1/pair`. The token is removed from the URL and sent as `Authorization: Bearer <token>`.

## Request

```http
POST /v1/pair HTTP/1.1
Authorization: Bearer <token>
Content-Type: application/json
```

```json
{
  "protocolVersion": 1,
  "clientPublicKey": "ssh-ed25519 <base64>",
  "remotePorts": [80, 8765, 8793]
}
```

The body is limited to 64 KiB. There must be 1–32 integer ports, each `1..65535`; the helper sorts and deduplicates them.

## Success response

```json
{
  "protocolVersion": 1,
  "serverHost": "linux-box.example-tailnet.ts.net",
  "tailnetDnsSuffix": "example-tailnet.ts.net",
  "tailscaleIpv4": "100.64.0.10",
  "sshUser": "alice",
  "sshPort": 22,
  "hostPublicKey": "ssh-ed25519 <base64>",
  "displayName": "linux-box",
  "clientKeyFingerprint": "SHA256:<base64>",
  "permitPorts": [80, 8765, 8793]
}
```

The client requires protocol V1, an exact endpoint IP match, a valid normalized MagicDNS name and suffix, a valid Ed25519 host public key, a safe SSH username/port, its own client-key fingerprint, and an exact match between requested and returned ports. The response is limited to 64 KiB.

After all checks pass, schema V3 stores an authorization receipt containing `clientKeyFingerprint` and the exact `permitPorts` response. This receipt is local state, not a change to protocol V1. Before future SSH startup, enabled service routes are compared with it. An unknown legacy receipt or any missing destination port requires a new pairing request; unused extra ports produce a least-privilege recommendation.

The in-development schema V4 migration does not require a change to this wire
response. A vNext client using the one-shot fallback may convert the same
verified response into its V4 per-device authorization state. It must preserve
all of these rules:

- the returned fingerprint matches the private key held by this Windows device;
- `permitPorts` exactly matches the requested Linux destination-port set;
- the SSH host key and MagicDNS identity are validated and pinned as before;
- migration or local service import never manufactures an authorization receipt;
- another Windows device must pair with its own public key rather than reuse the
  first device's private key or receipt.

### Development Agent coexistence and strict fallback

The unreleased `jkbridge-agentctl fallback` command stops only the Agent user
service and prints the retained `jkbridge-linux.py pair` command. It keeps Agent
state and credential verifiers, every SSH key, installed helper, environment,
and unit. It does not terminate an already established Windows SSH session; the
Windows tunnel must be stopped separately when immediate cutoff is required.

When fallback pairs the same Windows public key, the helper converts an Agent
line only if it is the sole matching entry and exactly matches the canonical
command-disabled, Tailscale-source-restricted, loopback-only format, valid
32-hex device ID, and key fingerprint. A personal, unrestricted, wildcard,
duplicate, modified, malformed, or marker-only lookalike entry is refused. The
replacement remains an exact V1 grant, so changing or expanding remote ports
requires a fresh one-shot request. Agent transactions and the helper share
`~/.ssh/.jkbridge.lock`; after restart, a later Agent transaction can recognize
that exact same-key one-shot line and reconcile it back to the enrolled device's
catalog-derived Agent line without adopting a broad key.

## Transport controls

- The server binds only to the Linux Tailscale IPv4 interface.
- The server accepts request source addresses only in the documented Tailscale IPv4/IPv6 ranges.
- The server shuts down after one success, expiry, cancellation, or 20 failed requests.
- Default lifetime is 600 seconds and may be selected from 60–3600 seconds.
- The Windows client disables proxy use and HTTP redirects.
- Both sides apply short timeouts and size limits.
- Responses include `Cache-Control: no-store` and `X-Content-Type-Options: nosniff`.

## Security rationale

TLS certificates are not provisioned for the ephemeral endpoint. Confidentiality and peer-network transport come from Tailscale; the random bearer token authorizes the one-time state change. The persistent channel immediately switches to SSH with a host public key read from the Linux filesystem and pinned by Windows.

The token prevents unrelated tailnet peers from pairing unless they learn the displayed code. It does not protect a code copied to an attacker during its validity, so the code must be treated like a temporary password.

## Compatibility

Unknown versions fail closed. Adding optional fields is allowed only when both implementations ignore them safely. Any change to required validation, endpoint path, authentication, or authorization semantics requires a new versioned path and protocol value.

Pairing Protocol V1 and the development Agent API V1 have different lifetimes and
responsibilities:

| Capability | One-shot Pairing V1 | Development Agent API V1 |
|---|---|---|
| Availability | Released | Implemented source; installation/acceptance pending; not released |
| Linux process | Starts for one success/timeout, then exits | Optional persistent Tailscale-only Python server |
| Bootstrap | User copies a short-lived pairing code | User enters an expiring one-use Linux-issued `admin`/`device` code |
| Device identity | One request installs one dedicated public key | Maintains multiple independently revocable device records |
| Additional device | Run a new one-shot pairing for that device | Enrolled admin may issue one expiring, one-use ordinary-device code; admin recovery remains Linux-local |
| Service ports | Complete request port set | Derived from a versioned Linux service manifest |
| Explicit target | Operator separately prepares any Linux loopback relay, then pairs its exact port | Admin-only read-only plan/probe and state-bound confirmed managed-relay apply/delete |
| Windows refresh | Re-run explicit one-shot pairing for a changed exact port set | Manual foreground sync reads the caller's own grant; administrators may also list/revoke devices |
| Fallback role | Primary v2.1 setup; vNext recovery/portable mode | Convenience path after one-time Linux preparation |
| Tunnel transport | Restricted OpenSSH | The same restricted OpenSSH data plane |

An Agent-capable client must not silently reinterpret a V1 pairing code as an
Agent enrollment credential. If Agent discovery, version negotiation,
authentication, or service synchronization fails, the UI may offer explicit
one-shot pairing; it must not fall back to a less restricted SSH entry.
