import base64
import fcntl
import importlib.util
import ipaddress
import json
import os
import sys
import tempfile
import threading
import unittest
import urllib.error
import urllib.request
from pathlib import Path
from unittest import mock


SCRIPT = Path(__file__).resolve().parents[2] / "scripts" / "jkbridge_agent.py"
SPEC = importlib.util.spec_from_file_location("jkbridge_agent", SCRIPT)
jkbridge_agent = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
sys.modules[SPEC.name] = jkbridge_agent
SPEC.loader.exec_module(jkbridge_agent)


def test_key(fill: int) -> str:
    algorithm = b"ssh-ed25519"
    blob = (
        len(algorithm).to_bytes(4, "big")
        + algorithm
        + (32).to_bytes(4, "big")
        + bytes([fill]) * 32
    )
    return "ssh-ed25519 " + base64.b64encode(blob).decode("ascii")


KEY_ONE = test_key(1)
KEY_TWO = test_key(2)


class AgentFixture:
    def __init__(self, root: Path):
        self.root = root
        self.home = root / "home"
        self.home.mkdir()
        self.authorized = self.home / ".ssh" / "authorized_keys"
        self.paths = jkbridge_agent.AgentPaths.from_values(
            root / "state",
            authorized_keys=self.authorized,
            home=self.home,
        )
        self.store = jkbridge_agent.AgentStore(self.paths)
        self.peer = jkbridge_agent.PeerIdentity(
            "100.64.0.20", "node-client-one", "alice@example.test"
        )
        self.other_peer = jkbridge_agent.PeerIdentity(
            "100.64.0.21", "node-client-two", "alice@example.test"
        )
        self.context = jkbridge_agent.ServerContext(
            identity=jkbridge_agent.ServerIdentity(
                "linux.example.ts.net",
                "example.ts.net",
                "100.64.0.10",
                "node-server",
            ),
            ssh_user="alice",
            ssh_port=22,
            host_public_key=KEY_ONE,
            display_name="linux-test",
        )
        self.app = jkbridge_agent.AgentApplication(self.store, self.context)

    def bootstrap(self, role="admin"):
        code, recovery = jkbridge_agent._make_recovery(role, 900)
        self.store.initialize(
            jkbridge_agent._initial_state(recovery),
            jkbridge_agent._initial_manifest(),
        )
        return code

    def add_recovery(self, role="device"):
        code, recovery = jkbridge_agent._make_recovery(role, 900)
        with self.store.locked():
            state, manifest = self.store.load_unlocked()
            state = json.loads(json.dumps(state))
            state["recoveryCodes"].append(recovery)
            self.store.transact_unlocked(state, manifest, restrict_first=False)
        return code

    def enroll(self, code, key=KEY_ONE, peer=None, label="Windows laptop", requested=None):
        return self.app.enroll(
            {
                "protocolVersion": 1,
                "clientPublicKey": key,
                "deviceLabel": label,
                "requestedServiceIds": [] if requested is None else requested,
            },
            {"authorization": "JKBridge-Enrollment " + code},
            peer or self.peer,
        )


