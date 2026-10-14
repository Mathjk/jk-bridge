# JK Bridge Linux packaging

This directory carries the optional Linux control daemon used by the Windows
app. `scripts/jkbridge-linux.py` remains the no-daemon, one-shot pairing
helper and needs no packaging of its own.

## jkbridge-ctl

`jkbridge-ctl` is a small, AI-independent systemd user service. It exposes a
token-protected HTTP API on `127.0.0.1:<port>` (default 8890) with three
actions over a fixed allow-list:

- `POST /api/restart/<target>` restart a managed service in the background
- `GET /api/log/<target>?lines=N` bounded, read-only service log tail
- `GET /api/status/<target>` systemd/docker state plus uptime

It never opens a shell and never accepts an arbitrary command: every target is
a key in the fixed tables inside the script. The token is generated on first
start into `~/.config/jkbridge-ctl/token` (mode 600) and must be entered in the
Windows app's service editor for the `jkbridge-ctl` entry.

Nothing happens merely by unpacking these files. Installation starts only when
the user installs the unit and starts the service.

## Prerequisites

- Linux user account used by the JK Bridge SSH connection
- Python 3.9 or newer (stdlib only)
- Tailscale running and logged in
- OpenSSH Server with an Ed25519 host public key
- the managed services themselves (systemd user units or docker containers)

Run the daemon as the ordinary SSH user, never with `sudo`.

## Suggested per-user layout

```text
~/.local/share/jkbridge/jkbridge-ctl.py
~/.config/systemd/user/jkbridge-ctl.service
~/.config/jkbridge-ctl/token        # generated on first start, mode 600
~/.local/share/jkbridge/ctl.log     # unit output
```

The packaged unit expects the script at
`~/.local/share/jkbridge/jkbridge-ctl.py`. If you choose a different path,
update `ExecStart` before installing the unit.

## Install

```bash
mkdir -p ~/.local/share/jkbridge
cp jkbridge-ctl.py ~/.local/share/jkbridge/
chmod +x ~/.local/share/jkbridge/jkbridge-ctl.py
cp jkbridge-ctl.service ~/.config/systemd/user/
systemctl --user daemon-reload
systemctl --user enable --now jkbridge-ctl.service
```

The daemon listens on `127.0.0.1:8890`. Authorize that port for JK Bridge by
re-running the one-shot pairing on Linux, then add a `jkbridge-ctl` service in
the Windows app (Windows and Linux port 8890, health path `/api/health`).

## Verify

```bash
curl -s http://127.0.0.1:8890/api/health
curl -s -H "X-JKBridge-Restart-Token: $(cat ~/.config/jkbridge-ctl/token)" \
     http://127.0.0.1:8890/api/status/gpu-dashboard
```
