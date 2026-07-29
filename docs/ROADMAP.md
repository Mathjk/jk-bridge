# Roadmap

> Released milestones describe shipped behavior. Everything under **vNext** is
> approved direction that remains in development and may move between releases
> after security, migration, and Windows acceptance testing.
>
> **Version boundary:** v2.1.0 is the latest stable release. The current App and
> installer source report `v2.2.0 development`; v2.2.0 has not been released,
> and the implemented vNext source below is not thereby stable or supported.

## V2.0 — portable public release (released)

- One-shot Linux pairing and Windows one-click setup.
- Generic, privacy-safe defaults and schema migration.
- Self-contained Windows x64 ZIP containing the Linux helper and bilingual documentation.
- Automated tests, privacy gate, checksums, CI, and release workflow.

## V2.1 — route and authorization integrity (released)

- One validated route definition for SSH, browser launch, health checks, and pairing authorization.
- Persisted schema V3 authorization receipts with proactive drift detection and one-time legacy re-pairing.
- TCP health mode for self-signed HTTPS and non-HTTP loopback services.
- Browser launch separated from advisory HTTP/TLS health results.
- Investment News default service and existing-installation migration helper.

## V2.2 — current development line, not released

- Complete clean-machine Windows acceptance on supported Windows 11 builds.
- Authenticode code signing and timestamping.
- Optional installer while retaining the portable ZIP.
- Manual fixed-official-Release checking without background download or silent
  installation (implemented source; acceptance pending).
- Allow-listed, redacted diagnostic ZIP export that excludes keys, device
  credentials, identities, service details, and pairing codes (implemented
  source; acceptance pending).

Distribution hardening does not by itself introduce the Linux Agent or claim
schema V4 compatibility. The v2.2.0 development tree also contains the
separately gated vNext Agent, schema V4, migration, and relay source described
below; those features remain in-development until their own exit criteria pass.

## vNext phase A — route UX and schema V4 (implemented source; acceptance pending)

- Add simple and advanced service-editing modes.
- Generate the Windows browser URL from scheme, Windows local port, and launch path.
- Preview `Windows 127.0.0.1:<local> -> Linux 127.0.0.1:<remote>` directly in the UI.
- Detect Windows local-port conflicts and suggest, but never silently select, a replacement.
- Keep health path separate from launch path while providing safe defaults.
- Add Overleaf, Sub2API, generic HTTP, and generic HTTPS templates.
- Migrate schema V3 to V4 without losing custom browser URLs or observed authorization.
- Preserve the rule that local-port/path changes do not expand Linux permission, while remote-port changes require authorization.

The editor, URL derivation, templates, and V3-to-V4 migration exist in source.
Exit still requires the complete Windows acceptance matrix, including local-port
conflicts and migration recovery on a real prior installation.

## vNext phase B — non-secret service migration (implemented source; acceptance pending)

- Define a versioned JK Bridge Service Profile format.
- Export/import service definitions and recommended local ports without private keys, server credentials, pairing tokens, reusable device credentials, or authorization receipts.
- Preview additions, changes, conflicts, and Linux ports requiring authorization before import.
- Apply validated imports atomically with a recoverable backup.
- Support extracting only non-secret service data from a V2.1 configuration.

The bounded serializer, validator, change analysis, native file-picker
export/import UI, tests, read-only preview, explicit confirmation, atomic save,
and backup path exist. The flow preserves device/server/Agent state and cannot
manufacture Linux authorization. Exit still requires clean-machine Windows
acceptance; implemented source alone is not a released migration feature.

## vNext phase C — optional Linux Agent MVP (implemented source; deployment unsupported)

- Provide a deterministic, no-AI Agent that runs as the ordinary Linux SSH user under systemd.
- Bind the management listener only to the Linux Tailscale interface.
- Maintain a versioned Linux service manifest as the source of truth for Linux destinations.
- Enroll, label, list, and revoke independent Windows device identities.
- Derive and atomically install exact per-device `permitopen` authorization.
- Reuse tested ownership, locking, backup, symlink-rejection, and atomic-write controls from the one-shot helper.
- Authenticate management operations using `tailscale whois --json` stable node/login identity plus an independently revocable device credential or one-use Linux enrollment code; source IP alone is insufficient.
- Add limits for message size, rate, concurrency, and failure attempts.
- Keep `jkbridge-linux.py pair/status/remove` as a supported fallback.

The Python server, fixed `/v1` endpoints, manifest and device transactions,
Windows bounded client, per-device credentials, listing/rename/revocation,
tests, systemd user-unit template, and non-root
`jkbridge-agentctl preflight/install/start/stop/status/fallback` source exist. Request and response bodies are limited to
64 KiB. Agent device records carry stable Tailscale node and login fields, and
revocation returns a fixed receipt while warning that existing SSH sessions
must be disconnected separately.