class AgentAuthorizationTests(unittest.TestCase):
    def test_authorized_line_is_exact_loopback_only_and_has_no_shell(self):
        public_key, fingerprint = jkbridge_agent.normalize_ed25519_key(KEY_ONE)
        line = jkbridge_agent.build_agent_authorized_line(
            {
                "id": "a" * 32,
                "publicKey": public_key,
                "fingerprint": fingerprint,
                "permitPorts": [8081, 80, 80],
            }
        )

        self.assertIn('command="/usr/bin/false"', line)
        self.assertIn('permitopen="127.0.0.1:80"', line)
        self.assertIn('permitopen="127.0.0.1:8081"', line)
        self.assertNotIn("127.0.0.1:*", line)
        self.assertNotIn("0.0.0.0", line)
        self.assertEqual(("a" * 32, public_key, fingerprint), jkbridge_agent.parse_agent_authorized_line(line))

    def test_malformed_agent_marker_is_rejected(self):
        line = KEY_ONE + " JK Bridge agent " + "a" * 32 + " invalid"
        with self.assertRaises(jkbridge_agent.AgentError):
            jkbridge_agent.parse_agent_authorized_line(line)

    def test_exact_one_shot_line_can_be_strictly_adopted(self):
        public_key, fingerprint = jkbridge_agent.normalize_ed25519_key(KEY_ONE)
        one_shot = (
            jkbridge_agent.RESTRICTED_OPTIONS_PREFIX
            + ',permitopen="127.0.0.1:80",permitopen="127.0.0.1:8081" '
            + public_key
            + " JK Bridge managed "
            + fingerprint
        )
        self.assertEqual(
            (public_key, fingerprint), jkbridge_agent.parse_one_shot_authorized_line(one_shot)
        )
        with self.assertRaises(jkbridge_agent.AgentError):
            jkbridge_agent.parse_one_shot_authorized_line(
                public_key + " JK Bridge managed " + fingerprint
            )

    def test_service_validation_rejects_wildcard_and_invalid_ports(self):
        services = json.loads(json.dumps(jkbridge_agent.DEFAULT_SERVICES))
        services[0]["remotePort"] = 0
        with self.assertRaises(jkbridge_agent.AgentError):
            jkbridge_agent.validate_services(services)

    def test_service_validation_rejects_noncanonical_paths_before_catalog_commit(self):
        for field, unsafe in (
            ("healthPath", "/health check"),
            ("healthPath", "/health%ZZ"),
            ("healthPath", "/health#fragment"),
            ("openPath", "/login\\external"),
            ("openPath", "/login "),
        ):
            with self.subTest(field=field, unsafe=unsafe):
                services = json.loads(json.dumps(jkbridge_agent.DEFAULT_SERVICES))
                services[0][field] = unsafe
                with self.assertRaises(jkbridge_agent.AgentError):
                    jkbridge_agent.validate_services(services)

    def test_paths_preserve_symlinks_for_store_rejection(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            victim = root / "victim"
            victim.mkdir()
            link = root / "state-link"
            link.symlink_to(victim, target_is_directory=True)
            paths = jkbridge_agent.AgentPaths.from_values(
                link, authorized_keys=root / "authorized_keys", home=root
            )
            store = jkbridge_agent.AgentStore(paths)
            _code, recovery = jkbridge_agent._make_recovery("admin", 900)
            with self.assertRaises(jkbridge_agent.AgentError):
                store.initialize(
                    jkbridge_agent._initial_state(recovery), jkbridge_agent._initial_manifest()
                )
            self.assertEqual([], list(victim.iterdir()))

    def test_authorized_keys_symlink_target_is_never_replaced(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            victim = root / "victim"
            victim.write_text("never replace me\n", encoding="utf-8")
            authorized = root / "home" / ".ssh" / "authorized_keys"
            authorized.parent.mkdir(parents=True)
            authorized.symlink_to(victim)
            paths = jkbridge_agent.AgentPaths.from_values(
                root / "state", authorized_keys=authorized, home=root / "home"
            )
            store = jkbridge_agent.AgentStore(paths)
            _code, recovery = jkbridge_agent._make_recovery("admin", 900)
            store.initialize(
                jkbridge_agent._initial_state(recovery), jkbridge_agent._initial_manifest()
            )
            with store.locked():
                state, manifest = store.load_unlocked()
                with self.assertRaises(jkbridge_agent.AgentError):
                    store.transact_unlocked(state, manifest, restrict_first=False)
            self.assertEqual("never replace me\n", victim.read_text(encoding="utf-8"))

    def test_authorized_keys_hardlink_target_is_never_replaced(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            victim = root / "victim"
            victim.write_text("never replace me\n", encoding="utf-8")
            authorized = root / "home" / ".ssh" / "authorized_keys"
            authorized.parent.mkdir(parents=True)
            authorized.hardlink_to(victim)
            paths = jkbridge_agent.AgentPaths.from_values(
                root / "state", authorized_keys=authorized, home=root / "home"
            )
            store = jkbridge_agent.AgentStore(paths)
            _code, recovery = jkbridge_agent._make_recovery("admin", 900)
            store.initialize(
                jkbridge_agent._initial_state(recovery), jkbridge_agent._initial_manifest()
            )
            with store.locked():
                state, manifest = store.load_unlocked()
                with self.assertRaises(jkbridge_agent.AgentError):
                    store.transact_unlocked(state, manifest, restrict_first=False)
            self.assertEqual("never replace me\n", victim.read_text(encoding="utf-8"))
        services = json.loads(json.dumps(jkbridge_agent.DEFAULT_SERVICES))
        services[0]["openPath"] = "//external.example/"
        with self.assertRaises(jkbridge_agent.AgentError):
            jkbridge_agent.validate_services(services)
        services = json.loads(json.dumps(jkbridge_agent.DEFAULT_SERVICES))
        services[1]["suggestedLocalPort"] = services[0]["suggestedLocalPort"]
        with self.assertRaises(jkbridge_agent.AgentError):
            jkbridge_agent.validate_services(services)
        services = json.loads(json.dumps(jkbridge_agent.DEFAULT_SERVICES))
        services[0]["description"] = "x" * 161
        with self.assertRaises(jkbridge_agent.AgentError):
            jkbridge_agent.validate_services(services)


class LoopbackDiscoveryTests(unittest.TestCase):
    def test_listener_inventory_uses_only_existing_exact_ipv4_loopback_sockets(self):
        output = (
            'LISTEN 0 4096 127.0.0.1:8384 0.0.0.0:* users:(("syncthing",pid=4,fd=8))\n'
            'LISTEN 0 4096 0.0.0.0:9000 0.0.0.0:* users:(("public",pid=5,fd=8))\n'
            'LISTEN 0 4096 [::1]:8888 [::]:* users:(("jupyter",pid=6,fd=8))\n'
            'LISTEN 0 4096 127.0.0.1:5432 0.0.0.0:* users:(("postgres",pid=7,fd=8))\n'
        )
        completed = mock.Mock(returncode=0, stdout=output, stderr="")
        probe = mock.Mock(
            return_value={
                "port": 8384,
                "bindAddress": "127.0.0.1",
                "processName": "syncthing",
                "scheme": "http",
                "webDetected": True,
                "statusCode": 200,
                "title": "Syncthing",
                "healthPath": "/",
                "suggestedTemplateId": "syncthing",
                "tlsCertificateVerified": None,
            }
        )

        with mock.patch.object(jkbridge_agent.shutil, "which", return_value="/usr/bin/ss"), \
             mock.patch.object(jkbridge_agent.subprocess, "run", return_value=completed) as runner, \
             mock.patch.object(jkbridge_agent, "_probe_loopback_http", probe):
            candidates = jkbridge_agent.discover_loopback_services()

        self.assertEqual([8384], [item["port"] for item in candidates])
        runner.assert_called_once_with(
            ["/usr/bin/ss", "-H", "-ltnp"],
            check=False,
            stdout=jkbridge_agent.subprocess.PIPE,
            stderr=jkbridge_agent.subprocess.PIPE,
            text=True,
            timeout=4,
        )
        probe.assert_called_once_with("127.0.0.1", 8384, "syncthing")


class AgentApplicationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.fixture = AgentFixture(Path(self.temporary.name))

    def tearDown(self):
        self.temporary.cleanup()

    def test_bootstrap_keeps_only_recovery_verifier(self):
        code = self.fixture.bootstrap()
        state_text = self.fixture.paths.state_file.read_text(encoding="utf-8")

        self.assertNotIn(code, state_text)
        state, manifest = self.fixture.store.load()
        self.assertEqual("admin", state["recoveryCodes"][0]["role"])
        self.assertEqual(4, len(manifest["services"]))
        self.assertEqual(0o600, self.fixture.paths.state_file.stat().st_mode & 0o777)
        self.assertEqual(0o700, self.fixture.paths.state_dir.stat().st_mode & 0o777)

    def test_enroll_consumes_code_writes_exact_key_and_returns_identity(self):
        self.fixture.authorized.parent.mkdir(mode=0o700)
        self.fixture.authorized.write_text("# unrelated key remains\n", encoding="utf-8")
        code = self.fixture.bootstrap()

        response = self.fixture.enroll(code)

        self.assertEqual("node-server", response["tailscaleNodeId"])
        self.assertEqual([80, 8081, 8765, 8793], response["permitPorts"])
        self.assertIn("deviceCredential", response)
        text = self.fixture.authorized.read_text(encoding="utf-8")
        self.assertIn("# unrelated key remains", text)
        self.assertEqual(4, text.count('permitopen="127.0.0.1:'))
        self.assertNotIn("127.0.0.1:*", text)
        state, _manifest = self.fixture.store.load()
        self.assertEqual([], state["recoveryCodes"])
        self.assertNotIn(response["deviceCredential"], self.fixture.paths.state_file.read_text())
        with self.assertRaises(jkbridge_agent.AgentError) as retry:
            self.fixture.enroll(code, key=KEY_TWO)
        self.assertEqual(401, retry.exception.status)

    def test_enroll_migrates_same_key_from_exact_one_shot_entry(self):
        public_key, fingerprint = jkbridge_agent.normalize_ed25519_key(KEY_ONE)
        self.fixture.authorized.parent.mkdir(mode=0o700)
        self.fixture.authorized.write_text(
            jkbridge_agent.RESTRICTED_OPTIONS_PREFIX
            + ',permitopen="127.0.0.1:80" '
            + public_key
            + " JK Bridge managed "
            + fingerprint
            + "\n# unrelated remains\n",
            encoding="utf-8",
        )

        receipt = self.fixture.enroll(self.fixture.bootstrap())

        authorized = self.fixture.authorized.read_text(encoding="utf-8")
        self.assertIn("JK Bridge agent " + receipt["deviceId"], authorized)
        self.assertNotIn("JK Bridge managed " + fingerprint, authorized)
        self.assertEqual(1, authorized.count(public_key))
        self.assertIn("# unrelated remains", authorized)

    def test_device_credential_is_bound_to_stable_tailscale_peer(self):
        receipt = self.fixture.enroll(self.fixture.bootstrap())
        headers = {"authorization": "Bearer " + receipt["deviceCredential"]}

        catalog = self.fixture.app.services_get(headers, self.fixture.peer)
        self.assertEqual(1, catalog["servicesRevision"])
        with self.assertRaises(jkbridge_agent.AgentError) as mismatch:
            self.fixture.app.services_get(headers, self.fixture.other_peer)
        self.assertEqual("peer_mismatch", mismatch.exception.code)

    def test_authenticated_device_can_read_its_exact_grant_and_revision(self):
        receipt = self.fixture.enroll(self.fixture.bootstrap())
        headers = {"authorization": "Bearer " + receipt["deviceCredential"]}

        response = self.fixture.app.me_get(headers, self.fixture.peer)

        self.assertEqual(
            {"protocolVersion", "servicesRevision", "device"}, set(response)
        )
        self.assertEqual(receipt["deviceId"], response["device"]["deviceId"])
        self.assertEqual(receipt["permitPorts"], response["device"]["permitPorts"])
        self.assertEqual(1, response["servicesRevision"])

    def test_admin_discovery_is_read_only_excludes_catalog_ports_and_never_auto_adds(self):
        receipt = self.fixture.enroll(self.fixture.bootstrap())
        headers = {"authorization": "Bearer " + receipt["deviceCredential"]}
        self.fixture.app.discovery_provider = lambda: [
            {
                "port": 80,
                "bindAddress": "127.0.0.1",
                "processName": "existing",
                "scheme": "http",
                "webDetected": True,
                "statusCode": 200,
                "title": "Existing",
                "suggestedTemplateId": "generic-http",
            },
            {
                "port": 8384,
                "bindAddress": "127.0.0.1",
                "processName": "syncthing",
                "scheme": "http",
                "webDetected": True,
                "statusCode": 200,
                "title": "Syncthing",
                "suggestedTemplateId": "syncthing",
            },
        ]
        before_state, before_manifest = self.fixture.store.load()

        response = self.fixture.app.discovery_get(headers, self.fixture.peer)

        self.assertEqual("current-127.0.0.1-listeners", response["scope"])
        self.assertEqual([8384], [item["port"] for item in response["candidates"]])
        self.assertEqual(1, response["excludedKnownServiceCount"])
        after_state, after_manifest = self.fixture.store.load()
        self.assertEqual(before_state["devices"][0]["serviceIds"], after_state["devices"][0]["serviceIds"])
        self.assertEqual(before_state["devices"][0]["permitPorts"], after_state["devices"][0]["permitPorts"])
        self.assertEqual(before_manifest, after_manifest)

    def test_non_admin_discovery_is_rejected_before_listener_inventory_runs(self):
        receipt = self.fixture.enroll(self.fixture.bootstrap(role="device"))
        provider = mock.Mock(return_value=[])
        self.fixture.app.discovery_provider = provider

        with self.assertRaises(jkbridge_agent.AgentError) as denied:
            self.fixture.app.discovery_get(
                {"authorization": "Bearer " + receipt["deviceCredential"]},
                self.fixture.peer,
            )

        self.assertEqual("admin_required", denied.exception.code)
        provider.assert_not_called()

    def test_non_admin_cannot_list_devices_or_update_catalog(self):
        code = self.fixture.bootstrap(role="device")
        receipt = self.fixture.enroll(code)
        headers = {"authorization": "Bearer " + receipt["deviceCredential"]}

        with self.assertRaises(jkbridge_agent.AgentError) as denied:
            self.fixture.app.devices_get(headers, self.fixture.peer)
        self.assertEqual("admin_required", denied.exception.code)
        with self.assertRaises(jkbridge_agent.AgentError) as denied_update:
            self.fixture.app.services_put(
                {
                    "protocolVersion": 1,
                    "expectedRevision": 1,
                    "services": jkbridge_agent.DEFAULT_SERVICES,
                },
                headers,
                self.fixture.peer,
            )
        self.assertEqual("admin_required", denied_update.exception.code)

    def test_admin_can_issue_one_use_device_only_enrollment_code(self):
        admin = self.fixture.enroll(self.fixture.bootstrap())
        headers = {"authorization": "Bearer " + admin["deviceCredential"]}

        response = self.fixture.app.enrollment_code_create(
            {"protocolVersion": 1, "ttlSeconds": 600}, headers, self.fixture.peer
        )

        self.assertEqual(
            {"protocolVersion", "enrollmentCode", "expiresAt"}, set(response)
        )
        self.assertNotIn(response["enrollmentCode"], self.fixture.paths.state_file.read_text())
        enrolled = self.fixture.enroll(
            response["enrollmentCode"], key=KEY_TWO, peer=self.fixture.other_peer, label="new device"
        )
        self.assertEqual("device", enrolled["role"])
        with self.assertRaises(jkbridge_agent.AgentError):
            self.fixture.enroll(
                response["enrollmentCode"], key=test_key(3), peer=self.fixture.other_peer
            )

    def test_ordinary_device_cannot_issue_enrollment_code(self):
        ordinary = self.fixture.enroll(self.fixture.bootstrap(role="device"))
        with self.assertRaises(jkbridge_agent.AgentError) as denied:
            self.fixture.app.enrollment_code_create(
                {"protocolVersion": 1, "ttlSeconds": 600},
                {"authorization": "Bearer " + ordinary["deviceCredential"]},
                self.fixture.peer,
            )
        self.assertEqual("admin_required", denied.exception.code)

    def test_admin_updates_catalog_and_authorized_ports_transactionally(self):
        receipt = self.fixture.enroll(self.fixture.bootstrap())
        headers = {"authorization": "Bearer " + receipt["deviceCredential"]}
        services = json.loads(json.dumps(jkbridge_agent.DEFAULT_SERVICES))
        services[0]["remotePort"] = 81

        response = self.fixture.app.services_put(
            {
                "protocolVersion": 1,
                "expectedRevision": 1,
                "services": services,
            },
            headers,
            self.fixture.peer,
        )

        self.assertEqual(2, response["servicesRevision"])
        authorized = self.fixture.authorized.read_text(encoding="utf-8")
        self.assertIn('permitopen="127.0.0.1:81"', authorized)
        self.assertNotIn('permitopen="127.0.0.1:80"', authorized)
        state, manifest = self.fixture.store.load()
        self.assertEqual(2, manifest["revision"])
        self.assertIn(81, state["devices"][0]["permitPorts"])
        self.assertFalse(self.fixture.paths.journal_file.exists())
        self.assertTrue(any((self.fixture.paths.state_dir / "backups").iterdir()))

    def test_agent_reconciles_exact_one_shot_fallback_entry_for_same_device_key(self):
        receipt = self.fixture.enroll(self.fixture.bootstrap())
        public_key, fingerprint = jkbridge_agent.normalize_ed25519_key(KEY_ONE)
        one_shot = (
            jkbridge_agent.RESTRICTED_OPTIONS_PREFIX
            + ''.join(
                ',permitopen="127.0.0.1:{}"'.format(port)
                for port in receipt["permitPorts"]
            )
            + " "
            + public_key
            + " JK Bridge managed "
            + fingerprint
            + "\n"
        )
        self.fixture.authorized.write_text(one_shot, encoding="utf-8")

        self.fixture.app.services_put(
            {
                "protocolVersion": 1,
                "expectedRevision": 1,
                "services": jkbridge_agent.DEFAULT_SERVICES,
            },
            {"authorization": "Bearer " + receipt["deviceCredential"]},
            self.fixture.peer,
        )

        reconciled = self.fixture.authorized.read_text(encoding="utf-8")
        self.assertIn(" JK Bridge agent " + receipt["deviceId"] + " " + fingerprint, reconciled)
        self.assertNotIn(" JK Bridge managed ", reconciled)
        self.assertEqual(1, reconciled.count(public_key))

    def test_agent_transactions_wait_for_the_shared_one_shot_ssh_writer_lock(self):
        receipt = self.fixture.enroll(self.fixture.bootstrap())
        headers = {"authorization": "Bearer " + receipt["deviceCredential"]}
        services = json.loads(json.dumps(jkbridge_agent.DEFAULT_SERVICES))
        services[0]["remotePort"] = 81
        lock_path = self.fixture.authorized.parent / ".jkbridge.lock"
        descriptor = os.open(str(lock_path), os.O_RDWR)
        started = threading.Event()
        finished = threading.Event()
        failures = []

        def update_catalog():
            started.set()
            try:
                self.fixture.app.services_put(
                    {
                        "protocolVersion": 1,
                        "expectedRevision": 1,
                        "services": services,
                    },
                    headers,
                    self.fixture.peer,
                )
            except Exception as exc:  # pragma: no cover - asserted below
                failures.append(exc)
            finally:
                finished.set()

        try:
            fcntl.flock(descriptor, fcntl.LOCK_EX)
            worker = threading.Thread(target=update_catalog)
            worker.start()
            self.assertTrue(started.wait(timeout=1))
            self.assertFalse(finished.wait(timeout=0.2))
        finally:
            fcntl.flock(descriptor, fcntl.LOCK_UN)
            os.close(descriptor)
        worker.join(timeout=2)

        self.assertFalse(worker.is_alive())
        self.assertEqual([], failures)
        self.assertIn(
            'permitopen="127.0.0.1:81"',
            self.fixture.authorized.read_text(encoding="utf-8"),
        )

    def test_last_seen_refresh_is_throttled_atomic_and_best_effort(self):
        receipt = self.fixture.enroll(self.fixture.bootstrap())
        headers = {"authorization": "Bearer " + receipt["deviceCredential"]}
        initial_text = self.fixture.paths.state_file.read_text(encoding="utf-8")

        self.fixture.app.me_get(headers, self.fixture.peer)
        self.assertEqual(initial_text, self.fixture.paths.state_file.read_text(encoding="utf-8"))

        with self.fixture.store.locked():
            state, manifest = self.fixture.store.load_unlocked()
            state["devices"][0]["lastSeenAt"] = "2000-01-01T00:00:00Z"
            self.fixture.store.transact_unlocked(state, manifest, restrict_first=False)
        refreshed = self.fixture.app.me_get(headers, self.fixture.peer)
        self.assertNotEqual("2000-01-01T00:00:00Z", refreshed["device"]["lastSeenAt"])

        with self.fixture.store.locked():
            state, manifest = self.fixture.store.load_unlocked()
            state["devices"][0]["lastSeenAt"] = "2000-01-01T00:00:00Z"
            self.fixture.store.transact_unlocked(state, manifest, restrict_first=False)
        with mock.patch.object(
            self.fixture.store,
            "_atomic_write_bytes",
            side_effect=OSError("simulated metadata write failure"),
        ):
            still_available = self.fixture.app.me_get(headers, self.fixture.peer)

        self.assertEqual("2000-01-01T00:00:00Z", still_available["device"]["lastSeenAt"])

    def test_device_list_uses_the_strict_windows_wire_shape(self):
        receipt = self.fixture.enroll(self.fixture.bootstrap())
        response = self.fixture.app.devices_get(
            {"authorization": "Bearer " + receipt["deviceCredential"]}, self.fixture.peer
        )

        self.assertEqual(
            {
                "deviceId",
                "deviceLabel",
                "clientKeyFingerprint",
                "role",
                "tailscaleNodeId",
                "tailscaleLogin",
                "serviceIds",
                "createdAt",
                "lastSeenAt",
                "revokedAt",
                "permitPorts",
            },
            set(response["devices"][0]),
        )
        self.assertEqual("node-client-one", response["devices"][0]["tailscaleNodeId"])
        self.assertIsNone(response["devices"][0]["revokedAt"])

    def test_admin_rename_is_atomic_audited_and_does_not_change_authority(self):
        admin = self.fixture.enroll(
            self.fixture.bootstrap(), key=KEY_ONE, peer=self.fixture.peer, label="Admin"
        )
        ordinary = self.fixture.enroll(
            self.fixture.add_recovery("device"),
            key=KEY_TWO,
            peer=self.fixture.other_peer,
            label="Old laptop",
        )
        before_state, before_manifest = self.fixture.store.load()
        before_device = next(
            item for item in before_state["devices"] if item["id"] == ordinary["deviceId"]
        )

        response = self.fixture.app.device_rename(
            ordinary["deviceId"],
            {"protocolVersion": 1, "deviceLabel": "Office desktop"},
            {"authorization": "Bearer " + admin["deviceCredential"]},
            self.fixture.peer,
        )

        self.assertEqual(
            {"protocolVersion", "renamed", "previousDeviceLabel", "device"}, set(response)
        )
        self.assertTrue(response["renamed"])
        self.assertEqual("Old laptop", response["previousDeviceLabel"])
        self.assertEqual("Office desktop", response["device"]["deviceLabel"])
        after_state, after_manifest = self.fixture.store.load()
        after_device = next(
            item for item in after_state["devices"] if item["id"] == ordinary["deviceId"]
        )
        for field in (
            "publicKey",
            "fingerprint",
            "credentialHash",
            "role",
            "tailscaleNodeId",
            "tailscaleLogin",
            "serviceIds",
            "permitPorts",
            "createdAt",
            "lastSeenAt",
            "revokedAt",
        ):
            self.assertEqual(before_device[field], after_device[field], field)
        self.assertEqual(before_manifest, after_manifest)
        self.assertEqual(1, len(after_state["auditEvents"]))
        audit = after_state["auditEvents"][0]
        self.assertEqual("device_renamed", audit["action"])
        self.assertEqual(admin["deviceId"], audit["actorDeviceId"])
        self.assertEqual(ordinary["deviceId"], audit["targetDeviceId"])

    def test_device_rename_requires_admin_and_canonical_new_label(self):
        ordinary = self.fixture.enroll(self.fixture.bootstrap(role="device"), label="Laptop")
        headers = {"authorization": "Bearer " + ordinary["deviceCredential"]}

        with self.assertRaises(jkbridge_agent.AgentError) as denied:
            self.fixture.app.device_rename(
                ordinary["deviceId"],
                {"protocolVersion": 1, "deviceLabel": "Renamed"},
                headers,
                self.fixture.peer,
            )
        self.assertEqual("admin_required", denied.exception.code)

        admin_code = self.fixture.add_recovery("admin")
        admin = self.fixture.enroll(
            admin_code, key=KEY_TWO, peer=self.fixture.other_peer, label="Admin"
        )
        with self.assertRaises(jkbridge_agent.AgentError):
            self.fixture.app.device_rename(
                ordinary["deviceId"],
                {"protocolVersion": 1, "deviceLabel": " padded "},
                {"authorization": "Bearer " + admin["deviceCredential"]},
                self.fixture.other_peer,
            )

    def test_revoke_removes_only_target_key_and_invalidates_credential(self):
        admin = self.fixture.enroll(self.fixture.bootstrap(), key=KEY_ONE, peer=self.fixture.peer)
        second = self.fixture.enroll(
            self.fixture.add_recovery("device"), key=KEY_TWO, peer=self.fixture.other_peer, label="Desktop"
        )
        headers = {"authorization": "Bearer " + admin["deviceCredential"]}

        response = self.fixture.app.revoke(second["deviceId"], headers, self.fixture.peer)

        self.assertTrue(response["revoked"])
        self.assertEqual(
            {"protocolVersion", "deviceId", "revoked", "revokedAt", "note"}, set(response)
        )
        text = self.fixture.authorized.read_text(encoding="utf-8")
        self.assertIn(KEY_ONE, text)
        self.assertNotIn(KEY_TWO, text)
        with self.assertRaises(jkbridge_agent.AgentError):
            self.fixture.app.services_get(
                {"authorization": "Bearer " + second["deviceCredential"]}, self.fixture.other_peer
            )

    def test_final_admin_cannot_revoke_itself(self):
        admin = self.fixture.enroll(self.fixture.bootstrap())
        with self.assertRaises(jkbridge_agent.AgentError) as blocked:
            self.fixture.app.revoke(
                admin["deviceId"],
                {"authorization": "Bearer " + admin["deviceCredential"]},
                self.fixture.peer,
            )
        self.assertEqual("last_admin", blocked.exception.code)

    def test_journal_recovery_rolls_forward_all_files(self):
        self.fixture.bootstrap()
        with self.fixture.store.locked():
            state, manifest = self.fixture.store.load_unlocked()
            manifest = json.loads(json.dumps(manifest))
            manifest["revision"] = 2
            manifest["updatedAt"] = jkbridge_agent.utc_now()
            journal = {
                "schemaVersion": 1,
                "createdAt": jkbridge_agent.utc_now(),
                "restrictFirst": False,
                "state": state,
                "manifest": manifest,
                "authorizedKeys": "# recovered\n",
            }
            self.fixture.store._write_json(self.fixture.paths.journal_file, journal, backup=False)

        state, recovered_manifest = self.fixture.store.load()
        self.assertEqual(1, state["schemaVersion"])
        self.assertEqual(2, recovered_manifest["revision"])
        self.assertEqual("# recovered\n", self.fixture.authorized.read_text(encoding="utf-8"))
        self.assertFalse(self.fixture.paths.journal_file.exists())


class AgentHttpTests(unittest.TestCase):
    def test_http_api_uses_headers_not_query_and_hides_secrets(self):
        with tempfile.TemporaryDirectory() as temporary:
            fixture = AgentFixture(Path(temporary))
            code = fixture.bootstrap()
            peer = jkbridge_agent.PeerIdentity("127.0.0.1", "node-client-one", "alice@example.test")
            allowed = (ipaddress.ip_network("127.0.0.0/8"),)
            with jkbridge_agent.AgentHttpServer(
                ("127.0.0.1", 0),
                fixture.app,
                jkbridge_agent.StaticPeerVerifier(peer),
                allowed_networks=allowed,
            ) as server:
                thread = threading.Thread(target=server.serve_forever, daemon=True)
                thread.start()
                base = "http://127.0.0.1:{}".format(server.server_address[1])
                health_request = urllib.request.Request(
                    base + "/v1/health",
                    headers={"X-JKBridge-Protocol-Version": "1"},
                )
                with urllib.request.urlopen(health_request, timeout=2) as response:
                    health = json.loads(response.read().decode("utf-8"))
                self.assertEqual("ok", health["status"])

                with self.assertRaises(urllib.error.HTTPError) as version_error:
                    urllib.request.urlopen(base + "/v1/version", timeout=2)
                self.assertEqual(400, version_error.exception.code)

                duplicate_body = (
                    '{"protocolVersion":1,"clientPublicKey":'
                    + json.dumps(KEY_ONE)
                    + ',"deviceLabel":"first","deviceLabel":"second","requestedServiceIds":[]}'
                ).encode("utf-8")
                duplicate_request = urllib.request.Request(
                    base + "/v1/enroll",
                    data=duplicate_body,
                    headers={
                        "Authorization": "JKBridge-Enrollment " + code,
                        "Content-Type": "application/json",
                        "X-JKBridge-Protocol-Version": "1",
                    },
                    method="POST",
                )
                with self.assertRaises(urllib.error.HTTPError) as duplicate_error:
                    urllib.request.urlopen(duplicate_request, timeout=2)
                self.assertEqual(400, duplicate_error.exception.code)

                request = urllib.request.Request(
                    base + "/v1/enroll",
                    data=json.dumps(
                        {
                            "protocolVersion": 1,
                            "clientPublicKey": KEY_ONE,
                            "deviceLabel": "HTTP client",
                            "requestedServiceIds": [],
                        }
                    ).encode("utf-8"),
                    headers={
                        "Authorization": "JKBridge-Enrollment " + code,
                        "Content-Type": "application/json",
                        "X-JKBridge-Protocol-Version": "1",
                    },
                    method="POST",
                )
                with urllib.request.urlopen(request, timeout=2) as response:
                    receipt = json.loads(response.read().decode("utf-8"))
                self.assertIn("deviceCredential", receipt)

                with self.assertRaises(urllib.error.HTTPError) as query_error:
                    urllib.request.urlopen(
                        urllib.request.Request(
                            base + "/v1/health?token=" + code,
                            headers={"X-JKBridge-Protocol-Version": "1"},
                        ),
                        timeout=2,
                    )
                self.assertEqual(400, query_error.exception.code)
                error_body = query_error.exception.read().decode("utf-8")
                self.assertNotIn(code, error_body)
                server.shutdown()
                thread.join(timeout=2)
                self.assertFalse(thread.is_alive())

    def test_server_refuses_wildcard_or_non_allowed_bind(self):
        with tempfile.TemporaryDirectory() as temporary:
            fixture = AgentFixture(Path(temporary))
            fixture.bootstrap()
            verifier = jkbridge_agent.StaticPeerVerifier(fixture.peer)
            with self.assertRaises(jkbridge_agent.AgentError):
                jkbridge_agent.AgentHttpServer(
                    ("0.0.0.0", 0), fixture.app, verifier, allowed_networks=(ipaddress.ip_network("0.0.0.0/0"),)
                )
            with self.assertRaises(jkbridge_agent.AgentError):
                jkbridge_agent.AgentHttpServer(("127.0.0.1", 0), fixture.app, verifier)


class RelayRunner:
    def __init__(self):
        self.calls = []

    def __call__(self, arguments):
        self.calls.append(tuple(arguments))
        module = jkbridge_agent.relay_module()
        return module.CommandResult(0, "", "")


class AgentRelayIntegrationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.fixture = AgentFixture(Path(self.temporary.name))
        self.admin = self.fixture.enroll(self.fixture.bootstrap())
        self.headers = {"authorization": "Bearer " + self.admin["deviceCredential"]}
        self.runner = RelayRunner()
        module = jkbridge_agent.relay_module()
        self.manager = module.RelayManager(
            self.fixture.root / "units",
            self.fixture.root / "relay-backups",
            socat_path=Path("/test/bin/socat"),
            systemctl_path="/test/bin/systemctl",
            runner=self.runner,
            dependency_checker=lambda _path: True,
            clock_ns=lambda: 42,
        )
        self.fixture.app.relay_manager = self.manager

    def tearDown(self):
        self.temporary.cleanup()

    @staticmethod
    def request():
        return {
            "protocolVersion": 1,
            "spec": {
                "slug": "campus-auth",
                "relayPort": 19000,
                "targetHost": "auth.example.test",
                "targetPort": 443,
                "scheme": "https",
                "openPath": "/#/login",
            },
            "service": {
                "name": "Campus Auth",
                "description": "Explicit Linux-only campus target",
                "icon": "Shield",
                "suggestedLocalPort": 19000,
                "healthPath": "/",
                "healthKind": "tcpConnect",
                "enabledByDefault": True,
            },
            "grantToAllDevices": True,
        }

    def test_plan_and_probe_are_read_only_then_apply_commits_relay_catalog_and_grant(self):
        request = self.request()

        status, plan = self.fixture.app.dispatch(
            "POST", "/v1/relays/plan", self.headers, request, self.fixture.peer
        )
        probe = self.fixture.app.relay_probe(
            {"protocolVersion": 1, "spec": request["spec"]}, self.headers, self.fixture.peer
        )

        self.assertEqual(200, status)
        self.assertEqual("create", plan["relayPlan"]["action"])
        self.assertFalse((self.fixture.root / "units").exists())
        self.assertTrue(probe["readyToInstall"])
        confirmed = dict(request, planToken=plan["planToken"], confirmed=True)
        result = self.fixture.app.relay_apply(confirmed, self.headers, self.fixture.peer)

        self.assertTrue(result["applied"])
        unit = self.fixture.root / "units" / "jkbridge-relay-campus-auth.service"
        self.assertTrue(unit.is_file())
        self.assertIn("bind=127.0.0.1", unit.read_text(encoding="utf-8"))
        state, manifest = self.fixture.store.load()
        self.assertIn("campus-auth", state["devices"][0]["serviceIds"])
        self.assertIn(19000, state["devices"][0]["permitPorts"])
        self.assertEqual(["campus-auth"], [relay["slug"] for relay in state["relays"]])
        self.assertIn("campus-auth", [service["id"] for service in manifest["services"]])
        authorized = self.fixture.authorized.read_text(encoding="utf-8")
        self.assertIn('permitopen="127.0.0.1:19000"', authorized)

    def test_generic_catalog_update_cannot_bypass_managed_relay_plan(self):
        request = self.request()
        plan = self.fixture.app.relay_plan(request, self.headers, self.fixture.peer)
        self.fixture.app.relay_apply(
            dict(request, planToken=plan["planToken"], confirmed=True),
            self.headers,
            self.fixture.peer,
        )
        _state, manifest = self.fixture.store.load()
        services = json.loads(json.dumps(manifest["services"]))
        next(service for service in services if service["id"] == "campus-auth")["remotePort"] = 19001

        with self.assertRaises(jkbridge_agent.AgentError) as blocked:
            self.fixture.app.services_put(
                {
                    "protocolVersion": 1,
                    "expectedRevision": manifest["revision"],
                    "services": services,
                },
                self.headers,
                self.fixture.peer,
            )

        self.assertEqual("managed_relay_requires_plan", blocked.exception.code)

    def test_relay_target_update_marks_every_granted_active_device_as_affected(self):
        request = self.request()
        created = self.fixture.app.relay_plan(request, self.headers, self.fixture.peer)
        self.fixture.app.relay_apply(
            dict(request, planToken=created["planToken"], confirmed=True),
            self.headers,
            self.fixture.peer,
        )
        changed = json.loads(json.dumps(request))
        changed["spec"]["targetPort"] = 8443

        plan = self.fixture.app.relay_plan(changed, self.headers, self.fixture.peer)

        self.assertEqual("update", plan["relayPlan"]["action"])
        self.assertEqual([], plan["serviceChange"]["addedRemotePorts"])
        self.assertEqual([], plan["serviceChange"]["removedRemotePorts"])
        self.assertEqual(
            [self.admin["deviceId"]], plan["serviceChange"]["affectedDeviceIds"]
        )

    def test_relay_plan_requires_admin_and_stale_token_cannot_apply(self):
        ordinary_code = self.fixture.add_recovery("device")
        ordinary = self.fixture.enroll(
            ordinary_code, key=KEY_TWO, peer=self.fixture.other_peer, label="ordinary"
        )
        with self.assertRaises(jkbridge_agent.AgentError) as denied:
            self.fixture.app.relay_plan(
                self.request(),
                {"authorization": "Bearer " + ordinary["deviceCredential"]},
                self.fixture.other_peer,
            )
        self.assertEqual("admin_required", denied.exception.code)

        plan = self.fixture.app.relay_plan(self.request(), self.headers, self.fixture.peer)
        stale = dict(self.request(), planToken="0" * 64, confirmed=True)
        with self.assertRaises(jkbridge_agent.AgentError) as stale_error:
            self.fixture.app.relay_apply(stale, self.headers, self.fixture.peer)
        self.assertEqual("stale_plan", stale_error.exception.code)
        self.assertFalse((self.fixture.root / "units").exists())
        self.assertRegex(plan["planToken"], r"^[0-9a-f]{64}$")

    def test_relay_delete_endpoint_does_not_remove_an_ordinary_catalog_service(self):
        before_state, before_manifest = self.fixture.store.load()
        plan = self.fixture.app.relay_delete_plan(
            {"protocolVersion": 1, "slug": "overleaf"}, self.headers, self.fixture.peer
        )
        self.assertEqual("absent", plan["relayPlan"]["action"])
        self.assertFalse(plan["relayPlan"]["canApply"])

        result = self.fixture.app.relay_delete(
            {
                "protocolVersion": 1,
                "slug": "overleaf",
                "planToken": plan["planToken"],
                "confirmed": True,
            },
            self.headers,
            self.fixture.peer,
        )

        self.assertFalse(result["applied"])
        after_state, after_manifest = self.fixture.store.load()
        self.assertEqual(before_state["devices"], after_state["devices"])
        self.assertEqual(before_manifest["services"], after_manifest["services"])

    def test_confirmed_delete_contracts_grant_before_removing_managed_unit(self):
        request = self.request()
        plan = self.fixture.app.relay_plan(request, self.headers, self.fixture.peer)
        self.fixture.app.relay_apply(
            dict(request, planToken=plan["planToken"], confirmed=True),
            self.headers,
            self.fixture.peer,
        )

        delete_plan = self.fixture.app.relay_delete_plan(
            {"protocolVersion": 1, "slug": "campus-auth"}, self.headers, self.fixture.peer
        )
        result = self.fixture.app.relay_delete(
            {
                "protocolVersion": 1,
                "slug": "campus-auth",
                "planToken": delete_plan["planToken"],
                "confirmed": True,
            },
            self.headers,
            self.fixture.peer,
        )

        self.assertTrue(result["applied"])
        self.assertFalse((self.fixture.root / "units" / "jkbridge-relay-campus-auth.service").exists())
        state, manifest = self.fixture.store.load()
        self.assertNotIn("campus-auth", state["devices"][0]["serviceIds"])
        self.assertNotIn(19000, state["devices"][0]["permitPorts"])
        self.assertNotIn("campus-auth", [service["id"] for service in manifest["services"]])
        self.assertEqual([], state["relays"])
        self.assertNotIn(
            'permitopen="127.0.0.1:19000"', self.fixture.authorized.read_text(encoding="utf-8")
        )

    def test_delete_cleans_managed_state_when_unit_was_removed_outside_agent(self):
        request = self.request()
        plan = self.fixture.app.relay_plan(request, self.headers, self.fixture.peer)
        self.fixture.app.relay_apply(
            dict(request, planToken=plan["planToken"], confirmed=True),
            self.headers,
            self.fixture.peer,
        )
        unit = self.fixture.root / "units" / "jkbridge-relay-campus-auth.service"
        unit.unlink()

        delete_plan = self.fixture.app.relay_delete_plan(
            {"protocolVersion": 1, "slug": "campus-auth"}, self.headers, self.fixture.peer
        )

        self.assertEqual("delete", delete_plan["relayPlan"]["action"])
        self.assertTrue(delete_plan["relayPlan"]["canApply"])
        result = self.fixture.app.relay_delete(
            {
                "protocolVersion": 1,
                "slug": "campus-auth",
                "planToken": delete_plan["planToken"],
                "confirmed": True,
            },
            self.headers,
            self.fixture.peer,
        )

        self.assertFalse(result["applied"])
        state, manifest = self.fixture.store.load()
        self.assertEqual([], state["relays"])
        self.assertNotIn("campus-auth", state["devices"][0]["serviceIds"])
        self.assertNotIn("campus-auth", [service["id"] for service in manifest["services"]])

    def test_catalog_failure_rolls_back_new_relay(self):
        request = self.request()
        plan = self.fixture.app.relay_plan(request, self.headers, self.fixture.peer)
        confirmed = dict(request, planToken=plan["planToken"], confirmed=True)

        with mock.patch.object(
            self.fixture.store,
            "transact_unlocked",
            side_effect=jkbridge_agent.AgentError("simulated commit failure"),
        ):
            with self.assertRaises(jkbridge_agent.AgentError):
                self.fixture.app.relay_apply(confirmed, self.headers, self.fixture.peer)

        self.assertFalse((self.fixture.root / "units" / "jkbridge-relay-campus-auth.service").exists())
        _state, manifest = self.fixture.store.load()
        self.assertNotIn("campus-auth", [service["id"] for service in manifest["services"]])

    def test_delete_failure_restores_catalog_and_exact_grant(self):
        request = self.request()
        plan = self.fixture.app.relay_plan(request, self.headers, self.fixture.peer)
        self.fixture.app.relay_apply(
            dict(request, planToken=plan["planToken"], confirmed=True), self.headers, self.fixture.peer
        )
        delete_plan = self.fixture.app.relay_delete_plan(
            {"protocolVersion": 1, "slug": "campus-auth"}, self.headers, self.fixture.peer
        )
        module = jkbridge_agent.relay_module()
        with mock.patch.object(
            self.manager, "delete", side_effect=module.RelayCommandError("simulated delete failure")
        ):
            with self.assertRaises(jkbridge_agent.AgentError):
                self.fixture.app.relay_delete(
                    {
                        "protocolVersion": 1,
                        "slug": "campus-auth",
                        "planToken": delete_plan["planToken"],
                        "confirmed": True,
                    },
                    self.headers,
                    self.fixture.peer,
                )

        state, manifest = self.fixture.store.load()
        self.assertIn("campus-auth", state["devices"][0]["serviceIds"])
        self.assertIn(19000, state["devices"][0]["permitPorts"])
        self.assertIn("campus-auth", [service["id"] for service in manifest["services"]])


if __name__ == "__main__":
    unittest.main()
