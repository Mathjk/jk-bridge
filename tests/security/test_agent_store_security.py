"""Filesystem-boundary tests for the persistent Agent store.

These cases intentionally present symlinks but keep both the links and their
targets below one TemporaryDirectory. They never reference the live home.
"""

from __future__ import annotations

import base64
import importlib.util
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
AGENT_PATH = ROOT / "scripts" / "jkbridge_agent.py"


def load_agent():
    specification = importlib.util.spec_from_file_location(
        "jkbridge_agent_security_store", AGENT_PATH
    )
    assert specification and specification.loader
    module = importlib.util.module_from_spec(specification)
    # Python 3.13 dataclasses consult sys.modules while executing decorators.
    sys.modules[specification.name] = module
    specification.loader.exec_module(module)
    return module


def fixture_key(fill: int) -> str:
    algorithm = b"ssh-ed25519"
    material = bytes([fill]) * 32
    blob = (
        len(algorithm).to_bytes(4, "big")
        + algorithm
        + len(material).to_bytes(4, "big")
        + material
    )
    return "ssh-ed25519 " + base64.b64encode(blob).decode("ascii")


@unittest.skipUnless(AGENT_PATH.is_file(), "jkbridge Agent has not been implemented yet")
class AgentStoreSecurityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.agent = load_agent()

    def initial_documents(self):
        _secret, recovery = self.agent._make_recovery("admin", 900)
        return self.agent._initial_state(recovery), self.agent._initial_manifest()

    def application(self, store):
        return self.agent.AgentApplication(
            store,
            self.agent.ServerContext(
                identity=self.agent.ServerIdentity(
                    "linux.example.ts.net",
                    "example.ts.net",
                    "100.64.0.10",
                    "node-server",
                ),
                ssh_user="alice",
                ssh_port=22,
                host_public_key=fixture_key(9),
                display_name="linux-test",
            ),
        )

    def test_rate_limiter_isolated_by_source_and_operation_bucket(self):
        limiter = self.agent.RateLimiter()

        self.assertTrue(limiter.allow("100.64.0.20", "enroll", 2, 60.0))
        self.assertTrue(limiter.allow("100.64.0.20", "enroll", 2, 60.0))
        self.assertFalse(limiter.allow("100.64.0.20", "enroll", 2, 60.0))
        self.assertTrue(limiter.allow("100.64.0.20", "all", 2, 60.0))
        self.assertTrue(limiter.allow("100.64.0.21", "enroll", 2, 60.0))

    def test_state_directory_symlink_is_rejected_without_writing_target(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            victim = root / "victim-state"
            victim.mkdir(mode=0o700)
            link = root / "state-link"
            link.symlink_to(victim, target_is_directory=True)
            home = root / "home"
            paths = self.agent.AgentPaths.from_values(
                link,
                authorized_keys=home / ".ssh" / "authorized_keys",
                home=home,
            )
            state, manifest = self.initial_documents()

            with self.assertRaises(self.agent.AgentError) as rejected:
                self.agent.AgentStore(paths).initialize(state, manifest)

            self.assertEqual("unsafe_path", rejected.exception.code)
            self.assertEqual([], list(victim.iterdir()))

    def test_authorized_keys_symlink_is_rejected_without_modifying_target(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            home = root / "home"
            ssh_dir = home / ".ssh"
            ssh_dir.mkdir(parents=True, mode=0o700)
            victim = root / "victim-authorized-keys"
            sentinel = "DO NOT CHANGE THIS FILE\n"
            victim.write_text(sentinel, encoding="utf-8")
            link = ssh_dir / "authorized_keys"
            link.symlink_to(victim)
            paths = self.agent.AgentPaths.from_values(
                root / "state",
                authorized_keys=link,
                home=home,
            )
            store = self.agent.AgentStore(paths)
            code, recovery = self.agent._make_recovery("admin", 900)
            store.initialize(
                self.agent._initial_state(recovery),
                self.agent._initial_manifest(),
            )
            application = self.application(store)

            with self.assertRaises(self.agent.AgentError) as rejected:
                application.enroll(
                    {
                        "protocolVersion": 1,
                        "clientPublicKey": fixture_key(1),
                        "deviceLabel": "Windows test",
                        "requestedServiceIds": [],
                    },
                    {"authorization": "JKBridge-Enrollment " + code},
                    self.agent.PeerIdentity(
                        "100.64.0.20", "node-client", "alice@example.test"
                    ),
                )

            self.assertEqual("unsafe_path", rejected.exception.code)
            self.assertEqual(sentinel, victim.read_text(encoding="utf-8"))
            persisted_state = json.loads(paths.state_file.read_text(encoding="utf-8"))
            self.assertEqual([], persisted_state["devices"])
            self.assertEqual(1, len(persisted_state["recoveryCodes"]))

    def test_shared_ssh_lock_hardlink_is_rejected_before_state_or_target_changes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            home = root / "home"
            ssh_dir = home / ".ssh"
            ssh_dir.mkdir(parents=True, mode=0o700)
            victim = root / "victim-shared-lock"
            victim.write_text("DO NOT CHANGE\n", encoding="utf-8")
            os.chmod(victim, 0o640)
            os.link(victim, ssh_dir / ".jkbridge.lock")
            paths = self.agent.AgentPaths.from_values(
                root / "state",
                authorized_keys=ssh_dir / "authorized_keys",
                home=home,
            )
            store = self.agent.AgentStore(paths)
            code, recovery = self.agent._make_recovery("admin", 900)
            store.initialize(
                self.agent._initial_state(recovery),
                self.agent._initial_manifest(),
            )

            with self.assertRaises(self.agent.AgentError) as rejected:
                self.application(store).enroll(
                    {
                        "protocolVersion": 1,
                        "clientPublicKey": fixture_key(1),
                        "deviceLabel": "Windows test",
                        "requestedServiceIds": [],
                    },
                    {"authorization": "JKBridge-Enrollment " + code},
                    self.agent.PeerIdentity(
                        "100.64.0.20", "node-client", "alice@example.test"
                    ),
                )

            self.assertEqual("unsafe_path", rejected.exception.code)
            self.assertEqual("DO NOT CHANGE\n", victim.read_text(encoding="utf-8"))
            self.assertEqual(0o640, victim.stat().st_mode & 0o777)
            self.assertFalse(paths.authorized_keys.exists())
            persisted_state = json.loads(paths.state_file.read_text(encoding="utf-8"))
            self.assertEqual([], persisted_state["devices"])
            self.assertEqual(1, len(persisted_state["recoveryCodes"]))

    def test_manifest_symlink_is_not_accepted_as_agent_state(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            state_dir = root / "state"
            state_dir.mkdir(mode=0o700)
            state, manifest = self.initial_documents()
            (state_dir / "state.json").write_text(
                json.dumps(state), encoding="utf-8"
            )
            victim = root / "victim-manifest"
            victim.write_text(json.dumps(manifest), encoding="utf-8")
            manifest_link = root / "manifest-link"
            manifest_link.symlink_to(victim)
            home = root / "home"
            paths = self.agent.AgentPaths.from_values(
                state_dir,
                manifest_file=manifest_link,
                authorized_keys=home / ".ssh" / "authorized_keys",
                home=home,
            )

            with self.assertRaises(self.agent.AgentError) as rejected:
                self.agent.AgentStore(paths).load()

            self.assertIn(rejected.exception.code, {"not_initialized", "unsafe_path"})
            self.assertEqual(json.dumps(manifest), victim.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
