# JK Bridge Agent API V1 — experimental contract

> **Advanced/experimental; not supported for routine deployment.** Stable
> v2.2.1 keeps one-shot pairing as the supported default. The release contains
> the working development Linux server (`scripts/jkbridge_agent.py`), bounded
> Windows client and enrollment orchestration, schema V4 persistence, strict
> relay companion (`scripts/jkbridge_relay.py`), tests, a reviewed systemd
> user-unit template, and a non-root lifecycle controller
> (`packaging/linux/jkbridge-agentctl`). The API paths below are now fixed for
> Agent protocol V1. Install/upgrade/rollback/uninstall/start/stop/status/fallback
> and their fail-closed package tests are implemented, but the resulting bundle
> has not passed clean-machine live acceptance and is not yet a stable Agent
> Release asset. Pairing Protocol V1 remains the supported setup and recovery
> path for stable v2.2.1.

The companion [AGENT_THREAT_MODEL.md](AGENT_THREAT_MODEL.md) defines release
security requirements. Passing development tests does not convert an unchecked
release gate into a supported feature.

## Purpose and non-purpose

The optional Agent removes repeated Linux terminal work after one-time Linux
preparation. It provides a narrow management plane for:

- enrolling independently revocable Windows devices;
- serving and updating a versioned Linux service catalog;
- deriving exact per-device OpenSSH forwarding authorization;
- listing devices, revoking one selected device, and issuing an ordinary-device
  enrollment code from an enrolled administrator;
- renaming one device label through an audited administrator-only endpoint;
- reporting bounded, read-only candidates from current Linux
  `127.0.0.1` TCP listeners without scanning disks or port ranges;
- managing one explicit TCP target behind one generated Linux loopback relay;
  and
- reporting sanitized Agent/bootstrap state.

Normal service bytes continue through restricted OpenSSH local forwards over
Tailscale. An optional managed relay is a separate transparent TCP hop from one
Linux `127.0.0.1` port to one exact DNS/IPv4 target and port. The Agent is not a
VPN, remote shell, package manager, arbitrary systemd editor, general website
proxy, wildcard relay, or application supervisor.

The Agent is deterministic and dependency-free beyond the documented OS
components. It does not use AI, a model API, Codex, or cloud inference. The
`jkbridge` Skill remains an optional development, diagnosis, and audited
recovery aid, not a runtime dependency.

## Release and compatibility boundary

| Component | Released v2.1 | vNext source state |
|---|---|---|
| Windows config | Schema V3 | Schema V4 implemented; not released |
| Bootstrap | One-shot Pairing Protocol V1 | Recovery-code Agent enrollment implemented; V1 fallback retained |
| Linux management | One-shot `jkbridge-linux.py` | Development Agent, user unit, and `jkbridge-agentctl` preflight/install/upgrade/rollback/uninstall/start/stop/status/fallback source; live lifecycle acceptance pending |
| Device model | One client key/receipt per config | Multiple per-device keys, credentials, stable node/login identities, roles, and revocation records |
| Service source | Windows config | Linux manifest/API catalog plus Windows materialization |
| Explicit relay | Manual operator-created unit outside V2.1 | Admin plan/probe/confirm/delete API and Windows UI implemented; clean-machine acceptance pending |
| SSH data plane | Restricted OpenSSH local forwards | Unchanged restricted OpenSSH local forwards |

An Agent remains optional. Stopping it must not remove existing exact SSH
grants or stop application services. Unknown API, manifest, or config versions
fail closed. Installing the Agent does not replace Tailscale login, SSH
host-key pinning, or Windows OpenSSH Client.

## Fixed V1 transport contract

The development API uses HTTP inside Tailscale encryption. Its origin is:

```text
http://<exact-linux-tailscale-ipv4>:<explicit-port>/
```

The default port is `45871`. The origin cannot contain a DNS name, credentials,
path, query, or fragment. The Linux server discovers and binds its exact current
Tailscale IPv4; it has no wildcard-host option. The Windows client disables
proxies, redirects, cookies, ambient credentials, and default authentication,
then verifies that each response remains on the requested origin.

Protocol constants and limits:

- protocol version: `1`;
- client version header: `X-JKBridge-Protocol-Version: 1`;
- default request timeout: 20 seconds, configurable only within 1–60 seconds;
- request body maximum: 64 KiB;
- response body maximum: 64 KiB;
- services per catalog: at most 64;
- distinct exact remote ports: at most 32;
- Agent device-state records and Windows device-list responses: at most 64;
- active recovery codes: at most 8;
- credentials: 32 random bytes encoded as 43-character unpadded base64url; and
- relay plan tokens: exactly 64 lowercase hexadecimal characters.

