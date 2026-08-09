# Changelog

All notable changes are documented here. The project follows semantic versioning after the first public release.

## 2.2.0 - 2026-08-09

This stable release consolidates the tested Preview line into the first
installer-based JK Bridge release. The recommended setup path remains the
one-shot Linux pairing helper; the optional Linux Agent and managed relay
features remain explicitly advanced/experimental.

### Added

- Added a first-run, four-step in-app tutorial covering the product boundary,
  one-shot pairing, Linux versus Windows-local services, diagnostics, settings,
  and updates. Choosing **Later** closes only the current tutorial; completing
  it persists the choice, and **User guide** can reopen it at any time.
- Grouped navigation into daily use, configuration/maintenance, and
  help/preferences, with concise purpose hints on every main page and navigation
  entry.
- Added bounded Windows-local web-service discovery that reads only active
  loopback listeners, performs lightweight header-only recognition, and never
  scans disks or unopened port ranges.
- Added simple/advanced Linux service editing, reusable service templates,
  local-port conflict guidance, and non-secret Service Profile
  export/preview/confirmed-import.
- Added an unsigned per-user Inno Setup installer with prerequisite checks,
  while retaining the verified portable ZIP.
- Added allow-listed, redacted diagnostic ZIP export.

### Changed

- Promoted the application informational version and all in-app version
  displays to stable 2.2.0; the sidebar no longer hides a prerelease suffix
  behind a hard-coded development label.
- Appearance selection previews immediately and clearly distinguishes a
  temporary preview from the saved startup preference.
- Configuration schema V6 records tutorial completion without writing the
  default false value, preserving strict legacy service-profile import
  compatibility.
- The optional Agent remains a deterministic no-AI management plane with the
  one-shot pairing path retained for normal setup and recovery. Agent and relay
  lifecycle operations are still documented as advanced/experimental.

### Updates

- Manual update checks now use the stable GitHub Release channel in stable
  builds and the bounded official release list in Preview builds.
- Download and installation are separate explicit actions. The app displays
  byte/percentage progress, checksum and verification stages, supports
  cancellation, and does not exit until the user starts the visible installer
  and process creation succeeds.
- GitHub metadata and assets use the Windows/.NET system proxy while retaining
  exact-host validation, one approved release-asset CDN redirect, response-size
  limits, and mandatory detached SHA-256 verification.
- An automated regression test proves 2.2.0-preview.6 selects stable 2.2.0
  and its matching installer/checksum pair as an upgrade.

### Security

- Windows-local discovery accepts only literal loopback listeners and browser
  targets, never follows redirects, and never reads response bodies.
- Generated/custom browser URLs and service profiles remain strictly bounded;
  profiles cannot contain device credentials, keys, server identity, or Linux
  authorization receipts.
- The updater accepts only the fixed official Mathjk/jk-bridge metadata and
  exact same-release installer/checksum assets.

### Testing

- Passed 361 .NET regression tests, the Linux helper/Agent suite, Agent security
  tests, strict Release compilation, privacy scanning, documentation-link
  validation, package-contract checks, and Windows x64 packaging gates.

## 2.2.0-preview.6 - 2026-08-09

This prerelease supersedes Preview 5 after its Windows acceptance run exposed a
proxy-dependent GitHub download path. It retains the Appearance fixes from
Preview 5.

### Fixed

- Release metadata and installer downloads now use the Windows/.NET system
  proxy instead of forcing a direct connection, so local or managed proxy
  environments can reach GitHub Release assets.
- Proxy compatibility does not relax the update trust boundary: automatic
  redirects, cookies, target credentials, and preauthentication remain disabled;
  exact HTTPS hosts, the single approved CDN redirect, size limits, and
  SHA-256 verification remain mandatory.

### Testing

- Windows acceptance reproduced the forced-direct timeout and confirmed that
  the same official Preview 5 asset succeeds through the default system path.

## 2.2.0-preview.5 - 2026-08-09

This prerelease keeps the v2.2.0 development line explicitly in Preview while
providing a small, visible update target for end-to-end acceptance of the
Preview 4 updater.

### Fixed

- The Appearance selector now previews System, Light, and Dark modes immediately
  instead of appearing inert until the separate Save action.
- The saved theme remains the startup source of truth and is visibly identified
  after configuration load or save; an unsaved preview falls back to the saved
  choice on the next launch.

### Changed

- Settings now explains the preview-versus-save behavior and reports whether the
  displayed theme is a temporary preview or the persisted preference.

## 2.2.0-preview.4 - 2026-08-09

This prerelease records source still under development. It does not change the
contents or support boundary of the published v2.1.0 stable Release. Preview 1
through Preview 3 were published only for development acceptance; Preview 4 builds
identify themselves as `2.2.0-preview.4` and remain prereleases rather than
stable v2.2.0.

