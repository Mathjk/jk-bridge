"""Security regression tests for the authorization substrate reused by Agent.

All filesystem writes are contained in a TemporaryDirectory. These cases test
the existing one-shot helper today and are intended to remain valid when its
safe primitives are moved behind jkbridge-agent.
"""

from __future__ import annotations

import base64
import importlib.util
import os
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
HELPER_PATH = ROOT / "scripts" / "jkbridge-linux.py"
SPEC = importlib.util.spec_from_file_location("jkbridge_linux_security", HELPER_PATH)
assert SPEC and SPEC.loader
helper = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(helper)


def fixture_key(seed: int) -> str:
    algorithm = b"ssh-ed25519"
    material = bytes((seed + index) % 256 for index in range(32))
    blob = (
        len(algorithm).to_bytes(4, "big")
        + algorithm
        + len(material).to_bytes(4, "big")
        + material
    )
    return "ssh-ed25519 " + base64.b64encode(blob).decode("ascii")


FIRST_KEY = fixture_key(1)
SECOND_KEY = fixture_key(101)


class AuthorizedKeysSecurityTests(unittest.TestCase):
    def assert_contained(self, root: Path, candidate: Path) -> None:
        self.assertTrue(candidate.resolve().is_relative_to(root.resolve()))

    def test_generated_entry_is_exact_loopback_only_and_shell_denied(self):
        normalized, fingerprint = helper.normalize_ed25519_key(FIRST_KEY)

        line = helper.build_authorized_line(normalized, fingerprint, [443, 80])

        self.assertIn('restrict,port-forwarding,command="/usr/bin/false"', line)
        self.assertIn('from="100.64.0.0/10,fd7a:115c:a1e0::/48"', line)
        self.assertIn('permitopen="127.0.0.1:80"', line)
        self.assertIn('permitopen="127.0.0.1:443"', line)
        self.assertNotIn('permitopen="127.0.0.1:*"', line)
        self.assertNotIn('permitopen="0.0.0.0:', line)
        self.assertNotIn('permitlisten=', line)

    def test_ports_reject_bool_strings_wildcards_and_out_of_range(self):
        invalid_sets = [
            [],
            [True],
            ["80"],
            ["*"],
            [0],
            [65536],
            list(range(1, helper.MAX_PORTS + 2)),
        ]
        for ports in invalid_sets:
            with self.subTest(ports=ports):
                with self.assertRaises(helper.PairingError):
                    helper.validate_ports(ports)

    def test_install_preserves_unrelated_entries_and_canonicalizes_ports(self):
        with tempfile.TemporaryDirectory() as temporary:
            home = Path(temporary)
            ssh_dir = home / ".ssh"
            ssh_dir.mkdir(mode=0o700)
            unrelated = SECOND_KEY + " personal-access\n# keep this comment exactly\n"
            authorized = ssh_dir / "authorized_keys"
            authorized.write_text(unrelated, encoding="utf-8")

            result = helper.install_authorized_key(home, FIRST_KEY, [443, 80, 80])

            final = authorized.read_text(encoding="utf-8")
            self.assertTrue(final.startswith(unrelated))
            self.assertEqual(1, final.count(FIRST_KEY))
            self.assertLess(final.index('127.0.0.1:80'), final.index('127.0.0.1:443'))
            self.assertEqual(0o600, authorized.stat().st_mode & 0o777)
            self.assertIsNotNone(result["backup"])
            backup = Path(result["backup"])
            self.assert_contained(home, backup)
            self.assertEqual(unrelated, backup.read_text(encoding="utf-8"))

    def test_install_rejects_ssh_directory_symlink_without_touching_target(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            home = root / "home"
            victim = root / "victim"
            home.mkdir()
            victim.mkdir()
            (victim / "authorized_keys").write_text("DO NOT CHANGE\n", encoding="utf-8")
            (home / ".ssh").symlink_to(victim, target_is_directory=True)

            with self.assertRaises(helper.PairingError):
                helper.install_authorized_key(home, FIRST_KEY, [80])

            self.assertEqual(
                "DO NOT CHANGE\n",
                (victim / "authorized_keys").read_text(encoding="utf-8"),
            )

    def test_install_rejects_authorized_keys_symlink_without_touching_target(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            home = root / "home"
            ssh_dir = home / ".ssh"
            ssh_dir.mkdir(parents=True, mode=0o700)
            victim = root / "victim-authorized-keys"
            victim.write_text("DO NOT CHANGE\n", encoding="utf-8")
            (ssh_dir / "authorized_keys").symlink_to(victim)

            with self.assertRaises(helper.PairingError):
                helper.install_authorized_key(home, FIRST_KEY, [80])

            self.assertEqual("DO NOT CHANGE\n", victim.read_text(encoding="utf-8"))

    @unittest.skipUnless(hasattr(os, "O_NOFOLLOW"), "platform lacks O_NOFOLLOW")
    def test_install_rejects_lock_symlink_without_touching_target(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            home = root / "home"
            ssh_dir = home / ".ssh"
            ssh_dir.mkdir(parents=True, mode=0o700)
            victim = root / "victim-lock-target"
            victim.write_text("DO NOT CHANGE\n", encoding="utf-8")
            (ssh_dir / ".jkbridge.lock").symlink_to(victim)

            with self.assertRaises(helper.PairingError):
                helper.install_authorized_key(home, FIRST_KEY, [80])

            self.assertEqual("DO NOT CHANGE\n", victim.read_text(encoding="utf-8"))

    def test_install_rejects_hardlinked_shared_lock_without_chmod_or_write(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            home = root / "home"
            ssh_dir = home / ".ssh"
            ssh_dir.mkdir(parents=True, mode=0o700)
            victim = root / "victim-lock-target"
            victim.write_text("DO NOT CHANGE\n", encoding="utf-8")
            os.chmod(victim, 0o640)
            os.link(victim, ssh_dir / ".jkbridge.lock")

            with self.assertRaises(helper.PairingError):
                helper.install_authorized_key(home, FIRST_KEY, [80])

            self.assertEqual("DO NOT CHANGE\n", victim.read_text(encoding="utf-8"))
            self.assertEqual(0o640, victim.stat().st_mode & 0o777)
            self.assertFalse((ssh_dir / "authorized_keys").exists())

    def test_remove_one_device_preserves_other_device_and_unrelated_text(self):
        with tempfile.TemporaryDirectory() as temporary:
            home = Path(temporary)
            first = helper.install_authorized_key(home, FIRST_KEY, [80])
            second = helper.install_authorized_key(home, SECOND_KEY, [443])
            authorized = home / ".ssh" / "authorized_keys"
            with authorized.open("a", encoding="utf-8") as stream:
                stream.write("# unrelated sentinel\n")

            result = helper.remove_managed_key(home, first["fingerprint"])

            final = authorized.read_text(encoding="utf-8")
            self.assertEqual(1, result["removed"])
            self.assertNotIn(FIRST_KEY, final)
            self.assertIn(SECOND_KEY, final)
            self.assertIn("# unrelated sentinel\n", final)
            self.assertIn(second["fingerprint"], final)

    def test_forged_managed_comment_does_not_allow_adoption_or_removal(self):
        with tempfile.TemporaryDirectory() as temporary:
            home = Path(temporary)
            ssh_dir = home / ".ssh"
            ssh_dir.mkdir(mode=0o700)
            _, fingerprint = helper.normalize_ed25519_key(FIRST_KEY)
            forged = FIRST_KEY + " JK Bridge managed " + fingerprint + "\n"
            authorized = ssh_dir / "authorized_keys"
            authorized.write_text(forged, encoding="utf-8")

            with self.assertRaises(helper.PairingError):
                helper.install_authorized_key(home, FIRST_KEY, [80])
            with self.assertRaises(helper.PairingError):
                helper.remove_managed_key(home, fingerprint)

            self.assertEqual(forged, authorized.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
