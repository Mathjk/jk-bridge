# JK Bridge Agent Threat Model

Status: implementation-aligned audit for the vNext **personal-tailnet preview**
(2026-07-29)
Applies to: `jkbridge-agent`, its Windows client, the service manifest, and
Agent-managed OpenSSH authorization and loopback relays
Does not replace: the one-shot pairing guarantees in `SECURITY.md`

This document deliberately separates two claims:

- **Current vNext** describes controls present in this source tree and covered
  by the Linux, .NET, security, or package tests. It is suitable for a
  single-user/private-tailnet preview when the administrator device is trusted.
- **Hardened release gate** describes controls still required before treating
  a remote administrative action as cryptographic step-up authorization or
  recommending the Agent to a multi-user/unattended deployment.

An unqualified `must` below is the target invariant. Where current vNext does
not yet meet it, the paragraph says so explicitly; a preview warning must not
be mistaken for an implemented control.

## 1. Security objective

The Agent removes repeated terminal work without turning JK Bridge into a
general-purpose proxy or a remote shell. A Windows device may access only the
Linux loopback ports explicitly granted to that device. Administrative actions
must be attributable, reversible, and unavailable to an ordinary enrolled
device. Current vNext grants those actions to a device whose persistent record
has role `admin`; the hardened release additionally requires a fresh step-up
factor for authority-expanding operations.

The following properties are invariants, not user-configurable conveniences:

1. The Agent listens on one current Tailscale address, never `0.0.0.0`, `::`, a
   LAN address, Tailscale Funnel, or a public reverse proxy.
2. Windows forwards listen on `127.0.0.1`; Linux services and managed relays
   listen on `127.0.0.1`.
3. Every Windows device has a distinct Ed25519 key and a distinct Agent
   credential. Copying a device identity is not a supported migration path.
4. Each OpenSSH entry contains `restrict`, command denial, Tailscale source
   restrictions, and one exact `permitopen="127.0.0.1:PORT"` per grant. Wildcard
   targets, wildcard ports, remote forwarding, SOCKS, shell, subnet routing,
   exit-node behavior, and UDP are outside the product boundary.
5. The Linux process runs as the ordinary SSH account whose managed
   `authorized_keys` it edits. It refuses root at runtime.
6. A local Windows port or browser path change cannot expand Linux authority.
   A new Linux remote port is unusable until the Agent commits a matching
   server-side grant.
7. Secrets never appear in URLs, command arguments, logs, diagnostics,
   manifests, export profiles, or `authorized_keys` comments.
8. Unknown or downgraded protocol/schema versions fail closed.

## 2. Assets and trust boundaries

### Assets

- Linux account integrity and the unrelated contents of `authorized_keys`.
- Per-device SSH public keys, Agent credential verifiers, bootstrap/recovery
  credential verifiers, and the Linux SSH host identity.
- The canonical service manifest, device/grant database, audit records, relay
  definitions, backups, and revision history.
- The confidentiality and integrity of traffic carried through a tunnel.
- The user's understanding of which Windows entry reaches which Linux service.

### Trust boundaries

```text
Windows UI / local user
        | localhost listener (untrusted local programs can connect)
Windows JK Bridge process and device secrets
        | authenticated request over the tailnet
Tailscale transport and LocalAPI identity assertion
        | Agent API on the Linux Tailscale address
jkbridge-agent ordinary Linux process
        | controlled state writes and exact OpenSSH entries
Linux loopback service or one explicit loopback relay
```

Tailscale supplies encrypted reachability and a useful node/user identity. It
does not make every peer in the tailnet an administrator, and membership in
`100.64.0.0/10` does not prove membership in this user's tailnet. OpenSSH host
pinning and per-device application credentials remain independent controls.

The model does not claim to protect against Linux root compromise, compromise
of the Agent's Linux account, compromise of the active Windows user, a
malicious loopback web service, or application-level vulnerabilities in a
bridged service. It must still limit damage from another tailnet node, leaked
enrollment material, malformed requests, filesystem races, partial writes,
operator error, and a stolen but subsequently revoked Windows device.

## 3. Threat actors and representative attacks

| Actor or failure | Desired attack | Required defense |
|---|---|---|
| Unrelated Internet/LAN host | Reach Agent or a bridged service | Exact Tailscale/loopback binding; fail closed when Tailscale is absent |
| Ordinary tailnet peer | Self-enroll, enumerate services, or alter grants | LocalAPI identity verification plus endpoint credential/role checks |
| Compromised ordinary device | Add ports, enroll another device, or create an SSRF-like relay | Server-side role enforcement; only `admin` can use management endpoints |
| Compromised admin device | Reuse its bearer to expand authority | Current preview: exact plan/confirmation where implemented; hardened release: independent expiring one-use step-up |
| Leaked bootstrap/recovery code | Replay or race legitimate enrollment | One-use atomic consumption, expiry, rate limits, no logging, optional strict approval |
| Network/request replay | Repeat a state-changing request | Current: one-use codes, expected revisions, state-bound relay plan and rate limits; hardened: nonce/timestamp, idempotency key, bounded replay cache |
| Malformed client | Resource exhaustion, parser ambiguity, injection | Size/count/time/concurrency limits; strict JSON; no shell command construction |
| Local process on Linux account | Tamper with state or links | File ownership/modes, `openat`/`O_NOFOLLOW`, atomic writes; this is defense in depth, not a boundary against account compromise |
| Interrupted update | Half-written device/grant/manifest state | One transaction, backup, fsync, atomic replace, deterministic reconciliation |
| Stolen Windows device | Continue using SSH or Agent API after revoke | Revoke both credentials and key; document existing-session limitation; revoke Tailscale node |
| Malicious/accidental old client | Downgrade security behavior | Exact version negotiation and minimum-version policy; no silent legacy fallback |