State-changing requests use UTF-8 `application/json` and include
`protocolVersion: 1`. Credentials submitted for authentication appear only in
`Authorization`. The newly issued ordinary-device enrollment code appears once
in a `no-store` response body; a state-bound relay plan token appears in its
plan response and confirmed apply/delete body but has no authority without the
same administrator Bearer credential and matching peer. Neither value may
appear in a URL, query, config, log, or diagnostic. Responses are UTF-8 JSON with
`Cache-Control: no-store`, `X-Content-Type-Options: nosniff`, and
`Referrer-Policy: no-referrer`.

Errors use:

```json
{
  "error": {
    "code": "safe_machine_code",
    "message": "Safe actionable detail."
  }
}
```

The Windows client bounds and sanitizes error text and removes any submitted
credential before surfacing it.

## Fixed V1 endpoints

| Method and path | Authentication | Role | Purpose |
|---|---|---|---|
| `GET /v1/health` | Verified Tailscale peer | Any peer | `ok` or `needsBootstrap` plus versions |
| `GET /v1/version` | Verified Tailscale peer | Any peer | Protocol and Agent versions |
| `GET /v1/bootstrap` | Verified Tailscale peer | Any peer | Server/SSH identity, catalog revision, enrollment availability |
| `POST /v1/enroll` | `JKBridge-Enrollment <one-time-code>` plus verified peer | Code-selected `admin` or `device` | Enroll one key and return one device credential/receipt |
| `GET /v1/me` | `Bearer <device-credential>` plus matching peer | Active device | Read only the caller's sanitized device/grant record and catalog revision |
| `GET /v1/services` | `Bearer <device-credential>` plus matching peer | Active device | Read the service catalog |
| `GET /v1/discovery` | `Bearer <device-credential>` plus matching peer | `admin` | Inspect bounded current `127.0.0.1` listeners without changing catalog or grants |
| `PUT /v1/services` | `Bearer <device-credential>` plus matching peer | `admin` | Replace catalog at an expected revision and reconcile grants |
| `GET /v1/devices` | `Bearer <device-credential>` plus matching peer | `admin` | List sanitized active/revoked records |
| `POST /v1/devices/<32-hex-id>/rename` | `Bearer <device-credential>` plus matching peer | `admin` | Change only one device label and append an audit event |
| `POST /v1/devices/<32-hex-id>/revoke` | `Bearer <device-credential>` plus matching peer | `admin` | Revoke exactly one device |
| `POST /v1/enrollment-codes` | `Bearer <device-credential>` plus matching peer | `admin` | Issue one expiring, one-use `device`-role enrollment code |
| `POST /v1/relays/plan` | `Bearer <device-credential>` plus matching peer | `admin` | Read-only normalized install/update plan and grant diff |
| `POST /v1/relays/probe` | `Bearer <device-credential>` plus matching peer | `admin` | Read-only dependency, port, and exact-target TCP probe |
| `POST /v1/relays/apply` | `Bearer <device-credential>` plus matching peer | `admin` | Apply the exact current install/update plan with confirmation |
| `POST /v1/relays/delete-plan` | `Bearer <device-credential>` plus matching peer | `admin` | Read-only managed-relay deletion and grant-contraction plan |
| `POST /v1/relays/delete` | `Bearer <device-credential>` plus matching peer | `admin` | Apply the exact current deletion plan with confirmation |

Every request first passes the Tailscale address filter and `tailscale whois
--json` peer verification. Authentication does not rely on a caller-supplied
`X-Tailscale-*` header.

## Server identity and bootstrap

`GET /v1/bootstrap` returns:

```json
{
  "protocolVersion": 1,
  "agentVersion": "0.2.0",
  "serverHost": "linux-box.example-tailnet.ts.net",
  "tailnetDnsSuffix": "example-tailnet.ts.net",
  "tailscaleIpv4": "100.64.0.10",
  "tailscaleNodeId": "stable-linux-node-id",
  "sshUser": "alice",
  "sshPort": 22,
  "hostPublicKey": "ssh-ed25519 <base64>",
  "displayName": "linux-box",
  "enrollmentAvailable": true,
  "servicesRevision": 1
}
```

Before Windows submits a key, it resolves the endpoint to exactly one online
peer in its local Tailscale status and requires the peer's stable node ID,
MagicDNS name, tailnet suffix, and endpoint IPv4 to agree with the Agent
response. It separately validates the Linux Ed25519 host key and later pins it
for OpenSSH. A self-reported MagicDNS name alone is not trusted.

`GET /v1/health` returns `protocolVersion`, `agentVersion`, and `status`; status
is `ok` only after state initialization and otherwise `needsBootstrap`.
`GET /v1/version` returns `protocolVersion` and `agentVersion`.

### Linux setup code

