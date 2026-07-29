#!/usr/bin/env python3
"""Safe, deterministic management primitives for JK Bridge TCP relays.

The module intentionally has no HTTP server or CLI.  It is an integration
boundary for the optional Linux Agent: callers validate one explicit TCP
target, inspect a read-only plan, and then apply that exact plan.  Managed
relays always listen on IPv4 loopback and use ``socat``; arbitrary proxying,
wildcards, CIDRs, UDP, shell commands, and public listeners are not supported.
"""

from __future__ import annotations

import dataclasses
import difflib
import enum
import hashlib
import hmac
import ipaddress
import json
import os
import re
import stat
import subprocess
import tempfile
import time
import urllib.parse
from pathlib import Path
from typing import Callable, Dict, List, Optional, Protocol, Sequence, Tuple


MANAGED_MARKER = "# JKBridge-Managed-Relay: schema=1"
METADATA_PREFIX = "# JKBridge-Relay-Spec: "
SKILL_MANAGED_MARKER = "# Managed by the jkbridge skill."
SLUG_PATTERN = re.compile(r"^[a-z][a-z0-9-]{0,47}$")
DNS_LABEL_PATTERN = re.compile(r"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$")
EXECUTABLE_PATTERN = re.compile(r"^/[A-Za-z0-9_./+-]+$")
DEVICE_ID_PATTERN = re.compile(r"^[0-9a-f]{32}$")
DEFAULT_SOCAT_PATH = Path("/usr/bin/socat")


class RelayError(RuntimeError):
    """Base class for expected relay-management failures."""


class RelayValidationError(RelayError):
    """A relay specification or public API argument is invalid."""


class RelaySafetyError(RelayError):
    """A filesystem object violates the no-symlink/regular-file policy."""


class RelayConflictError(RelayError):
    """A plan is blocked, stale, or would replace an unmanaged unit."""


class RelayDependencyError(RelayError):
    """The configured socat dependency is unavailable."""


class RelayCommandError(RelayError):
    """A systemctl operation failed and the file transaction was rolled back."""


class RelayConfirmationError(RelayError):
    """A mutating adoption call lacks explicit confirmation or its state token."""


@dataclasses.dataclass(frozen=True)
class RelaySpec:
    """One exact target exposed through one Linux IPv4-loopback TCP port."""

    slug: str
    relay_port: int
    target_host: str
    target_port: int
    scheme: str = "http"
    open_path: str = "/"

    @classmethod
    def create(
        cls,
        slug: str,
        relay_port: int,
        target_host: str,
        target_port: int,
        scheme: str = "http",
        open_path: str = "/",
    ) -> "RelaySpec":
        validated_slug = _validate_slug(slug)
        validated_relay_port = _validate_port(relay_port, "relay_port")
        validated_target = _validate_target_host(target_host)
        validated_target_port = _validate_port(target_port, "target_port")
        validated_scheme = _validate_scheme(scheme)
        validated_path = _validate_open_path(open_path)
        if (
            validated_relay_port == validated_target_port
            and validated_target in ("127.0.0.1", "localhost")
        ):
            raise RelayValidationError("A relay cannot forward back to its own loopback port.")
        return cls(
            slug=validated_slug,
            relay_port=validated_relay_port,
            target_host=validated_target,
            target_port=validated_target_port,
            scheme=validated_scheme,
            open_path=validated_path,
        )

    def to_metadata(self) -> Dict[str, object]:
        return {
            "openPath": self.open_path,
            "relayPort": self.relay_port,
            "scheme": self.scheme,
            "slug": self.slug,
            "targetHost": self.target_host,
            "targetPort": self.target_port,
        }

    @property
    def loopback_url(self) -> str:
        return "{}://127.0.0.1:{}{}".format(self.scheme, self.relay_port, self.open_path)


class PlanOperation(enum.Enum):
    INSTALL = "install"
    DELETE = "delete"


class PlanAction(enum.Enum):
    CREATE = "create"
    UPDATE = "update"
    NO_CHANGE = "no_change"
    DELETE = "delete"
    ABSENT = "absent"
    BLOCKED = "blocked"


class AdoptionAction(enum.Enum):
    ADOPT = "adopt"
    NO_CHANGE = "no_change"
    BLOCKED = "blocked"


@dataclasses.dataclass(frozen=True)
class RelayPlan:
    operation: PlanOperation
    action: PlanAction
    slug: str
    unit_name: str
    unit_path: Path
    spec: Optional[RelaySpec]
    desired_text: Optional[str]
    observed_sha256: Optional[str]
    reason: str
    requires_confirmation: bool = False
    affected_device_ids: Tuple[str, ...] = ()

    @property
    def can_apply(self) -> bool:
        return self.action not in (PlanAction.BLOCKED, PlanAction.ABSENT)

    def to_dict(self) -> Dict[str, object]:
        return {
            "operation": self.operation.value,
            "action": self.action.value,
            "slug": self.slug,
            "unitName": self.unit_name,
            "unitPath": str(self.unit_path),
            "spec": None if self.spec is None else self.spec.to_metadata(),
            "observedSha256": self.observed_sha256,
            "reason": self.reason,
            "requiresConfirmation": self.requires_confirmation,
            "affectedDeviceIds": list(self.affected_device_ids),
        }


@dataclasses.dataclass(frozen=True)
class CommandResult:
    returncode: int
    stdout: str = ""
    stderr: str = ""


class ProcessRunner(Protocol):
    def __call__(self, arguments: Sequence[str]) -> CommandResult:
        ...


class DependencyChecker(Protocol):
    def __call__(self, executable: Path) -> bool:
        ...


@dataclasses.dataclass(frozen=True)
class RelayProbe:
    spec: RelaySpec
    dependency_available: bool
    relay_port_in_use: Optional[bool]
    target_reachable: Optional[bool]
    listener_output: str
    target_error: str

    @property
    def ready_to_install(self) -> bool:
        return (
            self.dependency_available
            and self.relay_port_in_use is False
            and self.target_reachable is True
        )


@dataclasses.dataclass(frozen=True)
class RelayApplyResult:
    plan: RelayPlan
    applied: bool
    backup_path: Optional[Path] = None
    commands: Tuple[Tuple[str, ...], ...] = ()


