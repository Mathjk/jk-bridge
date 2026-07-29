# v2.2.0/vNext field acceptance record

This document records privacy-safe evidence from a deployed Linux/Windows pair.
It is not a substitute for the disposable clean-machine matrix in
[WINDOWS_ACCEPTANCE.md](WINDOWS_ACCEPTANCE.md), and it does not promote the
development build to a stable release.

## 2026-07-29 — first Agent-admin enrollment

Completed:

- downloaded the unsigned Windows installer produced by GitHub Actions for the
  tested commit and verified its detached SHA-256;
- installed and launched the self-contained per-user Windows application;
- kept Tailscale and Windows OpenSSH as explicit prerequisites;
- explicitly discovered one known online Agent candidate, selected it, and used
  its exact Tailscale endpoint;
- consumed one Linux-local, 15-minute, one-use administrator recovery code;
- created an independent Windows Ed25519 identity and enrolled one `admin`
  device without copying another device's configuration or private key;
- synchronized a seven-service catalog and installed one exact managed SSH
  authorization containing only the seven catalog destination ports;
- observed established SSH connections from the enrolled Windows peer;
- kept the Linux Agent bound to its exact Tailscale IPv4 address on port
  `45871`, with no wildcard or LAN listener;
- preserved the pre-install `authorized_keys` content other than the new exact
  Agent-managed entry; and
- kept the two pre-existing loopback relay units active while exposing their
  exact ports through the catalog.

Service-layer probes confirmed six configured destinations were listening.
One optional application was intentionally stopped; its exact SSH grant existed,
but application availability was correctly distinct from tunnel authorization.

## Gates that remain open

- disposable clean-profile prerequisite variants and installer uninstall;
- Authenticode signing and timestamp verification;
- reboot, login persistence, suspend/resume, and network-recovery coverage;
- a second independent Windows device, ordinary-device code enrollment,
  administrator replacement, revocation, and immediate session cutoff;
- supported Agent upgrade, rollback, and uninstall commands; and
- live Windows-managed relay create/update/delete and legacy-relay remote
  adoption.

No enrollment code, credential, public key, node identifier, account name,
machine name, Tailnet suffix, or deployed Tailscale address is recorded here.
