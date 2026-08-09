#!/usr/bin/env python3
"""Persistent, least-privilege Linux agent for JK Bridge.

The agent is deliberately small and dependency-free.  It listens only on the
machine's Tailscale IPv4 address, enrolls one Ed25519 key per Windows device,
and derives every SSH ``permitopen`` target from a validated service catalog.

The existing ``jkbridge-linux.py`` one-shot helper remains the fallback for
users who do not want to run a persistent service.
"""

from __future__ import annotations

import argparse
import base64
import concurrent.futures
import binascii
import calendar
import contextlib
import dataclasses
import fcntl
import hashlib
import hmac
import http.client
import http.server
import importlib.util
import ipaddress
import json
import os
import pwd
import re
import secrets
import shutil
import socket
import ssl
import stat
import subprocess
import sys
import tempfile
import threading
import time
import urllib.parse
import uuid
from pathlib import Path
from typing import Any, Dict, Iterator, List, Mapping, MutableMapping, Optional, Sequence, Tuple


AGENT_VERSION = "0.2.0"
PROTOCOL_VERSION = 1
DEFAULT_PORT = 45871
MAX_REQUEST_BYTES = 64 * 1024
MAX_SERVICES = 64
MAX_REMOTE_PORTS = 32
MAX_DEVICES = 64
MAX_RECOVERY_CODES = 8
MAX_AUDIT_EVENTS = 512
LAST_SEEN_WRITE_INTERVAL_SECONDS = 300
TAILSCALE_IPV4 = ipaddress.ip_network("100.64.0.0/10")
TAILSCALE_IPV6 = ipaddress.ip_network("fd7a:115c:a1e0::/48")
SSH_USER_PATTERN = re.compile(r"^[A-Za-z_][A-Za-z0-9_.-]{0,63}$")
NODE_ID_PATTERN = re.compile(r"^[A-Za-z0-9_-]{1,128}$")
SERVICE_ID_PATTERN = re.compile(r"^[a-z][a-z0-9-]{0,47}$")
DEVICE_ID_PATTERN = re.compile(r"^[0-9a-f]{32}$")
FINGERPRINT_PATTERN = re.compile(r"^SHA256:[A-Za-z0-9+/]{43}$")
MANAGED_MARKER = "JK Bridge agent"
ONE_SHOT_MANAGED_MARKER = "JK Bridge managed"
RESTRICTED_OPTIONS_PREFIX = (
    'restrict,port-forwarding,command="/usr/bin/false",'
    'from="100.64.0.0/10,fd7a:115c:a1e0::/48"'
)
AUTH_SCHEME_ENROLL = "JKBridge-Enrollment"
AUTH_SCHEME_DEVICE = "Bearer"

_RELAY_MODULE: Any = None


def relay_module() -> Any:
    """Load the sibling relay module without changing process import paths."""

    global _RELAY_MODULE
    if _RELAY_MODULE is not None:
        return _RELAY_MODULE
    path = Path(__file__).with_name("jkbridge_relay.py")
    if not path.is_file() or path.is_symlink():
        raise AgentError("The optional JK Bridge relay module is unavailable.", status=503, code="relay_unavailable")
    name = "jkbridge_relay"
    if name in sys.modules:
        _RELAY_MODULE = sys.modules[name]
        return _RELAY_MODULE
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise AgentError("The optional JK Bridge relay module cannot be loaded.", status=503, code="relay_unavailable")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    try:
        spec.loader.exec_module(module)
    except Exception as exc:
        sys.modules.pop(name, None)
        raise AgentError("The optional JK Bridge relay module cannot be loaded.", status=503, code="relay_unavailable") from exc
    _RELAY_MODULE = module
    return module


DEFAULT_SERVICES: List[Dict[str, Any]] = [
    {
        "id": "overleaf",
        "name": "Overleaf",
        "description": "LaTeX collaboration",
        "icon": "Document",
        "remotePort": 80,
        "suggestedLocalPort": 8080,
        "scheme": "http",
        "openPath": "/project",
        "healthPath": "/project",
        "healthKind": "httpStatus",
        "enabledByDefault": True,
    },
    {
        "id": "sub2api",
        "name": "Sub2API",
        "description": "Local API gateway",
        "icon": "Api",
        "remotePort": 8081,
        "suggestedLocalPort": 8081,
        "scheme": "http",
        "openPath": "/",
        "healthPath": "/",
        "healthKind": "tcpConnect",
        "enabledByDefault": True,
    },
    {
        "id": "gpu-dashboard",
        "name": "GPU Dashboard",
        "description": "GPU status and task monitoring",
        "icon": "Gauge",
        "remotePort": 8765,
        "suggestedLocalPort": 8765,
        "scheme": "http",
        "openPath": "/",
        "healthPath": "/api/health",
        "healthKind": "jsonOk",
        "enabledByDefault": True,
    },
    {
        "id": "investment-news",
        "name": "Investment News",
        "description": "Local investment-news dashboard",
        "icon": "Newspaper",
        "remotePort": 8793,
        "suggestedLocalPort": 8793,
        "scheme": "http",
        "openPath": "/",
        "healthPath": "/",
        "healthKind": "httpStatus",
        "enabledByDefault": True,
    },
]


class AgentError(RuntimeError):
    """An expected failure safe to return to an API caller."""

    def __init__(self, message: str, *, status: int = 400, code: str = "invalid_request") -> None:
        super().__init__(message)
        self.status = status
        self.code = code


@dataclasses.dataclass(frozen=True)
class PeerIdentity:
    address: str
    node_id: str
    login_name: str


@dataclasses.dataclass(frozen=True)
class ServerIdentity:
    dns_name: str
    suffix: str
    ipv4: str
    node_id: str


@dataclasses.dataclass(frozen=True)
class ServerContext:
    identity: ServerIdentity
    ssh_user: str
    ssh_port: int
    host_public_key: str
    display_name: str


@dataclasses.dataclass(frozen=True)
class AgentPaths:
    state_dir: Path
    state_file: Path
    manifest_file: Path
    authorized_keys: Path
    lock_file: Path
    journal_file: Path

    @classmethod
    def from_values(
        cls,
        state_dir: Path,
        manifest_file: Optional[Path] = None,
        authorized_keys: Optional[Path] = None,
        home: Optional[Path] = None,
    ) -> "AgentPaths":
        # Keep the lexical path so AgentStore can see and reject every symlink
        # component. Path.resolve() would erase that security evidence.
        def lexical_absolute(path: Path) -> Path:
            return Path(os.path.abspath(os.fspath(path.expanduser())))

        base = lexical_absolute(state_dir)
        user_home = lexical_absolute(home or Path.home())
        return cls(
            state_dir=base,
            state_file=base / "state.json",
            manifest_file=(lexical_absolute(manifest_file) if manifest_file else base / "services.json"),
            authorized_keys=(
                lexical_absolute(authorized_keys)
                if authorized_keys
                else user_home / ".ssh" / "authorized_keys"
            ),
            lock_file=base / ".agent.lock",
            journal_file=base / ".transaction.json",
        )


def utc_now() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def epoch_now() -> int:
    return int(time.time())


def _is_utc_timestamp(value: Any) -> bool:
    if not isinstance(value, str) or not re.fullmatch(
        r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z", value
    ):
        return False
    try:
        time.strptime(value, "%Y-%m-%dT%H:%M:%SZ")
    except ValueError:
        return False
    return True