@dataclasses.dataclass(frozen=True)
class RelayAdoptionPlan:
    """A state-bound, read-only proposal to adopt one exact legacy unit."""

    action: AdoptionAction
    spec: RelaySpec
    unit_name: str
    unit_path: Path
    observed_sha256: Optional[str]
    desired_sha256: str
    state_binding_token: str
    full_diff: str
    reason: str
    requires_confirmation: bool
    affected_device_ids: Tuple[str, ...] = ()

    @property
    def can_apply(self) -> bool:
        return self.action is AdoptionAction.ADOPT

    def to_dict(self) -> Dict[str, object]:
        return {
            "action": self.action.value,
            "spec": self.spec.to_metadata(),
            "unitName": self.unit_name,
            "unitPath": str(self.unit_path),
            "observedSha256": self.observed_sha256,
            "desiredSha256": self.desired_sha256,
            "stateBindingToken": self.state_binding_token,
            "fullDiff": self.full_diff,
            "reason": self.reason,
            "requiresConfirmation": self.requires_confirmation,
            "affectedDeviceIds": list(self.affected_device_ids),
        }


@dataclasses.dataclass(frozen=True)
class RelayAdoptionResult:
    plan: RelayAdoptionPlan
    applied: bool
    backup_path: Optional[Path] = None
    commands: Tuple[Tuple[str, ...], ...] = ()


def _validate_slug(value: object) -> str:
    if not isinstance(value, str) or not SLUG_PATTERN.fullmatch(value):
        raise RelayValidationError(
            "slug must start with a lowercase letter and contain only lowercase letters, digits, or hyphens"
        )
    return value


def _validate_port(value: object, field: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or not 1 <= value <= 65535:
        raise RelayValidationError("{} must be an integer from 1 to 65535".format(field))
    return value


def _validate_target_host(value: object) -> str:
    if not isinstance(value, str):
        raise RelayValidationError("target_host must be an IPv4 address or DNS name")
    host = value.strip()
    if not host or host != value or len(host) > 253:
        raise RelayValidationError("target_host must be a non-empty canonical host")
    if any(char.isspace() or ord(char) < 33 or ord(char) == 127 for char in host):
        raise RelayValidationError("target_host contains unsafe characters")
    if any(token in host for token in ("*", "/", "\\", ":", "@", "?", "#")):
        raise RelayValidationError("target_host must be one exact IPv4 address or DNS name")
    try:
        address = ipaddress.ip_address(host)
    except ValueError:
        if host.endswith("."):
            host = host[:-1]
        labels = host.split(".")
        if not host or any(not DNS_LABEL_PATTERN.fullmatch(label) for label in labels):
            raise RelayValidationError("target_host is not a valid DNS name")
        return host.lower()
    if not isinstance(address, ipaddress.IPv4Address):
        raise RelayValidationError("only an exact IPv4 address or DNS name is supported")
    if address.is_unspecified or address.is_multicast or str(address) == "255.255.255.255":
        raise RelayValidationError("target_host cannot be wildcard, multicast, or broadcast")
    return str(address)


def _validate_scheme(value: object) -> str:
    if not isinstance(value, str) or value not in ("http", "https"):
        raise RelayValidationError("scheme must be exactly http or https")
    return value


def _validate_open_path(value: object) -> str:
    if not isinstance(value, str) or not value or len(value) > 512:
        raise RelayValidationError("open_path must be a non-empty string no longer than 512 characters")
    if any(char.isspace() or ord(char) < 32 or ord(char) == 127 for char in value):
        raise RelayValidationError("open_path cannot contain whitespace or control characters")
    parsed = urllib.parse.urlsplit(value)
    if parsed.scheme or parsed.netloc or not parsed.path.startswith("/") or parsed.path.startswith("//"):
        raise RelayValidationError("open_path must be a local absolute path, optionally with query or fragment")
    return value


def _validate_executable_path(value: Path) -> Path:
    path = Path(value)
    if not path.is_absolute() or not EXECUTABLE_PATTERN.fullmatch(str(path)) or ".." in path.parts:
        raise RelayValidationError("socat_path must be a safe absolute executable path")
    return path


def _validate_affected_device_ids(values: Sequence[str]) -> Tuple[str, ...]:
    if isinstance(values, (str, bytes)):
        raise RelayValidationError("affected_device_ids must be a sequence of device ids")
    normalized = tuple(sorted(set(values)))
    if len(normalized) > 64 or any(
        not isinstance(value, str) or not DEVICE_ID_PATTERN.fullmatch(value)
        for value in normalized
    ):
        raise RelayValidationError("affected_device_ids contains an invalid device id")
    return normalized


def unit_name_for_slug(slug: str) -> str:
    return "jkbridge-relay-{}.service".format(_validate_slug(slug))


def render_systemd_unit(spec: RelaySpec, socat_path: Path = DEFAULT_SOCAT_PATH) -> str:
    spec = RelaySpec.create(**dataclasses.asdict(spec))
    executable = _validate_executable_path(socat_path)
    target_kind = "TCP4" if _is_ipv4(spec.target_host) else "TCP"
    metadata = json.dumps(spec.to_metadata(), ensure_ascii=True, sort_keys=True, separators=(",", ":"))
    lines = [
        MANAGED_MARKER,
        METADATA_PREFIX + metadata,
        "[Unit]",
        "Description=JK Bridge managed relay ({})".format(spec.slug),
        "After=network-online.target",
        "Wants=network-online.target",
        "",
        "[Service]",
        "Type=simple",
        "ExecStart={} TCP4-LISTEN:{},bind=127.0.0.1,reuseaddr,fork {}:{},connect-timeout=10".format(
            executable,
            spec.relay_port,
            target_kind,
            "{}:{}".format(spec.target_host, spec.target_port),
        ),
        "Restart=on-failure",
        "RestartSec=2s",
        "NoNewPrivileges=true",
        "PrivateTmp=true",
        "ProtectSystem=strict",
        "ProtectHome=true",
        "RestrictAddressFamilies=AF_INET AF_INET6",
        "RestrictSUIDSGID=true",
        "UMask=0077",
        "",
        "[Install]",
        "WantedBy=default.target",
        "",
    ]
    return "\n".join(lines)


def _is_ipv4(host: str) -> bool:
    try:
        return isinstance(ipaddress.ip_address(host), ipaddress.IPv4Address)
    except ValueError:
        return False


def _sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _require_file_digest(path: Path, expected_sha256: Optional[str], message: str) -> bytes:
    content = _read_regular(path)
    if content is None or expected_sha256 is None or _sha256(content) != expected_sha256:
        raise RelayConflictError(message)
    return content


def _assert_no_symlink_components(path: Path) -> None:
    absolute = Path(path).expanduser()
    if not absolute.is_absolute():
        raise RelaySafetyError("managed paths must be absolute")
    current = Path(absolute.anchor)
    missing_parent = False
    for part in absolute.parts[1:]:
        current = current / part
        if missing_parent:
            continue
        try:
            mode = os.lstat(str(current)).st_mode
        except FileNotFoundError:
            missing_parent = True
            continue
        if stat.S_ISLNK(mode):
            raise RelaySafetyError("refusing a path containing a symbolic link: {}".format(current))


def _read_regular(path: Path) -> Optional[bytes]:
    _assert_no_symlink_components(path)
    try:
        details = os.lstat(str(path))
    except FileNotFoundError:
        return None
    if not stat.S_ISREG(details.st_mode):
        raise RelaySafetyError("managed unit is not a regular file: {}".format(path))
    return path.read_bytes()


def _ensure_safe_directory(path: Path, mode: int = 0o700) -> None:
    _assert_no_symlink_components(path)
    path.mkdir(parents=True, exist_ok=True, mode=mode)
    _assert_no_symlink_components(path)
    if not path.is_dir():
        raise RelaySafetyError("expected a directory: {}".format(path))


def atomic_write_bytes(path: Path, content: bytes, mode: int = 0o600) -> None:
    """Atomically replace one regular file without following symlinks."""

    path = Path(path)
    _ensure_safe_directory(path.parent)
    existing = _read_regular(path)
    del existing
    descriptor, temporary_name = tempfile.mkstemp(prefix="." + path.name + ".", dir=str(path.parent))
    temporary = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "wb") as handle:
            os.fchmod(handle.fileno(), mode)
            handle.write(content)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(str(temporary), str(path))
        directory_descriptor = os.open(str(path.parent), os.O_RDONLY)
        try:
            os.fsync(directory_descriptor)
        finally:
            os.close(directory_descriptor)
    finally:
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass


def atomic_write_text(path: Path, content: str, mode: int = 0o600) -> None:
    atomic_write_bytes(path, content.encode("utf-8"), mode=mode)


def create_backup(
    source: Path,
    backup_dir: Path,
    *,
    clock_ns: Callable[[], int] = time.time_ns,
) -> Path:
    """Create a durable, uniquely named backup of one safe regular file."""

    source = Path(source)
    content = _read_regular(source)
    if content is None:
        raise RelaySafetyError("cannot back up a file that does not exist")
    backup_dir = Path(backup_dir)
    _ensure_safe_directory(backup_dir)
    stem = "{}.{}.{}.bak".format(source.name, clock_ns(), _sha256(content)[:12])
    destination = backup_dir / stem
    counter = 0
    while destination.exists():
        counter += 1
        destination = backup_dir / "{}.{}".format(stem, counter)
    atomic_write_bytes(destination, content, mode=0o600)
    return destination


def rollback_file(
    destination: Path,
    backup_path: Optional[Path],
    *,
    created: bool,
    expected_sha256: Optional[str] = None,
) -> None:
    """Restore a backup, or remove a newly-created managed file."""

    destination = Path(destination)
    current = _read_regular(destination)
    if created:
        if current is not None:
            if expected_sha256 is None or _sha256(current) != expected_sha256:
                raise RelaySafetyError("refusing to remove a file that changed during rollback")
            destination.unlink()
        return
    if backup_path is None:
        raise RelaySafetyError("rollback of an existing file requires a backup")
    backup = _read_regular(Path(backup_path))
    if backup is None:
        raise RelaySafetyError("rollback backup is missing")
    if current == backup:
        return
    if current is not None:
        if expected_sha256 is None or _sha256(current) != expected_sha256:
            raise RelaySafetyError("refusing to overwrite a file that changed during rollback")
    atomic_write_bytes(destination, backup, mode=0o600)


def _parse_managed_spec(content: bytes) -> Optional[RelaySpec]:
    try:
        text = content.decode("utf-8")
    except UnicodeDecodeError:
        return None
    lines = text.splitlines()
    if len(lines) < 2 or lines[0] != MANAGED_MARKER or not lines[1].startswith(METADATA_PREFIX):
        return None
    try:
        value = json.loads(lines[1][len(METADATA_PREFIX) :])
        if not isinstance(value, dict):
            return None
        return RelaySpec.create(
            slug=value.get("slug"),
            relay_port=value.get("relayPort"),
            target_host=value.get("targetHost"),
            target_port=value.get("targetPort"),
            scheme=value.get("scheme"),
            open_path=value.get("openPath"),
        )
    except (ValueError, RelayValidationError):
        return None