## 4. Tailscale identity assumptions

### Required checks

`JKBA-ID-01` The listener obtains the current local Tailscale IPv4 from the
Tailscale LocalAPI/status data and binds that exact address. Startup fails if
the address cannot be proven. A configured address is not silently replaced by
`0.0.0.0` or a LAN address.

`JKBA-ID-02` Source membership in `100.64.0.0/10` or
`fd7a:115c:a1e0::/48` is only an early rejection filter. Authorization uses the
Tailscale LocalAPI `WhoIs` result for the actual remote socket address. The
Agent verifies a stable node identifier, login identity, tailnet, and current
authorization state. A request header named `X-Tailscale-*` is never trusted
when the Agent accepts direct connections.

`JKBA-ID-03` If a future deployment runs behind Tailscale Serve, that mode has
a distinct configuration: the Agent accepts connections only from the trusted
local proxy socket and strips/ignores client-supplied identity headers. Direct
and proxy modes must not be mixed.

`JKBA-ID-04` WhoIs results have a short cache bounded by credential/session
lifetimes. Current vNext caches a positive address-to-node/login result for 30
seconds and still checks the device credential, stored node/login, role, and
revocation state on every API operation. Negative results are not converted
into anonymous access. Tailscale offline or LocalAPI unavailable means
management operations fail closed; existing SSH tunnels may continue under
their existing exact grants.

### Windows discovery and enrollment trust

Current vNext discovery is explicit and read-only. One button press takes a
bounded `tailscale status --json` snapshot, selects only known online peers,
and probes their exact Tailscale IPv4 at the fixed Agent port `45871`. It does
not scan a CIDR, send a device credential, enroll, or auto-select when several
candidates answer. Concurrency, target count, command output, per-peer time,
and total time are bounded. Health/bootstrap protocol values, endpoint IP,
stable node ID, MagicDNS name/suffix, and display strings are validated before
a candidate is shown. Ambiguous address or stable-node claims are discarded.

`/v1/health` and `/v1/bootstrap` do not require an application credential, but
the Linux handler still requires the socket source to pass Tailscale range and
`WhoIs` verification. They intentionally disclose only bounded Agent/server
identity, SSH host public key, catalog revision, and whether some enrollment
code is active; they do not disclose a code, device list, service catalog, or
credential. Tailnet peers can therefore discover that the Agent exists, which
is an accepted discovery tradeoff rather than anonymous Internet exposure.

A discovery candidate is a hint, not a trust anchor. Selecting it only fills
the endpoint; enrollment/management performs a fresh Tailscale status check
and compares the Agent's bootstrap identity before sending or using a
credential. The Windows Agent HTTP client also disables proxies, redirects,
cookies, and default credentials.

For an already enrolled device, current vNext also exposes a separate,
explicit endpoint-rebind action. It accepts only a discovered candidate whose
stable Tailscale node ID exactly matches the configured Linux node, then repeats
the normal endpoint trust check, verifies the unauthenticated bootstrap against
that trusted peer and pinned SSH server identity, then authenticates the
current-device identity/key and catalog-revision consistency before
atomically saving the new endpoint. It preserves the device ID, Credential
Manager reference, SSH key, and pinned server identity. A different stable node
must use enrollment; discovery selection alone never changes a registered
endpoint.

Residual race: `tailscale status` is a snapshot and the later HTTP connection
is not re-associated with a node by a post-connect LocalAPI `WhoIs`. A peer
address reassignment between those steps could make a new peer self-claim the
old snapshot's node ID/hostname. Discovery itself sends no secret and mutates
nothing, so its impact is low. During enrollment the same race could disclose
a one-time enrollment code to the wrong peer. The hardened release gate is a
fresh `WhoIs`/LocalAPI check for the exact connected address immediately
around bootstrap and immediately before sending an enrollment code or device
credential, with the selected stable node ID pinned into the enrollment attempt
and changes rejected.

### Auto-enrollment policy

"Same Tailscale login" is a convenience policy, not proof that a device is
safe. A compromised device belonging to the same user can satisfy it.

- Strict mode is the security default for general releases: an existing admin
  device, Linux-local confirmation, or a recovery credential approves a new
  device.
- Personal auto-enrollment may be offered only as an explicit, reversible
  policy for a single-user tailnet. It still requires an allowlisted login
  identity, a new device key, a short enrollment window, and rate limits.
- Tailnet tags and shared/ephemeral nodes require an explicit rule; they must
  not inherit administrator status from an email-string comparison.

### Deliberate HTTP-over-Tailscale transport profile

Agent API v1 uses plain HTTP on the exact Linux Tailscale IPv4. This is a
deliberate transport profile, not an opportunistic downgrade: Tailscale's
WireGuard tunnel supplies link confidentiality and integrity, while WhoIs,
the pinned server-node identity, and the per-device Agent credential supply
application authorization. The client must verify the target's stable
Tailscale node identity before trusting an SSH host key returned by the Agent.
Merely resolving a plausible MagicDNS name is insufficient bootstrap identity.
This is a Windows application-integration responsibility: it derives peer
identity from the local Tailscale client/status data for the exact configured
IP. A `nodeId` echoed or otherwise self-claimed by the Agent is not evidence.

