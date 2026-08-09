import base64
import hashlib
import json
import os
import shutil
import stat
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
CONTROLLER = ROOT / "packaging" / "linux" / "jkbridge-agentctl"
SOURCE_DIR = ROOT / "scripts"
USER_UNIT = ROOT / "packaging" / "linux" / "jkbridge-agent.service"
TEST_KEY = (
    "ssh-ed25519 "
    "AAAAC3NzaC1lZDI1NTE5AAAAII5PDcg58G9EbEkF2WSaZfPg2gmYobklFKxJt19PREep"
)


class AgentControllerFixture:
    def __init__(self, root: Path) -> None:
        self.root = root
        self.home = root / "home"
        self.home.mkdir(mode=0o700)
        self.bin = root / "bin"
        self.bin.mkdir(mode=0o700)
        self.log = root / "systemctl.log"
        self.host_key = root / "ssh_host_ed25519_key.pub"
        self.host_key.write_text(TEST_KEY + " fixture\n", encoding="utf-8")
        self._executable(
            "tailscale",
            "#!/bin/sh\n"
            "printf '%s\\n' "
            "'{\"BackendState\":\"Running\",\"CurrentTailnet\":{\"MagicDNSSuffix\":\"example.ts.net\"},\"Self\":{\"DNSName\":\"linux.example.ts.net.\",\"StableID\":\"node-fixture\",\"TailscaleIPs\":[\"100.64.0.10\"]}}'\n",
        )
        self._executable("socat", "#!/bin/sh\nexit 0\n")
        self._executable(
            "systemctl",
            "#!/bin/sh\nprintf '%s\\n' \"$*\" >> \"$JKBRIDGE_SYSTEMCTL_LOG\"\nexit 0\n",
        )

    def _executable(self, name: str, body: str) -> None:
        path = self.bin / name
        path.write_text(body, encoding="utf-8")
        path.chmod(0o700)

    @property
    def environment(self) -> dict[str, str]:
        environment = os.environ.copy()
        environment.update(
            {
                "JKBRIDGE_AGENTCTL_HOME": str(self.home),
                "JKBRIDGE_SYSTEMCTL_LOG": str(self.log),
                "PATH": str(self.bin) + ":/usr/bin:/bin",
            }
        )
        return environment

    def command(
        self, *arguments: str, source_dir: Path = SOURCE_DIR
    ) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [
                str(CONTROLLER),
                "--source-dir",
                str(source_dir),
                "--host-key",
                str(self.host_key),
                *arguments,
            ],
            cwd=ROOT,
            env=self.environment,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
        )


