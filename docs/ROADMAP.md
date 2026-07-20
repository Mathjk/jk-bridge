# Roadmap

## V2.0 — portable public release

- One-shot Linux pairing and Windows one-click setup.
- Generic, privacy-safe defaults and schema migration.
- Self-contained Windows x64 ZIP containing the Linux helper and bilingual documentation.
- Automated tests, privacy gate, checksums, CI, and release workflow.

## V2.1 — route and authorization integrity

- One validated route definition for SSH, browser launch, health checks, and pairing authorization.
- Persisted authorization receipts with proactive drift detection and one-time legacy re-pairing.
- TCP health mode for self-signed HTTPS and non-HTTP loopback services.
- Browser launch separated from advisory HTTP/TLS health results.
- Investment News default service and existing-installation migration helper.

## V2.2 — distribution hardening

- Complete clean-machine Windows acceptance on supported Windows 11 builds.
- Authenticode code signing and timestamping.
- Optional installer while retaining the portable ZIP.
- Clear in-app update notification without silent installation.

## Later candidates

- Multiple named Linux profiles.
- Export/import of service definitions without private keys or host identities.
- More health-check templates and service ordering.
- Dedicated Linux service account guidance and optional administrator-reviewed sshd hardening.

Items are deliberately not commitments. Security restrictions and the no-password/no-public-bind model remain invariant.