The Agent and one-shot helper also share the SSH-writer lock, and strict
same-key fallback conversion/reconciliation is tested. Exit still requires live
acceptance of the implemented lifecycle plus an explicit supported
upgrade/rollback/uninstall flow (those commands do not yet exist),
clean-machine end-to-end enrollment and independent-revocation acceptance, and
stable Release assets. The frozen development contract is in
[AGENT_PROTOCOL.md](AGENT_PROTOCOL.md).

## vNext phase D — Windows no-terminal enrollment and sync (implemented source; acceptance pending)

- Detect Tailscale and Windows OpenSSH prerequisites in the first-run UI.
- Generate and protect a unique Ed25519 identity on each Windows device.
- Select a trusted exact Tailscale Agent endpoint, cross-check its stable node and MagicDNS identity, and enroll with a one-use Linux-issued code.
- Manually synchronize the service manifest and exact current-device grant while
  preserving same-ID Windows local-port overrides.
- Let non-admin devices synchronize their own state without exposing the fleet
  device list.
- Add an administrator Devices UI for sanitized list/refresh and
  explicit-confirmation revoke.
- Let an enrolled administrator generate a short-lived, one-use ordinary-device
  enrollment code in Windows; keep administrator recovery Linux-local.
- Rename device labels without changing identity or grants (implemented).
- Add an explicit approval flow for replacement/old devices (planned).
- Add an opt-in, well-bounded background synchronization design after the manual
  flow is accepted.
- Show a clear one-shot-pairing fallback when the Agent is unavailable or disabled.
- Complete clean Windows 11 acceptance without requiring PowerShell or CMD for normal enrollment.

Exit criteria: after one-time Linux Agent preparation, the normal new-device
flow is `Tailscale login -> install JK Bridge -> Add this computer -> connect`.
The enrollment screen/client, bounded explicit discovery, same-stable-node
endpoint rebind, foreground catalog sync, administrator catalog
preview/confirm/write, device list/rename/revoke and ordinary-device code
controls, and Service Profile transfer exist in source. Old-device approval,
background sync, supported Linux lifecycle, and clean Windows/Linux acceptance
remain open.

## vNext phase E — managed explicit relays (implemented source; acceptance pending)

- Support direct Linux loopback services and one explicit target per managed loopback relay.
- Plan and probe before mutation; show final target, Linux relay port, Windows local port, protocol, and launch path.
- Bind every relay to `127.0.0.1` and refuse to overwrite an unrecognized unit.
- Apply relay creation, manifest update, per-device exact authorization, and Windows synchronization as a recoverable transaction.
- Require explicit confirmation before removal, back up state, and contract every affected device's grant afterward.
- Keep arbitrary proxies, UDP, wildcard targets, exit nodes, and subnet routes unsupported.

The Linux relay companion, strict `/v1/relays` plan/probe/apply/delete endpoints,
Windows advanced UI, state-bound plan tokens, explicit confirmation, managed
unit backup/rollback, catalog convergence, and exact affected-device grant
updates exist in source and tests. The package contract includes the sibling
relay script, but does not install `socat` or enable/test the systemd user
instance.

A strict legacy Skill-unit adoption plan/apply primitive also exists and is
tested at the relay-manager layer. It is not exposed through Agent API or the
Windows UI, so migration integration remains phase-F work.

Exit still requires supported dependency/lifecycle installation, clean Linux
systemd-user create/update/delete/rollback acceptance, clean Windows UI and
post-apply sync acceptance, DNS/TLS edge-case testing, and migration of the
currently deployed explicit relays without address or port changes.

## vNext phase F — deployment migration and release (planned)

- Inventory the deployed Windows services, Linux listeners, managed relays, and exact authorization before mutation.
- Import the current service set into the manifest while keeping existing browser URLs stable.
- Run the existing Windows through the Agent while retaining the one-shot fallback.
- Test enrollment and revocation with a second clean Windows device.
- Ship installer/portable artifacts, hashes, protocol compatibility tests, and rollback instructions.
- Stop or remove legacy management only after an observation period and explicit approval.

## Later candidates

- Multiple named Linux profiles.
- More health-check templates and service ordering.
- Dedicated Linux service-account guidance and optional administrator-reviewed sshd hardening.
- Carefully scoped local hostname aliases for services whose HTTPS identity cannot use `127.0.0.1`.

Security restrictions are release invariants: Windows listeners and managed
Linux relays remain loopback-only; management is Tailscale-only; SSH identity is
pinned; every device has its own key; authorizations name exact ports; and no
normal runtime feature depends on AI.
