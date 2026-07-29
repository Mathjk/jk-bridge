#!/usr/bin/env python3
"""One-shot Linux pairing helper for JK Bridge.

The helper binds only to this machine's Tailscale IPv4 address, authorizes one
Windows Ed25519 key with explicit loopback permitopen rules, returns the trusted
Linux SSH host key, and exits. It never runs as a daemon.
"""

from __future__ import annotations

import argparse
import base64
import binascii
import fcntl
import hashlib
import hmac
import http.server
import ipaddress
import json
import os
import pwd
import re
import secrets
import shutil
import socket
import stat
import subprocess
import sys
import tempfile
import time
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional, Sequence, Tuple


PROTOCOL_VERSION = 1
MAX_REQUEST_BYTES = 64 * 1024
MAX_PORTS = 32
MANAGED_COMMENT = "JK Bridge managed"
LEGACY_MANAGED_COMMENT = "JK Bridge dedicated tunnel key"
AGENT_MANAGED_COMMENT = "JK Bridge agent"
TAILSCALE_IPV4 = ipaddress.ip_network("100.64.0.0/10")
TAILSCALE_IPV6 = ipaddress.ip_network("fd7a:115c:a1e0::/48")
SSH_USER_PATTERN = re.compile(r"^[A-Za-z_][A-Za-z0-9_.-]{0,63}$")
RESTRICTED_OPTIONS_PREFIX = (
    'restrict,port-forwarding,command="/usr/bin/false",'
    'from="100.64.0.0/10,fd7a:115c:a1e0::/48",'
)
PERMITOPEN_PATTERN = re.compile(r'^permitopen="127\.0\.0\.1:(\*|[0-9]{1,5})"$')


class PairingError(RuntimeError):
    """A safe, actionable pairing error."""


def normalize_ed25519_key(value: str) -> Tuple[str, str]:
    parts = value.strip().split()
    if len(parts) < 2 or parts[0] != "ssh-ed25519":
        raise PairingError("Only an ssh-ed25519 public key is accepted.")
    try:
        blob = base64.b64decode(parts[1], validate=True)
    except (binascii.Error, ValueError) as exc:
        raise PairingError("The Ed25519 public key is not valid base64.") from exc

    algorithm = b"ssh-ed25519"
    expected_length = 4 + len(algorithm) + 4 + 32
    if len(blob) != expected_length:
        raise PairingError("The Ed25519 public-key blob has an invalid length.")
    if int.from_bytes(blob[0:4], "big") != len(algorithm) or blob[4:15] != algorithm:
        raise PairingError("The public-key algorithm metadata is invalid.")
    if int.from_bytes(blob[15:19], "big") != 32:
        raise PairingError("The Ed25519 public-key payload has an invalid length.")

    fingerprint = "SHA256:" + base64.b64encode(hashlib.sha256(blob).digest()).decode().rstrip("=")
    return "ssh-ed25519 " + parts[1], fingerprint


def key_from_authorized_line(line: str) -> Optional[Tuple[str, str]]:
    parts = line.strip().split()
    for index, part in enumerate(parts[:-1]):
        if part == "ssh-ed25519":
            try:
                return normalize_ed25519_key(" ".join(parts[index : index + 2]))
            except PairingError:
                return None
    return None


def validate_ports(values: Any) -> List[int]:
    if not isinstance(values, list) or not 1 <= len(values) <= MAX_PORTS:
        raise PairingError("remotePorts must contain between 1 and 32 ports.")
    ports: List[int] = []
    for value in values:
        if isinstance(value, bool) or not isinstance(value, int) or not 1 <= value <= 65535:
            raise PairingError("Every requested remote port must be an integer from 1 to 65535.")
        ports.append(value)
    return sorted(set(ports))