def _default_runner(arguments: Sequence[str]) -> CommandResult:
    completed = subprocess.run(
        list(arguments),
        check=False,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    return CommandResult(completed.returncode, completed.stdout, completed.stderr)


def _default_dependency_checker(executable: Path) -> bool:
    try:
        resolved = executable.resolve(strict=True)
    except (FileNotFoundError, OSError):
        return False
    return resolved.is_file() and os.access(str(resolved), os.X_OK)


def _checked_command(runner: ProcessRunner, arguments: Sequence[str]) -> CommandResult:
    result = runner(tuple(str(argument) for argument in arguments))
    if result.returncode != 0:
        detail = result.stderr.strip() or result.stdout.strip() or "exit {}".format(result.returncode)
        raise RelayCommandError("command failed: {} ({})".format(arguments[0], detail))
    return result


def probe_relay(
    spec: RelaySpec,
    *,
    socat_path: Path = DEFAULT_SOCAT_PATH,
    dependency_checker: DependencyChecker = _default_dependency_checker,
    runner: ProcessRunner = _default_runner,
    ss_path: str = "/usr/bin/ss",
) -> RelayProbe:
    """Read-only dependency, listener, and target-connectivity probes."""

    spec = RelaySpec.create(**dataclasses.asdict(spec))
    executable = _validate_executable_path(socat_path)
    dependency_available = bool(dependency_checker(executable))
    listener = runner((ss_path, "-H", "-ltn", "sport = :{}".format(spec.relay_port)))
    listener_in_use = None if listener.returncode != 0 else bool(listener.stdout.strip())
    target_reachable: Optional[bool] = None
    target_error = ""
    if dependency_available:
        target_kind = "TCP4" if _is_ipv4(spec.target_host) else "TCP"
        target = runner(
            (
                str(executable),
                "-T",
                "3",
                "-u",
                "/dev/null",
                "{}:{},connect-timeout=3".format(
                    target_kind, "{}:{}".format(spec.target_host, spec.target_port)
                ),
            )
        )
        target_reachable = target.returncode == 0
        target_error = target.stderr.strip() if target.returncode else ""
    return RelayProbe(
        spec=spec,
        dependency_available=dependency_available,
        relay_port_in_use=listener_in_use,
        target_reachable=target_reachable,
        listener_output=listener.stdout,
        target_error=target_error,
    )


def _allowed_adoption_unit_names(slug: str) -> Tuple[str, str]:
    validated = _validate_slug(slug)
    return (
        "jkbridge-{}-relay.service".format(validated),
        unit_name_for_slug(validated),
    )


def _default_user_unit_search_dirs(unit_dir: Path) -> Tuple[Path, ...]:
    home = Path.home()
    config_home = Path(os.environ.get("XDG_CONFIG_HOME", str(home / ".config")))
    data_home = Path(os.environ.get("XDG_DATA_HOME", str(home / ".local" / "share")))
    runtime_home = Path(os.environ.get("XDG_RUNTIME_DIR", "/run/user/{}".format(os.geteuid())))
    candidates = (
        unit_dir,
        config_home / "systemd" / "user.control",
        config_home / "systemd" / "user",
        Path("/etc/systemd/user"),
        runtime_home / "systemd" / "user.control",
        runtime_home / "systemd" / "user",
        Path("/run/systemd/user"),
        data_home / "systemd" / "user",
        Path("/usr/local/lib/systemd/user"),
        Path("/usr/lib/systemd/user"),
    )
    unique: List[Path] = []
    for candidate in candidates:
        expanded = candidate.expanduser()
        if expanded not in unique:
            unique.append(expanded)
    return tuple(unique)


def _applicable_drop_in_names(unit_name: str) -> Tuple[str, ...]:
    if not unit_name.endswith(".service"):
        raise RelayValidationError("relay adoption supports service units only")
    stem = unit_name[: -len(".service")]
    names = [unit_name + ".d"]
    for index, character in enumerate(stem):
        if character == "-":
            names.append(stem[: index + 1] + ".service.d")
    names.append("service.d")
    return tuple(dict.fromkeys(names))


def _parse_unit_directives(text: str) -> Dict[str, Dict[str, str]]:
    """Parse the deliberately small unit subset accepted for adoption."""

    if "\x00" in text or "\\\n" in text or "\\\r\n" in text:
        raise RelayConflictError("unit contains a NUL or line continuation")
    sections: Dict[str, Dict[str, str]] = {}
    current: Optional[str] = None
    for number, raw_line in enumerate(text.splitlines(), 1):
        line = raw_line.strip()
        if not line or line.startswith("#") or line.startswith(";"):
            continue
        if line.startswith("[") and line.endswith("]"):
            section = line[1:-1]
            if section not in ("Unit", "Service", "Install") or section in sections:
                raise RelayConflictError("unit contains an unknown or duplicate section at line {}".format(number))
            sections[section] = {}
            current = section
            continue
        if current is None or "=" not in line:
            raise RelayConflictError("unit contains an invalid directive at line {}".format(number))
        key, value = line.split("=", 1)
        if not re.fullmatch(r"[A-Za-z][A-Za-z0-9]*", key) or not value:
            raise RelayConflictError("unit contains an invalid directive at line {}".format(number))
        if key in sections[current]:
            raise RelayConflictError("unit contains duplicate {} directives".format(key))
        sections[current][key] = value
    if set(sections) != {"Unit", "Service", "Install"}:
        raise RelayConflictError("unit must contain exactly Unit, Service, and Install sections")
    return sections


def _require_exact_directives(
    section: str,
    directives: Dict[str, str],
    allowed: Sequence[str],
    required: Sequence[str],
) -> None:
    unknown = sorted(set(directives) - set(allowed))
    missing = sorted(set(required) - set(directives))
    if unknown:
        raise RelayConflictError(
            "{} contains unsupported directive {}".format(section, unknown[0])
        )
    if missing:
        raise RelayConflictError("{} is missing required directive {}".format(section, missing[0]))


def _strict_parse_skill_unit(content: bytes, spec: RelaySpec, socat_path: Path) -> None:
    """Fail unless a legacy Skill unit is exactly one safe equivalent relay."""

    try:
        text = content.decode("utf-8")
    except UnicodeDecodeError as exc:
        raise RelayConflictError("unit is not valid UTF-8") from exc
    lines = text.splitlines()
    first_content = next((line.strip() for line in lines if line.strip()), "")
    if first_content != SKILL_MANAGED_MARKER:
        raise RelayConflictError("unit does not carry the exact JK Bridge Skill provenance marker")

    sections = _parse_unit_directives(text)
    unit = sections["Unit"]
    service = sections["Service"]
    install = sections["Install"]
    _require_exact_directives(
        "Unit",
        unit,
        ("Description", "After", "Wants"),
        ("Description", "After", "Wants"),
    )
    for field in ("After", "Wants"):
        dependencies = unit[field].split()
        if not dependencies or any(
            dependency not in ("network-online.target", "tailscaled.service")
            for dependency in dependencies
        ):
            raise RelayConflictError("Unit {} contains an unapproved dependency".format(field))
    if any(ord(character) < 32 or character in "%$`" for character in unit["Description"]):
        raise RelayConflictError("Unit Description contains expansion or control characters")

    allowed_service = (
        "Type",
        "ExecStart",
        "Restart",
        "RestartSec",
        "NoNewPrivileges",
        "PrivateTmp",
        "ProtectSystem",
        "ProtectHome",
        "RestrictAddressFamilies",
        "RestrictSUIDSGID",
        "UMask",
    )
    required_service = (
        "Type",
        "ExecStart",
        "Restart",
        "RestartSec",
        "NoNewPrivileges",
        "PrivateTmp",
        "ProtectSystem",
        "ProtectHome",
        "RestrictAddressFamilies",
    )
    _require_exact_directives("Service", service, allowed_service, required_service)
    expected_values = {
        "Type": "simple",
        "Restart": "on-failure",
        "NoNewPrivileges": "true",
        "PrivateTmp": "true",
        "ProtectSystem": "strict",
        "ProtectHome": "true",
    }
    for key, expected in expected_values.items():
        if service[key] != expected:
            raise RelayConflictError("Service {} is not the required safe value".format(key))
    restart_match = re.fullmatch(r"([1-9][0-9]?)(?:s)?", service["RestartSec"])
    if restart_match is None or int(restart_match.group(1)) > 60:
        raise RelayConflictError("Service RestartSec is outside the accepted range")
    families = service["RestrictAddressFamilies"].split()
    if (
        "AF_INET" not in families
        or len(families) != len(set(families))
        or any(family not in ("AF_UNIX", "AF_INET", "AF_INET6") for family in families)
    ):
        raise RelayConflictError("Service RestrictAddressFamilies is not narrowly scoped")
    if service.get("RestrictSUIDSGID", "true") != "true":
        raise RelayConflictError("Service RestrictSUIDSGID is not the required safe value")
    if service.get("UMask", "0077") not in ("0077", "077"):
        raise RelayConflictError("Service UMask is not restrictive")

    exec_start = service["ExecStart"]
    if any(character in exec_start for character in ('"', "'", "`", "$", ";", "|", "&", ">", "<")):
        raise RelayConflictError("ExecStart contains quoting, expansion, or shell syntax")
    arguments = exec_start.split()
    if len(arguments) != 3 or arguments[0] != str(socat_path):
        raise RelayConflictError("ExecStart must invoke only the configured socat executable")

    listen_parts = arguments[1].split(",")
    listen_match = re.fullmatch(r"TCP4-LISTEN:([0-9]{1,5})", listen_parts[0])
    if listen_match is None or set(listen_parts[1:]) != {"bind=127.0.0.1", "reuseaddr", "fork"}:
        raise RelayConflictError("ExecStart must have one exact 127.0.0.1 TCP listener")
    if len(listen_parts[1:]) != 3:
        raise RelayConflictError("ExecStart contains duplicate or additional listener options")

    target_parts = arguments[2].split(",")
    target_match = re.fullmatch(r"(TCP4|TCP):([^:]+):([0-9]{1,5})", target_parts[0])
    if target_match is None or len(target_parts) > 2:
        raise RelayConflictError("ExecStart must have one explicit TCP target")
    if len(target_parts) == 2:
        timeout = re.fullmatch(r"connect-timeout=([1-9][0-9]?)", target_parts[1])
        if timeout is None or int(timeout.group(1)) > 60:
            raise RelayConflictError("ExecStart has an unsupported target option")
    parsed_relay_port = _validate_port(int(listen_match.group(1)), "relay_port")
    parsed_target_host = _validate_target_host(target_match.group(2))
    parsed_target_port = _validate_port(int(target_match.group(3)), "target_port")
    if (
        parsed_relay_port != spec.relay_port
        or parsed_target_host != spec.target_host
        or parsed_target_port != spec.target_port
    ):
        raise RelayConflictError("existing relay mapping does not exactly match the requested specification")

    _require_exact_directives("Install", install, ("WantedBy",), ("WantedBy",))
    if install["WantedBy"] != "default.target":
        raise RelayConflictError("Install WantedBy must be exactly default.target")


def _full_unit_diff(unit_name: str, before: str, after: str) -> str:
    lines = list(
        difflib.unified_diff(
            before.splitlines(),
            after.splitlines(),
            fromfile=unit_name + ".existing",
            tofile=unit_name + ".jkbridge-managed",
            lineterm="",
        )
    )
    return "\n".join(lines) + ("\n" if lines else "")


def _adoption_state_token(
    spec: RelaySpec,
    unit_name: str,
    unit_path: Path,
    observed_sha256: Optional[str],
    desired_sha256: str,
    affected_device_ids: Sequence[str],
) -> str:
    affected = _validate_affected_device_ids(affected_device_ids)
    state = {
        "affectedDeviceIds": list(affected),
        "desiredSha256": desired_sha256,
        "observedSha256": observed_sha256,
        "spec": spec.to_metadata(),
        "unitName": unit_name,
        "unitPath": str(unit_path),
    }
    encoded = json.dumps(state, ensure_ascii=True, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return "sha256:" + _sha256(encoded)


class RelayManager:
    """Plan and apply exact systemd user relay units.

    ``unit_dir`` and ``backup_dir`` are explicit to keep the class testable and
    prevent it from guessing a user's home.  The Linux Agent should construct
    this class once with its approved state locations.
    """

    def __init__(
        self,
        unit_dir: Path,
        backup_dir: Path,
        *,
        socat_path: Path = DEFAULT_SOCAT_PATH,
        systemctl_path: str = "/usr/bin/systemctl",
        runner: ProcessRunner = _default_runner,
        dependency_checker: DependencyChecker = _default_dependency_checker,
        clock_ns: Callable[[], int] = time.time_ns,
        unit_search_dirs: Optional[Sequence[Path]] = None,
    ) -> None:
        self.unit_dir = Path(unit_dir).expanduser()
        self.backup_dir = Path(backup_dir).expanduser()
        if not self.unit_dir.is_absolute() or not self.backup_dir.is_absolute():
            raise RelayValidationError("unit_dir and backup_dir must be absolute")
        self.socat_path = _validate_executable_path(socat_path)
        if not EXECUTABLE_PATTERN.fullmatch(systemctl_path):
            raise RelayValidationError("systemctl_path must be a safe absolute executable path")
        self.systemctl_path = systemctl_path
        self.runner = runner
        self.dependency_checker = dependency_checker
        self.clock_ns = clock_ns
        search_dirs = (
            _default_user_unit_search_dirs(self.unit_dir)
            if unit_search_dirs is None
            else tuple(Path(path).expanduser() for path in unit_search_dirs)
        )
        if not search_dirs or any(not path.is_absolute() for path in search_dirs):
            raise RelayValidationError("unit_search_dirs must contain absolute paths")
        self.unit_search_dirs = tuple(dict.fromkeys(search_dirs))

    def _path(self, slug: str) -> Path:
        return self.unit_dir / unit_name_for_slug(slug)

    def plan_install(
        self,
        spec: RelaySpec,
        *,
        affected_device_ids: Sequence[str] = (),
    ) -> RelayPlan:
        """Return an installation plan without creating directories or files."""

        spec = RelaySpec.create(**dataclasses.asdict(spec))
        affected = _validate_affected_device_ids(affected_device_ids)
        path = self._path(spec.slug)
        desired = render_systemd_unit(spec, self.socat_path)
        try:
            current = _read_regular(path)
        except RelaySafetyError as exc:
            return self._blocked(
                PlanOperation.INSTALL, spec.slug, path, spec, str(exc), affected
            )
        if current is None:
            return RelayPlan(
                PlanOperation.INSTALL,
                PlanAction.CREATE,
                spec.slug,
                path.name,
                path,
                spec,
                desired,
                None,
                "managed relay unit does not exist",
                affected_device_ids=affected,
            )
        digest = _sha256(current)
        parsed = _parse_managed_spec(current)
        if parsed is None or parsed.slug != spec.slug:
            return RelayPlan(
                PlanOperation.INSTALL,
                PlanAction.BLOCKED,
                spec.slug,
                path.name,
                path,
                spec,
                desired,
                digest,
                "an existing unit is not a valid JK Bridge managed relay; it will not be overwritten",
                affected_device_ids=affected,
            )
        if current == desired.encode("utf-8"):
            action = PlanAction.NO_CHANGE
            reason = "managed relay already matches the requested specification"
        else:
            action = PlanAction.UPDATE
            reason = "managed relay exists with a different specification"
        return RelayPlan(
            PlanOperation.INSTALL,
            action,
            spec.slug,
            path.name,
            path,
            spec,
            desired,
            digest,
            reason,
            affected_device_ids=affected,
        )

    def plan_delete(self, slug: str) -> RelayPlan:
        """Return a deletion plan without stopping services or changing files."""

        validated_slug = _validate_slug(slug)
        path = self._path(validated_slug)
        try:
            current = _read_regular(path)
        except RelaySafetyError as exc:
            return self._blocked(PlanOperation.DELETE, validated_slug, path, None, str(exc))
        if current is None:
            return RelayPlan(
                PlanOperation.DELETE,
                PlanAction.ABSENT,
                validated_slug,
                path.name,
                path,
                None,
                None,
                None,
                "managed relay unit does not exist",
            )
        digest = _sha256(current)
        spec = _parse_managed_spec(current)
        if spec is None or spec.slug != validated_slug:
            return RelayPlan(
                PlanOperation.DELETE,
                PlanAction.BLOCKED,
                validated_slug,
                path.name,
                path,
                None,
                None,
                digest,
                "an existing unit is not a valid JK Bridge managed relay; it will not be deleted",
            )
        return RelayPlan(
            PlanOperation.DELETE,
            PlanAction.DELETE,
            validated_slug,
            path.name,
            path,
            spec,
            None,
            digest,
            "managed relay will be disabled, backed up, and removed",
            requires_confirmation=True,
        )

    def plan_adopt_existing(
        self,
        spec: RelaySpec,
        unit_name: str,
        unit_path: Path,
        *,
        affected_device_ids: Sequence[str] = (),
    ) -> RelayAdoptionPlan:
        """Strictly inspect one legacy Skill relay without changing it.

        The exact name and path are caller-supplied so an Agent API cannot
        silently broaden a request into directory discovery.  Only the legacy
        ``jkbridge-<slug>-relay.service`` and canonical
        ``jkbridge-relay-<slug>.service`` names are accepted.
        """

        spec = RelaySpec.create(**dataclasses.asdict(spec))
        affected = _validate_affected_device_ids(affected_device_ids)
        desired_text = render_systemd_unit(spec, self.socat_path)
        desired_sha256 = _sha256(desired_text.encode("utf-8"))
        candidate_name = str(unit_name)
        candidate_path = Path(unit_path).expanduser()
        expected_path = self.unit_dir / candidate_name
        if (
            candidate_name not in _allowed_adoption_unit_names(spec.slug)
            or Path(candidate_name).name != candidate_name
            or not candidate_path.is_absolute()
            or candidate_path != expected_path
        ):
            return self._blocked_adoption(
                spec,
                candidate_name,
                candidate_path,
                desired_sha256,
                None,
                "unit name/path is outside the exact JK Bridge adoption allowlist",
                affected,
            )
        try:
            current = _read_regular(candidate_path)
        except RelaySafetyError as exc:
            return self._blocked_adoption(
                spec,
                candidate_name,
                candidate_path,
                desired_sha256,
                None,
                str(exc),
                affected,
            )
        if current is None:
            return self._blocked_adoption(
                spec,
                candidate_name,
                candidate_path,
                desired_sha256,
                None,
                "the exact unit does not exist",
                affected,
            )
        details = os.lstat(str(candidate_path))
        if details.st_uid != os.geteuid() or details.st_mode & 0o002:
            return self._blocked_adoption(
                spec,
                candidate_name,
                candidate_path,
                desired_sha256,
                _sha256(current),
                "unit must be owned by the current user and not world writable",
                affected,
            )
        for search_dir in self.unit_search_dirs:
            for drop_in_name in _applicable_drop_in_names(candidate_name):
                drop_in_path = search_dir / drop_in_name
                try:
                    _assert_no_symlink_components(drop_in_path)
                    os.lstat(str(drop_in_path))
                except FileNotFoundError:
                    continue
                except RelaySafetyError as exc:
                    return self._blocked_adoption(
                        spec,
                        candidate_name,
                        candidate_path,
                        desired_sha256,
                        _sha256(current),
                        str(exc),
                        affected,
                    )
                return self._blocked_adoption(
                    spec,
                    candidate_name,
                    candidate_path,
                    desired_sha256,
                    _sha256(current),
                    "unit has an applicable drop-in path and cannot be proven equivalent",
                    affected,
                )
        observed_sha256 = _sha256(current)
        parsed_managed = _parse_managed_spec(current)
        if parsed_managed is not None:
            if parsed_managed != spec:
                return self._blocked_adoption(
                    spec,
                    candidate_name,
                    candidate_path,
                    desired_sha256,
                    observed_sha256,
                    "existing JK Bridge managed metadata does not match the requested specification",
                    affected,
                )
            if current != desired_text.encode("utf-8"):
                return self._blocked_adoption(
                    spec,
                    candidate_name,
                    candidate_path,
                    desired_sha256,
                    observed_sha256,
                    "an already-managed unit is non-canonical; use the normal update plan",
                    affected,
                )
            token = _adoption_state_token(
                spec,
                candidate_name,
                candidate_path,
                observed_sha256,
                desired_sha256,
                affected,
            )
            return RelayAdoptionPlan(
                AdoptionAction.NO_CHANGE,
                spec,
                candidate_name,
                candidate_path,
                observed_sha256,
                desired_sha256,
                token,
                "",
                "unit is already an equivalent canonical JK Bridge managed relay",
                False,
                affected,
            )
        try:
            _strict_parse_skill_unit(current, spec, self.socat_path)
        except (RelayError, ValueError) as exc:
            return self._blocked_adoption(
                spec,
                candidate_name,
                candidate_path,
                desired_sha256,
                observed_sha256,
                str(exc),
                affected,
            )
        current_text = current.decode("utf-8")
        token = _adoption_state_token(
            spec,
            candidate_name,
            candidate_path,
            observed_sha256,
            desired_sha256,
            affected,
        )
        return RelayAdoptionPlan(
            AdoptionAction.ADOPT,
            spec,
            candidate_name,
            candidate_path,
            observed_sha256,
            desired_sha256,
            token,
            _full_unit_diff(candidate_name, current_text, desired_text),
            "safe legacy Skill relay is exactly equivalent and can be normalized",
            True,
            affected,
        )

    def apply_adoption(
        self,
        plan: RelayAdoptionPlan,
        *,
        confirmed: bool = False,
        state_binding_token: str = "",
    ) -> RelayAdoptionResult:
        """Apply one fresh adoption plan after explicit confirmation and token binding."""

        if plan.action is AdoptionAction.NO_CHANGE:
            return RelayAdoptionResult(plan, False)
        if plan.action is AdoptionAction.BLOCKED:
            raise RelayConflictError(plan.reason)
        if not confirmed:
            raise RelayConfirmationError("relay adoption requires confirmed=True")
        if not state_binding_token or not hmac.compare_digest(
            state_binding_token, plan.state_binding_token
        ):
            raise RelayConfirmationError("relay adoption state-binding token is missing or invalid")
        current = self.plan_adopt_existing(
            plan.spec,
            plan.unit_name,
            plan.unit_path,
            affected_device_ids=plan.affected_device_ids,
        )
        if (
            current.action is not AdoptionAction.ADOPT
            or current.observed_sha256 != plan.observed_sha256
            or current.desired_sha256 != plan.desired_sha256
            or current.full_diff != plan.full_diff
            or current.affected_device_ids != plan.affected_device_ids
            or not hmac.compare_digest(current.state_binding_token, plan.state_binding_token)
        ):
            raise RelayConflictError("relay adoption plan is stale; inspect a new plan before applying")
        if not self.dependency_checker(self.socat_path):
            raise RelayDependencyError("socat is not installed or executable at {}".format(self.socat_path))

        backup = create_backup(plan.unit_path, self.backup_dir, clock_ns=self.clock_ns)
        _require_file_digest(
            backup,
            plan.observed_sha256,
            "relay changed while the adoption backup was being created",
        )
        _require_file_digest(
            plan.unit_path,
            plan.observed_sha256,
            "relay changed after the adoption plan was confirmed",
        )
        desired_text = render_systemd_unit(plan.spec, self.socat_path)
        commands: List[Tuple[str, ...]] = []
        try:
            atomic_write_text(plan.unit_path, desired_text, mode=0o600)
            commands.append(self._systemctl("daemon-reload"))
            # Preserve inactive state while ensuring an active legacy relay
            # immediately adopts the canonical ExecStart.
            commands.append(self._systemctl("try-restart", plan.unit_name))
        except Exception as exc:
            rollback_file(
                plan.unit_path,
                backup,
                created=False,
                expected_sha256=plan.desired_sha256,
            )
            self._best_effort_systemctl("daemon-reload")
            self._best_effort_systemctl("try-restart", plan.unit_name)
            if not isinstance(exc, RelayError):
                raise RelayCommandError("relay adoption failed; legacy unit was rolled back") from exc
            raise RelayCommandError("{}; legacy unit was rolled back".format(exc)) from exc
        return RelayAdoptionResult(plan, True, backup, tuple(commands))

    def probe(self, spec: RelaySpec) -> RelayProbe:
        return probe_relay(
            spec,
            socat_path=self.socat_path,
            dependency_checker=self.dependency_checker,
            runner=self.runner,
        )

    def apply_install(self, plan: RelayPlan) -> RelayApplyResult:
        """Apply one fresh CREATE/UPDATE plan; roll the unit file back on failure."""

        if plan.operation is not PlanOperation.INSTALL or plan.spec is None:
            raise RelayConflictError("apply_install requires an installation plan")
        current = self.plan_install(
            plan.spec, affected_device_ids=plan.affected_device_ids
        )
        self._require_same_plan(plan, current)
        if current.action is PlanAction.BLOCKED:
            raise RelayConflictError(current.reason)
        if current.action is PlanAction.NO_CHANGE:
            return RelayApplyResult(plan=current, applied=False)
        if not self.dependency_checker(self.socat_path):
            raise RelayDependencyError("socat is not installed or executable at {}".format(self.socat_path))
        _ensure_safe_directory(self.unit_dir)
        backup = None
        created = current.action is PlanAction.CREATE
        if not created:
            backup = create_backup(current.unit_path, self.backup_dir, clock_ns=self.clock_ns)
            _require_file_digest(
                backup,
                current.observed_sha256,
                "relay changed while the update backup was being created",
            )
            _require_file_digest(
                current.unit_path,
                current.observed_sha256,
                "relay changed after the update plan was inspected",
            )
        elif _read_regular(current.unit_path) is not None:
            raise RelayConflictError("another unit appeared after the create plan was inspected")
        commands: List[Tuple[str, ...]] = []
        desired_sha256 = _sha256((current.desired_text or "").encode("utf-8"))
        try:
            atomic_write_text(current.unit_path, current.desired_text or "", mode=0o600)
            commands.append(self._systemctl("daemon-reload"))
            if created:
                commands.append(self._systemctl("enable", "--now", current.unit_name))
            else:
                # `enable --now` does not restart an already-active service, so
                # an UPDATE must explicitly restart after daemon-reload for the
                # changed ExecStart to take effect.
                commands.append(self._systemctl("enable", current.unit_name))
                commands.append(self._systemctl("restart", current.unit_name))
        except Exception as exc:
            rollback_file(
                current.unit_path,
                backup,
                created=created,
                expected_sha256=desired_sha256,
            )
            self._best_effort_systemctl("daemon-reload")
            if not created:
                self._best_effort_systemctl("restart", current.unit_name)
            if not isinstance(exc, RelayError):
                raise RelayCommandError("relay installation failed; unit file was rolled back") from exc
            raise RelayCommandError("{}; relay unit file was rolled back".format(exc)) from exc
        return RelayApplyResult(current, True, backup, tuple(commands))

    def delete(self, slug: str, *, confirmed: bool = False) -> RelayApplyResult:
        """Preview or explicitly apply deletion.

        With the default ``confirmed=False`` this method is strictly read-only
        and returns the exact plan.  This explicit flag is the API boundary that
        prevents an Agent request from accidentally deleting a relay.
        """

        plan = self.plan_delete(slug)
        if not confirmed:
            return RelayApplyResult(plan=plan, applied=False)
        if plan.action is PlanAction.ABSENT:
            return RelayApplyResult(plan=plan, applied=False)
        if plan.action is PlanAction.BLOCKED:
            raise RelayConflictError(plan.reason)
        current = self.plan_delete(slug)
        self._require_same_plan(plan, current)
        commands: List[Tuple[str, ...]] = []
        commands.append(self._systemctl("disable", "--now", plan.unit_name))
        backup: Optional[Path] = None
        try:
            backup = create_backup(plan.unit_path, self.backup_dir, clock_ns=self.clock_ns)
            safe_content = _read_regular(plan.unit_path)
            if safe_content is None or _sha256(safe_content) != plan.observed_sha256:
                raise RelayConflictError("relay unit changed after the deletion plan was created")
            plan.unit_path.unlink()
            commands.append(self._systemctl("daemon-reload"))
        except Exception as exc:
            if backup is not None and not plan.unit_path.exists():
                rollback_file(plan.unit_path, backup, created=False)
            self._best_effort_systemctl("daemon-reload")
            self._best_effort_systemctl("enable", "--now", plan.unit_name)
            if isinstance(exc, RelayError):
                raise
            raise RelayCommandError("relay deletion failed; unit file was rolled back") from exc
        return RelayApplyResult(plan, True, backup, tuple(commands))

    def _systemctl(self, *arguments: str) -> Tuple[str, ...]:
        command = (self.systemctl_path, "--user") + tuple(arguments)
        _checked_command(self.runner, command)
        return command

    def _best_effort_systemctl(self, *arguments: str) -> None:
        command = (self.systemctl_path, "--user") + tuple(arguments)
        try:
            self.runner(command)
        except Exception:
            pass

    @staticmethod
    def _require_same_plan(expected: RelayPlan, current: RelayPlan) -> None:
        if (
            expected.operation is not current.operation
            or expected.action is not current.action
            or expected.unit_path != current.unit_path
            or expected.observed_sha256 != current.observed_sha256
            or expected.desired_text != current.desired_text
            or expected.affected_device_ids != current.affected_device_ids
        ):
            raise RelayConflictError("relay plan is stale; inspect a new plan before applying")

    @staticmethod
    def _blocked(
        operation: PlanOperation,
        slug: str,
        path: Path,
        spec: Optional[RelaySpec],
        reason: str,
        affected_device_ids: Sequence[str] = (),
    ) -> RelayPlan:
        return RelayPlan(
            operation,
            PlanAction.BLOCKED,
            slug,
            path.name,
            path,
            spec,
            None,
            None,
            reason,
            affected_device_ids=_validate_affected_device_ids(affected_device_ids),
        )

    @staticmethod
    def _blocked_adoption(
        spec: RelaySpec,
        unit_name: str,
        unit_path: Path,
        desired_sha256: str,
        observed_sha256: Optional[str],
        reason: str,
        affected_device_ids: Sequence[str] = (),
    ) -> RelayAdoptionPlan:
        affected = _validate_affected_device_ids(affected_device_ids)
        token = _adoption_state_token(
            spec,
            unit_name,
            unit_path,
            observed_sha256,
            desired_sha256,
            affected,
        )
        return RelayAdoptionPlan(
            AdoptionAction.BLOCKED,
            spec,
            unit_name,
            unit_path,
            observed_sha256,
            desired_sha256,
            token,
            "",
            reason,
            False,
            affected,
        )


__all__ = [
    "AdoptionAction",
    "CommandResult",
    "DEFAULT_SOCAT_PATH",
    "DependencyChecker",
    "MANAGED_MARKER",
    "PlanAction",
    "PlanOperation",
    "ProcessRunner",
    "RelayApplyResult",
    "RelayAdoptionPlan",
    "RelayAdoptionResult",
    "RelayCommandError",
    "RelayConfirmationError",
    "RelayConflictError",
    "RelayDependencyError",
    "RelayError",
    "RelayManager",
    "RelayPlan",
    "RelayProbe",
    "RelaySafetyError",
    "RelaySpec",
    "RelayValidationError",
    "SKILL_MANAGED_MARKER",
    "atomic_write_bytes",
    "atomic_write_text",
    "create_backup",
    "probe_relay",
    "render_systemd_unit",
    "rollback_file",
    "unit_name_for_slug",
]