First bootstrap prints one `JKBA1.` base64url setup code instead of asking the
user to copy an address and secret separately. Its canonical JSON carries only
`version: 1`, the strict Agent `endpoint`, and one 32-byte
`enrollmentCredential`. The Windows parser rejects padding, non-canonical
encoding, duplicate or unknown fields, wrong types, invalid endpoints, and
invalid credentials. The input is cleared before enrollment; the code itself
is never persisted.

### Explicit Windows discovery

The Windows Setup page implements a bounded, user-triggered discovery pass. It
reads the local Tailscale status, probes only known online peers at the fixed
Agent port `45871`, and never scans an address range. Each candidate must make
`/v1/health` and `/v1/bootstrap` agree on protocol and Agent version, and its
claimed IPv4, stable node ID, MagicDNS host, and tailnet suffix must match the
local Tailscale peer record. Probe concurrency, target count, per-peer timeout,
overall timeout, and response size are bounded.

Discovery is read-only and unauthenticated in the same limited sense as health
and bootstrap. One verified candidate is preselected for review; multiple
candidates require a user selection. Neither case writes the endpoint until the
user clicks **Use selected address**, and discovery never consumes a code,
enrolls a device, or starts a tunnel. A one-use code plus the separate explicit
enrollment action remain mandatory. Source and automated tests are present;
disposable clean-Windows acceptance is still a release gate.

For an already enrolled device, the same discovery results also support an
explicit endpoint rebind when a Tailscale IPv4 changes. The user must select a
candidate, the candidate's stable node ID must equal the pinned Agent node ID,
and a separate rebind confirmation is required. Windows then repeats endpoint,
MagicDNS/tailnet, bootstrap/server/SSH identity, local node/login, stored device
credential, `/v1/me`, device ID/key fingerprint/grant, and catalog-revision
verification before atomically changing only the saved Agent endpoint and
observed revision. A different stable node fails closed and requires normal
enrollment; rebind never replaces the device ID, credential reference, SSH key,
or pinned server identity.

### Linux loopback-service discovery

`GET /v1/discovery` is an explicit administrator-only read. The Linux
implementation invokes only `ss -H -ltnp` with a four-second and 512-KiB output
bound, parses at most 64 exact IPv4 `127.0.0.1` listeners, excludes the Agent,
SSH, common database ports, and ports already in the server catalog, and never
walks a disk, reads command lines or environments, scans a port range, or
examines wildcard/LAN/IPv6 listeners.

For each remaining listener it may issue one bounded loopback `GET /` over
HTTP and then HTTPS solely to recognize a likely web UI, status, and title.
Each probe is limited to 350 ms and 16 KiB, with at most 12 workers. HTTPS
certificate validation is deliberately disabled only for this loopback
metadata probe and the result is marked `tlsCertificateVerified: false`; it
does not grant trust to the service certificate. The process field contains
only a sanitized executable basename.

The response is sorted by port, rate-limited to six requests per minute per
peer, and has scope `current-127.0.0.1-listeners`. A candidate is an in-memory
suggestion only. Windows adds it as a disabled draft after a second user action;
catalog and SSH authorization still require the existing diff preview and
explicit administrator confirmation.

## Enrollment

Linux initialization or recovery creates an expiring, one-use code with role
`admin` or `device`. The default lifetime is 900 seconds; the CLI accepts
60–86400 seconds. Only a domain-separated verifier is persisted. Code
consumption, device creation, exact SSH authorization, and credential issuance
occur under one locked transaction, so concurrent attempts cannot both use the
same code.

Request:

```http
POST /v1/enroll
Authorization: JKBridge-Enrollment <one-time-code>
Content-Type: application/json
```

```json
{
  "protocolVersion": 1,
  "deviceLabel": "Office PC",
  "clientPublicKey": "ssh-ed25519 <base64>",
  "requestedServiceIds": ["overleaf", "sub2api"]
}
```

An empty `requestedServiceIds` selects services whose catalog entry has
`enabledByDefault: true`. The server rejects an unknown service, duplicate
active public key, exhausted device limit, malformed Ed25519 key, expired/reused
code, or incompatible protocol.

The success response flattens the verified server fields from bootstrap and
adds:

```json
{
  "deviceId": "0123456789abcdef0123456789abcdef",
  "deviceLabel": "Office PC",
  "role": "admin",
  "deviceCredential": "<43-character-base64url>",
  "clientKeyFingerprint": "SHA256:<base64>",
  "permitPorts": [80, 8081],
  "servicesRevision": 1
}
```

The full response also contains `protocolVersion`, `agentVersion`, server
MagicDNS/tailnet/Tailscale node identity, SSH user/port, host public key, and
display name. Windows requires every server value to match the already verified
bootstrap, requires the returned label and client fingerprint to match its
request/key, and requires the exact permit-port set to match the fetched catalog
defaults. Only then does it save config and the device credential.

