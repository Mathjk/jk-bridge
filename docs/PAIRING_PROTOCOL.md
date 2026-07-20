# JK Bridge Pairing Protocol V1

This document describes the local one-shot setup protocol between the Linux helper and Windows application. It is not used for normal tunnel traffic.

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