def build_authorized_line(public_key: str, fingerprint: str, ports: Sequence[int]) -> str:
    permit = ",".join('permitopen="127.0.0.1:{}"'.format(port) for port in ports)
    options = RESTRICTED_OPTIONS_PREFIX + permit
    return "{} {} {} {}".format(options, public_key, MANAGED_COMMENT, fingerprint)


def _is_exact_restricted_entry(
    line: str,
    public_key: str,
    comment: str,
    allow_wildcard: bool,
) -> bool:
    value = line.strip()
    suffix = " {} {}".format(public_key, comment)
    if not value.startswith(RESTRICTED_OPTIONS_PREFIX) or not value.endswith(suffix):
        return False
    permit_text = value[len(RESTRICTED_OPTIONS_PREFIX) : -len(suffix)]
    permits = permit_text.split(",") if permit_text else []
    if not permits:
        return False
    for permit in permits:
        match = PERMITOPEN_PATTERN.fullmatch(permit)
        if not match:
            return False
        target = match.group(1)
        if target == "*":
            if not allow_wildcard:
                return False
        elif not 1 <= int(target) <= 65535:
            return False
    return True


def is_managed_authorized_line(line: str, public_key: str, fingerprint: str) -> bool:
    return _is_exact_restricted_entry(
        line,
        public_key,
        MANAGED_COMMENT + " " + fingerprint,
        allow_wildcard=False,
    )


def is_legacy_managed_authorized_line(line: str, public_key: str) -> bool:
    return _is_exact_restricted_entry(
        line,
        public_key,
        LEGACY_MANAGED_COMMENT,
        allow_wildcard=True,
    )


def is_agent_managed_authorized_line(
    line: str, public_key: str, fingerprint: str
) -> bool:
    """Recognize only the canonical per-device line emitted by the Agent.

    This permits a stopped Agent deployment to fall back to one-shot pairing
    with the same Windows key. A marker alone, wildcard grant, altered option,
    or malformed device id is never sufficient.
    """

    marker = " {} {} ".format(public_key, AGENT_MANAGED_COMMENT)
    value = line.strip()
    if marker not in value:
        return False
    comment = value.rsplit(marker, 1)[1].split()
    if (
        len(comment) != 2
        or not re.fullmatch(r"[0-9a-f]{32}", comment[0])
        or comment[1] != fingerprint
    ):
        return False
    return _is_exact_restricted_entry(
        line,
        public_key,
        AGENT_MANAGED_COMMENT + " " + comment[0] + " " + fingerprint,
        allow_wildcard=False,
    )


def _assert_safe_ssh_paths(home: Path) -> Path:
    home = home.resolve(strict=True)
    ssh_dir = home / ".ssh"
    if ssh_dir.is_symlink():
        raise PairingError("~/.ssh must not be a symbolic link.")
    ssh_dir.mkdir(mode=0o700, exist_ok=True)
    if ssh_dir.stat().st_uid != os.getuid():
        raise PairingError("~/.ssh is not owned by the current Linux user.")
    os.chmod(ssh_dir, 0o700)

    authorized = ssh_dir / "authorized_keys"
    if authorized.is_symlink():
        raise PairingError("~/.ssh/authorized_keys must not be a symbolic link.")
    if authorized.exists() and authorized.stat().st_uid != os.getuid():
        raise PairingError("authorized_keys is not owned by the current Linux user.")
    return authorized


def _backup_authorized_keys(authorized: Path) -> Optional[Path]:
    if not authorized.exists():
        return None
    backup_dir = authorized.parent / "jkbridge-backups"
    if backup_dir.is_symlink():
        raise PairingError("~/.ssh/jkbridge-backups must not be a symbolic link.")
    backup_dir.mkdir(mode=0o700, exist_ok=True)
    if backup_dir.stat().st_uid != os.getuid():
        raise PairingError("~/.ssh/jkbridge-backups is not owned by the current Linux user.")
    os.chmod(backup_dir, 0o700)
    stamp = time.strftime("%Y%m%d-%H%M%S")
    candidate = backup_dir / ("authorized_keys." + stamp)
    suffix = 1
    while candidate.exists():
        candidate = backup_dir / ("authorized_keys.{}.{}".format(stamp, suffix))
        suffix += 1
    shutil.copy2(str(authorized), str(candidate))
    os.chmod(candidate, 0o600)
    return candidate


