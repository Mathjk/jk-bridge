# ADR 0001: One-shot Tailscale pairing

- Status: Accepted
- Date: 2026-07-13

## Context

Manual first-run setup required users to copy a client public key, edit Linux `authorized_keys`, separately verify an SSH host public key, and write Windows configuration. That process was secure when followed exactly but error-prone and hard to reuse.

## Decision

Linux uses a standalone, non-root Python helper that binds only to its Tailscale IPv4 interface, prints a 256-bit-token pairing code, accepts one versioned request, atomically installs an exact restricted authorization, returns locally read server identity, and exits. Windows owns the durable UI, key lifecycle, configuration, SSH supervision, and diagnostics.

## Consequences

- The normal setup becomes one Linux command and one Windows action.
- Linux requires Python 3 but no JK Bridge daemon, package, root access, or long-lived listening port.
- The one-time code is a temporary secret and pairing depends on tailnet policy permitting the ephemeral port.
- Persistent traffic remains standard OpenSSH with strict host-key pinning.
- New remote ports require an intentional re-pair to expand the exact whitelist.