The Windows client accepts only `http://<tailscale-ipv4>:<port>` for API v1.
The configured base endpoint has no userinfo, query, fragment, extra path, or
token. Requests disable system/user HTTP proxies, redirects, cookies, default
credentials, and ambient authentication; otherwise a proxy or redirect target
could receive the bearer credential. Both sides impose short timeouts and a
64-KiB response/request bound. The server returns `Cache-Control: no-store`
and `X-Content-Type-Options: nosniff`.

Residual risk: a bearer credential copied from a Windows endpoint, Linux
process, crash dump, or unsafe log can be replayed until it is rotated or
revoked. WireGuard does not mitigate endpoint compromise. Current vNext keeps
credentials device- and peer-bound, role-checked, revocable, rate-limited, and
out of URLs, but it does **not** yet use a cryptographic step-up factor for
administrative mutations. A later TLS-capable API version must be negotiated
explicitly and must never silently fall back to v1 HTTP after TLS or identity
validation fails.

### Current administrative preview boundary

The current administrative bearer is the real authorization credential.
Creating a device-only enrollment code, changing the service catalog, listing
or revoking devices, and relay management all require an active `admin` device
credential whose stored stable Tailscale node ID and login match `WhoIs`.
Ordinary device credentials cannot call them.

Relay apply/delete adds two safety controls: a literal `confirmed: true` and a
plan token recomputed while the Agent store lock is held. The token is an
unkeyed deterministic SHA-256 digest of the exact normalized operation, relay
plan (including the observed unit hash), catalog revision, service/grant
inputs, and current device/grant/revocation digest. It detects stale or changed
state and makes the Windows preview correspond to what is applied.

That token is **not a secret, bearer credential, signature, step-up factor,
one-use nonce, or proof of a human gesture**. It has no independent expiry or
actor binding, and an authenticated admin can obtain it and submit
`confirmed: true` with a raw API client. Therefore admin bearer + state-bound
plan token + explicit confirmation is sufficient only as a vNext
personal-tailnet **preview and stale-state/UX boundary**. It must not be
described as cryptographic step-up authorization.

For a hardened release, authority expansion needs an unpredictable
server-side challenge (or an HMAC/signature over an unpredictable nonce),
short expiry, single-use atomic consumption, actor/device binding, canonical
operation/body hash, and an independent approval source such as Linux-local
confirmation or a second administrator device. Failure or downgrade keeps the
mutation disabled; it never falls back to bearer-only authorization.

## 5. Bootstrap, recovery, and device credentials

### Credential classes

| Credential | Current vNext use | Storage rule |
|---|---|---|
| Initial bootstrap code | First administrator | Generated on Linux, expiring and one-use; verifier only on disk |
| Linux recovery code | Recover an admin or explicitly add a device | High entropy, role-bearing, expiring and one-use; verifier only on disk |
| Admin-issued enrollment code | Add one ordinary `device` | Returned once to the admin UI; expiring and one-use; verifier only on Linux |
| Future step-up challenge | Not implemented | Must be actor/operation-bound, expiring, unpredictable, and one-use |
| Device credential | Authenticate one enrolled installation | 256 random bits; Windows Credential Manager; verifier only on Linux |
| SSH private key | OpenSSH forwarding | Generated and retained on that Windows device only |

`JKBA-CRED-01` Credentials contain at least 128 bits of CSPRNG entropy; device
credentials use 256 bits. They are transported in `Authorization` or a signed
request, never a query string. Only a constant-time verifier is stored.

Current vNext stores a domain-separated SHA-256 verifier of each CSPRNG
256-bit code/credential. This does not make offline preimage guessing practical
for these generated values, but it is not a keyed verifier and permits
cross-copy correlation of an unchanged secret. A hardened release should store
`HMAC-SHA-256(server_secret, credential)` with a protected per-installation
server secret and per-record context. If the product ever uses a human-chosen
password or short PIN, it must instead use Argon2id with a unique salt and
documented memory/time parameters. Never truncate a human-entered secret and
call it a recovery code.

`JKBA-CRED-02` Current vNext consumes bootstrap, recovery, and admin-issued
enrollment codes in the same locked journaled transaction that creates the
device and installs the exact OpenSSH entry. Concurrent attempts cannot both
succeed. There is no enrollment idempotency key: if the success response is
lost after commit, the code remains consumed and the administrator must inspect
the device list, revoke any orphaned record if necessary, and issue a new code.
A hardened retry protocol must bind an idempotency key to the same node, public
key, and requested service revision without making the secret reusable.

`JKBA-CRED-03` Current vNext binds the resulting device credential to the
requesting peer's `WhoIs` node/login and to the submitted Ed25519 key at atomic
code consumption. The code itself is pre-bound only to a role and expiry, not
to a particular node, key, nonce, or service revision; whoever can present it
first from an accepted Tailscale peer can consume it. Admin-issued codes are
hard-coded to role `device`. The hardened approval flow pre-binds node, key,
requested role, nonce, expiry, and service revision so changing any field
invalidates approval.

`JKBA-CRED-04` The CLI prints a secret once without echoing it back in status.
It should warn about terminal scrollback. Secrets are not accepted on command
lines when stdin or a protected file descriptor is possible because process
arguments can be observed.

