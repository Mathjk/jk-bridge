# JK Bridge Linux Agent packaging

The Agent is an optional, AI-independent user service. It automates repeatable
device enrollment, exact SSH authorization, service-catalog synchronization,
and device revocation. `scripts/jkbridge-linux.py` remains the no-daemon,
one-shot fallback.

Nothing happens merely by unpacking these files. Installation starts only after
the user invokes the reviewed controller (directly or through the command
embedded in the Windows app).

## Prerequisites

- Linux user account used for the JK Bridge SSH connection
- Python 3.9 or newer
- Tailscale running and logged in, including `tailscale whois --json`
- OpenSSH Server with an Ed25519 host public key
- `socat`, `ss`, and a working systemd user manager
- the final services listening only on Linux loopback

Run the Agent as the ordinary SSH user, never with `sudo`. The service does not
need root privileges.

## Suggested per-user layout

```text
~/.local/lib/jkbridge/jkbridge_agent.py
~/.local/lib/jkbridge/jkbridge_relay.py
~/.local/lib/jkbridge/jkbridge-linux.py       # retained one-shot fallback
~/.local/bin/jkbridge-agentctl                # lifecycle controller
~/.config/systemd/user/jkbridge-agent.service
~/.config/jkbridge-agent/agent.env        # optional
~/.local/state/jkbridge-agent/state.json
~/.local/state/jkbridge-agent/services.json
```

The packaged service expects the script at
`~/.local/lib/jkbridge/jkbridge_agent.py`. If a distribution package chooses a
different path, update `ExecStart` before installing the unit.

## Auditable one-command lifecycle

The package includes `linux/jkbridge-agentctl`. It refuses root/sudo, checks
Python, Tailscale login, `socat`, the systemd user manager, the SSH host public
key, and all companion payloads before writing anything. It prompts once before
installation; `--yes` is available for a reviewed non-interactive deployment.
It never installs system packages or invokes sudo/UAC. Install those
prerequisites through the operating system first, then run the controller as
the ordinary Linux SSH user.

From a release delivery directory:

```bash
./linux/jkbridge-agentctl --dry-run install
./linux/jkbridge-agentctl install
```

From a repository checkout, use
`packaging/linux/jkbridge-agentctl`; it locates `scripts/` automatically. A
deployment with additional existing services should provide the complete,
reviewed catalog on the first run:

```bash
packaging/linux/jkbridge-agentctl \
  --services-file /absolute/path/to/services.json \
  install
```

The controller replaces each Agent, relay companion, retained one-shot helper,
controller, unit, and non-secret environment file atomically and backs up an
older managed copy. It then initializes private state, prints one short-lived
administrator setup code beginning with `JKBA1.`, verifies that
`authorized_keys` did not change, writes a SHA-256 install manifest, and
enables the systemd user service. The setup code contains the exact Agent
endpoint and one-time enrollment credential, so Windows needs only that one
paste. SSH authorization changes only after the enrollment succeeds.

Before `start`, `stop`, `status`, or `fallback`, the installed controller checks
every path component, regular-file type, owner, exact private mode, and the
recorded SHA-256 for the three Python helpers, controller, unit, and environment
file. It refuses a replaced or foreign same-name unit before calling systemd.

The multi-file installation is not a single filesystem transaction. If it is
interrupted, lifecycle commands fail closed on the missing/stale manifest;
rerun the same reviewed `install` command to converge the payload files.
Per-file backups are retained for manual recovery. If interruption leaves only
one of `state.json` and `services.json`, the controller refuses to overwrite or
guess at private enrollment state; inspect that incomplete first-bootstrap
state locally before retrying. `upgrade` first verifies the current install,
preserves the custom Agent port and private state, and saves one verified
rollback snapshot before replacement. `rollback` accepts only that controller-
created snapshot. `uninstall` removes managed runtime files and the user unit
but deliberately preserves Agent state, credentials, rollback snapshots, and
SSH grants for an explicit later recovery or reinstall.

Lifecycle commands do not require a terminal workflow on both computers:

```bash
~/.local/bin/jkbridge-agentctl upgrade
~/.local/bin/jkbridge-agentctl rollback
~/.local/bin/jkbridge-agentctl uninstall
~/.local/bin/jkbridge-agentctl status
~/.local/bin/jkbridge-agentctl stop
~/.local/bin/jkbridge-agentctl start
~/.local/bin/jkbridge-agentctl fallback
```

`install` on an already managed installation converges through the same
verified upgrade path. Therefore the one-line command copied from Windows can
also be rerun for an upgrade: it preserves the enrolled identity and does not
require another Windows code paste. Use `rollback` for the newest controller-
created snapshot.

`stop`, `fallback`, and `uninstall` preserve Agent state, credentials, every SSH key, and the
old one-shot pairing helper. Stopping the Agent does not terminate an already
established Windows SSH tunnel; stop that tunnel separately when immediate
cutoff is intended. Changing or expanding Linux remote ports while in fallback
mode still requires a fresh one-shot pairing. The helper converts an existing
Agent entry only when the same key has the exact canonical, loopback-only
restrictions; personal, wildcard, unrestricted, duplicated, or modified keys
remain fail-closed. A later Agent transaction can safely reconcile that exact
one-shot entry back to per-device Agent ownership.