The returned device credential is never returned again by list, status, or
diagnostics. Windows stores it in Credential Manager and writes only an opaque
reference into schema V4.

### Administrator-issued ordinary-device code

An already enrolled administrator can create a code for one additional
ordinary device without using the Linux console:

```http
POST /v1/enrollment-codes
Authorization: Bearer <administrator-device-credential>
Content-Type: application/json
```

```json
{
  "protocolVersion": 1,
  "ttlSeconds": 600
}
```

`ttlSeconds` is an integer from 60 through 3600. The Agent repeats the normal
credential plus stable node/login check, requires `admin`, expires old records,
and refuses more than eight active codes. It always creates role `device`; this
endpoint cannot create another administrator. Success is HTTP 201:

```json
{
  "protocolVersion": 1,
  "enrollmentCode": "<43-character-base64url>",
  "expiresAt": "2026-07-29T12:10:00Z"
}
```

Only the domain-separated verifier is persisted. The code follows the same
one-use transactional consumption rules as Linux-issued enrollment material.
The Windows UI requests 600 seconds, displays and copies it transiently, clears
it at expiry, and warns that it cannot clear an already populated system
clipboard. Linux-local `recovery-code --role admin` remains the implemented
path to recover or create administrator enrollment authority.

## Stable peer and device identity

For each request the Agent resolves the remote socket address through
`tailscale whois --json` and obtains:

- `tailscaleNodeId`: the Tailscale stable node ID; and
- `tailscaleLogin`: the normalized Tailscale login name.

Enrollment binds both values to the new device credential. Every later Bearer
request must present the credential from a peer with both the same stable node
ID and login. Being inside `100.64.0.0/10`, knowing a credential from another
node, or sending a forged identity header is insufficient.

The server device record contains:

```text
deviceId, deviceLabel, publicKey, clientKeyFingerprint, role,
tailscaleNodeId, tailscaleLogin, serviceIds, permitPorts,
createdAt, lastSeenAt, revokedAt, credential verifier
```

`deviceId` is exactly 32 lowercase hexadecimal characters matching
`[a-f0-9]{32}`. The server and Windows client both cap the device list at 64.
`lastSeenAt` is initialized at enrollment and refreshed by authenticated read
requests at most once every five minutes. Its atomic metadata write is
best-effort so a storage hiccup does not turn a valid read into an outage; the
field is bounded activity evidence, not real-time online presence.

`GET /v1/devices` omits the public key and credential verifier but returns the
remaining audit fields, including `tailscaleNodeId`, `tailscaleLogin`,
`serviceIds`, exact `permitPorts`, timestamps, and any revocation timestamp.
Each Windows installation must use a different SSH key and credential.

`GET /v1/me` deliberately has no administrator requirement and returns exactly
one sanitized active-device record together with the catalog revision:

```json
{
  "protocolVersion": 1,
  "servicesRevision": 1,
  "device": {
    "deviceId": "0123456789abcdef0123456789abcdef",
    "deviceLabel": "Office PC",
    "clientKeyFingerprint": "SHA256:<base64>",
    "role": "device",
    "tailscaleNodeId": "stable-windows-node-id",
    "tailscaleLogin": "owner@example.com",
    "serviceIds": ["overleaf", "sub2api"],
    "permitPorts": [80, 8081],
    "createdAt": "2026-07-29T12:00:00Z",
    "lastSeenAt": "2026-07-29T12:05:00Z",
    "revokedAt": null
  }
}
```

The endpoint authenticates the same credential/node/login tuple as every other
Bearer operation. It never accepts a requested device ID, never returns another
device, and never grants list, rename, approval, catalog-write, or revoke
authority. A revoked credential cannot use it. This narrow endpoint lets the
Windows client validate its own complete service IDs and exact-port grant
without granting a `device` role access to the fleet list.

The current implemented enrollment policy is explicit Linux-issued
`admin`/`device` recovery codes plus the narrower administrator-issued
ordinary-device code above. Automatic same-login enrollment, approval bound to
a specific pending/old device, and remote administrator-code issuance are not
part of API V1.

### Administrator device rename

An administrator may change only the display label of one existing device:

```http
POST /v1/devices/<device-id>/rename
Authorization: Bearer <administrator-device-credential>
Content-Type: application/json
```

```json
{
  "protocolVersion": 1,
  "deviceLabel": "Office desktop"
}
```

The ID is exactly 32 lowercase hexadecimal characters. `deviceLabel` is
canonical text without outer whitespace, control characters, or more than 80
characters. A non-admin, missing device, unchanged label, incompatible
protocol, or full bounded audit log fails without mutation. Success returns:

```json
{
  "protocolVersion": 1,
  "renamed": true,
  "previousDeviceLabel": "Old laptop",
  "device": { "deviceId": "<same-device-id>", "deviceLabel": "Office desktop" }
}
```

The complete `device` object has the same sanitized shape as the device-list
record. The transaction changes no key, credential, role, Tailscale identity,
service grant, permit port, or catalog revision; it appends a bounded audit
event recording actor, target, previous label, new label, and time. The Linux
endpoint, bounded Windows client/receipt validation, administrator management
service, controller, and Setup-page rename control are implemented and tested.
Renaming the current device also updates its local non-secret Agent label after
the server receipt; clean-machine acceptance remains pending.

## Service manifest and catalog

Linux persists a manifest with this exact development schema:

```json
{
  "schemaVersion": 1,
  "revision": 1,
  "updatedAt": "2026-07-29T12:00:00Z",
  "services": [
    {
      "id": "sub2api",
      "name": "Sub2API",
      "description": "Local API gateway",
      "icon": "Api",
      "remotePort": 8081,
      "suggestedLocalPort": 8081,
      "scheme": "http",
      "openPath": "/",
      "healthPath": "/",
      "healthKind": "tcpConnect",
      "enabledByDefault": true
    }
  ]
}
```

The network view is called the service catalog. `GET /v1/services` returns
`protocolVersion`, numeric `servicesRevision`, and the same service array.
Linux `remoteHost` is intentionally absent: the Windows client reconstructs it
as literal `127.0.0.1`. `favoriteByDefault` is also not a V1 wire field.

Validation requires:

- 1–64 services with unique 1–48 character lowercase slug IDs matching
  `[a-z][a-z0-9-]{0,47}` and descriptions no longer than 160 characters;
- one integer Linux `remotePort` and suggested Windows local port per service;
- at most 32 distinct Linux remote ports;
- `http` or `https`;
- local absolute `openPath` and fragment-free `healthPath`;
- `httpStatus`, `jsonOk`, or `tcpConnect`; and
- no supplied remote hostname, wildcard, port range, relay, UDP, or arbitrary
  proxy target.

Those restrictions describe the generic catalog endpoint. A managed relay may
introduce an exact target only through the separate plan/probe/confirm contract
below. `PUT /v1/services` rejects removal or material change of a service owned
by managed relay state, preventing a generic catalog write from bypassing the
relay safety checks.

The Windows browser URL is materialized as:

```text
http(s)://127.0.0.1:<Windows local port>/<openPath>
```

Changing a Windows local port, launch path, or local favorite does not expand a
Linux grant. Linux authority comes only from catalog service IDs and their
exact `remotePort` values.

### Administrator catalog update

`PUT /v1/services` accepts:

```json
{
  "protocolVersion": 1,
  "expectedRevision": 1,
  "services": ["<complete validated service objects>"],
  "applyDefaultsToAllDevices": false
}
```

The expected revision prevents blind last-writer-wins. The update is rejected
if its revision is stale or it would leave an active device with no service.
For each active device the Agent intersects previous service IDs with the new
catalog and optionally adds new defaults, then recalculates the complete exact
port set. Manifest, device state, and `authorized_keys` update through one
journaled transaction. Permission contractions write SSH authorization first.

The response returns `protocolVersion`, incremented `servicesRevision`, and the
normalized complete service catalog.

The Windows administrator flow is also implemented in source. It first fetches
the current catalog and device list, presents an addition/modification/removal,
remote-port, and affected-device preview, and requires a separate confirmation
before sending `PUT /v1/services`. A stale or changed preview is discarded. A
generic catalog write cannot alter a managed-relay-owned service; that must use
the relay plan contract.

### Windows manual synchronization

The implemented Windows sync is an explicit foreground action. It re-verifies
the endpoint's current Tailscale peer and bootstrap/SSH identity, loads the
per-device credential from Credential Manager, and fetches `/v1/me` plus
`/v1/services`. The bootstrap revision, self-receipt revision, and catalog
revision must agree. The client also requires its configured device ID, local
Tailscale stable node/login, SSH-key fingerprint, service IDs, and catalog-
derived exact permit ports to match the receipt before changing local state.

For an administrator only, Windows additionally requests `/v1/devices` and
requires the current-device entry there to match `/v1/me` before showing the
sanitized fleet list. A non-admin sync does not call `/v1/devices`. On success,
Windows preserves the existing Windows local port for the same stable service
ID while taking Linux destination, service membership, catalog revision, and
exact authorization from the verified Agent state. There is no background
subscription or automatic refresh in API V1.

## Managed explicit relay contract