On Windows, the enrollment input is cleared before the network request and the
device credential is saved to Credential Manager with only an opaque reference
in configuration. An admin-issued enrollment code is held transiently in UI
memory until expiry and can be copied to the clipboard; it is never logged or
persisted by JK Bridge. The clipboard is not automatically cleared, so the UI
must retain its explicit warning and the operator should overwrite it after
use. Process memory, terminal scrollback, clipboard history, and a compromised
desktop session remain endpoint risks.

## 6. API controls: CSRF, replay, parsing, and availability

`JKBA-API-01` State changes use `POST`, `PUT`, or `DELETE`, require
`Content-Type: application/json`, and require an `Authorization` header. The
Agent does not authenticate with ambient browser cookies. CORS is disabled;
unexpected `Origin` values are rejected. GET/HEAD are read-only.

`JKBA-API-02` Current vNext authenticates state changes with a peer-bound device
Bearer credential and checks the administrator role where required. Catalog
updates use an expected revision; enrollment codes are expiring and one-use;
relay mutations additionally use the state-bound plan token and literal
confirmation described above. There is no general request nonce, timestamp,
signature, persisted replay cache, or idempotency key, and catalog changes,
enrollment-code issuance, revocation, and relays are presently bearer-authorized
administrative expansion. This is an explicit preview limitation.

The hardened release authenticates an expanding request with mutual TLS or an
HMAC/signature covering protocol version, method, canonical path, body hash,
device identifier, server-issued unpredictable nonce, and timestamp. Nonces
are single-use and expire quickly. The replay cache is bounded and persisted
when repeating a request after restart could expand authority. A bearer-only
fallback must never perform administrative expansion.

`JKBA-API-03` Current vNext uses exact versioned routes such as `/v1/...`,
rejects duplicate JSON object keys, duplicate critical headers, and unknown
request fields, requires UTF-8 JSON objects, limits bodies to 64 KiB, caps
devices/services/ports/string lengths, sets socket/client timeouts, and bounds
concurrent handlers. JSON nesting is bounded only indirectly by body size; a
hardened parser should enforce explicit depth and collection limits and return
a safe client error for depth exhaustion.

`JKBA-API-04` Current server limits all requests per source IP, relay requests
per source IP, and enrollment/enrollment-code requests per source plus a global
bucket. Buckets are in memory and do not separately key the stable node, device
identifier, or credential identifier. A hardened release adds those bounded
independent buckets (and an appropriate persisted cooldown for sensitive
recovery paths). Authentication/enrollment failures remain generic so callers
cannot enumerate device IDs, code validity, or registered login identities.

`JKBA-API-05` Current Windows Agent requests use an exact origin and disable
redirects and proxy environment/configuration; the Linux relay probe invokes a
fixed `socat` argument vector and performs no HTTP redirect. Request IDs are
not implemented. The hardened protocol makes safely retryable operations
idempotent and rejects reuse of an ID with a different body. LocalAPI, health,
and target probes never follow redirects or honor ambient proxy settings.

## 7. OpenSSH authorization

`JKBA-SSH-01` Build an entry only from a normalized Ed25519 key and a sorted,
deduplicated list of integers in `1..65535`. The only target form is
`permitopen="127.0.0.1:<port>"`.

The line must include at least:

```text
restrict,port-forwarding,command="/usr/bin/false",from="100.64.0.0/10,fd7a:115c:a1e0::/48",permitopen="127.0.0.1:<port>" ... ssh-ed25519 <key> JK Bridge managed <fingerprint>
```

`JKBA-SSH-02` Never produce `permitopen="127.0.0.1:*"`, `permitopen="*:..."`,
`permitlisten`, `no-port-forwarding` combined with an unreviewed override,
shell commands, environment options, `cert-authority`, or an unqualified
duplicate of a managed key. Refuse a key that already has an unmanaged or
ambiguous entry.

`JKBA-SSH-03` A device gets the union of the exact Linux remote ports for its
currently granted enabled services, not every port present in a client request.
The Agent computes authority from the committed server manifest. A local
Windows port or launch URL never enters `permitopen` computation.

`JKBA-SSH-04` Preserve comments and unrelated keys byte-for-byte where
possible. Serialize updates with a lock, create a `0600` backup in a `0700`
directory, fsync a same-directory temporary file, atomically replace, chmod
`0600`, and fsync the directory. Re-read and verify the final managed set before
committing the device database revision.

Current vNext Agent transactions acquire `state_dir/.agent.lock` and then the
same `~/.ssh/.jkbridge.lock` used by the one-shot helper. The one-shot helper
takes only the SSH lock, so there is one writer order and no lock cycle. Both
implementations reject a symlinked, non-regular, foreign, or hard-linked lock
before chmod/write. The tested fallback controller stops the Agent first; the
shared lock is still the safety net for accidental overlap and Agent recovery.

## 8. Non-root runtime and filesystem safety

`JKBA-FS-01` The network service refuses effective UID 0. Installation may use
temporary elevation, but the installed service is a systemd **user** service
for the SSH account. It has `UMask=0077`, `NoNewPrivileges=yes`, a private temp
directory, restricted address families, no new device access, and write access
only to its state directory, managed user unit directory (when relay support is
enabled), and `.ssh` files it owns.

`JKBA-FS-02` The Agent verifies every managed directory/file is owned by the
current UID, is a regular expected type, and is not group/world writable. It
rejects symlinks and unexpected hard links. Checking with `Path.is_symlink()`
and later opening by pathname is not sufficient: open from a trusted directory
file descriptor with `O_NOFOLLOW`/`openat`, then validate `fstat` before I/O.