def _canonical_json(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


def _strict_json_object(pairs: Sequence[Tuple[str, Any]]) -> Dict[str, Any]:
    result: Dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise AgentError("Duplicate JSON object keys are not accepted.", code="invalid_json")
        result[key] = value
    return result


def _require_object_fields(
    value: Any, required: Sequence[str], optional: Sequence[str] = ()
) -> Dict[str, Any]:
    if not isinstance(value, dict):
        raise AgentError("The request body must be a JSON object.", code="invalid_json")
    expected = set(required) | set(optional)
    missing = set(required) - set(value)
    unknown = set(value) - expected
    if missing or unknown:
        raise AgentError("The request contains missing or unknown fields.", code="invalid_request")
    return value


def _secret_hash(kind: str, secret: str) -> str:
    return hashlib.sha256(("jkbridge-agent:" + kind + ":v1\0" + secret).encode("utf-8")).hexdigest()


def _new_secret() -> str:
    return secrets.token_urlsafe(32)


def encode_setup_code(endpoint: str, enrollment_code: str) -> str:
    """Return one opaque copy/paste value without placing a secret in a URL."""

    try:
        decoded = base64.urlsafe_b64decode(enrollment_code + "=")
    except (binascii.Error, ValueError) as exc:
        raise AgentError(
            "The enrollment code is invalid.", code="invalid_enrollment_code"
        ) from exc
    if (
        len(enrollment_code) != 43
        or len(decoded) != 32
        or not re.fullmatch(r"[A-Za-z0-9_-]{43}", enrollment_code)
    ):
        raise AgentError("The enrollment code is invalid.", code="invalid_enrollment_code")
    payload = _canonical_json(
        {"endpoint": endpoint, "enrollmentCode": enrollment_code, "version": 1}
    ).encode("utf-8")
    return "JKBA1." + base64.urlsafe_b64encode(payload).decode("ascii").rstrip("=")


def normalize_ed25519_key(value: str) -> Tuple[str, str]:
    parts = value.strip().split()
    if len(parts) < 2 or parts[0] != "ssh-ed25519":
        raise AgentError("Only an ssh-ed25519 public key is accepted.", code="invalid_public_key")
    try:
        blob = base64.b64decode(parts[1], validate=True)
    except (binascii.Error, ValueError) as exc:
        raise AgentError("The Ed25519 public key is not valid base64.", code="invalid_public_key") from exc
    algorithm = b"ssh-ed25519"
    if (
        len(blob) != 4 + len(algorithm) + 4 + 32
        or int.from_bytes(blob[0:4], "big") != len(algorithm)
        or blob[4:15] != algorithm
        or int.from_bytes(blob[15:19], "big") != 32
    ):
        raise AgentError("The Ed25519 public-key blob is invalid.", code="invalid_public_key")
    fingerprint = "SHA256:" + base64.b64encode(hashlib.sha256(blob).digest()).decode().rstrip("=")
    return "ssh-ed25519 " + parts[1], fingerprint


def key_from_authorized_line(line: str) -> Optional[Tuple[str, str]]:
    parts = line.strip().split()
    for index, part in enumerate(parts[:-1]):
        if part == "ssh-ed25519":
            try:
                return normalize_ed25519_key(" ".join(parts[index : index + 2]))
            except AgentError:
                return None
    return None


def _validate_plain_text(value: Any, field: str, maximum: int, *, allow_empty: bool = False) -> str:
    if not isinstance(value, str):
        raise AgentError(field + " must be a string.", code="invalid_" + field)
    result = value.strip()
    if (not result and not allow_empty) or len(result) > maximum or any(
        ord(char) < 32 or ord(char) == 127 for char in result
    ):
        raise AgentError(field + " is empty, too long, or contains control characters.", code="invalid_" + field)
    return result


def _validate_port(value: Any, field: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or not 1 <= value <= 65535:
        raise AgentError(field + " must be an integer from 1 to 65535.", code="invalid_port")
    return value


def _validate_local_uri_path(
    value: Any, field: str, maximum: int, *, allow_fragment: bool
) -> str:
    result = _validate_plain_text(value, field, maximum)
    if result != value or any(char.isspace() for char in result) or "\\" in result:
        raise AgentError(
            field + " must be one canonical local URI path.", code="invalid_" + field
        )
    if re.search(r"%(?![0-9A-Fa-f]{2})", result):
        raise AgentError(
            field + " contains an invalid percent escape.", code="invalid_" + field
        )
    parsed = urllib.parse.urlsplit(result)
    if (
        parsed.scheme
        or parsed.netloc
        or not parsed.path.startswith("/")
        or parsed.path.startswith("//")
        or (not allow_fragment and bool(parsed.fragment))
    ):
        suffix = "" if allow_fragment else " without a fragment"
        raise AgentError(
            field + " must be a local absolute path" + suffix + ".",
            code="invalid_" + field,
        )
    return result


def validate_services(value: Any) -> List[Dict[str, Any]]:
    if not isinstance(value, list) or not 1 <= len(value) <= MAX_SERVICES:
        raise AgentError("services must contain between 1 and 64 entries.", code="invalid_services")
    services: List[Dict[str, Any]] = []
    ids: set[str] = set()
    for raw in value:
        if not isinstance(raw, dict):
            raise AgentError("Every service must be a JSON object.", code="invalid_service")
        _require_object_fields(
            raw,
            (
                "id",
                "name",
                "description",
                "icon",
                "remotePort",
                "suggestedLocalPort",
                "scheme",
                "openPath",
                "healthPath",
                "healthKind",
                "enabledByDefault",
            ),
        )
        service_id = str(raw.get("id") or "")
        if not SERVICE_ID_PATTERN.fullmatch(service_id) or service_id in ids:
            raise AgentError("Service ids must be unique lowercase slugs.", code="invalid_service_id")
        ids.add(service_id)
        scheme = str(raw.get("scheme") or "").lower()
        if scheme not in ("http", "https"):
            raise AgentError("Service scheme must be http or https.", code="invalid_service_scheme")
        open_path = _validate_local_uri_path(
            raw.get("openPath", "/"), "openPath", 512, allow_fragment=True
        )
        health_path = _validate_local_uri_path(
            raw.get("healthPath", "/"), "healthPath", 512, allow_fragment=False
        )
        health_kind = str(raw.get("healthKind") or "httpStatus")
        if health_kind not in ("httpStatus", "jsonOk", "tcpConnect"):
            raise AgentError("healthKind is not supported.", code="invalid_health_kind")
        enabled = raw.get("enabledByDefault", True)
        if not isinstance(enabled, bool):
            raise AgentError("enabledByDefault must be boolean.", code="invalid_service_default")
        services.append(
            {
                "id": service_id,
                "name": _validate_plain_text(raw.get("name"), "name", 80),
                "description": _validate_plain_text(
                    raw.get("description", ""), "description", 160, allow_empty=True
                ),
                "icon": _validate_plain_text(raw.get("icon", "Globe"), "icon", 32),
                "remotePort": _validate_port(raw.get("remotePort"), "remotePort"),
                "suggestedLocalPort": _validate_port(
                    raw.get("suggestedLocalPort"), "suggestedLocalPort"
                ),
                "scheme": scheme,
                "openPath": open_path,
                "healthPath": health_path,
                "healthKind": health_kind,
                "enabledByDefault": enabled,
            }
        )
    if len({service["remotePort"] for service in services}) > MAX_REMOTE_PORTS:
        raise AgentError("The catalog contains more than 32 distinct remote ports.", code="too_many_ports")
    if len({service["suggestedLocalPort"] for service in services}) != len(services):
        raise AgentError("suggestedLocalPort values must be unique.", code="duplicate_local_port")
    return services


def validate_manifest(value: Any) -> Dict[str, Any]:
    if not isinstance(value, dict) or value.get("schemaVersion") != 1:
        raise AgentError("The service manifest schema is incompatible.", code="invalid_manifest")
    revision = value.get("revision")
    if isinstance(revision, bool) or not isinstance(revision, int) or revision < 1:
        raise AgentError("The service manifest revision is invalid.", code="invalid_manifest")
    return {
        "schemaVersion": 1,
        "revision": revision,
        "updatedAt": str(value.get("updatedAt") or utc_now()),
        "services": validate_services(value.get("services")),
    }


def validate_state(value: Any) -> Dict[str, Any]:
    if not isinstance(value, dict) or value.get("schemaVersion") != 1:
        raise AgentError("The agent state schema is incompatible.", code="invalid_state")
    # Normalize state created by an earlier unreleased v1 build.
    if "relays" not in value or "auditEvents" not in value:
        value = dict(value)
        value.setdefault("relays", [])
        value.setdefault("auditEvents", [])
    if (
        not isinstance(value.get("devices"), list)
        or not isinstance(value.get("recoveryCodes"), list)
        or not isinstance(value.get("relays"), list)
        or not isinstance(value.get("auditEvents"), list)
    ):
        raise AgentError("The agent state is malformed.", code="invalid_state")
    if (
        len(value["devices"]) > MAX_DEVICES
        or len(value["recoveryCodes"]) > MAX_RECOVERY_CODES
        or len(value["relays"]) > MAX_SERVICES
        or len(value["auditEvents"]) > MAX_AUDIT_EVENTS
    ):
        raise AgentError("The agent state exceeds configured safety limits.", code="invalid_state")
    instance_id = str(value.get("serverInstanceId") or "")
    if not DEVICE_ID_PATTERN.fullmatch(instance_id):
        raise AgentError("The Agent instance id is malformed.", code="invalid_state")
    recovery_ids: set[str] = set()
    for recovery in value["recoveryCodes"]:
        if not isinstance(recovery, dict):
            raise AgentError("A recovery-code record is malformed.", code="invalid_state")
        recovery_id = str(recovery.get("id") or "")
        verifier = str(recovery.get("verifier") or "")
        expires = recovery.get("expiresAtEpoch")
        if (
            not DEVICE_ID_PATTERN.fullmatch(recovery_id)
            or recovery_id in recovery_ids
            or not re.fullmatch(r"[0-9a-f]{64}", verifier)
            or recovery.get("role") not in ("admin", "device")
            or isinstance(expires, bool)
            or not isinstance(expires, int)
            or expires < 1
        ):
            raise AgentError("A recovery-code record is malformed.", code="invalid_state")
        recovery_ids.add(recovery_id)
    device_ids: set[str] = set()
    public_keys: set[str] = set()
    for device in value["devices"]:
        if not isinstance(device, dict):
            raise AgentError("A device record is malformed.", code="invalid_state")
        device_id = str(device.get("id") or "")
        public_key, fingerprint = normalize_ed25519_key(str(device.get("publicKey") or ""))
        credential_hash = str(device.get("credentialHash") or "")
        service_ids = device.get("serviceIds")
        ports = device.get("permitPorts")
        revoked = device.get("revokedAt") is not None
        if (
            not DEVICE_ID_PATTERN.fullmatch(device_id)
            or device_id in device_ids
            or public_key in public_keys
            or device.get("fingerprint") != fingerprint
            or device.get("role") not in ("admin", "device")
            or not NODE_ID_PATTERN.fullmatch(str(device.get("tailscaleNodeId") or ""))
            or not isinstance(device.get("tailscaleLogin"), str)
            or not 1 <= len(device["tailscaleLogin"]) <= 254
            or not isinstance(service_ids, list)
            or len(service_ids) > MAX_SERVICES
            or any(not isinstance(item, str) or not SERVICE_ID_PATTERN.fullmatch(item) for item in service_ids)
            or len(set(service_ids)) != len(service_ids)
            or not isinstance(ports, list)
            or len(ports) > MAX_REMOTE_PORTS
            or any(isinstance(port, bool) or not isinstance(port, int) or not 1 <= port <= 65535 for port in ports)
            or ports != sorted(set(ports))
            or (revoked and (credential_hash or ports))
            or (not revoked and (not re.fullmatch(r"[0-9a-f]{64}", credential_hash) or not ports))
        ):
            raise AgentError("A device record is malformed.", code="invalid_state")
        _validate_plain_text(device.get("deviceLabel"), "deviceLabel", 80)
        device_ids.add(device_id)
        public_keys.add(public_key)
    relay_slugs: set[str] = set()
    for relay in value["relays"]:
        relay = _require_object_fields(
            relay, ("slug", "relayPort", "targetHost", "targetPort", "scheme", "openPath")
        )
        slug = relay["slug"]
        if not isinstance(slug, str) or not SERVICE_ID_PATTERN.fullmatch(slug) or slug in relay_slugs:
            raise AgentError("A managed relay record is malformed.", code="invalid_state")
        if (
            isinstance(relay["relayPort"], bool)
            or not isinstance(relay["relayPort"], int)
            or not 1 <= relay["relayPort"] <= 65535
            or isinstance(relay["targetPort"], bool)
            or not isinstance(relay["targetPort"], int)
            or not 1 <= relay["targetPort"] <= 65535
            or relay["scheme"] not in ("http", "https")
        ):
            raise AgentError("A managed relay record is malformed.", code="invalid_state")
        module = relay_module()
        try:
            canonical_relay = module.RelaySpec.create(
                slug=relay["slug"],
                relay_port=relay["relayPort"],
                target_host=relay["targetHost"],
                target_port=relay["targetPort"],
                scheme=relay["scheme"],
                open_path=relay["openPath"],
            ).to_metadata()
        except module.RelayError as exc:
            raise AgentError("A managed relay record is malformed.", code="invalid_state") from exc
        if relay != canonical_relay:
            raise AgentError("A managed relay record is not canonical.", code="invalid_state")
        relay_slugs.add(slug)
    audit_ids: set[str] = set()
    for event in value["auditEvents"]:
        event = _require_object_fields(
            event,
            (
                "id",
                "action",
                "actorDeviceId",
                "targetDeviceId",
                "occurredAt",
                "previousDeviceLabel",
                "deviceLabel",
            ),
        )
        event_id = str(event["id"])
        if (
            not DEVICE_ID_PATTERN.fullmatch(event_id)
            or event_id in audit_ids
            or event["action"] != "device_renamed"
            or event["actorDeviceId"] not in device_ids
            or event["targetDeviceId"] not in device_ids
            or not _is_utc_timestamp(event["occurredAt"])
        ):
            raise AgentError("An Agent audit record is malformed.", code="invalid_state")
        _validate_plain_text(event["previousDeviceLabel"], "previousDeviceLabel", 80)
        _validate_plain_text(event["deviceLabel"], "deviceLabel", 80)
        audit_ids.add(event_id)
    return value


def ports_for_service_ids(manifest: Mapping[str, Any], service_ids: Sequence[str]) -> List[int]:
    by_id = {service["id"]: service for service in manifest["services"]}
    unknown = sorted(set(service_ids) - set(by_id))
    if unknown:
        raise AgentError("Unknown service id: " + unknown[0], code="unknown_service")
    ports = sorted({int(by_id[service_id]["remotePort"]) for service_id in service_ids})
    if not 1 <= len(ports) <= MAX_REMOTE_PORTS:
        raise AgentError("A device must select between 1 and 32 exact remote ports.", code="invalid_ports")
    return ports


def build_agent_authorized_line(device: Mapping[str, Any]) -> str:
    public_key, fingerprint = normalize_ed25519_key(str(device["publicKey"]))
    device_id = str(device["id"])
    if not DEVICE_ID_PATTERN.fullmatch(device_id) or not FINGERPRINT_PATTERN.fullmatch(fingerprint):
        raise AgentError("The stored device identity is malformed.", code="invalid_state")
    ports = sorted(set(int(port) for port in device.get("permitPorts", [])))
    if not 1 <= len(ports) <= MAX_REMOTE_PORTS or any(not 1 <= port <= 65535 for port in ports):
        raise AgentError("The stored device grant is empty or malformed.", code="invalid_state")
    permits = "".join(',permitopen="127.0.0.1:{}"'.format(port) for port in ports)
    return "{}{} {} {} {} {}".format(
        RESTRICTED_OPTIONS_PREFIX,
        permits,
        public_key,
        MANAGED_MARKER,
        device_id,
        fingerprint,
    )


def parse_agent_authorized_line(line: str) -> Optional[Tuple[str, str, str]]:
    marker = " " + MANAGED_MARKER + " "
    if marker not in line:
        return None
    parsed = key_from_authorized_line(line)
    if not parsed:
        raise AgentError("A JK Bridge Agent authorized_keys entry is malformed.", code="unsafe_authorized_keys")
    suffix = line.rsplit(marker, 1)[1].split()
    if len(suffix) != 2 or not DEVICE_ID_PATTERN.fullmatch(suffix[0]) or suffix[1] != parsed[1]:
        raise AgentError("A JK Bridge Agent authorized_keys marker is malformed.", code="unsafe_authorized_keys")
    expected_prefix = RESTRICTED_OPTIONS_PREFIX + ",permitopen="
    if not line.startswith(expected_prefix):
        raise AgentError("A JK Bridge Agent key is missing exact restrictions.", code="unsafe_authorized_keys")
    # Re-rendering is the strict validator for options, key, marker, and ordering.
    permits = [int(value) for value in re.findall(r'permitopen="127\.0\.0\.1:([0-9]{1,5})"', line)]
    probe = {
        "id": suffix[0],
        "publicKey": parsed[0],
        "permitPorts": permits,
    }
    if line.strip() != build_agent_authorized_line(probe):
        raise AgentError("A JK Bridge Agent key has unexpected SSH options.", code="unsafe_authorized_keys")
    return suffix[0], parsed[0], parsed[1]


def parse_one_shot_authorized_line(line: str) -> Optional[Tuple[str, str]]:
    """Recognize only the exact restricted line emitted by jkbridge-linux.py.

    This permits an existing Windows installation to move from one-shot pairing
    to Agent management without copying or regenerating its private key. A
    marker on an unrestricted or otherwise altered entry is never sufficient.
    """

    marker = " " + ONE_SHOT_MANAGED_MARKER + " "
    if marker not in line:
        return None
    parsed = key_from_authorized_line(line)
    if not parsed:
        raise AgentError("A one-shot JK Bridge authorized_keys entry is malformed.", code="unsafe_authorized_keys")
    suffix = line.rsplit(marker, 1)[1].split()
    if len(suffix) != 1 or suffix[0] != parsed[1]:
        raise AgentError("A one-shot JK Bridge marker is malformed.", code="unsafe_authorized_keys")
    permits = [int(value) for value in re.findall(r'permitopen="127\.0\.0\.1:([0-9]{1,5})"', line)]
    if not permits or len(permits) > MAX_REMOTE_PORTS or permits != sorted(set(permits)):
        raise AgentError("A one-shot JK Bridge grant is not an exact bounded port set.", code="unsafe_authorized_keys")
    expected = (
        RESTRICTED_OPTIONS_PREFIX
        + "".join(',permitopen="127.0.0.1:{}"'.format(port) for port in permits)
        + " "
        + parsed[0]
        + " "
        + ONE_SHOT_MANAGED_MARKER
        + " "
        + parsed[1]
    )
    if line.strip() != expected:
        raise AgentError("A one-shot JK Bridge key has unexpected SSH options.", code="unsafe_authorized_keys")
    return parsed


def _assert_no_symlink_components(path: Path) -> None:
    """Reject an existing symlink anywhere in an absolute lexical path."""

    if not path.is_absolute():
        raise AgentError("Internal path must be absolute.", code="unsafe_path")
    current = Path(path.anchor)
    for component in path.parts[1:]:
        current = current / component
        try:
            metadata = os.lstat(str(current))
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(metadata.st_mode):
            raise AgentError("Symbolic-link path components are not allowed: " + str(current), code="unsafe_path")


def _read_bytes_nofollow(path: Path, maximum: int) -> bytes:
    _assert_no_symlink_components(path)
    flags = os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
    try:
        descriptor = os.open(str(path), flags)
    except OSError as exc:
        raise AgentError("File is missing or unsafe: " + str(path), code="unsafe_path") from exc
    try:
        metadata = os.fstat(descriptor)
        if (
            metadata.st_uid != os.getuid()
            or not stat.S_ISREG(metadata.st_mode)
            or metadata.st_nlink != 1
        ):
            raise AgentError("File has an unsafe owner or type: " + str(path), code="unsafe_path")
        if metadata.st_size > maximum:
            raise AgentError("File exceeds its safety limit: " + str(path), code="invalid_file")
        chunks: List[bytes] = []
        total = 0
        while True:
            chunk = os.read(descriptor, min(65536, maximum + 1 - total))
            if not chunk:
                break
            total += len(chunk)
            if total > maximum:
                raise AgentError("File exceeds its safety limit: " + str(path), code="invalid_file")
            chunks.append(chunk)
        return b"".join(chunks)
    finally:
        os.close(descriptor)


class AgentStore:
    """Locked JSON state, manifest, and authorized_keys transaction manager."""

    def __init__(self, paths: AgentPaths) -> None:
        self.paths = paths

    def _ensure_state_dir(self) -> None:
        _assert_no_symlink_components(self.paths.state_dir)
        self.paths.state_dir.mkdir(mode=0o700, parents=True, exist_ok=True)
        _assert_no_symlink_components(self.paths.state_dir)
        metadata = self.paths.state_dir.stat()
        if metadata.st_uid != os.getuid() or not stat.S_ISDIR(metadata.st_mode):
            raise AgentError("The agent state directory has an unsafe owner or type.", code="unsafe_path")
        os.chmod(self.paths.state_dir, 0o700)

    @contextlib.contextmanager
    def locked(self) -> Iterator[None]:
        self._ensure_state_dir()
        flags = os.O_RDWR | os.O_CREAT | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
        try:
            descriptor = os.open(str(self.paths.lock_file), flags, 0o600)
        except OSError as exc:
            raise AgentError("The agent lock file is unsafe or unavailable.", code="unsafe_path") from exc
        metadata = os.fstat(descriptor)
        if (
            metadata.st_uid != os.getuid()
            or not stat.S_ISREG(metadata.st_mode)
            or metadata.st_nlink != 1
        ):
            os.close(descriptor)
            raise AgentError("The agent lock file has an unsafe owner or type.", code="unsafe_path")
        os.fchmod(descriptor, 0o600)
        with os.fdopen(descriptor, "a+", encoding="utf-8") as lock:
            fcntl.flock(lock.fileno(), fcntl.LOCK_EX)
            self._recover_unlocked()
            yield

    def _read_json(self, path: Path) -> Dict[str, Any]:
        try:
            value = json.loads(_read_bytes_nofollow(path, 2 * 1024 * 1024).decode("utf-8"))
        except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise AgentError("JSON file is unreadable: " + str(path), code="invalid_json") from exc
        if not isinstance(value, dict):
            raise AgentError("JSON root must be an object: " + str(path), code="invalid_json")
        return value

    def load_unlocked(self) -> Tuple[Dict[str, Any], Dict[str, Any]]:
        return validate_state(self._read_json(self.paths.state_file)), validate_manifest(
            self._read_json(self.paths.manifest_file)
        )

    def load(self) -> Tuple[Dict[str, Any], Dict[str, Any]]:
        with self.locked():
            return self.load_unlocked()

    def is_initialized(self) -> bool:
        return self.paths.state_file.is_file() and self.paths.manifest_file.is_file()

    def _atomic_write_bytes(self, path: Path, payload: bytes, mode: int = 0o600) -> None:
        _assert_no_symlink_components(path.parent)
        path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        _assert_no_symlink_components(path)
        if path.exists():
            metadata = os.lstat(str(path))
            if (
                not stat.S_ISREG(metadata.st_mode)
                or metadata.st_uid != os.getuid()
                or metadata.st_nlink != 1
            ):
                raise AgentError("Refusing to replace a foreign or non-regular file: " + str(path), code="unsafe_path")
        descriptor, temporary = tempfile.mkstemp(prefix="." + path.name + ".", dir=str(path.parent))
        try:
            os.fchmod(descriptor, mode)
            with os.fdopen(descriptor, "wb") as stream:
                stream.write(payload)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, str(path))
            os.chmod(path, mode)
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

    def _backup(self, path: Path) -> Optional[Path]:
        if not path.exists():
            return None
        backup_dir = self.paths.state_dir / "backups"
        _assert_no_symlink_components(backup_dir)
        backup_dir.mkdir(mode=0o700, parents=True, exist_ok=True)
        stamp = time.strftime("%Y%m%d-%H%M%S", time.localtime())
        candidate = backup_dir / (path.name + "." + stamp)
        suffix = 1
        while candidate.exists():
            candidate = backup_dir / (path.name + ".{}.{}".format(stamp, suffix))
            suffix += 1
        self._atomic_write_bytes(candidate, _read_bytes_nofollow(path, 8 * 1024 * 1024), 0o600)
        return candidate

    def _write_json(self, path: Path, value: Mapping[str, Any], *, backup: bool = True) -> None:
        payload = (_canonical_json(value) + "\n").encode("utf-8")
        if path.exists() and _read_bytes_nofollow(path, 2 * 1024 * 1024) == payload:
            return
        if backup:
            self._backup(path)
        self._atomic_write_bytes(path, payload)

    def _assert_authorized_parent(self) -> None:
        parent = self.paths.authorized_keys.parent
        _assert_no_symlink_components(parent)
        parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        _assert_no_symlink_components(self.paths.authorized_keys)
        metadata = parent.stat()
        if metadata.st_uid != os.getuid() or not stat.S_ISDIR(metadata.st_mode):
            raise AgentError("The authorized_keys directory has an unsafe owner or type.", code="unsafe_path")
        os.chmod(parent, 0o700)
        path = self.paths.authorized_keys
        if path.exists():
            metadata = os.lstat(str(path))
            if (
                not stat.S_ISREG(metadata.st_mode)
                or metadata.st_uid != os.getuid()
                or metadata.st_nlink != 1
            ):
                raise AgentError("authorized_keys has an unsafe owner or type.", code="unsafe_path")

    @contextlib.contextmanager
    def _ssh_writer_locked(self) -> Iterator[None]:
        """Share the exact authorized_keys writer lock with the one-shot helper.

        Callers already hold ``state_dir/.agent.lock``. The invariant is always
        Agent state lock -> SSH writer lock; the one-shot helper takes only the
        SSH writer lock, so the two writers serialize without a lock cycle.
        """

        self._assert_authorized_parent()
        lock_path = self.paths.authorized_keys.parent / ".jkbridge.lock"
        flags = os.O_RDWR | os.O_CREAT | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
        try:
            descriptor = os.open(str(lock_path), flags, 0o600)
        except OSError as exc:
            raise AgentError("The shared JK Bridge SSH lock is unsafe or unavailable.", code="unsafe_path") from exc
        metadata = os.fstat(descriptor)
        if (
            metadata.st_uid != os.getuid()
            or not stat.S_ISREG(metadata.st_mode)
            or metadata.st_nlink != 1
        ):
            os.close(descriptor)
            raise AgentError("The shared JK Bridge SSH lock has an unsafe owner or type.", code="unsafe_path")
        os.fchmod(descriptor, 0o600)
        with os.fdopen(descriptor, "a+", encoding="utf-8") as lock:
            fcntl.flock(lock.fileno(), fcntl.LOCK_EX)
            yield

    def read_authorized_text_unlocked(self) -> str:
        self._assert_authorized_parent()
        if not self.paths.authorized_keys.exists():
            return ""
        try:
            return _read_bytes_nofollow(self.paths.authorized_keys, 8 * 1024 * 1024).decode("utf-8")
        except UnicodeDecodeError as exc:
            raise AgentError("authorized_keys is not valid UTF-8.", code="unsafe_authorized_keys") from exc

    def _write_authorized_text(self, value: str, *, backup: bool = True) -> None:
        self._assert_authorized_parent()
        canonical = "" if not value else value.rstrip("\r\n") + "\n"
        current = self.read_authorized_text_unlocked()
        if current == canonical:
            if self.paths.authorized_keys.exists():
                os.chmod(self.paths.authorized_keys, 0o600)
            return
        if backup:
            self._backup(self.paths.authorized_keys)
        self._atomic_write_bytes(self.paths.authorized_keys, canonical.encode("utf-8"), 0o600)

    def render_authorized_text_unlocked(self, state: Mapping[str, Any]) -> str:
        current = self.read_authorized_text_unlocked()
        unrelated_records: List[Tuple[str, Optional[str]]] = []
        existing_keys: Dict[str, str] = {}
        existing_device_ids: set[str] = set()
        for line in current.splitlines():
            agent_entry = parse_agent_authorized_line(line) if MANAGED_MARKER in line else None
            parsed = key_from_authorized_line(line)
            if agent_entry:
                device_id, public_key, _fingerprint = agent_entry
                if device_id in existing_device_ids:
                    raise AgentError("A managed device appears more than once in authorized_keys.", code="unsafe_authorized_keys")
                existing_device_ids.add(device_id)
                existing_keys[public_key] = device_id
            else:
                one_shot = (
                    parse_one_shot_authorized_line(line)
                    if (" " + ONE_SHOT_MANAGED_MARKER + " ") in line
                    else None
                )
                one_shot_key = one_shot[0] if one_shot else None
                unrelated_records.append((line, one_shot_key))
                if one_shot:
                    existing_keys[one_shot[0]] = "one-shot"
                elif parsed:
                    existing_keys[parsed[0]] = "unrelated"

        desired_lines: List[str] = []
        desired_keys: set[str] = set()
        for device in state["devices"]:
            if device.get("revokedAt") or not device.get("permitPorts"):
                continue
            public_key, _fingerprint = normalize_ed25519_key(str(device["publicKey"]))
            if public_key in desired_keys:
                raise AgentError("Two devices share one SSH key; refusing unsafe state.", code="duplicate_key")
            desired_keys.add(public_key)
            owner = existing_keys.get(public_key)
            if owner not in (None, "one-shot", str(device["id"])):
                raise AgentError(
                    "A device public key already exists outside its managed Agent entry.",
                    code="duplicate_key",
                )
            desired_lines.append(build_agent_authorized_line(device))

        unrelated = [line for line, one_shot_key in unrelated_records if one_shot_key not in desired_keys]
        while unrelated and not unrelated[-1].strip():
            unrelated.pop()
        combined = unrelated + desired_lines
        return "" if not combined else "\n".join(combined) + "\n"

    def initialize(self, state: Mapping[str, Any], manifest: Mapping[str, Any]) -> None:
        with self.locked():
            if self.paths.state_file.exists() or self.paths.manifest_file.exists():
                raise AgentError("The Agent is already initialized.", status=409, code="already_initialized")
            self._write_json(self.paths.state_file, state, backup=False)
            self._write_json(self.paths.manifest_file, manifest, backup=False)

    def transact_unlocked(
        self,
        state: Mapping[str, Any],
        manifest: Mapping[str, Any],
        *,
        restrict_first: bool,
    ) -> None:
        validated_state = validate_state(dict(state))
        validated_manifest = validate_manifest(dict(manifest))
        with self._ssh_writer_locked():
            authorized_text = self.render_authorized_text_unlocked(validated_state)
            journal = {
                "schemaVersion": 1,
                "createdAt": utc_now(),
                "restrictFirst": bool(restrict_first),
                "state": validated_state,
                "manifest": validated_manifest,
                "authorizedKeys": authorized_text,
            }
            self._write_json(self.paths.journal_file, journal, backup=False)
            self._apply_journal_unlocked(journal)
            self.paths.journal_file.unlink(missing_ok=True)

    def _apply_journal_unlocked(self, journal: Mapping[str, Any]) -> None:
        def write_authorized() -> None:
            self._write_authorized_text(str(journal["authorizedKeys"]))

        def write_json_files() -> None:
            self._write_json(self.paths.state_file, journal["state"])
            self._write_json(self.paths.manifest_file, journal["manifest"])

        if journal.get("restrictFirst"):
            write_authorized()
            write_json_files()
        else:
            write_json_files()
            write_authorized()

    def _recover_unlocked(self) -> None:
        if not self.paths.journal_file.exists():
            return
        journal = self._read_json(self.paths.journal_file)
        if (
            journal.get("schemaVersion") != 1
            or not isinstance(journal.get("state"), dict)
            or not isinstance(journal.get("manifest"), dict)
            or not isinstance(journal.get("authorizedKeys"), str)
        ):
            raise AgentError("The transaction journal is malformed; no files were changed.", code="invalid_journal")
        validate_state(journal["state"])
        validate_manifest(journal["manifest"])
        with self._ssh_writer_locked():
            self._apply_journal_unlocked(journal)
            self.paths.journal_file.unlink(missing_ok=True)


class TailscalePeerVerifier:
    """Resolve a source address to a stable Tailscale node/login identity."""

    def __init__(self, executable: Optional[str] = None, cache_seconds: int = 30) -> None:
        self.executable = executable or shutil.which("tailscale") or ""
        self.cache_seconds = cache_seconds
        self._cache: Dict[str, Tuple[float, PeerIdentity]] = {}
        self._lock = threading.Lock()

    def verify(self, address: str) -> PeerIdentity:
        try:
            parsed = ipaddress.ip_address(address)
        except ValueError as exc:
            raise AgentError("The peer address is invalid.", status=403, code="peer_forbidden") from exc
        if parsed not in TAILSCALE_IPV4 and parsed not in TAILSCALE_IPV6:
            raise AgentError("Only Tailscale peers may access the Agent.", status=403, code="peer_forbidden")
        with self._lock:
            cached = self._cache.get(address)
            if cached and cached[0] > time.monotonic():
                return cached[1]
        if not self.executable:
            raise AgentError("tailscale was not found for peer verification.", status=503, code="tailscale_unavailable")
        completed = subprocess.run(
            [self.executable, "whois", "--json", address],
            check=False,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            timeout=5,
        )
        if completed.returncode != 0:
            raise AgentError("Tailscale could not verify the requesting peer.", status=403, code="peer_forbidden")
        try:
            result = json.loads(completed.stdout)
        except json.JSONDecodeError as exc:
            raise AgentError("Tailscale peer verification returned invalid data.", status=503, code="tailscale_unavailable") from exc
        node = result.get("Node") or {}
        user = result.get("UserProfile") or {}
        node_id = str(node.get("StableID") or node.get("ID") or "")
        login_name = str(user.get("LoginName") or "").strip().lower()
        if not NODE_ID_PATTERN.fullmatch(node_id) or not login_name or len(login_name) > 254:
            raise AgentError("Tailscale did not return a stable peer identity.", status=403, code="peer_forbidden")
        peer = PeerIdentity(address=address, node_id=node_id, login_name=login_name)
        with self._lock:
            self._cache[address] = (time.monotonic() + self.cache_seconds, peer)
        return peer


class StaticPeerVerifier:
    """Explicit test double; production CLI never selects this verifier."""

    def __init__(self, identity: PeerIdentity) -> None:
        self.identity = identity

    def verify(self, address: str) -> PeerIdentity:
        return dataclasses.replace(self.identity, address=address)


def tailscale_identity() -> ServerIdentity:
    executable = shutil.which("tailscale")
    if not executable:
        raise AgentError("tailscale was not found in PATH.", code="tailscale_unavailable")
    completed = subprocess.run(
        [executable, "status", "--json"],
        check=False,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        timeout=10,
    )
    if completed.returncode != 0:
        raise AgentError("tailscale status failed.", code="tailscale_unavailable")
    try:
        status = json.loads(completed.stdout)
    except json.JSONDecodeError as exc:
        raise AgentError("tailscale status returned invalid JSON.", code="tailscale_unavailable") from exc
    if status.get("BackendState") != "Running":
        raise AgentError("Tailscale must be running and logged in.", code="tailscale_unavailable")
    self_node = status.get("Self") or {}
    dns_name = str(self_node.get("DNSName") or "").strip().rstrip(".").lower()
    current_tailnet = status.get("CurrentTailnet") or {}
    suffix = str(current_tailnet.get("MagicDNSSuffix") or "").strip().rstrip(".").lower()
    if not suffix and "." in dns_name:
        suffix = dns_name.split(".", 1)[1]
    node_id = str(self_node.get("StableID") or self_node.get("ID") or "")
    ipv4 = ""
    for value in self_node.get("TailscaleIPs") or []:
        try:
            address = ipaddress.ip_address(str(value))
        except ValueError:
            continue
        if isinstance(address, ipaddress.IPv4Address) and address in TAILSCALE_IPV4:
            ipv4 = str(address)
            break
    if (
        not dns_name
        or not suffix
        or not ipv4
        or not NODE_ID_PATTERN.fullmatch(node_id)
        or not dns_name.endswith("." + suffix)
    ):
        raise AgentError("Tailscale did not report a complete stable server identity.", code="tailscale_unavailable")
    return ServerIdentity(dns_name=dns_name, suffix=suffix, ipv4=ipv4, node_id=node_id)


def read_host_public_key(path: Path) -> str:
    if path.is_symlink() or not path.is_file():
        raise AgentError("The SSH Ed25519 host public key is missing or unsafe.", code="host_key_unavailable")
    first = path.read_text(encoding="utf-8").splitlines()
    if not first:
        raise AgentError("The SSH Ed25519 host public key is empty.", code="host_key_unavailable")
    return normalize_ed25519_key(first[0])[0]


def current_user() -> Tuple[str, Path]:
    if os.geteuid() == 0:
        raise AgentError("Do not run JK Bridge Agent as root or with sudo.", code="root_forbidden")
    entry = pwd.getpwuid(os.getuid())
    if not SSH_USER_PATTERN.fullmatch(entry.pw_name):
        raise AgentError("The current Linux username contains unsupported SSH characters.", code="invalid_user")
    return entry.pw_name, Path(entry.pw_dir)


def _public_device(device: Mapping[str, Any]) -> Dict[str, Any]:
    return {
        "deviceId": device["id"],
        "deviceLabel": device["deviceLabel"],
        "clientKeyFingerprint": device["fingerprint"],
        "role": device["role"],
        "tailscaleNodeId": device["tailscaleNodeId"],
        "tailscaleLogin": device["tailscaleLogin"],
        "serviceIds": list(device.get("serviceIds", [])),
        "permitPorts": list(device.get("permitPorts", [])),
        "createdAt": device["createdAt"],
        "lastSeenAt": device.get("lastSeenAt"),
        "revokedAt": device.get("revokedAt"),
    }


def _suggest_service_template(process_name: str, title: str, port: int) -> str:
    haystack = f"{process_name} {title}".lower()
    rules = (
        ("syncthing", "syncthing"),
        ("jupyter", "jupyter"),
        ("overleaf", "overleaf"),
        ("sub2api", "sub2api"),
        ("ollama", "ollama"),
        ("grafana", "grafana"),
        ("comfyui", "comfyui"),
        ("automatic1111", "stable-diffusion-webui"),
        ("stable diffusion", "stable-diffusion-webui"),
    )
    for needle, template_id in rules:
        if needle in haystack:
            return template_id
    if port in (80, 8080, 8000, 3000, 5000, 8888):
        return "generic-http"
    return "generic-local-web"


def _probe_loopback_http(address: str, port: int, process_name: str) -> Dict[str, Any]:
    for scheme in ("http", "https"):
        connection: Any = None
        try:
            if scheme == "https":
                connection = http.client.HTTPSConnection(
                    address,
                    port,
                    timeout=0.35,
                    context=ssl._create_unverified_context(),
                )
            else:
                connection = http.client.HTTPConnection(address, port, timeout=0.35)
            connection.request(
                "GET",
                "/",
                headers={
                    "Accept": "text/html,application/xhtml+xml,*/*;q=0.1",
                    "Connection": "close",
                    "User-Agent": "JKBridge-Agent-Discovery/1",
                },
            )
            response = connection.getresponse()
            body = response.read(16 * 1024)
            title = ""
            content_type = str(response.getheader("Content-Type") or "").lower()
            if "html" in content_type or body.lstrip().startswith((b"<!DOCTYPE", b"<html", b"<HTML")):
                text = body.decode("utf-8", errors="replace")
                match = re.search(r"<title[^>]*>(.*?)</title>", text, re.IGNORECASE | re.DOTALL)
                if match:
                    title = re.sub(r"\s+", " ", match.group(1)).strip()
                    title = "".join(char for char in title if char.isprintable())[:120]
            display = title or process_name or f"Local service on {port}"
            return {
                "port": port,
                "bindAddress": address,
                "processName": process_name,
                "scheme": scheme,
                "webDetected": True,
                "statusCode": int(response.status),
                "title": display,
                "healthPath": "/",
                "suggestedTemplateId": _suggest_service_template(
                    process_name, title, port
                ),
                "tlsCertificateVerified": False if scheme == "https" else None,
            }
        except (OSError, TimeoutError, http.client.HTTPException, ssl.SSLError):
            pass
        finally:
            if connection is not None:
                with contextlib.suppress(Exception):
                    connection.close()
    return {
        "port": port,
        "bindAddress": address,
        "processName": process_name,
        "scheme": "tcp",
        "webDetected": False,
        "statusCode": None,
        "title": process_name or f"Local listener on {port}",
        "healthPath": "/",
        "suggestedTemplateId": _suggest_service_template(process_name, "", port),
        "tlsCertificateVerified": None,
    }


def discover_loopback_services() -> List[Dict[str, Any]]:
    """Inspect current loopback listeners; never scan ports, processes, or disks."""

    executable = shutil.which("ss")
    if not executable:
        raise AgentError(
            "The ss utility is required for loopback service discovery.",
            status=503,
            code="discovery_unavailable",
        )
    completed = subprocess.run(
        [executable, "-H", "-ltnp"],
        check=False,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        timeout=4,
    )
    if completed.returncode != 0 or len(completed.stdout) > 512 * 1024:
        raise AgentError(
            "Loopback listener discovery failed safely.",
            status=503,
            code="discovery_unavailable",
        )

    listeners: Dict[int, str] = {}
    for line in completed.stdout.splitlines():
        fields = line.split()
        if len(fields) < 4:
            continue
        local = fields[3]
        host, separator, port_text = local.rpartition(":")
        if not separator or host != "127.0.0.1" or not port_text.isdigit():
            continue
        port = int(port_text)
        if port < 1 or port > 65535 or port in (
            22,
            DEFAULT_PORT,
            3306,
            5432,
            6379,
            27017,
        ):
            continue
        process_match = re.search(r'users:\(\("([^"\\]{1,128})"', line)
        process_name = ""
        if process_match:
            candidate = Path(process_match.group(1)).name
            if re.fullmatch(r"[A-Za-z0-9_.+-]{1,64}", candidate):
                process_name = candidate
        listeners.setdefault(port, process_name)
        if len(listeners) >= 64:
            break

    if not listeners:
        return []
    with concurrent.futures.ThreadPoolExecutor(max_workers=12) as executor:
        futures = [
            executor.submit(_probe_loopback_http, "127.0.0.1", port, process)
            for port, process in sorted(listeners.items())
        ]
        return [future.result() for future in futures]


class AgentApplication:
    """Protocol core independent of sockets for deterministic unit tests."""

    def __init__(
        self,
        store: AgentStore,
        context: ServerContext,
        relay_manager: Any = None,
        discovery_provider: Any = None,
    ) -> None:
        self.store = store
        self.context = context
        self.relay_manager = relay_manager
        self.discovery_provider = discovery_provider or discover_loopback_services

    def _server_payload(self) -> Dict[str, Any]:
        identity = self.context.identity
        return {
            "protocolVersion": PROTOCOL_VERSION,
            "agentVersion": AGENT_VERSION,
            "serverHost": identity.dns_name,
            "tailnetDnsSuffix": identity.suffix,
            "tailscaleIpv4": identity.ipv4,
            "tailscaleNodeId": identity.node_id,
            "sshUser": self.context.ssh_user,
            "sshPort": self.context.ssh_port,
            "hostPublicKey": self.context.host_public_key,
            "displayName": self.context.display_name,
        }

    @staticmethod
    def _authorization(headers: Mapping[str, str], expected_scheme: str) -> str:
        value = headers.get("authorization", "")
        prefix = expected_scheme + " "
        if not value.startswith(prefix):
            raise AgentError("Authentication is required.", status=401, code="authentication_required")
        secret = value[len(prefix) :]
        if len(secret) < 32 or len(secret) > 256 or any(char.isspace() for char in secret):
            raise AgentError("Authentication failed.", status=401, code="authentication_failed")
        return secret

    @staticmethod
    def _authenticate_device_unlocked(
        state: MutableMapping[str, Any], headers: Mapping[str, str], peer: PeerIdentity, *, admin: bool
    ) -> Dict[str, Any]:
        credential = AgentApplication._authorization(headers, AUTH_SCHEME_DEVICE)
        supplied_hash = _secret_hash("device", credential)
        match: Optional[Dict[str, Any]] = None
        for candidate in state["devices"]:
            stored = str(candidate.get("credentialHash") or "")
            if len(stored) == 64 and hmac.compare_digest(stored, supplied_hash):
                match = candidate
        if not match or match.get("revokedAt"):
            raise AgentError("Authentication failed.", status=401, code="authentication_failed")
        if not hmac.compare_digest(str(match.get("tailscaleNodeId") or ""), peer.node_id) or not hmac.compare_digest(
            str(match.get("tailscaleLogin") or ""), peer.login_name
        ):
            raise AgentError("The credential does not belong to this Tailscale peer.", status=403, code="peer_mismatch")
        if admin and match.get("role") != "admin":
            raise AgentError("Administrator authorization is required.", status=403, code="admin_required")
        return match

    def _best_effort_touch_last_seen_unlocked(
        self, state: MutableMapping[str, Any], device: Mapping[str, Any]
    ) -> Dict[str, Any]:
        """Persist non-authority activity metadata at most once per interval.

        A failed metadata write must not turn an otherwise valid read request
        into an outage. The state replacement is atomic but deliberately does
        not create a backup/journal: lastSeenAt is advisory, does not change
        credentials or grants, and frequent backup growth would be harmful.
        """

        try:
            previous_epoch = calendar.timegm(
                time.strptime(str(device.get("lastSeenAt") or ""), "%Y-%m-%dT%H:%M:%SZ")
            )
        except (OverflowError, ValueError):
            previous_epoch = 0
        if epoch_now() - previous_epoch < LAST_SEEN_WRITE_INTERVAL_SECONDS:
            return dict(device)
        next_state = json.loads(json.dumps(state))
        next_device = next(item for item in next_state["devices"] if item["id"] == device["id"])
        next_device["lastSeenAt"] = utc_now()
        try:
            validated = validate_state(next_state)
            payload = (_canonical_json(validated) + "\n").encode("utf-8")
            self.store._atomic_write_bytes(self.store.paths.state_file, payload, 0o600)
        except (AgentError, OSError):
            return dict(device)
        state.clear()
        state.update(validated)
        return next(item for item in state["devices"] if item["id"] == device["id"])

    def health(self) -> Dict[str, Any]:
        initialized = self.store.is_initialized()
        return {
            "protocolVersion": PROTOCOL_VERSION,
            "agentVersion": AGENT_VERSION,
            "status": "ok" if initialized else "needsBootstrap",
        }

    def version(self) -> Dict[str, Any]:
        return {"protocolVersion": PROTOCOL_VERSION, "agentVersion": AGENT_VERSION}

    def bootstrap_info(self) -> Dict[str, Any]:
        with self.store.locked():
            state, manifest = self.store.load_unlocked()
            available = sum(
                1 for item in state["recoveryCodes"] if int(item.get("expiresAtEpoch", 0)) > epoch_now()
            )
            return {
                **self._server_payload(),
                "enrollmentAvailable": available > 0,
                "servicesRevision": manifest["revision"],
            }

    def enroll(
        self, payload: Any, headers: Mapping[str, str], peer: PeerIdentity
    ) -> Dict[str, Any]:
        code = self._authorization(headers, AUTH_SCHEME_ENROLL)
        payload = _require_object_fields(
            payload,
            ("protocolVersion", "clientPublicKey", "deviceLabel", "requestedServiceIds"),
        )
        if payload.get("protocolVersion") != PROTOCOL_VERSION:
            raise AgentError("The enrollment protocol version is incompatible.", code="protocol_mismatch")
        public_key, fingerprint = normalize_ed25519_key(str(payload.get("clientPublicKey") or ""))
        label = _validate_plain_text(payload.get("deviceLabel"), "deviceLabel", 80)
        requested = payload.get("requestedServiceIds", [])
        if not isinstance(requested, list) or len(requested) > MAX_SERVICES or any(
            not isinstance(item, str) or not SERVICE_ID_PATTERN.fullmatch(item) for item in requested
        ) or len(set(requested)) != len(requested):
            raise AgentError("requestedServiceIds must be a list of service ids.", code="invalid_services")
        with self.store.locked():
            state, manifest = self.store.load_unlocked()
            now = epoch_now()
            code_hash = _secret_hash("recovery", code)
            recovery: Optional[Dict[str, Any]] = None
            for candidate in state["recoveryCodes"]:
                stored = str(candidate.get("verifier") or "")
                if (
                    len(stored) == 64
                    and int(candidate.get("expiresAtEpoch", 0)) > now
                    and hmac.compare_digest(stored, code_hash)
                ):
                    recovery = candidate
            if not recovery:
                raise AgentError("The enrollment code is invalid or expired.", status=401, code="enrollment_failed")
            if len([device for device in state["devices"] if not device.get("revokedAt")]) >= MAX_DEVICES:
                raise AgentError("The active device limit has been reached.", status=409, code="device_limit")
            for device in state["devices"]:
                if not device.get("revokedAt") and device.get("publicKey") == public_key:
                    raise AgentError("This SSH key already belongs to an enrolled device.", status=409, code="duplicate_key")
            if not requested:
                requested = [service["id"] for service in manifest["services"] if service["enabledByDefault"]]
            service_ids = sorted(set(requested))
            ports = ports_for_service_ids(manifest, service_ids)
            credential = _new_secret()
            device = {
                "id": uuid.uuid4().hex,
                "deviceLabel": label,
                "publicKey": public_key,
                "fingerprint": fingerprint,
                "credentialHash": _secret_hash("device", credential),
                "role": recovery["role"],
                "tailscaleNodeId": peer.node_id,
                "tailscaleLogin": peer.login_name,
                "serviceIds": service_ids,
                "permitPorts": ports,
                "createdAt": utc_now(),
                "lastSeenAt": utc_now(),
                "revokedAt": None,
            }
            next_state = json.loads(json.dumps(state))
            next_state["recoveryCodes"] = [item for item in next_state["recoveryCodes"] if item["id"] != recovery["id"]]
            next_state["devices"].append(device)
            self.store.transact_unlocked(next_state, manifest, restrict_first=False)
            return {
                **self._server_payload(),
                "deviceId": device["id"],
                "deviceLabel": label,
                "clientKeyFingerprint": fingerprint,
                "role": device["role"],
                "deviceCredential": credential,
                "permitPorts": ports,
                "servicesRevision": manifest["revision"],
            }

    def discovery_get(
        self, headers: Mapping[str, str], peer: PeerIdentity
    ) -> Dict[str, Any]:
        with self.store.locked():
            state, manifest = self.store.load_unlocked()
            device = self._authenticate_device_unlocked(
                state, headers, peer, admin=True
            )
            self._best_effort_touch_last_seen_unlocked(state, device)
            known_ports = {
                int(service["remotePort"]) for service in manifest["services"]
            }

        discovered = self.discovery_provider()
        if not isinstance(discovered, list):
            raise AgentError(
                "Loopback discovery returned an invalid result.",
                status=503,
                code="discovery_unavailable",
            )
        candidates: List[Dict[str, Any]] = []
        excluded_known = 0
        for item in discovered[:64]:
            if not isinstance(item, dict):
                continue
            port = item.get("port")
            if isinstance(port, bool) or not isinstance(port, int) or not 1 <= port <= 65535:
                continue
            if port in known_ports:
                excluded_known += 1
                continue
            process_name = str(item.get("processName") or "")
            if not re.fullmatch(r"[A-Za-z0-9_.+-]{0,64}", process_name):
                process_name = ""
            scheme = str(item.get("scheme") or "tcp")
            if scheme not in ("http", "https", "tcp"):
                scheme = "tcp"
            title = "".join(
                char for char in str(item.get("title") or "") if char.isprintable()
            )[:120]
            template_id = str(item.get("suggestedTemplateId") or "generic-local-web")
            if not SERVICE_ID_PATTERN.fullmatch(template_id):
                template_id = "generic-local-web"
            status_code = item.get("statusCode")
            if isinstance(status_code, bool) or not isinstance(status_code, int):
                status_code = None
            candidates.append(
                {
                    "port": port,
                    "bindAddress": "127.0.0.1",
                    "processName": process_name,
                    "scheme": scheme,
                    "webDetected": bool(item.get("webDetected")),
                    "statusCode": status_code,
                    "title": title or process_name or f"Local listener on {port}",
                    "healthPath": "/",
                    "suggestedTemplateId": template_id,
                    "tlsCertificateVerified": (
                        False if scheme == "https" else None
                    ),
                }
            )
        return {
            "protocolVersion": PROTOCOL_VERSION,
            "discoveredAt": utc_now(),
            "scope": "current-127.0.0.1-listeners",
            "candidates": sorted(candidates, key=lambda item: int(item["port"])),
            "excludedKnownServiceCount": excluded_known,
            "note": "No disks or port ranges were scanned. Nothing was added or authorized.",
        }

    def services_get(self, headers: Mapping[str, str], peer: PeerIdentity) -> Dict[str, Any]:
        with self.store.locked():
            state, manifest = self.store.load_unlocked()
            device = self._authenticate_device_unlocked(state, headers, peer, admin=False)
            self._best_effort_touch_last_seen_unlocked(state, device)
            return {
                "protocolVersion": PROTOCOL_VERSION,
                "servicesRevision": manifest["revision"],
                "services": manifest["services"],
            }

    def me_get(self, headers: Mapping[str, str], peer: PeerIdentity) -> Dict[str, Any]:
        with self.store.locked():
            state, manifest = self.store.load_unlocked()
            device = self._authenticate_device_unlocked(state, headers, peer, admin=False)
            device = self._best_effort_touch_last_seen_unlocked(state, device)
            return {
                "protocolVersion": PROTOCOL_VERSION,
                "servicesRevision": manifest["revision"],
                "device": _public_device(device),
            }

    def services_put(
        self, payload: Any, headers: Mapping[str, str], peer: PeerIdentity
    ) -> Dict[str, Any]:
        payload = _require_object_fields(
            payload,
            ("protocolVersion", "expectedRevision", "services"),
            ("applyDefaultsToAllDevices",),
        )
        if payload.get("protocolVersion") != PROTOCOL_VERSION:
            raise AgentError("The service update protocol is incompatible.", code="protocol_mismatch")
        expected = payload.get("expectedRevision")
        if isinstance(expected, bool) or not isinstance(expected, int):
            raise AgentError("expectedRevision must be an integer.", code="invalid_revision")
        services = validate_services(payload.get("services"))
        apply_defaults = payload.get("applyDefaultsToAllDevices", False)
        if not isinstance(apply_defaults, bool):
            raise AgentError("applyDefaultsToAllDevices must be boolean.", code="invalid_request")
        with self.store.locked():
            state, manifest = self.store.load_unlocked()
            self._authenticate_device_unlocked(state, headers, peer, admin=True)
            if manifest["revision"] != expected:
                raise AgentError("The service catalog changed; reload it before saving.", status=409, code="revision_conflict")
            incoming_by_id = {service["id"]: service for service in services}
            for relay in state["relays"]:
                matching = incoming_by_id.get(relay["slug"])
                if (
                    matching is None
                    or matching["remotePort"] != relay["relayPort"]
                    or matching["scheme"] != relay["scheme"]
                    or matching["openPath"] != relay["openPath"]
                ):
                    raise AgentError(
                        "Managed relay routes must be changed or removed through the relay plan API.",
                        status=409,
                        code="managed_relay_requires_plan",
                    )
            next_manifest = {
                "schemaVersion": 1,
                "revision": expected + 1,
                "updatedAt": utc_now(),
                "services": services,
            }
            next_state = json.loads(json.dumps(state))
            valid_ids = {service["id"] for service in services}
            defaults = {service["id"] for service in services if service["enabledByDefault"]}
            removed_any = False
            for device in next_state["devices"]:
                if device.get("revokedAt"):
                    continue
                selected = set(device.get("serviceIds", [])) & valid_ids
                if apply_defaults:
                    selected |= defaults
                if not selected:
                    raise AgentError(
                        "The update would leave an active device with no authorized service.",
                        status=409,
                        code="empty_device_grant",
                    )
                old_ports = set(device.get("permitPorts", []))
                new_ports = ports_for_service_ids(next_manifest, sorted(selected))
                if old_ports - set(new_ports):
                    removed_any = True
                device["serviceIds"] = sorted(selected)
                device["permitPorts"] = new_ports
            self.store.transact_unlocked(next_state, next_manifest, restrict_first=removed_any)
            return {
                "protocolVersion": PROTOCOL_VERSION,
                "servicesRevision": next_manifest["revision"],
                "services": next_manifest["services"],
            }

    def devices_get(self, headers: Mapping[str, str], peer: PeerIdentity) -> Dict[str, Any]:
        with self.store.locked():
            state, _manifest = self.store.load_unlocked()
            device = self._authenticate_device_unlocked(state, headers, peer, admin=True)
            self._best_effort_touch_last_seen_unlocked(state, device)
            return {
                "protocolVersion": PROTOCOL_VERSION,
                "devices": [_public_device(device) for device in state["devices"]],
            }

    def device_rename(
        self,
        device_id: str,
        payload: Any,
        headers: Mapping[str, str],
        peer: PeerIdentity,
    ) -> Dict[str, Any]:
        if not DEVICE_ID_PATTERN.fullmatch(device_id):
            raise AgentError("The device id is invalid.", code="invalid_device_id")
        payload = _require_object_fields(payload, ("protocolVersion", "deviceLabel"))
        if payload["protocolVersion"] != PROTOCOL_VERSION:
            raise AgentError("The rename protocol version is incompatible.", code="protocol_mismatch")
        label = _validate_plain_text(payload["deviceLabel"], "deviceLabel", 80)
        if label != payload["deviceLabel"]:
            raise AgentError(
                "deviceLabel must be canonical without outer whitespace.",
                code="invalid_deviceLabel",
            )
        with self.store.locked():
            state, manifest = self.store.load_unlocked()
            actor = self._authenticate_device_unlocked(state, headers, peer, admin=True)
            target = next((item for item in state["devices"] if item["id"] == device_id), None)
            if target is None:
                raise AgentError("The device was not found.", status=404, code="device_not_found")
            previous = target["deviceLabel"]
            if previous == label:
                raise AgentError("The device already has that label.", status=409, code="no_change")
            if len(state["auditEvents"]) >= MAX_AUDIT_EVENTS:
                raise AgentError(
                    "The bounded audit log is full; archive it locally before another rename.",
                    status=409,
                    code="audit_limit",
                )
            next_state = json.loads(json.dumps(state))
            next_target = next(item for item in next_state["devices"] if item["id"] == device_id)
            next_target["deviceLabel"] = label
            next_state["auditEvents"].append(
                {
                    "id": uuid.uuid4().hex,
                    "action": "device_renamed",
                    "actorDeviceId": actor["id"],
                    "targetDeviceId": device_id,
                    "occurredAt": utc_now(),
                    "previousDeviceLabel": previous,
                    "deviceLabel": label,
                }
            )
            self.store.transact_unlocked(next_state, manifest, restrict_first=False)
            return {
                "protocolVersion": PROTOCOL_VERSION,
                "renamed": True,
                "previousDeviceLabel": previous,
                "device": _public_device(next_target),
            }

    def enrollment_code_create(
        self, payload: Any, headers: Mapping[str, str], peer: PeerIdentity
    ) -> Dict[str, Any]:
        payload = _require_object_fields(payload, ("protocolVersion", "ttlSeconds"))
        if payload["protocolVersion"] != PROTOCOL_VERSION:
            raise AgentError("The enrollment-code protocol version is incompatible.", code="protocol_mismatch")
        ttl = payload["ttlSeconds"]
        if isinstance(ttl, bool) or not isinstance(ttl, int) or not 60 <= ttl <= 3600:
            raise AgentError("ttlSeconds must be an integer from 60 to 3600.", code="invalid_ttl")
        secret, recovery = _make_recovery("device", ttl)
        with self.store.locked():
            state, manifest = self.store.load_unlocked()
            self._authenticate_device_unlocked(state, headers, peer, admin=True)
            now = epoch_now()
            active_codes = [
                item for item in state["recoveryCodes"] if int(item.get("expiresAtEpoch", 0)) > now
            ]
            if len(active_codes) >= MAX_RECOVERY_CODES:
                raise AgentError(
                    "Too many active enrollment codes; wait for one to expire.",
                    status=409,
                    code="code_limit",
                )
            next_state = json.loads(json.dumps(state))
            next_state["recoveryCodes"] = active_codes + [recovery]
            self.store.transact_unlocked(next_state, manifest, restrict_first=False)
        return {
            "protocolVersion": PROTOCOL_VERSION,
            "enrollmentCode": secret,
            "expiresAt": recovery["expiresAt"],
        }

    def revoke(
        self, device_id: str, headers: Mapping[str, str], peer: PeerIdentity
    ) -> Dict[str, Any]:
        if not DEVICE_ID_PATTERN.fullmatch(device_id):
            raise AgentError("The device id is invalid.", code="invalid_device_id")
        with self.store.locked():
            state, manifest = self.store.load_unlocked()
            actor = self._authenticate_device_unlocked(state, headers, peer, admin=True)
            target = next((item for item in state["devices"] if item["id"] == device_id), None)
            if not target or target.get("revokedAt"):
                raise AgentError("The active device was not found.", status=404, code="device_not_found")
            active_admins = [
                item for item in state["devices"] if item.get("role") == "admin" and not item.get("revokedAt")
            ]
            if target["id"] == actor["id"] and len(active_admins) == 1:
                raise AgentError("The final administrator cannot revoke itself.", status=409, code="last_admin")
            next_state = json.loads(json.dumps(state))
            next_target = next(item for item in next_state["devices"] if item["id"] == device_id)
            revoked_at = utc_now()
            next_target["revokedAt"] = revoked_at
            next_target["credentialHash"] = ""
            next_target["permitPorts"] = []
            self.store.transact_unlocked(next_state, manifest, restrict_first=True)
            return {
                "protocolVersion": PROTOCOL_VERSION,
                "deviceId": device_id,
                "revoked": True,
                "revokedAt": revoked_at,
                "note": "Existing SSH sessions must be disconnected separately.",
            }

    def _relay_manager(self) -> Any:
        if self.relay_manager is None:
            raise AgentError("Relay management is not configured on this Agent.", status=503, code="relay_unavailable")
        return self.relay_manager

    @staticmethod
    def _relay_spec_from_wire(value: Any) -> Any:
        value = _require_object_fields(
            value,
            ("slug", "relayPort", "targetHost", "targetPort", "scheme", "openPath"),
        )
        module = relay_module()
        try:
            return module.RelaySpec.create(
                slug=value["slug"],
                relay_port=value["relayPort"],
                target_host=value["targetHost"],
                target_port=value["targetPort"],
                scheme=value["scheme"],
                open_path=value["openPath"],
            )
        except module.RelayError as exc:
            raise AgentError(str(exc), code="invalid_relay") from exc

    @staticmethod
    def _relay_service(spec: Any, value: Any) -> Dict[str, Any]:
        value = _require_object_fields(
            value,
            (
                "name",
                "description",
                "icon",
                "suggestedLocalPort",
                "healthPath",
                "healthKind",
                "enabledByDefault",
            ),
        )
        return validate_services(
            [
                {
                    "id": spec.slug,
                    "name": value["name"],
                    "description": value["description"],
                    "icon": value["icon"],
                    "remotePort": spec.relay_port,
                    "suggestedLocalPort": value["suggestedLocalPort"],
                    "scheme": spec.scheme,
                    "openPath": spec.open_path,
                    "healthPath": value["healthPath"],
                    "healthKind": value["healthKind"],
                    "enabledByDefault": value["enabledByDefault"],
                }
            ]
        )[0]

    @staticmethod
    def _public_relay_plan(plan: Any) -> Dict[str, Any]:
        return {
            "operation": plan.operation.value,
            "action": plan.action.value,
            "slug": plan.slug,
            "unitName": plan.unit_name,
            "spec": None if plan.spec is None else plan.spec.to_metadata(),
            "observedSha256": plan.observed_sha256,
            "reason": plan.reason,
            "requiresConfirmation": True,
            "canApply": bool(plan.can_apply),
        }

    @staticmethod
    def _relay_state_digest(state: Mapping[str, Any]) -> str:
        relevant = [
            {
                "deviceId": device["id"],
                "serviceIds": device.get("serviceIds", []),
                "permitPorts": device.get("permitPorts", []),
                "revokedAt": device.get("revokedAt"),
            }
            for device in state["devices"]
        ]
        return hashlib.sha256(_canonical_json(relevant).encode("utf-8")).hexdigest()

    def _prepare_relay_install_unlocked(
        self,
        state: Mapping[str, Any],
        manifest: Mapping[str, Any],
        spec: Any,
        service: Mapping[str, Any],
        grant_to_all: bool,
    ) -> Dict[str, Any]:
        manager = self._relay_manager()
        module = relay_module()
        try:
            relay_plan = manager.plan_install(spec)
        except module.RelayError as exc:
            raise AgentError(str(exc), status=409, code="relay_conflict") from exc
        for existing in manifest["services"]:
            if existing["id"] != spec.slug and existing["remotePort"] == spec.relay_port:
                raise AgentError(
                    "The relay port already belongs to another catalog service.",
                    status=409,
                    code="relay_port_conflict",
                )
        services = [dict(item) for item in manifest["services"] if item["id"] != spec.slug]
        services.append(dict(service))
        services = validate_services(services)
        next_manifest = {
            "schemaVersion": 1,
            "revision": manifest["revision"] + 1,
            "updatedAt": utc_now(),
            "services": services,
        }
        next_state = json.loads(json.dumps(state))
        next_state["relays"] = [
            relay for relay in next_state["relays"] if relay["slug"] != spec.slug
        ] + [spec.to_metadata()]
        affected: set[str] = set()
        removed_ports: set[int] = set()
        added_ports: set[int] = set()
        for device in next_state["devices"]:
            if device.get("revokedAt"):
                continue
            before_ids = set(device.get("serviceIds", []))
            selected = set(before_ids)
            if grant_to_all:
                selected.add(spec.slug)
            old_ports = set(device.get("permitPorts", []))
            new_ports = set(ports_for_service_ids(next_manifest, sorted(selected)))
            if selected != before_ids or new_ports != old_ports:
                affected.add(device["id"])
                removed_ports |= old_ports - new_ports
                added_ports |= new_ports - old_ports
            device["serviceIds"] = sorted(selected)
            device["permitPorts"] = sorted(new_ports)
        if relay_plan.action is module.PlanAction.UPDATE:
            # An unchanged relay/listener port does not imply unchanged
            # behavior: every active device holding this service is redirected
            # when its explicit final target changes. Surface those devices in
            # the preview even though SSH permitopen has no port delta.
            affected.update(
                device["id"]
                for device in next_state["devices"]
                if not device.get("revokedAt")
                and spec.slug in device.get("serviceIds", [])
            )
        public_plan = self._public_relay_plan(relay_plan)
        token_material = {
            "operation": "install",
            "relayPlan": public_plan,
            "servicesRevision": manifest["revision"],
            "service": service,
            "grantToAllDevices": grant_to_all,
            "stateDigest": self._relay_state_digest(state),
        }
        plan_token = hashlib.sha256(_canonical_json(token_material).encode("utf-8")).hexdigest()
        return {
            "relayPlanObject": relay_plan,
            "relayPlan": public_plan,
            "planToken": plan_token,
            "nextState": next_state,
            "nextManifest": next_manifest,
            "removedPorts": sorted(removed_ports),
            "addedPorts": sorted(added_ports),
            "affectedDeviceIds": sorted(affected),
        }

    def _prepare_relay_delete_unlocked(
        self, state: Mapping[str, Any], manifest: Mapping[str, Any], slug: str
    ) -> Dict[str, Any]:
        module = relay_module()
        try:
            relay_plan = self._relay_manager().plan_delete(slug)
        except module.RelayError as exc:
            raise AgentError(str(exc), status=409, code="relay_conflict") from exc
        managed_relay_exists = any(relay["slug"] == slug for relay in state["relays"])
        service_exists = managed_relay_exists and any(
            service["id"] == slug for service in manifest["services"]
        )
        next_manifest = json.loads(json.dumps(manifest))
        next_state = json.loads(json.dumps(state))
        affected: List[str] = []
        removed_ports: set[int] = set()
        if service_exists:
            remaining = [service for service in manifest["services"] if service["id"] != slug]
            if not remaining:
                raise AgentError(
                    "The final catalog service cannot be deleted.", status=409, code="final_service"
                )
            next_manifest = {
                "schemaVersion": 1,
                "revision": manifest["revision"] + 1,
                "updatedAt": utc_now(),
                "services": validate_services(remaining),
            }
            for device in next_state["devices"]:
                if device.get("revokedAt"):
                    continue
                before_ids = set(device.get("serviceIds", []))
                selected = before_ids - {slug}
                if not selected:
                    raise AgentError(
                        "Deleting this relay would leave an active device with no service.",
                        status=409,
                        code="empty_device_grant",
                    )
                old_ports = set(device.get("permitPorts", []))
                new_ports = set(ports_for_service_ids(next_manifest, sorted(selected)))
                if selected != before_ids or new_ports != old_ports:
                    affected.append(device["id"])
                    removed_ports |= old_ports - new_ports
                device["serviceIds"] = sorted(selected)
                device["permitPorts"] = sorted(new_ports)
        next_state["relays"] = [relay for relay in next_state["relays"] if relay["slug"] != slug]
        public_plan = self._public_relay_plan(relay_plan)
        if relay_plan.action is module.PlanAction.ABSENT and managed_relay_exists:
            # The unit may have been removed outside the Agent while the Agent still
            # owns catalog/grant state for the relay.  Expose this as an actionable
            # delete (rather than an absent no-op) so strict clients can confirm the
            # state cleanup using the same plan/apply contract.
            public_plan["action"] = "delete"
            public_plan["canApply"] = True
            public_plan["reason"] = "relay unit is absent; confirmed deletion will clean up catalog grants"
        token_material = {
            "operation": "delete",
            "relayPlan": public_plan,
            "servicesRevision": manifest["revision"],
            "serviceExists": service_exists,
            "stateDigest": self._relay_state_digest(state),
        }
        plan_token = hashlib.sha256(_canonical_json(token_material).encode("utf-8")).hexdigest()
        return {
            "relayPlanObject": relay_plan,
            "relayPlan": public_plan,
            "planToken": plan_token,
            "nextState": next_state,
            "nextManifest": next_manifest,
            "removedPorts": sorted(removed_ports),
            "addedPorts": [],
            "affectedDeviceIds": sorted(affected),
            "serviceExists": service_exists,
        }

    @staticmethod
    def _relay_plan_response(prepared: Mapping[str, Any]) -> Dict[str, Any]:
        return {
            "protocolVersion": PROTOCOL_VERSION,
            "planToken": prepared["planToken"],
            "relayPlan": prepared["relayPlan"],
            "serviceChange": {
                "addedRemotePorts": prepared["addedPorts"],
                "removedRemotePorts": prepared["removedPorts"],
                "affectedDeviceIds": prepared["affectedDeviceIds"],
            },
            "nextServicesRevision": prepared["nextManifest"]["revision"],
        }

    @staticmethod
    def _relay_request(value: Any, *, confirmation: bool) -> Tuple[Any, Dict[str, Any], bool, Optional[str]]:
        required = ["protocolVersion", "spec", "service", "grantToAllDevices"]
        if confirmation:
            required += ["planToken", "confirmed"]
        value = _require_object_fields(value, required)
        if value["protocolVersion"] != PROTOCOL_VERSION:
            raise AgentError("The relay protocol version is incompatible.", code="protocol_mismatch")
        if not isinstance(value["grantToAllDevices"], bool):
            raise AgentError("grantToAllDevices must be boolean.", code="invalid_relay")
        if confirmation and value["confirmed"] is not True:
            raise AgentError("Explicit relay confirmation is required.", status=409, code="confirmation_required")
        spec = AgentApplication._relay_spec_from_wire(value["spec"])
        service = AgentApplication._relay_service(spec, value["service"])
        token = str(value["planToken"]) if confirmation else None
        if token is not None and not re.fullmatch(r"[0-9a-f]{64}", token):
            raise AgentError("The relay plan token is invalid.", code="invalid_plan_token")
        return spec, service, value["grantToAllDevices"], token

    def relay_plan(self, payload: Any, headers: Mapping[str, str], peer: PeerIdentity) -> Dict[str, Any]:
        spec, service, grant_to_all, _token = self._relay_request(payload, confirmation=False)
        with self.store.locked():
            state, manifest = self.store.load_unlocked()
            self._authenticate_device_unlocked(state, headers, peer, admin=True)
            prepared = self._prepare_relay_install_unlocked(
                state, manifest, spec, service, grant_to_all
            )
            return self._relay_plan_response(prepared)

    def relay_probe(self, payload: Any, headers: Mapping[str, str], peer: PeerIdentity) -> Dict[str, Any]:
        payload = _require_object_fields(payload, ("protocolVersion", "spec"))
        if payload["protocolVersion"] != PROTOCOL_VERSION:
            raise AgentError("The relay protocol version is incompatible.", code="protocol_mismatch")
        spec = self._relay_spec_from_wire(payload["spec"])
        with self.store.locked():
            state, _manifest = self.store.load_unlocked()
            self._authenticate_device_unlocked(state, headers, peer, admin=True)
        module = relay_module()
        try:
            probe = self._relay_manager().probe(spec)
        except module.RelayError as exc:
            raise AgentError(str(exc), status=409, code="relay_probe_failed") from exc
        target_error = "".join(
            char for char in str(probe.target_error) if ord(char) >= 32 and ord(char) != 127
        )[:240]
        return {
            "protocolVersion": PROTOCOL_VERSION,
            "spec": spec.to_metadata(),
            "dependencyAvailable": probe.dependency_available,
            "relayPortInUse": probe.relay_port_in_use,
            "targetReachable": probe.target_reachable,
            "readyToInstall": probe.ready_to_install,
            "targetError": target_error,
        }

    def relay_apply(self, payload: Any, headers: Mapping[str, str], peer: PeerIdentity) -> Dict[str, Any]:
        spec, service, grant_to_all, supplied_token = self._relay_request(payload, confirmation=True)
        manager = self._relay_manager()
        module = relay_module()
        with self.store.locked():
            state, manifest = self.store.load_unlocked()
            self._authenticate_device_unlocked(state, headers, peer, admin=True)
            prepared = self._prepare_relay_install_unlocked(
                state, manifest, spec, service, grant_to_all
            )
            if not hmac.compare_digest(str(supplied_token), str(prepared["planToken"])):
                raise AgentError("The relay plan is stale; inspect a new plan.", status=409, code="stale_plan")
            plan = prepared["relayPlanObject"]
            if not plan.can_apply:
                raise AgentError(plan.reason, status=409, code="relay_conflict")
            # Preflight the SSH mutation before starting any external process.
            self.store.render_authorized_text_unlocked(prepared["nextState"])
            previous = manager.plan_delete(spec.slug)
            try:
                result = manager.apply_install(plan)
            except module.RelayError as exc:
                raise AgentError(str(exc), status=409, code="relay_apply_failed") from exc
            try:
                self.store.transact_unlocked(
                    prepared["nextState"],
                    prepared["nextManifest"],
                    restrict_first=bool(prepared["removedPorts"]),
                )
            except Exception as commit_error:
                if self.store.paths.journal_file.exists():
                    raise AgentError(
                        "Relay applied; the catalog transaction is pending recovery. Restart the Agent.",
                        status=503,
                        code="transaction_pending",
                    ) from commit_error
                if result.applied:
                    try:
                        if plan.action is module.PlanAction.CREATE:
                            manager.delete(spec.slug, confirmed=True)
                        elif previous.spec is not None:
                            manager.apply_install(manager.plan_install(previous.spec))
                    except module.RelayError as rollback_error:
                        raise AgentError(
                            "Relay applied but catalog commit and relay rollback both failed.",
                            status=500,
                            code="relay_rollback_failed",
                        ) from rollback_error
                raise
            response = self._relay_plan_response(prepared)
            response.update({"applied": bool(result.applied), "backupCreated": result.backup_path is not None})
            return response

    @staticmethod
    def _relay_delete_request(value: Any, *, confirmation: bool) -> Tuple[str, Optional[str]]:
        required = ["protocolVersion", "slug"]
        if confirmation:
            required += ["planToken", "confirmed"]
        value = _require_object_fields(value, required)
        if value["protocolVersion"] != PROTOCOL_VERSION:
            raise AgentError("The relay protocol version is incompatible.", code="protocol_mismatch")
        slug = str(value["slug"])
        if not SERVICE_ID_PATTERN.fullmatch(slug):
            raise AgentError("The relay slug is invalid.", code="invalid_relay")
        token = str(value["planToken"]) if confirmation else None
        if confirmation and value["confirmed"] is not True:
            raise AgentError("Explicit relay deletion confirmation is required.", status=409, code="confirmation_required")
        if token is not None and not re.fullmatch(r"[0-9a-f]{64}", token):
            raise AgentError("The relay plan token is invalid.", code="invalid_plan_token")
        return slug, token

    def relay_delete_plan(
        self, payload: Any, headers: Mapping[str, str], peer: PeerIdentity
    ) -> Dict[str, Any]:
        slug, _token = self._relay_delete_request(payload, confirmation=False)
        with self.store.locked():
            state, manifest = self.store.load_unlocked()
            self._authenticate_device_unlocked(state, headers, peer, admin=True)
            return self._relay_plan_response(
                self._prepare_relay_delete_unlocked(state, manifest, slug)
            )

    def relay_delete(
        self, payload: Any, headers: Mapping[str, str], peer: PeerIdentity
    ) -> Dict[str, Any]:
        slug, supplied_token = self._relay_delete_request(payload, confirmation=True)
        manager = self._relay_manager()
        module = relay_module()
        with self.store.locked():
            state, manifest = self.store.load_unlocked()
            self._authenticate_device_unlocked(state, headers, peer, admin=True)
            prepared = self._prepare_relay_delete_unlocked(state, manifest, slug)
            if not hmac.compare_digest(str(supplied_token), str(prepared["planToken"])):
                raise AgentError("The relay deletion plan is stale.", status=409, code="stale_plan")
            plan = prepared["relayPlanObject"]
            if plan.action is module.PlanAction.BLOCKED:
                raise AgentError(plan.reason, status=409, code="relay_conflict")
            self.store.render_authorized_text_unlocked(prepared["nextState"])
            self.store.transact_unlocked(
                prepared["nextState"], prepared["nextManifest"], restrict_first=True
            )
            try:
                result = manager.delete(slug, confirmed=True)
            except module.RelayError as exc:
                try:
                    self.store.transact_unlocked(state, manifest, restrict_first=False)
                except Exception as rollback_error:
                    raise AgentError(
                        "Relay deletion and catalog rollback both failed.",
                        status=500,
                        code="relay_rollback_failed",
                    ) from rollback_error
                raise AgentError(str(exc), status=409, code="relay_delete_failed") from exc
            response = self._relay_plan_response(prepared)
            response.update({"applied": bool(result.applied), "backupCreated": result.backup_path is not None})
            return response

    def dispatch(
        self,
        method: str,
        path: str,
        headers: Mapping[str, str],
        payload: Any,
        peer: PeerIdentity,
    ) -> Tuple[int, Dict[str, Any]]:
        if method == "GET" and path == "/v1/health":
            return 200, self.health()
        if method == "GET" and path == "/v1/version":
            return 200, self.version()
        if method == "GET" and path == "/v1/bootstrap":
            return 200, self.bootstrap_info()
        if method == "POST" and path == "/v1/enroll":
            return 201, self.enroll(payload, headers, peer)
        if method == "GET" and path == "/v1/services":
            return 200, self.services_get(headers, peer)
        if method == "GET" and path == "/v1/discovery":
            return 200, self.discovery_get(headers, peer)
        if method == "GET" and path == "/v1/me":
            return 200, self.me_get(headers, peer)
        if method == "PUT" and path == "/v1/services":
            return 200, self.services_put(payload, headers, peer)
        if method == "GET" and path == "/v1/devices":
            return 200, self.devices_get(headers, peer)
        if method == "POST" and path == "/v1/enrollment-codes":
            return 201, self.enrollment_code_create(payload, headers, peer)
        if method == "POST" and path == "/v1/relays/plan":
            return 200, self.relay_plan(payload, headers, peer)
        if method == "POST" and path == "/v1/relays/probe":
            return 200, self.relay_probe(payload, headers, peer)
        if method == "POST" and path == "/v1/relays/apply":
            return 200, self.relay_apply(payload, headers, peer)
        if method == "POST" and path == "/v1/relays/delete-plan":
            return 200, self.relay_delete_plan(payload, headers, peer)
        if method == "POST" and path == "/v1/relays/delete":
            return 200, self.relay_delete(payload, headers, peer)
        match = re.fullmatch(r"/v1/devices/([0-9a-f]{32})/revoke", path)
        if method == "POST" and match:
            body = _require_object_fields(payload, ("protocolVersion",))
            if body["protocolVersion"] != PROTOCOL_VERSION:
                raise AgentError("The revocation protocol version is incompatible.", code="protocol_mismatch")
            return 200, self.revoke(match.group(1), headers, peer)
        match = re.fullmatch(r"/v1/devices/([0-9a-f]{32})/rename", path)
        if method == "POST" and match:
            return 200, self.device_rename(match.group(1), payload, headers, peer)
        raise AgentError("The Agent endpoint was not found.", status=404, code="not_found")


class RateLimiter:
    def __init__(self) -> None:
        self._events: Dict[Tuple[str, str], List[float]] = {}
        self._lock = threading.Lock()

    def allow(self, address: str, bucket: str, limit: int, window: float) -> bool:
        now = time.monotonic()
        key = (address, bucket)
        with self._lock:
            if len(self._events) > 1024:
                self._events = {
                    item_key: values
                    for item_key, values in self._events.items()
                    if any(value > now - 600.0 for value in values)
                }
            recent = [value for value in self._events.get(key, []) if value > now - window]
            if len(recent) >= limit:
                self._events[key] = recent
                return False
            recent.append(now)
            self._events[key] = recent
            return True


class AgentHttpServer(http.server.ThreadingHTTPServer):
    allow_reuse_address = False
    daemon_threads = True
    request_queue_size = 16

    def __init__(
        self,
        address: Tuple[str, int],
        application: AgentApplication,
        peer_verifier: Any,
        *,
        allowed_networks: Sequence[ipaddress._BaseNetwork] = (TAILSCALE_IPV4, TAILSCALE_IPV6),
    ) -> None:
        host = ipaddress.ip_address(address[0])
        if not any(host in network for network in allowed_networks):
            raise AgentError("The Agent may bind only to an explicitly allowed address.", code="unsafe_bind")
        if host.is_unspecified:
            raise AgentError("The Agent never binds a wildcard address.", code="unsafe_bind")
        self.application = application
        self.peer_verifier = peer_verifier
        self.allowed_networks = tuple(allowed_networks)
        self.rate_limiter = RateLimiter()
        self._worker_slots = threading.BoundedSemaphore(16)
        super().__init__(address, AgentRequestHandler)

    def process_request(self, request: Any, client_address: Any) -> None:
        if not self._worker_slots.acquire(blocking=False):
            self.shutdown_request(request)
            return
        try:
            super().process_request(request, client_address)
        except Exception:
            self._worker_slots.release()
            raise

    def process_request_thread(self, request: Any, client_address: Any) -> None:
        try:
            super().process_request_thread(request, client_address)
        finally:
            self._worker_slots.release()


class AgentRequestHandler(http.server.BaseHTTPRequestHandler):
    server: AgentHttpServer
    server_version = "JKBridgeAgent"
    sys_version = ""

    def setup(self) -> None:
        super().setup()
        self.connection.settimeout(5.0)

    def log_message(self, _format: str, *args: Any) -> None:
        # Do not log request headers, bodies, credentials, or enrollment codes.
        return

    def do_GET(self) -> None:  # noqa: N802
        self._handle("GET")

    def do_POST(self) -> None:  # noqa: N802
        self._handle("POST")

    def do_PUT(self) -> None:  # noqa: N802
        self._handle("PUT")

    def _handle(self, method: str) -> None:
        try:
            address = str(ipaddress.ip_address(self.client_address[0]))
            parsed_address = ipaddress.ip_address(address)
            if not any(parsed_address in network for network in self.server.allowed_networks):
                raise AgentError("Only Tailscale peers may access the Agent.", status=403, code="peer_forbidden")
            if not self.server.rate_limiter.allow(address, "all", 120, 60.0):
                raise AgentError("Too many requests.", status=429, code="rate_limited")
            parsed_path = urllib.parse.urlsplit(self.path)
            if parsed_path.query or parsed_path.fragment:
                raise AgentError("Query strings are not accepted.", code="invalid_path")
            if parsed_path.path == "/v1/enroll" and not self.server.rate_limiter.allow(
                address, "enroll", 5, 600.0
            ):
                raise AgentError("Too many enrollment attempts.", status=429, code="rate_limited")
            if parsed_path.path == "/v1/enroll" and not self.server.rate_limiter.allow(
                "__global__", "enroll", 20, 600.0
            ):
                raise AgentError("Enrollment is temporarily rate limited.", status=429, code="rate_limited")
            if parsed_path.path.startswith("/v1/relays/") and not self.server.rate_limiter.allow(
                address, "relay", 20, 60.0
            ):
                raise AgentError("Relay operations are temporarily rate limited.", status=429, code="rate_limited")
            if parsed_path.path == "/v1/discovery" and not self.server.rate_limiter.allow(
                address, "discovery", 6, 60.0
            ):
                raise AgentError("Discovery is temporarily rate limited.", status=429, code="rate_limited")
            if parsed_path.path == "/v1/enrollment-codes" and (
                not self.server.rate_limiter.allow(address, "enrollment-code", 5, 600.0)
                or not self.server.rate_limiter.allow("__global__", "enrollment-code", 20, 600.0)
            ):
                raise AgentError("Enrollment-code creation is temporarily rate limited.", status=429, code="rate_limited")
            peer = self.server.peer_verifier.verify(address)
            for header_name in ("Authorization", "Content-Length", "X-JKBridge-Protocol-Version"):
                if len(self.headers.get_all(header_name, [])) > 1:
                    raise AgentError("Duplicate security-sensitive headers are not accepted.", code="invalid_headers")
            if self.headers.get("Origin"):
                raise AgentError("Browser-origin requests are not accepted.", status=403, code="origin_forbidden")
            if self.headers.get("X-JKBridge-Protocol-Version") != str(PROTOCOL_VERSION):
                raise AgentError("The Agent protocol version is incompatible.", code="protocol_mismatch")
            body: Any = None
            if method in ("POST", "PUT"):
                if self.headers.get_content_type() != "application/json":
                    raise AgentError("The request must use application/json.", code="invalid_content_type")
                try:
                    length = int(self.headers.get("Content-Length", "0"))
                except ValueError as exc:
                    raise AgentError("The request length is invalid.", code="invalid_length") from exc
                if not 1 <= length <= MAX_REQUEST_BYTES:
                    raise AgentError("The request is empty or too large.", status=413, code="invalid_length")
                try:
                    body = json.loads(
                        self.rfile.read(length).decode("utf-8"),
                        object_pairs_hook=_strict_json_object,
                    )
                except (UnicodeDecodeError, json.JSONDecodeError) as exc:
                    raise AgentError("The request is not valid UTF-8 JSON.", code="invalid_json") from exc
            headers = {key.lower(): value for key, value in self.headers.items()}
            status_code, response = self.server.application.dispatch(
                method, parsed_path.path, headers, body, peer
            )
            self._json(status_code, response)
        except AgentError as exc:
            self._json(exc.status, {"error": {"code": exc.code, "message": str(exc)}})
        except (BrokenPipeError, ConnectionResetError, TimeoutError):
            return
        except Exception as exc:
            print("JK Bridge Agent request failed safely: " + type(exc).__name__, file=sys.stderr)
            self._json(500, {"error": {"code": "internal_error", "message": "The Agent failed safely."}})

    def _json(self, status_code: int, value: Mapping[str, Any]) -> None:
        payload = _canonical_json(value).encode("utf-8")
        self.send_response(status_code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(payload)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Referrer-Policy", "no-referrer")
        self.end_headers()
        self.wfile.write(payload)


def _initial_manifest(services: Optional[List[Dict[str, Any]]] = None) -> Dict[str, Any]:
    return {
        "schemaVersion": 1,
        "revision": 1,
        "updatedAt": utc_now(),
        "services": validate_services(services if services is not None else DEFAULT_SERVICES),
    }


def _initial_state(recovery: Mapping[str, Any]) -> Dict[str, Any]:
    return {
        "schemaVersion": 1,
        "serverInstanceId": uuid.uuid4().hex,
        "createdAt": utc_now(),
        "devices": [],
        "relays": [],
        "auditEvents": [],
        "recoveryCodes": [dict(recovery)],
    }


def _make_recovery(role: str, ttl: int) -> Tuple[str, Dict[str, Any]]:
    if role not in ("admin", "device"):
        raise AgentError("Recovery role must be admin or device.", code="invalid_role")
    if not 60 <= ttl <= 86400:
        raise AgentError("Recovery code lifetime must be between 60 and 86400 seconds.", code="invalid_ttl")
    secret = _new_secret()
    expires = epoch_now() + ttl
    return secret, {
        "id": uuid.uuid4().hex,
        "verifier": _secret_hash("recovery", secret),
        "role": role,
        "createdAt": utc_now(),
        "expiresAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(expires)),
        "expiresAtEpoch": expires,
    }


def _load_services_file(path: Path) -> List[Dict[str, Any]]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise AgentError("The services input file is unreadable JSON.", code="invalid_services") from exc
    if isinstance(value, dict):
        value = value.get("services")
    return validate_services(value)


def _paths_from_args(args: argparse.Namespace, home: Path) -> AgentPaths:
    return AgentPaths.from_values(
        Path(args.state_dir),
        Path(args.manifest) if args.manifest else None,
        Path(args.authorized_keys) if args.authorized_keys else None,
        home,
    )


def run_bootstrap(args: argparse.Namespace) -> int:
    if args.agent_port not in range(1, 65536):
        raise AgentError("The Agent port must be between 1 and 65535.", code="invalid_port")
    _user, home = current_user()
    paths = _paths_from_args(args, home)
    services = _load_services_file(Path(args.services_file)) if args.services_file else None
    identity = tailscale_identity()
    code, recovery = _make_recovery("admin", args.code_ttl)
    AgentStore(paths).initialize(_initial_state(recovery), _initial_manifest(services))
    setup_code = encode_setup_code(f"http://{identity.ipv4}:{args.agent_port}/", code)
    print("JK Bridge Agent initialized.")
    print("Copy this one-time setup code to JK Bridge for Windows (expires in {} seconds):".format(args.code_ttl))
    print(setup_code)
    print("It contains the Agent address and one-time administrator credential.")
    print("The credential is not stored in plaintext. Do not save the setup code in notes or logs.")
    return 0


def run_recovery_code(args: argparse.Namespace) -> int:
    _user, home = current_user()
    store = AgentStore(_paths_from_args(args, home))
    secret, recovery = _make_recovery(args.role, args.ttl)
    with store.locked():
        state, manifest = store.load_unlocked()
        now = epoch_now()
        active_codes = [item for item in state["recoveryCodes"] if int(item.get("expiresAtEpoch", 0)) > now]
        if len(active_codes) >= MAX_RECOVERY_CODES:
            raise AgentError("Too many active recovery codes; wait for one to expire.", status=409, code="code_limit")
        next_state = json.loads(json.dumps(state))
        next_state["recoveryCodes"] = active_codes + [recovery]
        store.transact_unlocked(next_state, manifest, restrict_first=False)
    print("One-time {} enrollment code (expires in {} seconds):".format(args.role, args.ttl))
    print(secret)
    print("This code is not stored in plaintext. Do not save it in notes or logs.")
    return 0


def run_status(args: argparse.Namespace) -> int:
    _user, home = current_user()
    store = AgentStore(_paths_from_args(args, home))
    with store.locked():
        state, manifest = store.load_unlocked()
        authorized_text = store.read_authorized_text_unlocked()
        managed_entries = sum(1 for line in authorized_text.splitlines() if parse_agent_authorized_line(line))
        result = {
            "agentVersion": AGENT_VERSION,
            "stateDirectory": str(store.paths.state_dir),
            "manifest": str(store.paths.manifest_file),
            "authorizedKeys": str(store.paths.authorized_keys),
            "servicesRevision": manifest["revision"],
            "serviceCount": len(manifest["services"]),
            "activeDevices": sum(1 for device in state["devices"] if not device.get("revokedAt")),
            "managedAuthorizedKeys": managed_entries,
            "managedRelays": len(state["relays"]),
            "activeRecoveryCodes": sum(
                1 for item in state["recoveryCodes"] if int(item.get("expiresAtEpoch", 0)) > epoch_now()
            ),
        }
    if args.json:
        print(json.dumps(result, ensure_ascii=False, indent=2))
    else:
        for key, value in result.items():
            print("{}: {}".format(key, value))
    return 0


def _relay_process_runner(arguments: Sequence[str]) -> Any:
    module = relay_module()
    try:
        completed = subprocess.run(
            list(arguments),
            check=False,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            timeout=20,
        )
    except subprocess.TimeoutExpired:
        return module.CommandResult(124, "", "operation timed out")
    return module.CommandResult(completed.returncode, completed.stdout, completed.stderr)


def run_serve(args: argparse.Namespace) -> int:
    ssh_user, home = current_user()
    identity = tailscale_identity()
    if args.port not in range(1, 65536):
        raise AgentError("The listen port must be between 1 and 65535.", code="invalid_port")
    context = ServerContext(
        identity=identity,
        ssh_user=ssh_user,
        ssh_port=_validate_port(args.ssh_port, "sshPort"),
        host_public_key=read_host_public_key(Path(args.host_key)),
        display_name=socket.gethostname(),
    )
    store = AgentStore(_paths_from_args(args, home))
    store.load()
    module = relay_module()
    relay_manager = module.RelayManager(
        Path(args.relay_unit_dir).expanduser()
        if args.relay_unit_dir
        else home / ".config" / "systemd" / "user",
        store.paths.state_dir / "relay-backups",
        socat_path=Path(args.socat_path),
        systemctl_path=args.systemctl_path,
        runner=_relay_process_runner,
    )
    if args.dry_run:
        print("Configuration valid. Agent would bind {}:{} only.".format(identity.ipv4, args.port))
        return 0
    with AgentHttpServer(
        (identity.ipv4, args.port),
        AgentApplication(store, context, relay_manager),
        TailscalePeerVerifier(),
    ) as server:
        print("JK Bridge Agent {} listening on {}:{} (Tailscale only).".format(AGENT_VERSION, identity.ipv4, args.port))
        try:
            server.serve_forever(poll_interval=0.5)
        except KeyboardInterrupt:
            print("\nJK Bridge Agent stopped.", file=sys.stderr)
        finally:
            server.shutdown()
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Persistent JK Bridge Linux Agent over Tailscale.")
    parser.add_argument(
        "--state-dir",
        default="~/.local/state/jkbridge-agent",
        help="private Agent state directory",
    )
    parser.add_argument("--manifest", help="service manifest path (default: STATE_DIR/services.json)")
    parser.add_argument("--authorized-keys", help="authorized_keys path (default: ~/.ssh/authorized_keys)")
    subparsers = parser.add_subparsers(dest="command", required=True)

    bootstrap = subparsers.add_parser("bootstrap", help="initialize state and issue the first admin code")
    bootstrap.add_argument("--services-file", help="JSON service list or manifest to import")
    bootstrap.add_argument("--code-ttl", type=int, default=900)
    bootstrap.add_argument("--agent-port", type=int, default=DEFAULT_PORT)
    bootstrap.set_defaults(handler=run_bootstrap)

    recovery = subparsers.add_parser("recovery-code", help="issue one expiring one-use enrollment code")
    recovery.add_argument("--role", choices=("device", "admin"), default="device")
    recovery.add_argument("--ttl", type=int, default=900)
    recovery.set_defaults(handler=run_recovery_code)

    status = subparsers.add_parser("status", help="show non-secret Agent state")
    status.add_argument("--json", action="store_true")
    status.set_defaults(handler=run_status)

    serve = subparsers.add_parser("serve", help="serve the persistent Tailscale-only API")
    serve.add_argument("--port", type=int, default=DEFAULT_PORT)
    serve.add_argument("--ssh-port", type=int, default=22)
    serve.add_argument("--host-key", default="/etc/ssh/ssh_host_ed25519_key.pub")
    serve.add_argument("--relay-unit-dir", help="managed systemd user-unit directory")
    serve.add_argument("--socat-path", default="/usr/bin/socat")
    serve.add_argument("--systemctl-path", default="/usr/bin/systemctl")
    serve.add_argument("--dry-run", action="store_true", help="validate without opening a socket")
    serve.set_defaults(handler=run_serve)
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        return int(args.handler(args))
    except KeyboardInterrupt:
        return 130
    except (AgentError, OSError, subprocess.SubprocessError) as exc:
        print("ERROR: " + str(exc), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
