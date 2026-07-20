# Changelog

All notable changes are documented here. The project follows semantic versioning after the first public release.

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
