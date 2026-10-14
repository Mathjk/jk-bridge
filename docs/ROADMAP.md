# Roadmap

> Released milestones describe shipped behavior. Everything under **vNext** is
> approved direction that remains in development and may move between releases
> after security, migration, and Windows acceptance testing.
>
> **Version boundary:** v2.2.1 is the latest stable release.
> `2.2.0-preview.1` through `2.2.0-preview.6` remain historical prereleases.
> Stable v2.2.1 covers the desktop, one-shot pairing, service-profile, local
> discovery, diagnostics, installer, controllable updater, and setup flows described below; the

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

## V2.2 — desktop usability and controlled updates (released)

- Publish an unsigned per-user Windows installer while retaining the verified
  portable ZIP.
- Discover active Windows loopback web services without disk or unopened-port
  scanning, then explicitly save selected results.
- Ship simple/advanced service editing, templates, local-port conflict
  guidance, and non-secret Service Profile export/preview/confirmed-import.
- Provide categorized navigation, concise page guidance, and a persistent
  four-step first-run tutorial that can be reopened later.
- Ship manual fixed-official-Release checking plus explicit, SHA-256-verified
  download and separately confirmed visible installation, with progress and
  cancellation but no background download or silent installation.
- Use the Windows/.NET system proxy for GitHub metadata/assets without relaxing
  exact-host, redirect, size, or checksum boundaries.
- Export allow-listed, redacted diagnostic ZIPs that exclude keys, device
  credentials, identities, service details, and pairing codes.
- Preview System/Light/Dark appearance choices immediately and persist only on
  explicit settings save.

The package also carries separately gated migration and
relay components. Their lifecycle remains advanced/experimental until the vNext
exit criteria below pass; one-shot Linux pairing is the stable default and
recovery path.

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
and backup path exist. The flow preserves device/server state and cannot
manufacture Linux authorization. Exit still requires clean-machine Windows
acceptance; implemented source alone is not a released migration feature.

## Later candidates

- Multiple named Linux profiles.
- More health-check templates and service ordering.
- Dedicated Linux service-account guidance and optional administrator-reviewed sshd hardening.
- Carefully scoped local hostname aliases for services whose HTTPS identity cannot use `127.0.0.1`.

Security restrictions are release invariants: Windows listeners and managed
Linux relays remain loopback-only; management is Tailscale-only; SSH identity is
pinned; every device has its own key; authorizations name exact ports; and no
normal runtime feature depends on AI.