`JKBA-FS-03` Remote input never becomes a filesystem path. Service/device IDs
use a tight ASCII slug grammar and server-generated IDs. Reject `/`, `\\`, `..`,
NUL, Unicode confusables, leading dots, overlong names, and normalization
changes. Relay unit names are derived from the validated ID, not supplied as a
path.

`JKBA-FS-04` Never invoke a shell to build a relay, probe, `systemctl`, or
Tailscale command. Pass a fixed executable and argument vector. Validate target
host/port separately and treat DNS rebinding/SSRF as an administrative routing
decision, not ordinary device data.

### Current relay target and unit boundary

Current vNext accepts one ASCII DNS name or exact IPv4 address and one integer
target port, plus a distinct exact loopback relay port. It rejects URL syntax,
userinfo, wildcard/CIDR targets, IPv6, unspecified/multicast/broadcast
addresses, unsafe slugs/paths, and a same-port loopback self-loop. `scheme` and
`openPath` are validated presentation metadata for the Windows loopback URL;
the relay itself is raw TCP, so it neither interprets HTTP nor follows a
redirect. An application/browser reached through the tunnel can still follow
its own redirect outside JK Bridge, which the preview must disclose.

Raw TCP also does not rewrite HTTP `Host`, TLS SNI, certificates, redirects, or
cookie domains. Opening `https://127.0.0.1:<windowsPort>` can therefore fail
certificate/SNI validation when the upstream expects its DNS name, and an
upstream absolute redirect can leave the loopback route. JK Bridge must not
disable certificate verification or claim that a successful TCP probe proves
HTTP login correctness; the UI caveat and an application-level test remain
necessary.

The generated managed unit is named only as
`jkbridge-relay-<validated-slug>.service` and runs `socat` with
`TCP4-LISTEN:<relayPort>,bind=127.0.0.1,...` and one separately validated
`TCP`/`TCP4` target argument. No shell is involved. Creation, update, and
deletion use an observed unit digest, exact managed marker/metadata, backups,
atomic replacement, rollback, and injected `systemctl --user` argument arrays.
An unmanaged, malformed, symlinked, or unexpectedly changed unit blocks the
operation instead of being adopted or removed.

The relay port is an indirection capability: changing a target while keeping
the slug and relay port unchanged redirects every device (and any surviving
SSH session) that can open that port. Current vNext therefore marks every
active device holding the relay service as affected on a target/unit update,
even when `addedRemotePorts` and `removedRemotePorts` are empty. The exact old
and new target remains part of the state-bound preview and confirmation.

This validation prevents injection and unintended listener widening; it does
not turn a relay into a non-SSRF capability. Private, loopback (other than the
exact self-loop), link-local, public IPv4, and DNS targets are currently
permitted by design because the feature exists specifically to reach a target
visible from Linux. A compromised admin credential can therefore ask Linux to
connect to an otherwise inaccessible TCP service. DNS is resolved during the
probe and again by `socat` connections; the plan binds the hostname string,
not the resolved address set, so DNS rebinding or later resolution drift is a
residual risk. Hardened policy should display resolved addresses and address
classes, optionally pin an approved address set, and deny loopback/link-local
or other sensitive classes unless separately enabled.

The packaged Agent runs as a systemd user service without root, capabilities,
or new privileges and with a read-only home/system view. It needs write access
to Agent state, `.ssh`, and the user systemd unit area. The present sandbox
grants the process write access to the entire `%h/.config/systemd/user`
directory even though the application logic restricts names to the managed
prefix. Process compromise would bypass that application-level name rule. A
hardened package should use a narrower dedicated helper/directory or another
design that does not give the network-facing process general user-unit write
authority.

### Existing relay adoption boundary

The relay library contains a strict, local adoption primitive for converting
one exact legacy Skill-created unit into the canonical Agent-managed form. It
does not search directories: the caller supplies one exact path under the user
unit directory, and only the canonical or legacy JK Bridge name for the
validated slug is accepted. The parser accepts a deliberately small systemd
subset and requires an equivalent loopback listener, target, `socat` path,
service type, restart policy, provenance marker, ownership and mode. Symlinks,
drop-ins, shell/environment/extra-exec directives, widened listeners, target
changes, non-service units, and unfamiliar markers block adoption. A read-only
full diff, observed digest, affected-device set, explicit confirmation, and
state-binding token are rechecked before atomic replacement; failure restores
the exact legacy unit and attempts to restore its running state.

This is currently a tested Linux-library primitive only. It is not dispatched
by the Agent API and has no Windows workflow, so current remote administrators
cannot invoke it. If exposed later, the Agent must derive the exact candidate
path and affected-device set from locked server state, require the same admin
and hardened step-up boundary as relay mutation, include adoption in the
journal/audit model, and never turn the primitive into directory-wide discovery.

### Linux lifecycle-controller boundary

`jkbridge-agentctl` is an AI-independent, non-root lifecycle controller. Its
current `preflight`, `install`, `start`, `stop`, `status`, and `fallback`
commands validate the explicit home, prerequisites, source/destination
ownership and type, private modes, and a SHA-256 manifest covering the installed
Agent, relay helper, one-shot helper, controller, environment, and systemd user
unit. Lifecycle commands refuse a missing, replaced, symlinked, foreign, or
mode-broadened payload before invoking `systemctl`; fallback stops the Agent
before handing control to the retained one-shot helper. It stores only the
Agent port in its environment file and does not invoke `sudo` or install system
packages.