def _atomic_write_lines(path: Path, lines: Sequence[str]) -> None:
    descriptor, temporary = tempfile.mkstemp(prefix=".authorized_keys.", dir=str(path.parent))
    try:
        os.fchmod(descriptor, 0o600)
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
            for line in lines:
                stream.write(line.rstrip("\r\n") + "\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, str(path))
        os.chmod(path, 0o600)
        directory_fd = os.open(str(path.parent), os.O_RDONLY)
        try:
            os.fsync(directory_fd)
        finally:
            os.close(directory_fd)
    finally:
        try:
            os.unlink(temporary)
        except FileNotFoundError:
            pass


def _open_lock(path: Path):
    flags = os.O_RDWR | os.O_CREAT
    flags |= getattr(os, "O_CLOEXEC", 0)
    flags |= getattr(os, "O_NOFOLLOW", 0)
    try:
        descriptor = os.open(str(path), flags, 0o600)
    except OSError as exc:
        raise PairingError("The JK Bridge SSH lock file is unsafe or unavailable.") from exc
    metadata = os.fstat(descriptor)
    if (
        metadata.st_uid != os.getuid()
        or not stat.S_ISREG(metadata.st_mode)
        or metadata.st_nlink != 1
    ):
        os.close(descriptor)
        raise PairingError("The JK Bridge SSH lock file has an unsafe owner or type.")
    os.fchmod(descriptor, 0o600)
    return os.fdopen(descriptor, "a+", encoding="utf-8")


def install_authorized_key(home: Path, public_key: str, ports: Sequence[int]) -> Dict[str, Any]:
    normalized, fingerprint = normalize_ed25519_key(public_key)
    canonical_ports = validate_ports(list(ports))
    desired = build_authorized_line(normalized, fingerprint, canonical_ports)
    authorized = _assert_safe_ssh_paths(home)
    lock_path = authorized.parent / ".jkbridge.lock"

    with _open_lock(lock_path) as lock:
        fcntl.flock(lock.fileno(), fcntl.LOCK_EX)
        lines = authorized.read_text(encoding="utf-8").splitlines() if authorized.exists() else []
        matching_indexes: List[int] = []
        for index, line in enumerate(lines):
            parsed = key_from_authorized_line(line)
            if parsed and parsed[0] == normalized:
                matching_indexes.append(index)

        if len(matching_indexes) > 1:
            raise PairingError(
                "The same public key appears more than once in authorized_keys. "
                "Resolve duplicates manually before pairing."
            )

        changed = False
        if matching_indexes:
            index = matching_indexes[0]
            existing_is_managed = is_managed_authorized_line(
                lines[index], normalized, fingerprint
            )
            existing_is_legacy = is_legacy_managed_authorized_line(
                lines[index], normalized
            )
            existing_is_agent = is_agent_managed_authorized_line(
                lines[index], normalized, fingerprint
            )
            if not existing_is_managed and not existing_is_legacy and not existing_is_agent:
                raise PairingError(
                    "The same public key already exists outside a JK Bridge managed entry. "
                    "Generate a different Windows identity key instead of adding a restricted duplicate."
                )
            if lines[index].rstrip("\r\n") != desired:
                lines[index] = desired
                changed = True
        else:
            while lines and not lines[-1].strip():
                lines.pop()
            lines.append(desired)
            changed = True

        backup = None
        if changed:
            backup = _backup_authorized_keys(authorized)
            _atomic_write_lines(authorized, lines)
        elif authorized.exists():
            os.chmod(authorized, 0o600)

        return {
            "fingerprint": fingerprint,
            "changed": changed,
            "backup": str(backup) if backup else None,
            "authorizedKeys": str(authorized),
        }


def remove_managed_key(home: Path, fingerprint: str) -> Dict[str, Any]:
    authorized = _assert_safe_ssh_paths(home)
    if not authorized.exists():
        raise PairingError("authorized_keys does not exist.")
    lock_path = authorized.parent / ".jkbridge.lock"
    with _open_lock(lock_path) as lock:
        fcntl.flock(lock.fileno(), fcntl.LOCK_EX)
        lines = authorized.read_text(encoding="utf-8").splitlines()
        kept: List[str] = []
        removed = 0
        for line in lines:
            parsed = key_from_authorized_line(line)
            if (
                parsed
                and parsed[1] == fingerprint
                and is_managed_authorized_line(line, parsed[0], parsed[1])
            ):
                removed += 1
            else:
                kept.append(line)
        if removed != 1:
            raise PairingError("Expected exactly one matching JK Bridge managed key; no changes were made.")
        backup = _backup_authorized_keys(authorized)
        _atomic_write_lines(authorized, kept)
        return {"removed": removed, "backup": str(backup)}


def tailscale_identity() -> Dict[str, Any]:
    executable = shutil.which("tailscale")
    if not executable:
        raise PairingError("tailscale was not found in PATH.")
    completed = subprocess.run(
        [executable, "status", "--json"],
        check=False,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        timeout=10,
    )
    if completed.returncode != 0:
        raise PairingError("tailscale status failed: " + completed.stderr.strip())
    try:
        status = json.loads(completed.stdout)
    except json.JSONDecodeError as exc:
        raise PairingError("tailscale status returned invalid JSON.") from exc
    if status.get("BackendState") != "Running":
        raise PairingError("Tailscale must be running and logged in before pairing.")

    self_node = status.get("Self") or {}
    dns_name = str(self_node.get("DNSName") or "").strip().rstrip(".").lower()
    if not dns_name or "." not in dns_name:
        raise PairingError("Tailscale did not report a MagicDNS name for this Linux node.")
    current_tailnet = status.get("CurrentTailnet") or {}
    suffix = str(current_tailnet.get("MagicDNSSuffix") or "").strip().rstrip(".").lower()
    if not suffix:
        suffix = dns_name.split(".", 1)[1]

    ipv4 = None
    for value in self_node.get("TailscaleIPs") or []:
        try:
            address = ipaddress.ip_address(str(value))
        except ValueError:
            continue
        if isinstance(address, ipaddress.IPv4Address) and address in TAILSCALE_IPV4:
            ipv4 = str(address)
            break
    if not ipv4:
        raise PairingError("This pairing version requires a Tailscale IPv4 address.")
    return {"dnsName": dns_name, "suffix": suffix, "ipv4": ipv4}


def read_host_public_key(path: Path) -> str:
    if path.is_symlink() or not path.is_file():
        raise PairingError("The SSH Ed25519 host public key file is missing or unsafe: " + str(path))
    lines = path.read_text(encoding="utf-8").splitlines()
    if not lines:
        raise PairingError("The SSH Ed25519 host public key file is empty: " + str(path))
    first_line = lines[0]
    normalized, _ = normalize_ed25519_key(first_line)
    return normalized


def current_user() -> Tuple[str, Path]:
    if os.geteuid() == 0:
        raise PairingError("Do not run this helper with sudo. Run it as the Linux account used by JK Bridge.")
    entry = pwd.getpwuid(os.getuid())
    if not SSH_USER_PATTERN.fullmatch(entry.pw_name):
        raise PairingError("The current Linux username contains unsupported SSH characters.")
    return entry.pw_name, Path(entry.pw_dir)


class PairingHttpServer(http.server.HTTPServer):
    allow_reuse_address = False

    def __init__(
        self,
        address: Tuple[str, int],
        token: str,
        identity: Dict[str, Any],
        ssh_user: str,
        home: Path,
        ssh_port: int,
        host_public_key: str,
    ) -> None:
        super().__init__(address, PairingRequestHandler)
        self.token = token
        self.identity = identity
        self.ssh_user = ssh_user
        self.home = home
        self.ssh_port = ssh_port
        self.host_public_key = host_public_key
        self.completed = False
        self.failed_attempts = 0


class PairingRequestHandler(http.server.BaseHTTPRequestHandler):
    server: PairingHttpServer

    def log_message(self, _format: str, *args: Any) -> None:
        return

    def do_POST(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler API
        try:
            self._handle_pair()
        except PairingError as exc:
            self.server.failed_attempts += 1
            self._json_response(400, {"error": str(exc)})
        except Exception as exc:
            self.server.failed_attempts += 1
            if self.server.completed:
                print(
                    "WARNING: Linux authorization succeeded, but the response connection closed ({}). "
                    "It is safe to run pairing again.".format(type(exc).__name__),
                    file=sys.stderr,
                )
                return
            print(
                "ERROR: pairing request failed safely ({}).".format(type(exc).__name__),
                file=sys.stderr,
            )
            self._json_response(500, {"error": "Linux pairing failed safely; inspect the Linux console."})

    def _handle_pair(self) -> None:
        if self.path != "/v1/pair":
            self._json_response(404, {"error": "Unknown pairing endpoint."})
            return
        try:
            client_address = ipaddress.ip_address(self.client_address[0])
        except ValueError as exc:
            raise PairingError("The client address is invalid.") from exc
        if client_address not in TAILSCALE_IPV4 and client_address not in TAILSCALE_IPV6:
            raise PairingError("Pairing requests are accepted only from a Tailscale address.")

        expected = "Bearer " + self.server.token
        supplied = self.headers.get("Authorization", "")
        if not hmac.compare_digest(supplied, expected):
            self.server.failed_attempts += 1
            self._json_response(401, {"error": "The one-time pairing token is invalid."})
            return
        content_type = self.headers.get_content_type()
        if content_type != "application/json":
            raise PairingError("Pairing requests must use application/json.")
        try:
            content_length = int(self.headers.get("Content-Length", "0"))
        except ValueError as exc:
            raise PairingError("The request length is invalid.") from exc
        if not 1 <= content_length <= MAX_REQUEST_BYTES:
            raise PairingError("The pairing request is empty or too large.")
        try:
            payload = json.loads(self.rfile.read(content_length).decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise PairingError("The pairing request is not valid UTF-8 JSON.") from exc
        if not isinstance(payload, dict) or payload.get("protocolVersion") != PROTOCOL_VERSION:
            raise PairingError("The Windows application uses an incompatible pairing protocol.")

        public_key, fingerprint = normalize_ed25519_key(str(payload.get("clientPublicKey") or ""))
        ports = validate_ports(payload.get("remotePorts"))
        receipt = install_authorized_key(self.server.home, public_key, ports)
        response = {
            "protocolVersion": PROTOCOL_VERSION,
            "serverHost": self.server.identity["dnsName"],
            "tailnetDnsSuffix": self.server.identity["suffix"],
            "tailscaleIpv4": self.server.identity["ipv4"],
            "sshUser": self.server.ssh_user,
            "sshPort": self.server.ssh_port,
            "hostPublicKey": self.server.host_public_key,
            "displayName": socket.gethostname(),
            "clientKeyFingerprint": fingerprint,
            "permitPorts": ports,
        }
        self.server.completed = True
        print("\nPaired successfully / 配对成功")
        print("Client key: " + fingerprint)
        print("authorized_keys: " + receipt["authorizedKeys"])
        if receipt.get("backup"):
            print("Backup: " + receipt["backup"])
        self._json_response(200, response)

    def _json_response(self, status: int, payload: Dict[str, Any]) -> None:
        body = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.end_headers()
        self.wfile.write(body)
        self.wfile.flush()


def run_pair(args: argparse.Namespace) -> int:
    ssh_user, home = current_user()
    identity = tailscale_identity()
    host_public_key = read_host_public_key(Path(args.host_key))
    token = secrets.token_urlsafe(32)
    with PairingHttpServer(
        (identity["ipv4"], 0),
        token,
        identity,
        ssh_user,
        home,
        args.ssh_port,
        host_public_key,
    ) as server:
        port = server.server_address[1]
        code = "jkbridge://{}:{}/v1?token={}".format(identity["ipv4"], port, token)
        print("JK Bridge Linux one-shot pairing / Linux 一次性配对")
        print("User / 用户: " + ssh_user)
        print("Host / 主机: " + identity["dnsName"])
        print("Expires / 有效期: {} seconds".format(args.timeout))
        print("\nPaste this code into the Windows app / 将此配对码粘贴到 Windows 软件：\n")
        print(code)
        print("\nWaiting on the Tailscale interface; press Ctrl+C to cancel.")
        server.timeout = 1.0
        deadline = time.monotonic() + args.timeout
        while not server.completed and time.monotonic() < deadline:
            if server.failed_attempts >= 20:
                raise PairingError("Too many failed requests; restart the helper to obtain a new code.")
            server.handle_request()
        if not server.completed:
            raise PairingError("The one-time pairing code expired without changing authorized_keys.")
    return 0


def run_status(_args: argparse.Namespace) -> int:
    ssh_user, home = current_user()
    identity = tailscale_identity()
    authorized = _assert_safe_ssh_paths(home)
    managed: List[str] = []
    if authorized.exists():
        for line in authorized.read_text(encoding="utf-8").splitlines():
            parsed = key_from_authorized_line(line)
            if parsed and is_managed_authorized_line(line, parsed[0], parsed[1]):
                managed.append(parsed[1])
    print("User: " + ssh_user)
    print("MagicDNS: " + identity["dnsName"])
    print("Tailscale IPv4: " + identity["ipv4"])
    print("Managed JK Bridge keys: {}".format(len(managed)))
    for fingerprint in managed:
        print("- " + fingerprint)
    return 0


def run_remove(args: argparse.Namespace) -> int:
    _ssh_user, home = current_user()
    result = remove_managed_key(home, args.fingerprint)
    print("Removed one JK Bridge managed key.")
    if result.get("backup"):
        print("Backup: " + result["backup"])
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="One-shot Linux pairing helper for JK Bridge over Tailscale."
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    pair = subparsers.add_parser("pair", help="start one-shot pairing")
    pair.add_argument("--ssh-port", type=int, default=22)
    pair.add_argument("--timeout", type=int, default=600)
    pair.add_argument(
        "--host-key",
        default="/etc/ssh/ssh_host_ed25519_key.pub",
        help="trusted local Ed25519 SSH host public key",
    )
    pair.set_defaults(handler=run_pair)

    status = subparsers.add_parser("status", help="show pairing prerequisites and managed keys")
    status.set_defaults(handler=run_status)

    remove = subparsers.add_parser("remove", help="remove one managed key by SHA256 fingerprint")
    remove.add_argument("--fingerprint", required=True)
    remove.set_defaults(handler=run_remove)
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    if getattr(args, "ssh_port", 22) not in range(1, 65536):
        parser.error("--ssh-port must be between 1 and 65535")
    if getattr(args, "timeout", 600) not in range(60, 3601):
        parser.error("--timeout must be between 60 and 3600 seconds")
    try:
        return int(args.handler(args))
    except KeyboardInterrupt:
        print("\nPairing cancelled; no pending server remains.", file=sys.stderr)
        return 130
    except (PairingError, OSError, subprocess.SubprocessError) as exc:
        print("ERROR: " + str(exc), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
