#!/usr/bin/env python3
"""jkbridge-ctl: loopback-only control daemon for JK Bridge managed services.

Exposes a small, token-protected HTTP API on 127.0.0.1 that the Windows JK
Bridge app reaches through its existing SSH tunnel. It owns exactly three
read/execute actions over a fixed allow-list:

    POST /api/restart/<target>   restart a managed service (background)
    GET  /api/log/<target>?lines bounded, read-only service log tail
    GET  /api/status/<target>    systemd/docker state plus uptime

The daemon never opens a shell and never accepts an arbitrary command: every
target is a key in the fixed tables below. Stdlib only; no third-party imports.
"""
import argparse
import hmac
import json
import os
import secrets
import subprocess
import sys
import threading
import time
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse

CONFIG_DIR = Path.home() / ".config" / "jkbridge-ctl"
TOKEN_PATH = CONFIG_DIR / "token"
TOKEN_HEADER = "X-JKBridge-Restart-Token"
MAX_LOG_LINES = 500
MAX_LOG_BYTES = 256 * 1024
COMMAND_TIMEOUT_SECONDS = 120

# Fixed allow-list: target -> restart command. An empty list means the target is
# not restartable through this daemon.
RESTART_TARGETS = {
    "gpu-dashboard": ["systemctl", "--user", "restart", "gpu-dashboard"],
    "investment-news": ["systemctl", "--user", "restart", "investment-news"],
    "cn-fund-backtest": ["systemctl", "--user", "restart", "cn-fund-backtest"],
    "overleaf": ["docker", "restart", "sharelatex"],
    "sub2api": ["docker", "restart", "sub2api"],
    "campus-auth": ["systemctl", "--user", "restart", "jkbridge-campus-auth-relay"],
    "campus-mgmt": ["systemctl", "--user", "restart", "jkbridge-campus-mgmt-relay"],
}

# Fixed allow-list: target -> log command factory. Read-only, bounded output.
LOG_TARGETS = {
    "gpu-dashboard": lambda lines: ["journalctl", "--user", "-u", "gpu-dashboard", "-n", str(lines), "--no-pager"],
    "investment-news": lambda lines: ["journalctl", "--user", "-u", "investment-news", "-n", str(lines), "--no-pager"],
    "cn-fund-backtest": lambda lines: ["journalctl", "--user", "-u", "cn-fund-backtest", "-n", str(lines), "--no-pager"],
    "overleaf": lambda lines: ["docker", "logs", "--tail", str(lines), "sharelatex"],
    "sub2api": lambda lines: ["docker", "logs", "--tail", str(lines), "sub2api"],
    "campus-auth": lambda lines: ["journalctl", "--user", "-u", "jkbridge-campus-auth-relay", "-n", str(lines), "--no-pager"],
    "campus-mgmt": lambda lines: ["journalctl", "--user", "-u", "jkbridge-campus-mgmt-relay", "-n", str(lines), "--no-pager"],
}

# Fixed allow-list: target -> status probe. Read-only.
STATUS_TARGETS = {
    "gpu-dashboard": ("systemd", "gpu-dashboard"),
    "investment-news": ("systemd", "investment-news"),
    "cn-fund-backtest": ("systemd", "cn-fund-backtest"),
    "overleaf": ("docker", "sharelatex"),
    "sub2api": ("docker", "sub2api"),
    "campus-auth": ("systemd", "jkbridge-campus-auth-relay"),
    "campus-mgmt": ("systemd", "jkbridge-campus-mgmt-relay"),
}


def load_token():
    try:
        return TOKEN_PATH.read_text(encoding="utf-8").strip()
    except OSError:
        return ""


def ensure_token():
    token = load_token()
    if token:
        return token
    token = secrets.token_urlsafe(32)
    CONFIG_DIR.mkdir(parents=True, exist_ok=True)
    TOKEN_PATH.write_text(token, encoding="utf-8")
    os.chmod(TOKEN_PATH, 0o600)
    return token


TOKEN = ensure_token()


def run_command(command, timeout=COMMAND_TIMEOUT_SECONDS):
    """Runs an allow-listed command and returns (exit_code, bounded_output)."""
    try:
        completed = subprocess.run(
            command,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            timeout=timeout,
            check=False,
        )
        output = (completed.stdout or b"")[:MAX_LOG_BYTES].decode("utf-8", "replace")
        return completed.returncode, output
    except subprocess.TimeoutExpired:
        return 124, f"command timed out after {timeout}s"
    except Exception as exc:  # noqa: BLE001 - diagnostics only
        return 127, str(exc)