Relay support requires `jkbridge_relay.py` beside `jkbridge_agent.py`, executable
`/usr/bin/socat`, a working systemd user instance, and the reviewed per-user
unit directory. The packaged Agent unit grants write access to that directory;
the package does not install `socat`, enable user lingering, or prove the
real-machine lifecycle.

One relay specification contains:

```json
{
  "slug": "campus-auth",
  "relayPort": 19000,
  "targetHost": "auth.example.test",
  "targetPort": 443,
  "scheme": "https",
  "openPath": "/login"
}
```

The slug matches `[a-z][a-z0-9-]{0,47}`. Ports are exact integers in
`1..65535`. `targetHost` is one canonical IPv4 literal or normalized DNS name,
not a URI, IPv6 literal, wildcard, CIDR, username, path, query, or fragment.
`scheme` is `http` or `https`; `openPath` is one bounded local absolute path.
The Agent derives unit name `jkbridge-relay-<slug>.service` and renders its
listener as `TCP4-LISTEN:<relayPort>,bind=127.0.0.1,reuseaddr,fork`. Callers
cannot provide unit text, executable paths, shell arguments, or a listener
address.

### Install/update plan and probe

The read-only planning request combines the relay spec, its catalog
presentation, and one explicit grant policy:

```json
{
  "protocolVersion": 1,
  "spec": { "slug": "campus-auth", "relayPort": 19000, "targetHost": "auth.example.test", "targetPort": 443, "scheme": "https", "openPath": "/login" },
  "service": { "name": "Campus Auth", "description": "Private login", "icon": "Globe", "suggestedLocalPort": 19000, "healthPath": "/", "healthKind": "tcpConnect", "enabledByDefault": true },
  "grantToAllDevices": true
}
```

`grantToAllDevices: true` selects the new service for every active device;
`false` preserves existing selections and does not silently grant a newly
created service. The response has this shape:

```json
{
  "protocolVersion": 1,
  "planToken": "<64-lowercase-hex>",
  "relayPlan": {
    "operation": "install",
    "action": "create",
    "slug": "campus-auth",
    "unitName": "jkbridge-relay-campus-auth.service",
    "spec": { "slug": "campus-auth", "relayPort": 19000, "targetHost": "auth.example.test", "targetPort": 443, "scheme": "https", "openPath": "/login" },
    "observedSha256": null,
    "reason": "managed relay unit does not exist",
    "requiresConfirmation": true,
    "canApply": true
  },
  "serviceChange": {
    "addedRemotePorts": [19000],
    "removedRemotePorts": [],
    "affectedDeviceIds": ["0123456789abcdef0123456789abcdef"]
  },
  "nextServicesRevision": 2
}
```

Install actions are `create`, `update`, `no_change`, or `blocked`. An existing
unit must contain the exact JK Bridge managed marker and parseable canonical
metadata; otherwise the action is `blocked`, and it will not be overwritten.
The 64-hex token binds the operation, normalized plan/unit digest, current
catalog revision, service presentation, grant policy, and current active/
revoked device grant state.

`POST /v1/relays/probe` accepts only `protocolVersion` and `spec`. It is
read-only and returns the normalized spec plus `dependencyAvailable`, nullable
`relayPortInUse`, nullable `targetReachable`, `readyToInstall`, and a bounded
sanitized `targetError`. Readiness is true only when `socat` exists, the Linux
relay port is reported free, and a three-second TCP probe from Linux reaches
the exact target. The Windows workflow performs plan and probe, shows the
normalized mapping and authorization impact, and enables create/update only
when both the plan and probe are acceptable.

### Confirmed apply

`POST /v1/relays/apply` repeats the complete planning body and adds the returned
`planToken` plus literal `"confirmed": true`. The Agent recomputes the plan
under its store lock and rejects a changed unit, manifest, service request,
device grant, grant policy, role, credential, or peer as stale/conflicting. A
plan token is not a reusable authorization credential.

Create/update preflights the next `authorized_keys` rendering, then atomically
writes only the generated `0600` user unit and runs systemd user
daemon-reload/enable/restart as needed. Updates are backed up. It then commits
relay metadata, the catalog revision, every affected device receipt, and exact
managed SSH entries through the Agent journaled transaction. If that commit
fails with no pending recovery journal, the Agent attempts to delete a new unit
or restore the previous managed specification. The response repeats the plan
receipt and adds boolean `applied` and `backupCreated`.

### Confirmed deletion

`POST /v1/relays/delete-plan` accepts `protocolVersion` and one slug. Its
read-only response uses `operation: "delete"` and action `delete`, `absent`, or
`blocked`, and reports the exact removed ports and affected device IDs. It
refuses to delete the final catalog service or leave an active device with no
selected service. An ordinary catalog service with the same slug is not treated
as a managed relay.