class AgentControllerTests(unittest.TestCase):
    def test_user_unit_avoids_root_only_capability_transitions(self):
        unit = USER_UNIT.read_text(encoding="utf-8")

        incompatible_directives = (
            "CapabilityBoundingSet=",
            "PrivateDevices=",
            "ProtectClock=",
            "ProtectKernelLogs=",
            "ProtectKernelModules=",
        )
        for directive in incompatible_directives:
            with self.subTest(directive=directive):
                self.assertNotIn(f"\n{directive}", unit)
        self.assertIn("\nNoNewPrivileges=true", unit)

    def test_dry_run_checks_prerequisites_without_writing_or_enabling(self):
        with tempfile.TemporaryDirectory() as temporary:
            fixture = AgentControllerFixture(Path(temporary))

            result = fixture.command("--dry-run", "install")

            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn("DRY-RUN", result.stdout)
            self.assertIn("authorized_keys was not changed", result.stdout)
            self.assertFalse((fixture.home / ".local").exists())
            self.assertEqual(
                "--user show-environment\n",
                fixture.log.read_text(encoding="utf-8"),
            )

    def test_install_bootstraps_user_service_and_preserves_unrelated_authorized_keys(self):
        with tempfile.TemporaryDirectory() as temporary:
            fixture = AgentControllerFixture(Path(temporary))
            ssh_dir = fixture.home / ".ssh"
            ssh_dir.mkdir(mode=0o700)
            authorized = ssh_dir / "authorized_keys"
            original = "# personal key remains byte-for-byte\n"
            authorized.write_text(original, encoding="utf-8")
            before = hashlib.sha256(authorized.read_bytes()).hexdigest()

            result = fixture.command("--yes", "--port", "45999", "install")

            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(before, hashlib.sha256(authorized.read_bytes()).hexdigest())
            self.assertEqual(original, authorized.read_text(encoding="utf-8"))
            library = fixture.home / ".local" / "lib" / "jkbridge"
            self.assertTrue((library / "jkbridge_agent.py").is_file())
            self.assertTrue((library / "jkbridge_relay.py").is_file())
            self.assertTrue((library / "jkbridge-linux.py").is_file())
            self.assertTrue(
                (fixture.home / ".local" / "bin" / "jkbridge-agentctl").is_file()
            )
            self.assertTrue(
                os.stat(library / "jkbridge_agent.py").st_mode & stat.S_IXUSR
            )
            self.assertIn(
                "JKBRIDGE_AGENT_PORT=45999",
                (fixture.home / ".config" / "jkbridge-agent" / "agent.env").read_text(
                    encoding="utf-8"
                ),
            )
            state = fixture.home / ".local" / "state" / "jkbridge-agent" / "state.json"
            manifest = state.with_name("services.json")
            install_manifest = state.with_name("install-manifest.sha256")
            self.assertTrue(state.is_file())
            self.assertTrue(manifest.is_file())
            self.assertTrue(install_manifest.is_file())
            self.assertEqual(0o600, install_manifest.stat().st_mode & 0o777)
            setup_code = next(
                line for line in result.stdout.splitlines() if line.startswith("JKBA1.")
            )
            encoded = setup_code.removeprefix("JKBA1.")
            payload = json.loads(
                base64.urlsafe_b64decode(encoded + "=" * (-len(encoded) % 4))
            )
            self.assertEqual(1, payload["version"])
            self.assertEqual("http://100.64.0.10:45999/", payload["endpoint"])
            self.assertEqual(43, len(payload["enrollmentCode"]))
            self.assertNotIn(
                payload["enrollmentCode"],
                state.read_text(encoding="utf-8"),
            )
            calls = fixture.log.read_text(encoding="utf-8")
            self.assertIn("--user daemon-reload", calls)
            self.assertIn("--user enable --now jkbridge-agent.service", calls)

    def test_upgrade_and_rollback_preserve_state_keys_and_custom_port(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            fixture = AgentControllerFixture(root)
            ssh_dir = fixture.home / ".ssh"
            ssh_dir.mkdir(mode=0o700)
            authorized = ssh_dir / "authorized_keys"
            authorized.write_text("# retained through lifecycle\n", encoding="utf-8")
            installed = fixture.command("--yes", "--port", "45999", "install")
            self.assertEqual(0, installed.returncode, installed.stderr)

            state_dir = fixture.home / ".local" / "state" / "jkbridge-agent"
            state_digest = hashlib.sha256((state_dir / "state.json").read_bytes()).hexdigest()
            key_digest = hashlib.sha256(authorized.read_bytes()).hexdigest()
            upgrade_source = root / "upgrade-source"
            shutil.copytree(SOURCE_DIR, upgrade_source)
            source_agent = upgrade_source / "jkbridge_agent.py"
            source_agent.write_text(
                source_agent.read_text(encoding="utf-8") + "\n# upgraded fixture\n",
                encoding="utf-8",
            )

            upgraded = fixture.command("--yes", "upgrade", source_dir=upgrade_source)

            self.assertEqual(0, upgraded.returncode, upgraded.stderr)
            installed_agent = (
                fixture.home / ".local" / "lib" / "jkbridge" / "jkbridge_agent.py"
            )
            self.assertIn("# upgraded fixture", installed_agent.read_text(encoding="utf-8"))
            snapshots = list((state_dir / "rollback").iterdir())
            self.assertEqual(1, len(snapshots))
            self.assertTrue((snapshots[0] / "snapshot.sha256").is_file())
            self.assertIn(
                "JKBRIDGE_AGENT_PORT=45999",
                (
                    fixture.home / ".config" / "jkbridge-agent" / "agent.env"
                ).read_text(encoding="utf-8"),
            )
            self.assertEqual(
                state_digest,
                hashlib.sha256((state_dir / "state.json").read_bytes()).hexdigest(),
            )
            self.assertEqual(key_digest, hashlib.sha256(authorized.read_bytes()).hexdigest())

            rolled_back = fixture.command("--yes", "rollback")

            self.assertEqual(0, rolled_back.returncode, rolled_back.stderr)
            self.assertNotIn("# upgraded fixture", installed_agent.read_text(encoding="utf-8"))
            self.assertEqual(
                state_digest,
                hashlib.sha256((state_dir / "state.json").read_bytes()).hexdigest(),
            )
            self.assertEqual(key_digest, hashlib.sha256(authorized.read_bytes()).hexdigest())
            calls = fixture.log.read_text(encoding="utf-8")
            self.assertIn("--user stop jkbridge-agent.service", calls)
            self.assertIn("--user start jkbridge-agent.service", calls)

    def test_uninstall_removes_verified_runtime_but_preserves_private_state_and_keys(self):
        with tempfile.TemporaryDirectory() as temporary:
            fixture = AgentControllerFixture(Path(temporary))
            ssh_dir = fixture.home / ".ssh"
            ssh_dir.mkdir(mode=0o700)
            authorized = ssh_dir / "authorized_keys"
            authorized.write_text("# preserved after uninstall\n", encoding="utf-8")
            installed = fixture.command("--yes", "install")
            self.assertEqual(0, installed.returncode, installed.stderr)
            state_dir = fixture.home / ".local" / "state" / "jkbridge-agent"
            state_digest = hashlib.sha256((state_dir / "state.json").read_bytes()).hexdigest()
            key_digest = hashlib.sha256(authorized.read_bytes()).hexdigest()

            removed = fixture.command("--yes", "uninstall")

            self.assertEqual(0, removed.returncode, removed.stderr)
            self.assertFalse(
                (fixture.home / ".local" / "lib" / "jkbridge" / "jkbridge_agent.py").exists()
            )
            self.assertFalse(
                (fixture.home / ".local" / "bin" / "jkbridge-agentctl").exists()
            )
            self.assertFalse(
                (
                    fixture.home
                    / ".config"
                    / "systemd"
                    / "user"
                    / "jkbridge-agent.service"
                ).exists()
            )
            self.assertFalse((state_dir / "install-manifest.sha256").exists())
            self.assertEqual(
                state_digest,
                hashlib.sha256((state_dir / "state.json").read_bytes()).hexdigest(),
            )
            self.assertEqual(key_digest, hashlib.sha256(authorized.read_bytes()).hexdigest())
            self.assertIn(
                "--user disable --now jkbridge-agent.service",
                fixture.log.read_text(encoding="utf-8"),
            )

    def test_fallback_stops_only_and_retains_state_keys_and_one_shot_helper(self):
        with tempfile.TemporaryDirectory() as temporary:
            fixture = AgentControllerFixture(Path(temporary))
            ssh_dir = fixture.home / ".ssh"
            ssh_dir.mkdir(mode=0o700)
            authorized = ssh_dir / "authorized_keys"
            authorized.write_text("# retained\n", encoding="utf-8")
            installed = fixture.command("--yes", "install")
            self.assertEqual(0, installed.returncode, installed.stderr)
            state = fixture.home / ".local" / "state" / "jkbridge-agent" / "state.json"
            state_digest = hashlib.sha256(state.read_bytes()).hexdigest()
            key_digest = hashlib.sha256(authorized.read_bytes()).hexdigest()

            result = fixture.command("fallback")

            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn("one-shot fallback", result.stdout)
            self.assertIn("jkbridge-linux.py pair", result.stdout)
            self.assertEqual(state_digest, hashlib.sha256(state.read_bytes()).hexdigest())
            self.assertEqual(key_digest, hashlib.sha256(authorized.read_bytes()).hexdigest())
            self.assertTrue(
                (fixture.home / ".local" / "lib" / "jkbridge" / "jkbridge-linux.py").is_file()
            )
            self.assertIn(
                "--user stop jkbridge-agent.service",
                fixture.log.read_text(encoding="utf-8"),
            )

    def test_install_refuses_unmanaged_unit_before_copying_any_payload(self):
        with tempfile.TemporaryDirectory() as temporary:
            fixture = AgentControllerFixture(Path(temporary))
            unit_dir = fixture.home / ".config" / "systemd" / "user"
            unit_dir.mkdir(parents=True, mode=0o700)
            unit = unit_dir / "jkbridge-agent.service"
            unit.write_text("[Unit]\nDescription=Someone else\n", encoding="utf-8")

            result = fixture.command("--yes", "install")

            self.assertNotEqual(0, result.returncode)
            self.assertIn("unmanaged file", result.stderr)
            self.assertEqual(
                "[Unit]\nDescription=Someone else\n", unit.read_text(encoding="utf-8")
            )
            self.assertFalse((fixture.home / ".local" / "lib" / "jkbridge").exists())

    def test_stop_dry_run_never_removes_state_or_authorized_keys(self):
        with tempfile.TemporaryDirectory() as temporary:
            fixture = AgentControllerFixture(Path(temporary))
            ssh_dir = fixture.home / ".ssh"
            ssh_dir.mkdir(mode=0o700)
            authorized = ssh_dir / "authorized_keys"
            authorized.write_text("# sentinel\n", encoding="utf-8")
            installed = fixture.command("--yes", "install")
            self.assertEqual(0, installed.returncode, installed.stderr)
            state = fixture.home / ".local" / "state" / "jkbridge-agent" / "state.json"
            state_before = state.read_text(encoding="utf-8")

            result = fixture.command("--dry-run", "stop")

            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(state_before, state.read_text(encoding="utf-8"))
            self.assertEqual("# sentinel\n", authorized.read_text(encoding="utf-8"))
            self.assertIn("State, credentials, SSH keys", result.stdout)

    def test_lifecycle_refuses_tampered_managed_unit_before_systemctl_action(self):
        with tempfile.TemporaryDirectory() as temporary:
            fixture = AgentControllerFixture(Path(temporary))
            installed = fixture.command("--yes", "install")
            self.assertEqual(0, installed.returncode, installed.stderr)
            before_calls = fixture.log.read_text(encoding="utf-8")
            unit = fixture.home / ".config" / "systemd" / "user" / "jkbridge-agent.service"
            unit.write_text(unit.read_text(encoding="utf-8") + "# tampered\n", encoding="utf-8")

            result = fixture.command("fallback")

            self.assertNotEqual(0, result.returncode)
            self.assertIn("differs from its install manifest", result.stderr)
            self.assertEqual(before_calls, fixture.log.read_text(encoding="utf-8"))

    def test_lifecycle_refuses_unsafe_payload_mode(self):
        with tempfile.TemporaryDirectory() as temporary:
            fixture = AgentControllerFixture(Path(temporary))
            installed = fixture.command("--yes", "install")
            self.assertEqual(0, installed.returncode, installed.stderr)
            agent = fixture.home / ".local" / "lib" / "jkbridge" / "jkbridge_agent.py"
            agent.chmod(0o755)
            before_calls = fixture.log.read_text(encoding="utf-8")

            result = fixture.command("stop")

            self.assertNotEqual(0, result.returncode)
            self.assertIn("unsafe mode", result.stderr)
            self.assertEqual(before_calls, fixture.log.read_text(encoding="utf-8"))

    def test_lifecycle_refuses_symlinked_fallback_before_stopping_service(self):
        with tempfile.TemporaryDirectory() as temporary:
            fixture = AgentControllerFixture(Path(temporary))
            installed = fixture.command("--yes", "install")
            self.assertEqual(0, installed.returncode, installed.stderr)
            fallback = fixture.home / ".local" / "lib" / "jkbridge" / "jkbridge-linux.py"
            fallback.unlink()
            fallback.symlink_to(fixture.host_key)
            before_calls = fixture.log.read_text(encoding="utf-8")

            result = fixture.command("fallback")

            self.assertNotEqual(0, result.returncode)
            self.assertIn("symbolic-link", result.stderr)
            self.assertEqual(before_calls, fixture.log.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
