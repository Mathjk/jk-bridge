# Changelog

All notable changes are documented here. The project follows semantic versioning after the first public release.

## Unreleased — v2.2.0 / Preview 2 development

This section records source currently under development. It does not change the
contents or support boundary of the published v2.1.0 stable Release. Preview 1
is published only for development acceptance; Preview 2 builds identify
themselves as `2.2.0-preview.2` and remain prereleases rather than stable v2.2.0.

### Added

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
