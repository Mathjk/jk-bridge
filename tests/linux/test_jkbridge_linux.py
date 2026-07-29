import importlib.util
import ipaddress
import io
import json
import tempfile
import threading
import unittest
import urllib.request
from contextlib import redirect_stdout
from pathlib import Path
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / "scripts" / "jkbridge-linux.py"
SPEC = importlib.util.spec_from_file_location("jkbridge_linux", MODULE_PATH)
assert SPEC and SPEC.loader
jkbridge_linux = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(jkbridge_linux)

TEST_KEY = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAII5PDcg58G9EbEkF2WSaZfPg2gmYobklFKxJt19PREep"


class LinuxHelperTests(unittest.TestCase):
    def test_normalize_key_accepts_comment_and_computes_fingerprint(self):
        normalized, fingerprint = jkbridge_linux.normalize_ed25519_key(TEST_KEY + " fixture")

        self.assertEqual(TEST_KEY, normalized)
        self.assertTrue(fingerprint.startswith("SHA256:"))

    def test_read_host_key_rejects_empty_file(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "ssh_host_ed25519_key.pub"
            path.touch()

            with self.assertRaises(jkbridge_linux.PairingError):
                jkbridge_linux.read_host_public_key(path)

    def test_install_is_atomic_restricted_and_idempotent(self):
        with tempfile.TemporaryDirectory() as temporary:
            home = Path(temporary)

            first = jkbridge_linux.install_authorized_key(home, TEST_KEY, [8765, 80, 80])
            second = jkbridge_linux.install_authorized_key(home, TEST_KEY, [80, 8765])

            authorized = home / ".ssh" / "authorized_keys"
            text = authorized.read_text(encoding="utf-8")
            self.assertTrue(first["changed"])
            self.assertFalse(second["changed"])
            self.assertIn('restrict,port-forwarding,command="/usr/bin/false"', text)
            self.assertIn('permitopen="127.0.0.1:80"', text)
            self.assertIn('permitopen="127.0.0.1:8765"', text)
            self.assertEqual(1, text.count(TEST_KEY))
            self.assertEqual(0o600, authorized.stat().st_mode & 0o777)
            self.assertEqual(0o700, authorized.parent.stat().st_mode & 0o777)

    def test_install_refuses_restricted_duplicate_of_unmanaged_key(self):
        with tempfile.TemporaryDirectory() as temporary:
            home = Path(temporary)
            ssh_dir = home / ".ssh"
            ssh_dir.mkdir(mode=0o700)
            (ssh_dir / "authorized_keys").write_text(TEST_KEY + " personal key\n", encoding="utf-8")

            with self.assertRaises(jkbridge_linux.PairingError):
                jkbridge_linux.install_authorized_key(home, TEST_KEY, [80])

            self.assertEqual(
                TEST_KEY + " personal key\n",
                (ssh_dir / "authorized_keys").read_text(encoding="utf-8"),
            )

    def test_install_refuses_marker_on_unrestricted_entry(self):
        with tempfile.TemporaryDirectory() as temporary:
            home = Path(temporary)
            ssh_dir = home / ".ssh"
            ssh_dir.mkdir(mode=0o700)
            fingerprint = jkbridge_linux.normalize_ed25519_key(TEST_KEY)[1]
            unsafe = TEST_KEY + " JK Bridge managed " + fingerprint + "\n"
            (ssh_dir / "authorized_keys").write_text(unsafe, encoding="utf-8")

            with self.assertRaises(jkbridge_linux.PairingError):
                jkbridge_linux.install_authorized_key(home, TEST_KEY, [80])

            self.assertEqual(
                unsafe,
                (ssh_dir / "authorized_keys").read_text(encoding="utf-8"),
            )

    def test_install_safely_converts_exact_agent_entry_for_one_shot_fallback(self):
        with tempfile.TemporaryDirectory() as temporary:
            home = Path(temporary)
            ssh_dir = home / ".ssh"
            ssh_dir.mkdir(mode=0o700)
            normalized, fingerprint = jkbridge_linux.normalize_ed25519_key(TEST_KEY)
            agent_line = (
                jkbridge_linux.RESTRICTED_OPTIONS_PREFIX
                + 'permitopen="127.0.0.1:80",permitopen="127.0.0.1:8081" '
                + normalized
                + " JK Bridge agent "
                + "a" * 32
                + " "
                + fingerprint
            )
            authorized = ssh_dir / "authorized_keys"
            authorized.write_text("# unrelated\n" + agent_line + "\n", encoding="utf-8")

            result = jkbridge_linux.install_authorized_key(home, TEST_KEY, [80, 8877])

            text = authorized.read_text(encoding="utf-8")
            self.assertTrue(result["changed"])
            self.assertTrue(Path(result["backup"]).is_file())
            self.assertEqual(1, text.count(TEST_KEY))
            self.assertIn("# unrelated\n", text)
            self.assertIn("JK Bridge managed " + fingerprint, text)
            self.assertNotIn("JK Bridge agent", text)
            self.assertIn('permitopen="127.0.0.1:80"', text)
            self.assertIn('permitopen="127.0.0.1:8877"', text)
            self.assertNotIn('permitopen="127.0.0.1:8081"', text)

    def test_install_refuses_fake_or_wildcard_agent_entry_without_changes(self):
        normalized, fingerprint = jkbridge_linux.normalize_ed25519_key(TEST_KEY)
        unsafe_lines = (
            normalized
            + " JK Bridge agent "
            + "a" * 32
            + " "
            + fingerprint,
            jkbridge_linux.RESTRICTED_OPTIONS_PREFIX
            + 'permitopen="127.0.0.1:*" '
            + normalized
            + " JK Bridge agent "
            + "a" * 32
            + " "
            + fingerprint,
            jkbridge_linux.RESTRICTED_OPTIONS_PREFIX
            + 'permitopen="127.0.0.1:80" '
            + normalized
            + " JK Bridge agent malformed "
            + fingerprint,
        )
        for unsafe in unsafe_lines:
            with self.subTest(unsafe=unsafe):
                with tempfile.TemporaryDirectory() as temporary:
                    home = Path(temporary)
                    ssh_dir = home / ".ssh"
                    ssh_dir.mkdir(mode=0o700)
                    authorized = ssh_dir / "authorized_keys"
                    authorized.write_text(unsafe + "\n", encoding="utf-8")

                    with self.assertRaises(jkbridge_linux.PairingError):
                        jkbridge_linux.install_authorized_key(home, TEST_KEY, [80])

                    self.assertEqual(unsafe + "\n", authorized.read_text(encoding="utf-8"))

    def test_install_migrates_exact_v1_restricted_entry(self):
        with tempfile.TemporaryDirectory() as temporary:
            home = Path(temporary)
            ssh_dir = home / ".ssh"
            ssh_dir.mkdir(mode=0o700)
            legacy = (
                'restrict,port-forwarding,command="/usr/bin/false",'
                'from="100.64.0.0/10,fd7a:115c:a1e0::/48",'
                'permitopen="127.0.0.1:*" '
                + TEST_KEY
                + " JK Bridge dedicated tunnel key\n"
            )
            (ssh_dir / "authorized_keys").write_text(legacy, encoding="utf-8")

            result = jkbridge_linux.install_authorized_key(home, TEST_KEY, [80, 8765])

            migrated = (ssh_dir / "authorized_keys").read_text(encoding="utf-8")
            self.assertTrue(result["changed"])
            self.assertIn("JK Bridge managed SHA256:", migrated)
            self.assertIn('permitopen="127.0.0.1:80"', migrated)
            self.assertIn('permitopen="127.0.0.1:8765"', migrated)
            self.assertNotIn('permitopen="127.0.0.1:*"', migrated)
            self.assertTrue(Path(result["backup"]).is_file())

    def test_remove_deletes_only_matching_managed_key_and_creates_backup(self):
        with tempfile.TemporaryDirectory() as temporary:
            home = Path(temporary)
            installed = jkbridge_linux.install_authorized_key(home, TEST_KEY, [80])
            authorized = home / ".ssh" / "authorized_keys"
            with authorized.open("a", encoding="utf-8") as stream:
                stream.write("# unrelated entry remains\n")

            result = jkbridge_linux.remove_managed_key(home, installed["fingerprint"])

            self.assertEqual(1, result["removed"])
            self.assertTrue(Path(result["backup"]).is_file())
            self.assertEqual("# unrelated entry remains\n", authorized.read_text(encoding="utf-8"))

    def test_one_shot_http_pairing_installs_key_and_returns_receipt(self):
        with tempfile.TemporaryDirectory() as temporary:
            home = Path(temporary)
            identity = {
                "dnsName": "linux-box.example-tailnet.ts.net",
                "suffix": "example-tailnet.ts.net",
                "ipv4": "127.0.0.1",
            }
            token = "A" * 43
            with mock.patch.object(
                jkbridge_linux,
                "TAILSCALE_IPV4",
                ipaddress.ip_network("127.0.0.0/8"),
            ):
                with jkbridge_linux.PairingHttpServer(
                    ("127.0.0.1", 0),
                    token,
                    identity,
                    "alice",
                    home,
                    22,
                    TEST_KEY,
                ) as server:
                    with redirect_stdout(io.StringIO()):
                        thread = threading.Thread(target=server.handle_request)
                        thread.start()
                        request = urllib.request.Request(
                            "http://127.0.0.1:{}/v1/pair".format(server.server_address[1]),
                            data=json.dumps(
                                {
                                    "protocolVersion": 1,
                                    "clientPublicKey": TEST_KEY,
                                    "remotePorts": [8765, 80],
                                }
                            ).encode("utf-8"),
                            headers={
                                "Authorization": "Bearer " + token,
                                "Content-Type": "application/json",
                            },
                            method="POST",
                        )
                        with urllib.request.urlopen(request, timeout=2) as response:
                            receipt = json.loads(response.read().decode("utf-8"))
                        thread.join(timeout=2)

                    self.assertFalse(thread.is_alive())
                    self.assertTrue(server.completed)
                    self.assertEqual([80, 8765], receipt["permitPorts"])
                    self.assertEqual(identity["dnsName"], receipt["serverHost"])
                    self.assertIn(TEST_KEY, (home / ".ssh" / "authorized_keys").read_text())


if __name__ == "__main__":
    unittest.main()