Installation is atomic per file but not across the complete payload. An
interruption can leave a partial installation which lifecycle verification
rejects; rerunning the same reviewed install converges payload files. If only
one private bootstrap state file exists, the controller refuses to guess or
overwrite it and requires local inspection. Upgrade, rollback, and uninstall
are not yet supported. Existing-file recognition during install
still begins with an allowlisted marker check before replacing and recording
the new digest, so installation must continue to require a reviewed delivery
directory and explicit confirmation; it is not a general package-manager trust
mechanism.

## 9. Manifest integrity and route UX

`JKBA-MAN-01` The manifest has an exact schema version, monotonically
increasing revision, stable server-generated service IDs, and strict field
validation. It is written with backup/fsync/atomic replace. A stored MAC can
detect accidental/offline tampering, but a MAC key owned by the same Linux user
does not protect against compromise of that user; documentation must not claim
otherwise.

`JKBA-MAN-02` Current relay updates return a normalized port/device/unit diff
before commit, and Windows Service Profile import has its own read-only diff.
The direct catalog-update API uses an expected revision but has no independent
server-issued preview token. Windows does construct an explicit read-only
service/port/affected-device preview, invalidates it when the local proposal
changes, requires a confirmation checkbox, and submits its expected revision.
Material Agent updates use a journaled transaction for manifest, device/grant
state, and managed authorization; relay application adds unit backup/rollback
and deletion restricts grants before removing the unit. After a successful
catalog PUT, Windows refetches authoritative Agent state and atomically saves
the refreshed catalog and exact authorization. A Linux commit can still
succeed immediately before Windows persistence or refresh fails; the UI reports
that split outcome and requires an explicit resync, while stale local
authorization remains fail closed. A hardened generic administration API
should return one normalized, state-bound diff for all manifest/grant/relay
changes and ensure rollback never revives a device revoked after the target
revision.

`JKBA-MAN-03` Clients pin a minimum Agent API version. A new Agent does not
interpret an old/unknown manifest under weaker rules. Migration makes a backup,
validates the complete result, and is one-way only after successful commit.
Fallback to one-shot pairing is an explicit user action, not an automatic
downgrade after authentication failure.

`JKBA-ROUTE-01` Simple-mode URLs are generated from `http|https`, the literal
host `127.0.0.1`, the Windows local port, and a validated path. Reject
`javascript:`, `data:`, `file:`, credentials, UNC paths, non-loopback hosts,
control characters, and ambiguous encoded authority such as `%2f%2fhost`.
Custom URLs are an advanced, clearly marked feature and still cannot expand SSH
authority.

`JKBA-ROUTE-02` The UI displays the two ports separately:

```text
Windows 127.0.0.1:<localPort> -> Linux 127.0.0.1:<remotePort>
```

Changing only `localPort`, scheme, path, query, or fragment does not require a
Linux grant. Adding/changing an enabled `remotePort` is blocked until a new
Agent receipt includes it. A local conflict is explained and never silently
changes a bookmarked URL.

`JKBA-ROUTE-03` Ordinary devices may read services and update their local
preferences; they cannot introduce an arbitrary target or relay. Relay creation
is currently an administrator-bearer operation with a preview, direct
Linux-side probe, state-bound token, explicit confirmation, exact loopback
listener, and rollback. The hardened release adds the independent step-up
described in `JKBA-API-02`. Never accept an arbitrary URL and turn it into an
ordinary-device proxy.

## 10. Revocation, rollback, and recovery

`JKBA-REV-01` Current vNext revocation is one locked journaled transaction that
clears the Agent credential verifier and permitted ports, writes a `revokedAt`
tombstone, and removes exactly the matching managed SSH key. It never removes
another key with a similar comment or fingerprint prefix. The response and UI
identify the target and warn about existing sessions. It does not yet append a
durable actor-attributed revocation audit event or advance a per-device
revision; those are hardened-release requirements.

Deleting an authorized key stops new SSH authentication but does not terminate
an already authenticated SSH session. The UI must say this explicitly. Incident
response therefore also revokes the Windows node in Tailscale and, when
immediate cutoff is required, terminates verified sessions for that device or
restarts the narrowly scoped SSH access path. Do not kill all user processes as
a convenience.

`JKBA-REV-02` API requests from a revoked credential fail even if a stale cache
or old manifest exists. Re-enrollment creates a fresh device ID, credential,
key, and approval; a rollback cannot clear the revocation tombstone.

`JKBA-RB-01` Current state/manifest/`authorized_keys` transactions use a
validated journal, and relay unit replacement uses backups and rollback.
Request IDs and a user-facing generic restore operation are not implemented.
On startup, reconciliation applies the validated pending transaction rather
than guessing by granting the union of old and new ports. A hardened restore
operation shows a diff, requires local/admin approval, and never discards a
later revocation tombstone.

`JKBA-RB-02` Stopping or uninstalling the Agent does not delete unrelated SSH
entries, Windows keys, or working exact grants. The one-shot helper remains an
explicit recovery path. Importing existing entries requires exact recognition;
the Agent never adopts an entry merely because its comment says "JK Bridge".

