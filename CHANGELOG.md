# Changelog

All notable changes are documented here. The project follows semantic versioning after the first public release.

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