`POST /v1/relays/delete` repeats the slug and adds the fresh `planToken` plus
literal `"confirmed": true`. Under the lock the Agent recomputes the plan,
contracts the manifest/device grants and writes restrictive SSH authorization
first, then disables/stops, backs up, and deletes only the recognized unit. A
unit deletion failure attempts to restore the prior manifest/grants. The same
`applied`/`backupCreated` receipt shape is returned. Windows then reads the
committed revision and atomically refreshes its local service/authorization
state; timeout or uncertainty requires a new sync and plan, never reuse of the
old token.

The relay is transparent TCP. Probe success does not validate HTTP Host, TLS
SNI/certificate identity, redirects, cookies, browser origin policy, or
application login. UDP, arbitrary proxying, wildcard listeners/targets, subnet
routes, and exit-node behavior remain outside V1.

The relay companion additionally contains a manager-level migration primitive
for adopting one exact legacy `jkbridge-<slug>-relay.service`. It accepts only
the allow-listed legacy/canonical name at the exact reviewed user-unit path,
rejects symlinks, foreign/world-writable files, drop-ins, unknown directives,
non-equivalent `socat` arguments, or a missing provenance marker, and requires a
read-only full diff followed by explicit confirmation and a state-bound token.
Apply backs up the observed unit, rechecks its digest, writes the canonical
loopback-only unit, and rolls back on command failure. This primitive exists and
is tested only on `RelayManager`; it is not exposed through Agent API V1 or the
Windows UI, and it has not passed migration acceptance on the deployed relays.

## SSH authorization

For each active device the Agent emits one independently revocable line:

```text
restrict,port-forwarding,command="/usr/bin/false",from="100.64.0.0/10,fd7a:115c:a1e0::/48",permitopen="127.0.0.1:<p1>",permitopen="127.0.0.1:<pn>" ssh-ed25519 <PUBLIC_KEY> JK Bridge agent <DEVICE_ID> SHA256:<FINGERPRINT>
```

Every port is an integer in `1..65535`, sorted, deduplicated, bounded, and
derived from the committed catalog and that device's service IDs. The Agent
does not accept a client-provided hostname or render wildcard `permitopen`.
Managed keys cannot run commands or acquire a shell, PTY, agent/X11 forwarding,
or Linux user rc behavior.

The store preserves unrelated SSH entries and rejects malformed managed entries
or duplicate unmanaged copies. State, manifest, and `authorized_keys` writes
use ownership/type checks, symlink rejection, locks, `0600` files, `0700` state
and backup directories, `fsync`, same-directory atomic replacement, backups,
and a recovery journal. Every Agent transaction takes the private Agent state
lock and then the same `~/.ssh/.jkbridge.lock` used by the one-shot helper. The
one-shot helper takes only that shared SSH-writer lock, so both writers serialize
without a lock cycle or lost `authorized_keys` update.

Windows treats the fingerprint and `permitPorts` response as observed Linux
state. Editing schema V4 or importing a Service Profile never fabricates that
receipt.

## Revocation receipt and session boundary

An administrator revokes exactly one 32-hex device ID with:

```http
POST /v1/devices/<device-id>/revoke
Authorization: Bearer <administrator-device-credential>
Content-Type: application/json
```

```json
{
  "protocolVersion": 1
}
```

The fixed success receipt is:

```json
{
  "protocolVersion": 1,
  "deviceId": "0123456789abcdef0123456789abcdef",
  "revoked": true,
  "revokedAt": "2026-07-29T12:00:00Z",
  "note": "Existing SSH sessions must be disconnected separately."
}
```

Revocation empties the target's exact ports, invalidates its device credential,
removes only its managed public key, and retains a tombstone/audit record. The
final active administrator cannot revoke itself.

Deleting an `authorized_keys` entry blocks future SSH authentication but does
not terminate an already authenticated SSH session. Immediate incident response
must separately stop the verified Windows tunnel/session and revoke the device
in Tailscale when appropriate. The receipt's `note` makes this boundary
explicit.

## Failure, downgrade, and fallback behavior

- Unsupported protocol/config/manifest versions fail before mutation.
- Wrong peer node/login, missing role, invalid/expired/reused code, bad
  credential, oversized body, unexpected response member, redirect, and catalog
  mismatch fail closed. Rejecting unknown security-sensitive request members
  remains a release-hardening gate in the threat model.
- Agent downtime leaves existing exact SSH grants intact. Enrollment and sync
  report Agent unavailability instead of changing Tailscale, SSH, or service
  exposure.
- A non-admin sync uses `/v1/me`; a `403` from the administrator device list
  cannot be treated as permission to skip validation or expose other devices.