Current fallback interoperability is bidirectional and fail closed. The Agent
can strictly adopt the restricted one-shot
`JK Bridge managed <fingerprint>` line. Conversely, the one-shot helper can
replace exactly one same-key, fully restricted Agent
`JK Bridge agent <32-hex-deviceId> <fingerprint>` line with the requested
one-shot exact-port line. It makes a backup and preserves unrelated entries.
An unrestricted entry, wildcard grant, changed option prefix, malformed device
ID/fingerprint, duplicate same-key entry, or unrelated marker remains an
external-key conflict and is not changed. If the Agent later resumes, its own
strict reconciliation can restore the canonical Agent line from committed
device/grant state. The shared SSH-writer lock serializes any accidental
overlap, while `jkbridge-agentctl fallback` stops the Agent before instructing
the operator to start one-shot pairing.

## 11. Logging and redaction

`JKBA-LOG-01` A hardened structured audit record contains event type, time,
request ID, actor device ID, shortened public-key fingerprint, affected service
IDs, revision, outcome, and a safe error code. It excludes:

- `Authorization`, Cookie, signatures, nonces, bootstrap/recovery/device tokens;
- pairing codes and URLs containing query secrets;
- SSH private/public key bodies (a shortened fingerprint is sufficient);
- request/response bodies, browser page content, and health response bodies;
- full Tailscale status/WhoIs JSON, environment variables, and proxy settings;
- full home paths, personal login email, tailnet name, or MagicDNS hostname in
  exported diagnostics unless the user explicitly opts in.

`JKBA-LOG-02` Redaction occurs before formatting and before exception objects
are logged. Header names are allowlisted instead of blacklisting known secrets.
Logs use `0600`, rotate with bounded retention, and diagnostic export applies a
second redaction pass. Authentication failure messages do not reveal whether a
device or token exists.

Current vNext's HTTP handler suppresses request logging entirely, never logs
headers/bodies, emits only the exception type for an unexpected server error,
and returns a generic error body. Windows credential wrappers redact string
conversion, secrets are never placed in URLs, enrollment input is cleared
before use, and admin-code logging records only its expiry. Package inspection
rejects known runtime-state, private-key, pairing/recovery/device material, and
symlink patterns. Durable Agent audit events are currently limited to bounded,
actor-attributed device renames; revocation, enrollment-code, catalog, and relay
mutations do not yet have the full audit schema above. These controls reduce
accidental disclosure; they cannot erase secrets already copied into terminal
scrollback, clipboard history, a process dump, or another compromised endpoint
facility.

The Windows diagnostic-bundle implementation is wired to one explicit
Diagnostics-page export action and a user-selected destination. It exports only
a fixed three-entry ZIP (bounded manifest, anonymous service phases, and
redacted bounded log events), never serializes configuration or a tunnel
snapshot directly, applies both seeded-value and pattern redaction, and rejects
symbolic-link/reparse-point destinations around an atomic replacement.
Redaction remains defense in depth: an arbitrary short secret without a known
field pattern could evade heuristics if an upstream logger had already accepted
it, and destination pathname checks retain a check-to-replace TOCTOU window.
The UI accurately describes the allowlist and reports cancellation/failure
without logging the destination or archive contents. Final release artifacts
still require an independent seeded-secret scan.

The fixed-endpoint GitHub release checker is wired only to a manual Settings
button; initialization does not contact GitHub. It performs a bounded metadata
request to the official repository, disables proxies, redirects, cookies,
ambient credentials and decompression, validates stable semantic-version
metadata and the exact official release-page URL, and then displays that URL as
text. It neither downloads, opens, nor launches an installer. The UI discloses
the GitHub contact and no-auto-install boundary. Signed or hash-verified
installer delivery remains a separate release gate.

## 12. Verification and release gates

The security scaffold under `tests/security/` must run entirely in temporary
directories and must never reference the live `$HOME`, live Tailscale socket,
systemd user instance, or `/etc/ssh`. An Agent that has not yet implemented its
test interface is skipped clearly; a present but insecure implementation fails.

Minimum release gates:

1. Listener refuses wildcard/LAN binding and root runtime.
2. Enrollment rejects wrong Tailscale identity, bad/expired/reused secrets,
   replay, oversized bodies, and brute-force attempts.
3. Credential storage contains only verifiers, not submitted plaintext.
4. Every generated SSH line has exact loopback `permitopen` and no shell.
5. Symlink, traversal, hard-link, owner, duplicate-key, and concurrent-write
   tests fail safely without touching the target.
6. Manifest tampering, version downgrade, partial commit, and stale-revision
   updates fail closed and recover the last consistent revision.
7. Revoking one of two devices affects only that device and does not reappear
   after restart/rollback.
8. Logs and error responses pass a seeded-secret redaction scan.
9. Windows route tests reject non-loopback launch URLs and distinguish local
   from remote port changes.
10. Admin-issued enrollment codes can create only `device` records, are absent
    from persistent files/output after issuance, expire, and cannot be reused.
11. Relay plan/probe is read-only; apply/delete rejects an ordinary device,
    missing confirmation, stale state/unit digests, unsafe target syntax, and
    unmanaged units. The generated listener remains exact IPv4 loopback.
12. Discovery is explicit, bounded to exact online status peers, credential-free,
    ambiguous-result preserving, and followed by a fresh enrollment trust check.
13. Package inspection proves a non-root user unit, no wildcard listener or
    literal secret, no runtime state/private key, and no delivery symlink.
14. One-shot fallback converts only one exact same-key Agent-managed restricted
    entry, preserves unrelated lines, and rejects malformed/wildcard/duplicate
    entries without mutation; Agent reconciliation accepts only its own exact
    state-derived replacement. Agent and helper share one no-follow,
    single-link SSH-writer lock, and a held helper lock blocks an Agent
    transaction rather than allowing last-writer-wins drift.