### Fixed

- Split in-app updates into an explicit download-and-verify phase and a separate install phase, so the first click never launches setup or exits JK Bridge.
- Added streamed byte/percentage progress, checksum and verification stages, cancellation, and clear failure states for official installer downloads.
- Start the normal visible Windows setup wizard first, then request a controlled application shutdown only after process creation succeeds; failed launches leave JK Bridge running.

### Added

- Added bounded Windows-local service discovery that enumerates only active
  `127.0.0.1` / `::1` TCP listeners, attributes owning processes when
  available, uses header-only proxy-free HTTP(S) probes, and lets users save,
  edit, check, copy, open, or remove independent local-service cards.
- Added a strict loopback URL builder/parser and an in-development simple/advanced service editor that separates the Windows access port, Linux service port, protocol, launch path, and custom URL.
- Added core service templates for Overleaf, Sub2API, generic HTTP, and generic HTTPS routes.
- Added a versioned, non-secret Service Profile model, strict serialization/validation, import change analysis, native file pickers, atomic export, and read-only preview plus explicit-confirmation import UI. The importer can also validate an exact v2.1/schema-v3 legacy config and project only its Services while discarding all device-specific state.
- Added schema V4 Agent-registration scaffolding that stores non-secret metadata and an opaque Windows credential-store reference while preserving migrated V3 service and authorization state.
- Added Windows-side Agent endpoint/protocol validation, bounded HTTP client, credential protection, dedicated SSH-identity provisioning, and enrollment orchestration scaffolding.
- Added a standard-library development Linux Agent script with Tailscale-only binding, one-use enrollment codes, per-device credentials, service-manifest validation, exact managed `permitopen` transactions, device listing/revocation, and crash-recovery state. The optional relay path adds the documented sibling script, `socat`, and systemd-user dependencies.
- Wired the vNext Windows setup flow to the bounded Agent client while retaining explicit one-shot pairing on the same screen.
- Added authenticated `/v1/me` self-receipts so ordinary devices can manually synchronize their own service selection without device-list privilege, plus administrator device-list refresh and explicit confirmed revocation of a selected non-current device in the Windows Setup page.
- Added administrator-generated, transient 10-minute one-use enrollment codes for adding ordinary devices from the Windows Setup page; the first administrator still requires Linux bootstrap/recovery.
- Added administrator-only controlled relay APIs and Windows UI for read-only plan/probe, explicit-confirmation apply, read-only delete planning, and confirmed deletion. Managed TCP relays use the sibling `jkbridge_relay.py`, `socat`, per-user systemd units, Linux loopback listeners, catalog-bound plan tokens, and exact catalog-derived device grants.
- Added a bounded, read-only Windows Agent discovery engine and Setup integration that inspect only known online Tailscale peers on port `45871`, verify stable-node, MagicDNS, tailnet, protocol, and Agent-version claims, and require an explicit **Use selected address** action before the existing enrollment flow.
- Added hardened per-user Linux systemd/environment packaging assets, an operator packaging guide, and a package contract check; one deployed pair has now exercised install/bootstrap/start/restart/status and first-admin enrollment, while the complete clean-machine lifecycle remains a release gate.
- Added an Inno Setup installer skeleton and Windows build script with prerequisite detection, optional UAC-confirmed OpenSSH Client installation, Tailscale official-download handoff, per-user startup choice, and detached SHA-256 output.
- Added a tagged-release aggregation job so preview tags publish both the portable ZIP and unsigned installer with detached hashes while remaining GitHub prereleases.
- Added privacy-safe field-acceptance evidence and bilingual multi-device/administrator operating guidance.
- Added an explicit Settings workflow that selects the appropriate stable or Preview GitHub channel, validates an exact official installer/checksum asset pair, downloads only on user action, verifies SHA-256, and starts the normal per-user overlay installer without silent flags.
- Added vNext Agent protocol and threat-model drafts plus security black-box test scaffolding.

### Changed

- Clarified in user documentation that the browser always uses the Windows `localPort`, which may differ from the Linux `remotePort`; local URL/path edits do not broaden Linux authorization.
- Documented independent per-device identities, safe non-secret migration, Agent-independent SSH tunneling, and the one-shot pairing fallback.
- Tailscale login identity comparison now treats account-name casing consistently with the Linux Agent, preventing a valid administrator from being hidden when Windows reports `LoginName` with different letter casing.

### Security

- Windows-local discovery never scans disks or unopened port ranges, does not
  follow redirects or read response bodies, excludes existing SSH-forward
  ports from saving, and accepts browser targets only on literal loopback.
