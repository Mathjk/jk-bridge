# Security tests

This directory contains destructive-looking security cases implemented against
temporary fixtures only. The suite must never read or write the operator's live
home directory, `~/.ssh/authorized_keys`, Tailscale LocalAPI socket, or systemd
user instance.

Run from the repository root:

```bash
python3 -m unittest discover -s tests/security -p 'test_*.py' -v
```

The tests seed credentials with recognizable marker strings and scan all
captured output and state files. Any occurrence is a failure. All paths passed
to the helper live below a fresh `TemporaryDirectory`; the adapter performs a
second containment check before invoking helper code.

Coverage and requirement identifiers are defined in
`docs/SECURITY.md`. This security gate covers:

- non-mutating, redacted CLI lifecycle/help behavior;
- exact loopback-only `permitopen` generation;
- preservation and atomic removal of unrelated SSH entries;
- state-directory, SSH-directory, `authorized_keys`, backup, and lock symlink
  rejection without target modification;
- hard-linked shared SSH-writer lock rejection without chmod, state
  consumption, or target mutation;
- forged managed-marker rejection; and
- seeded-environment output redaction.

The functional helper suite in `tests/linux/test_jkbridge_linux.py`
additionally covers verifier-only recovery, one-use pairing codes, exact
Windows wire fields, HTTP query-secret rejection, and wildcard/non-allowed
listener rejection. It also holds the shared SSH lock while a second writer
runs, proving the second writer blocks instead of committing last-writer-wins
state.

The .NET suite covers credential-free update metadata validation, bounded
diagnostic-bundle construction, restart-token handling, and configuration
validation. Package checks reject literal secrets, runtime state, private
keys, and symlinks in staged delivery trees.

Real Tailscale WhoIs, status-to-connection identity races, concurrent crash
points, active-session revocation, DNS rebinding/address drift, and live
OpenSSH/systemd behavior still need a later process-level harness with a fake
Tailscale CLI/LocalAPI and disposable namespaces. None of these cases may
target the user's real services. Helper-to-one-shot same-key fallback is
covered with temporary `authorized_keys` fixtures: only an exact restricted
managed line may be converted, while malformed, wildcard, duplicate, and
unrelated entries fail without mutation.