15. Endpoint rebinding preserves the device identity and credential, accepts
    only the configured stable Tailscale node, verifies bootstrap against the
    trusted peer and pinned server, authenticates the current device, and
    leaves the old configuration intact on failure or cancellation.
16. Lifecycle commands verify the installed manifest, owner, regular-file
    type, no-symlink path and exact private modes before any `systemctl` action;
    tampered payloads fail without touching the live user manager.
17. A future relay-adoption API remains disabled until it preserves the local
    primitive's exact-name/parser/digest/diff/affected-device checks and adds
    Agent-locked authorization, audit, and hardened step-up.
18. Diagnostic export and release checking occur only after an explicit user
    action. Export preserves its fixed entry/size/redaction allowlist; update
    checking contacts only the fixed official metadata endpoint and never
    downloads, opens, or executes release content.

## 13. Current audit result and priority findings

Within the explicitly scoped single-user/private-tailnet preview, the current
tree has no known P0 that widens an ordinary device beyond its exact grants:
production requests use Tailscale `WhoIs`, device credentials are node/login
bound, roles are enforced server-side, code consumption and authorization
writes are transactional, relay listeners/grants are exact loopback, and
unsafe identity/protocol/path/unit inputs fail closed.

That conclusion is not a stable-release approval. The following items are open:

- **P0 before a general/multi-user/unattended release — Add real step-up.** The
  admin bearer authorizes catalog expansion, enrollment-code creation,
  revocation, and relay operations. A relay plan token is a deterministic
  stale-state digest, not independent authorization. Add the expiring,
  one-use, actor/operation-bound approval defined above and never downgrade to
  bearer-only mutation if it fails.
- **P1 — Close the Windows status-to-connection identity race.** Re-associate
  the exact connected Agent address using Tailscale LocalAPI/`WhoIs` around
  authenticated bootstrap/enrollment instead of relying only on a prior
  status snapshot and server self-claims. Explicit stable-node endpoint rebind
  is implemented, but it deliberately inherits this transport-identity race.
- **P1 — Constrain relay SSRF/DNS behavior.** Display resolved address classes,
  define a policy for loopback/link-local/private destinations, and optionally
  pin resolution so a hostname cannot silently move after preview.
- **P1 — Narrow relay unit write authority.** The user-service sandbox allows
  the Agent to write the whole user systemd unit directory; isolate the
  managed-unit operation from the network-facing process.
- **P1 — Use descriptor-based no-follow file operations throughout.** Current
  lexical/lstat checks reject known symlinks, but a remaining check-then-open
  pathname sequence can lose a concurrent TOCTOU race. Prefer trusted dirfds,
  `openat`, `O_NOFOLLOW`, `fstat`, and safe rename/unlink primitives.
- **P1 — Define active-session revocation behavior.** Current revocation removes
  the credential verifier, exact grant, and future SSH authentication, and its
  response/UI warns that an already authenticated tunnel survives. Add an
  explicit, narrowly targeted session-disconnect mechanism if immediate cutoff
  is a promised product behavior; Tailscale-node revocation remains an
  operator incident-response step.
- **P1 — Complete durable administrative audit.** Only device rename currently
  writes an actor-attributed bounded audit event. Add request IDs and redacted
  actor/target/outcome events for enrollment-code issuance, catalog mutation,
  revocation, and relay apply/delete so incident reconstruction does not depend
  on transient Windows logs.
- **P1 before exposing legacy relay adoption remotely — Bind it into the Agent
  transaction.** The strict local parser/diff/rollback primitive is implemented
  but intentionally has no API/UI route. Any remote workflow must derive the
  exact candidate and affected devices from locked state and add role, step-up,
  journal, and durable audit enforcement before dispatching it.
- **P2 — Key credential verifiers.** Domain-separated SHA-256 is adequate
  against guessing of the current 256-bit generated secrets, but a protected
  per-installation HMAC key removes correlation and is the preferred hardened
  design.
- **P2 — Do not overstate `lastSeenAt`.** Authenticated read requests refresh it
  at most once every five minutes through a best-effort atomic metadata write;
  write failure does not fail the read. It is therefore bounded activity
  evidence, not a real-time presence signal, and stale data must not drive
  incident-response decisions by itself.
- **P2 — Keep support actions narrow and independently scan final artifacts.**
  Diagnostic export and official-release metadata checking are now explicit
  user-visible actions. Preserve the fixed archive allowlist, manual-only
  network contact, and no-download/no-open/no-execute boundary, and scan final
  bundles with seeded secrets before claiming release completion.
- **P2 — Complete lifecycle packaging.** `agentctl` now authenticates installed
  payloads with its local SHA-256 manifest before lifecycle actions, but a
  multi-file install is only convergent and supported automatic
  upgrade/rollback/uninstall is absent. Preserve fail-closed lifecycle checks
  while adding those operations and stronger package provenance/signing.
- **P2 — Make redaction and packaging scans exhaustive.** Continue seeding every
  credential class and assert markers are absent from logs, diagnostics, CLI,
  errors, profiles, installers, and release archives.

The current preview label is a deployment boundary, not a compensating
security control. Any distribution that removes the personal-tailnet/admin
trust assumption must first satisfy the P0 step-up gate or keep the affected
administrative endpoints disabled/local-only.