class Handler(BaseHTTPRequestHandler):
    server_version = "jkbridge-ctl/1.0"

    def log_message(self, fmt, *args):
        sys.stderr.write("[%s] %s\n" % (self.log_date_time_string(), fmt % args))

    def send_json(self, payload, status=HTTPStatus.OK):
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def send_text(self, text, status=HTTPStatus.OK):
        body = text.encode("utf-8")[:MAX_LOG_BYTES]
        self.send_response(status)
        self.send_header("Content-Type", "text/plain; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def authorized(self):
        presented = self.headers.get(TOKEN_HEADER, "")
        return bool(TOKEN) and hmac.compare_digest(presented, TOKEN)

    def target_from(self, path, prefix):
        target = path[len(prefix):].strip("/")
        if not target or "/" in target:
            return None
        return target

    def do_GET(self):
        try:
            parsed = urlparse(self.path)
            if parsed.path == "/api/health":
                return self.send_json({"ok": True, "time": time.strftime("%Y-%m-%d %H:%M:%S")})
            if not self.authorized():
                return self.send_json(
                    {"ok": False, "error": "Invalid token"}, status=HTTPStatus.UNAUTHORIZED)
            if parsed.path.startswith("/api/log/"):
                return self.handle_log(parsed)
            if parsed.path.startswith("/api/status/"):
                return self.handle_status(parsed)
            return self.send_json(
                {"ok": False, "error": "Unknown endpoint"}, status=HTTPStatus.NOT_FOUND)
        except Exception as exc:  # noqa: BLE001 - diagnostics only
            sys.stderr.write(f"[ctl] GET failed: {exc}\n")
            self.send_json({"ok": False, "error": str(exc)}, status=HTTPStatus.INTERNAL_SERVER_ERROR)

    def do_POST(self):
        try:
            parsed = urlparse(self.path)
            if not self.authorized():
                return self.send_json(
                    {"ok": False, "error": "Invalid token"}, status=HTTPStatus.UNAUTHORIZED)
            if parsed.path.startswith("/api/restart/"):
                return self.handle_restart(parsed)
            return self.send_json(
                {"ok": False, "error": "Unknown endpoint"}, status=HTTPStatus.NOT_FOUND)
        except Exception as exc:  # noqa: BLE001 - diagnostics only
            sys.stderr.write(f"[ctl] POST failed: {exc}\n")
            self.send_json({"ok": False, "error": str(exc)}, status=HTTPStatus.INTERNAL_SERVER_ERROR)

    def handle_restart(self, parsed):
        target = self.target_from(parsed.path, "/api/restart/")
        command = RESTART_TARGETS.get(target) if target else None
        if command is None:
            return self.send_json(
                {"ok": False, "error": "Unknown restart target"}, status=HTTPStatus.NOT_FOUND)

        self.send_json({"ok": True, "restarting": target})

        def run():
            code, output = run_command(command)
            sys.stderr.write(
                f"[ctl] restart {target} exited {code} {output.strip()}\n")
            sys.stderr.flush()

        threading.Thread(target=run, daemon=True).start()

    def handle_log(self, parsed):
        target = self.target_from(parsed.path, "/api/log/")
        factory = LOG_TARGETS.get(target) if target else None
        if factory is None:
            return self.send_json(
                {"ok": False, "error": "Unknown log target"}, status=HTTPStatus.NOT_FOUND)
        query = parse_qs(parsed.query)
        try:
            lines = int(query.get("lines", ["100"])[0])
        except (TypeError, ValueError):
            lines = 100
        lines = max(1, min(MAX_LOG_LINES, lines))
        code, output = run_command(factory(lines))
        if code != 0:
            return self.send_json(
                {"ok": False, "error": f"log command exited {code}", "output": output})
        return self.send_text(output)

    def handle_status(self, parsed):
        target = self.target_from(parsed.path, "/api/status/")
        entry = STATUS_TARGETS.get(target) if target else None
        if entry is None:
            return self.send_json(
                {"ok": False, "error": "Unknown status target"}, status=HTTPStatus.NOT_FOUND)
        kind, unit = entry
        if kind == "docker":
            code, output = run_command(
                ["docker", "inspect", "-f", "{{.State.Status}} {{.State.StartedAt}}", unit],
                timeout=20,
            )
        else:
            code, output = run_command(
                ["systemctl", "--user", "show", unit, "--property=ActiveState,ExecMainStartTimestamp", "--no-pager"],
                timeout=20,
            )
        if code != 0:
            return self.send_json({"ok": False, "error": f"status exited {code}", "output": output})
        return self.send_json({"ok": True, "target": target, "detail": output.strip()})


def main():
    parser = argparse.ArgumentParser(description="JK Bridge loopback control daemon")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8890)
    args = parser.parse_args()

    if not TOKEN:
        sys.stderr.write("[ctl] refusing to start without a token\n")
        raise SystemExit(1)

    httpd = ThreadingHTTPServer((args.host, args.port), Handler)
    sys.stderr.write(f"jkbridge-ctl running at http://{args.host}:{args.port}\n")
    sys.stderr.flush()
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        httpd.server_close()


if __name__ == "__main__":
    main()