## One-time initialization

The controller above is the recommended lifecycle. For auditing or a manual
installation, after placing both companion scripts and the unit, initialize
private state as the ordinary user:

```bash
python3 ~/.local/lib/jkbridge/jkbridge_agent.py \
  --state-dir ~/.local/state/jkbridge-agent \
  bootstrap
```

Bootstrap prints one short-lived `JKBA1.` setup code containing the exact
endpoint and administrator enrollment credential. Only the credential's
SHA-256 verifier is stored. Paste the setup code directly into JK Bridge for
Windows; do not save it in a note, configuration file, shell script, or
screenshot.

The built-in development catalog contains Overleaf, Sub2API, GPU Dashboard,
and Investment News. Before enrolling a device, deployments with additional
loopback services or Skill-created relays should pass a reviewed complete
catalog using `bootstrap --services-file <json>`. Enrollment grants only ports
present in that server-owned catalog.

When the Windows public key already has the fully restricted line emitted by
the one-shot `jkbridge-linux.py` helper, first Agent enrollment can safely
replace that exact line with its per-device Agent record. An unrestricted,
personal, legacy-wildcard, duplicated, or otherwise modified key is never
adopted. Any former port absent from the Agent catalog is intentionally removed.

Inspect without changing SSH authorization:

```bash
python3 ~/.local/lib/jkbridge/jkbridge_agent.py \
  --state-dir ~/.local/state/jkbridge-agent \
  status

python3 ~/.local/lib/jkbridge/jkbridge_agent.py \
  --state-dir ~/.local/state/jkbridge-agent \
  serve --dry-run
```

After review, enable the user service with the ordinary systemd user workflow.
The repository deliberately does not perform that external state change.

## Recovery enrollment

If no trusted administrator device remains, issue one new, expiring code from
the Linux console:

```bash
python3 ~/.local/lib/jkbridge/jkbridge_agent.py \
  --state-dir ~/.local/state/jkbridge-agent \
  recovery-code --role admin --ttl 900
```

For an additional ordinary Windows device, use `--role device`. Codes expire,
are consumed atomically once, and are never accepted in a URL or query string.

## Security boundaries

- The production CLI discovers the local Tailscale IPv4 address and binds only
  that exact address. It has no wildcard-host option.
- Tailscale source range alone is insufficient. The Agent resolves each peer
  with `tailscale whois` and binds the device credential to the stable node ID
  and Tailnet login observed during enrollment.
- Each Windows device generates its own Ed25519 key and random Agent credential.
  Only credential hashes are persisted.
- SSH entries contain `command="/usr/bin/false"`, no shell, and one exact
  `permitopen="127.0.0.1:PORT"` per selected catalog port.
- Ordinary devices can read their service catalog. Only administrators can
  update the catalog, list devices, or revoke devices.
- State, manifest, and `authorized_keys` updates use locking, backups, atomic
  replacement, and a recovery journal. Unrelated SSH keys are preserved.
- Agent transactions take the state lock and then the same
  `~/.ssh/.jkbridge.lock` used by the one-shot helper, preventing concurrent
  writers from replacing each other's exact grants.
- Request bodies, request counts, concurrent handlers, peer ranges, and service
  and device counts are bounded. Authentication material is never logged.

Revocation removes the key from future OpenSSH authentication but cannot end an
SSH connection that was already established. Stop the corresponding Windows
tunnel when immediate session termination is required.

## API summary

The API is versioned under `/v1` on the exact Tailscale address. Enrollment uses
`Authorization: JKBridge-Enrollment <one-time-code>`. Normal requests use
`Authorization: Bearer <per-device-credential>`. Neither secret is allowed in a
path or query string.

```text
GET  /v1/health
GET  /v1/version
GET  /v1/bootstrap
POST /v1/enroll
GET  /v1/me
GET  /v1/services
GET  /v1/discovery                 # administrator, read-only
PUT  /v1/services                 # administrator
GET  /v1/devices                  # administrator
POST /v1/devices/<id>/rename      # administrator; label only, audited
POST /v1/devices/<id>/revoke      # administrator
POST /v1/enrollment-codes         # administrator; device-role code only
POST /v1/relays/plan              # administrator, read-only
POST /v1/relays/probe             # administrator, read-only
POST /v1/relays/apply             # administrator, exact plan + confirmation
POST /v1/relays/delete-plan       # administrator, read-only
POST /v1/relays/delete            # administrator, exact plan + confirmation
```

Relay mutations require a freshly returned plan token and literal
`confirmed: true`. Creating or changing a relay and adding its catalog service
are treated as one convergent operation: grants remain catalog-derived, and a
failed catalog commit rolls the managed relay back when no recovery journal is
pending. Deletion contracts device grants and catalog state before removing
the loopback unit. The Agent never overwrites or deletes an unmanaged unit.