- Generated and custom browser URLs remain exact HTTP(S) loopback targets; service profiles reject unknown or secret-bearing fields and never import authorization state.
- Agent client requests reject proxies, redirects, non-Tailscale endpoints, oversized or unknown responses, peer-identity mismatches, and port/catalog inconsistencies.
- Release installation accepts only exact `Mathjk/jk-bridge` asset URLs, one approved GitHub release-asset CDN redirect, a bounded installer, and a matching detached SHA-256; failed downloads are not launched and their staging directory is removed.
- Device credentials are designed for OS-protected storage and independent revocation; private keys, enrollment codes, and reusable credentials remain outside `config.json` and Service Profiles.

### Development status

- The latest stable release remains v2.1.0. The v2.2.0 development version
  label is an explicit pre-release boundary, not evidence of a stable artifact.
- The development Linux Agent, packaging assets, Windows enrollment/management client, and Service Profile UI exist, but none is part of the published v2.1.0 Release.
- Linux systemd packaging has completed one deployed install/bootstrap/start/restart/status run, but still needs clean-machine login persistence plus supported upgrade/rollback/uninstall acceptance; Windows Agent/Profile paths still need disposable clean-machine, independent-revocation, fallback, and security acceptance.
- Agent discovery engine/controller/UI source integration exists, but still needs disposable clean-Windows acceptance; discovery only selects an endpoint and never replaces the one-time code or explicit enrollment action.
- Administrator device-code and relay-management UI/API paths are implemented in source, but still need Linux live-lifecycle and disposable clean-Windows end-to-end acceptance before release.
- Manual catalog sync uses authenticated `GET /v1/me` plus `/v1/services`; only administrators receive the complete device list and revoke capability.
- The Windows installer skeleton is not a signed or clean-machine-accepted Release asset, and the one-shot Linux helper remains the released setup path.

## 2.1.0 - 2026-07-20

### Added

- Added a schema V3 authorization receipt containing the paired client-key fingerprint and exact Linux destination ports.
- Added authorization-drift detection: a newly enabled remote port now requires re-pairing before SSH starts, while stale extra grants produce a least-privilege recommendation.
- Added TCP-connect health checks for self-signed HTTPS and other services where application-level TLS validation is not an appropriate readiness signal.
- Added Investment News as a default service, tray action, setup/recovery mapping, and migration helper for existing installations.

### Changed

- Centralized each service's SSH forward, browser URL, health URL, and authorized remote port in one validated route definition.
- Separated SSH listener readiness, service health, and browser launch. An unavailable HTTP/TLS health check remains visible but no longer blocks opening a live forward.
- Exposed TCP, HTTP status, and JSON `ok=true` health modes in the Services editor and extended Windows verification accordingly.
- Schema V1/V2 upgrades preserve device and service settings but require one re-pair so V3 can establish a trustworthy authorization baseline.

### Security

- Browser targets, health targets, SSH destinations, and authorization targets are now checked against the same exact IPv4-loopback route.
- The app blocks before Tailscale or SSH process startup when a known authorization receipt is missing any enabled Linux destination port.

## 2.0.1 - 2026-07-13

### Documentation

- Rebuilt the English and Chinese README files around downloads, device roles, official prerequisites, a five-minute setup, daily use, troubleshooting, and security.
- Added bilingual step-by-step installation and troubleshooting guides with official Tailscale, Microsoft OpenSSH, Ubuntu OpenSSH, Python, and .NET links.
- Added a contributor guide, development map, private security-reporting policy, issue forms, and privacy-safe product screenshot.
- Corrected the Release instructions: the Linux helper is included inside the Windows ZIP rather than published as a separate asset.

### Changed

- Simplified the release manifest into one authoritative payload list and packaged all user-facing guides and images.
- Removed the obsolete `USER_GUIDE.md` redirect, unused Avalonia template icon, unused logo variants, and empty app `Models` folder declaration.
- Expanded the public-release privacy scan to cover the changelog, contribution policy, security policy, and issue templates.

## 2.0.0 - 2026-07-13

### Added

- One-click Windows device setup using a one-time Linux pairing code.
- Standalone Linux Python helper for pairing, status, managed-key backup, and revocation.
- Automatic dedicated Ed25519 key creation and exact Windows private-key ACL enforcement.
- Automatic SSH host-key pinning and exact Linux loopback-port authorization.
- Pairing protocol validation, timeouts, size limits, proxy/redirect blocking, and unit tests.
- Schema V1-to-V2 migration and safe unconfigured first-run state.
- Detailed English and Chinese documentation, public-release privacy scanner, and CI/release workflows.

### Retained

- Supervised multi-forward SSH tunnel, Tailscale MagicDNS resolution, health checks, reconnects, tray integration, startup option, diagnostics, and editable services.
