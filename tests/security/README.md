# Agent security tests

This directory contains destructive-looking security cases implemented against
temporary fixtures only. The suite must never read or write the operator's live
home directory, `~/.ssh/authorized_keys`, Tailscale LocalAPI socket, or systemd
user instance.

Run from the repository root:

```bash
python3 -m unittest discover -s tests/security -p 'test_*.py' -v
```

If `scripts/jkbridge_agent.py` is absent on an older branch, Agent-specific
cases report a clean skip. Once the script exists, a missing or incompatible
CLI/store contract is a failure rather than a skip, so the Agent cannot
accidentally bypass the gate.

The tests seed credentials with recognizable marker strings and scan all
captured output and state files. Any occurrence is a failure. All paths passed
to the Agent live below a fresh `TemporaryDirectory`; the adapter performs a
second containment check before invoking Agent code.

Coverage and requirement identifiers are defined in
`docs/AGENT_THREAT_MODEL.md`. This security gate covers:

- non-mutating, redacted CLI lifecycle/help behavior;
- exact loopback-only `permitopen` generation;
- preservation and atomic removal of unrelated SSH entries;
- state-directory, manifest, SSH-directory, `authorized_keys`, backup, and lock
  symlink rejection without target modification;
- hard-linked shared SSH-writer lock rejection by both the Agent and one-shot
  helper without chmod, state consumption, or target mutation;
- forged managed-marker rejection; and
- seeded-environment output redaction and per-source/per-operation rate-limit
  isolation.

The functional Agent suite in `tests/linux/test_jkbridge_agent.py` additionally
covers verifier-only recovery/device credential persistence, one-use recovery,
stable peer binding, role isolation, exact Windows wire fields, device-isolated
revocation, journal recovery, HTTP query-secret rejection, and
wildcard/non-allowed listener rejection. It also covers the current vNext
preview boundaries: administrator-only issuance of device-role enrollment
codes with no plaintext persistence, one-use consumption, read-only relay
planning/probing, literal confirmation, stale-plan rejection, exact loopback
managed units, transactional grant/catalog updates, and rollback.
It also holds the one-shot helper's shared SSH lock while an Agent transaction
runs, proving the second writer blocks instead of committing last-writer-wins
state; strict local relay-adoption parsing/diff/token/rollback behavior and
target-only update affected-device reporting are covered in
`tests/linux/test_jkbridge_relay.py`.

The .NET suite covers credential-free, explicit, bounded Agent discovery over
known online Tailscale peers; identity/ambiguity checks; no-proxy/no-redirect
Agent HTTP behavior; stable-node-preserving explicit endpoint rebind; transient
enrollment-code handling; Credential Manager references; Windows relay/catalog
preview invalidation/confirmation; bounded diagnostic-bundle construction; and
fixed-origin update-metadata validation. Package checks cover a non-root
systemd user unit and reject literal secrets, runtime state, private keys, and
symlinks in staged delivery trees. Linux controller tests additionally prove a
tampered or mode-broadened installed payload is refused before `systemctl`.

Real Tailscale WhoIs, status-to-connection identity races, concurrent crash
points, active-session revocation, DNS rebinding/address drift, and live
OpenSSH/systemd behavior still need a later process-level harness with a fake
Tailscale CLI/LocalAPI and disposable namespaces. None of these cases may
target the user's real services. Agent-to-one-shot same-key fallback is covered
with temporary `authorized_keys` fixtures: only an exact restricted Agent line
may be converted, while malformed, wildcard, duplicate, and unrelated entries
fail without mutation.