- A stale service revision returns a conflict, not a blind overwrite.
- The Agent rejects relay apply/delete with missing confirmation, a stale plan
  token, an unmanaged/conflicting unit, or a required dependency failure. The
  Windows workflow additionally refuses to submit create/update while its
  read-only probe reports an occupied/unknown listener or an unreachable/unknown
  exact target. After an uncertain result it discards the preview and requires
  sync plus a fresh plan.
- A crash during material writes replays the validated transaction journal to a
  consistent state.
- Pairing Protocol V1 remains available when the Agent is absent, stopped,
  incompatible, or intentionally disabled.
- A Pairing V1 token is never an Agent enrollment or device credential. Explicit
  fallback does not skip SSH host pinning, key isolation, or exact ports.
- `jkbridge-agentctl fallback` stops only the Agent user service. It preserves
  Agent state, credential verifiers, all SSH entries, installed helpers, and
  the retained `jkbridge-linux.py`; it does not stop an already authenticated
  Windows SSH session. Stop that tunnel separately when immediate cutoff is
  intended.
- The one-shot helper may reuse the same Windows public key only when its sole
  existing entry is the exact canonical, command-disabled, source-restricted,
  loopback-only Agent line for a valid device ID and matching fingerprint. It
  then atomically replaces that line with the exact one-shot port set.
  Unrestricted, personal, wildcard, duplicate, malformed, modified, or
  marker-only lookalike entries remain refused.
- Changing or expanding Linux remote ports in fallback mode requires a fresh
  one-shot pairing. An already established tunnel may continue until Windows
  stops it. After the Agent restarts, a later managed transaction can recognize
  the exact one-shot line for the same enrolled key and reconcile it back to
  that device's catalog-derived Agent entry; it never adopts a broad entry.

## Implemented versus unsupported

Implemented and tested in the vNext source tree:

- standard-library Python Linux Agent server, relay companion, and server CLI
  commands `bootstrap`, `recovery-code`, `status`, and `serve`;
- non-root `jkbridge-agentctl` source with bounded preflight, one-confirmation
  install, start, stop, status, and state-preserving fallback; it installs the
  Agent, relay companion, retained one-shot helper, environment, and user unit
  without invoking `sudo` or modifying `authorized_keys` during installation
  (`socat` and a systemd user instance are runtime prerequisites);
- fixed Agent API V1 endpoints and exact response validation;
- stable Tailscale node/login binding via `tailscale whois --json`, throttled
  best-effort `lastSeenAt`, and explicit same-stable-node endpoint rebind;
- per-device roles, credentials, Ed25519 keys, exact grants, listing, audited
  rename endpoint/client/controller/UI, and revocation receipt;
- service manifest/catalog revision, transactional authorization updates, and
  administrator preview/confirmation Windows catalog-write flow, with a shared
  one-shot/Agent SSH-writer lock;
- schema V4 Agent registration reference and Windows Credential Manager secret
  storage;
- Windows enrollment orchestration and one-shot fallback UI;
- explicit bounded Agent discovery wired through the controller and Setup UI;
- Windows foreground catalog synchronization for both roles, administrator
  sanitized device list/refresh, and explicit-confirmation revoke UI;
- administrator-issued, expiring one-use ordinary-device enrollment codes;
- strict explicit-relay plan/probe/apply/delete server/client/UI, generated
  loopback-only user units, state-bound confirmation tokens, affected-device
  grant convergence, backup/rollback, and companion/package tests;
- strict legacy-relay adoption plan/apply at the relay-manager layer only, with
  no Agent endpoint or Windows control yet;
- native non-secret Service Profile export/import with read-only diff preview,
  explicit confirmation, atomic save, and authorization-impact reporting; and
- Linux/.NET/security tests plus package-content checks.

Still unsupported and required before a stable release:

- live clean-Linux acceptance of the implemented `jkbridge-agentctl` install,
  upgrade, rollback, uninstall, start, stop, status, fallback, and repeated
  managed install across supported systemd-user environments, plus signed
  package provenance;
- clean Windows 11 plus Linux end-to-end installation, enrollment, reconnect,
  second-device isolation, revoke, and Agent-unavailable acceptance;
- signed and published Windows installer/Agent Release assets;
- background catalog sync, same-login auto-enrollment, approval of a
  replacement/old device by an existing Windows device, and credential
  rotation; and
- supported `socat` installation and systemd-user relay lifecycle, migration of
  currently deployed relays, and clean-machine DNS/TLS/create/update/delete/
  rollback acceptance.

Arbitrary route creation remains unsupported by design; the implemented relay
does not make wildcard, UDP, URI, CIDR, public-listener, or general proxy
targets part of V1.

The fixed V1 paths and payloads may be implemented without claiming these
distribution and acceptance requirements are complete. An incompatible future
change to authentication, authorization, required fields, or endpoint semantics
requires a new negotiated protocol version; it must not silently weaken V1.
