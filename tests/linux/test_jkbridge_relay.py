import hashlib
import importlib.util
import os
import sys
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[2] / "scripts" / "jkbridge_relay.py"
SPEC = importlib.util.spec_from_file_location("jkbridge_relay", SCRIPT)
jkbridge_relay = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
sys.modules[SPEC.name] = jkbridge_relay
SPEC.loader.exec_module(jkbridge_relay)


class FakeRunner:
    def __init__(self, outcomes=None):
        self.calls = []
        self.outcomes = list(outcomes or [])

    def __call__(self, arguments):
        command = tuple(arguments)
        self.calls.append(command)
        if self.outcomes:
            return self.outcomes.pop(0)
        return jkbridge_relay.CommandResult(0, "", "")


def relay_spec(**changes):
    values = {
        "slug": "campus-app",
        "relay_port": 18877,
        "target_host": "service.example.test",
        "target_port": 443,
        "scheme": "https",
        "open_path": "/dashboard?tab=jobs#active",
    }
    values.update(changes)
    return jkbridge_relay.RelaySpec.create(**values)


def skill_unit(spec, *, exec_start=None, extra_service=(), marker=None):
    command = exec_start or (
        "/test/bin/socat TCP4-LISTEN:{},bind=127.0.0.1,reuseaddr,fork "
        "TCP4:{}:{}".format(spec.relay_port, spec.target_host, spec.target_port)
    )
    service_lines = [
        "Type=simple",
        "ExecStart=" + command,
        "Restart=on-failure",
        "RestartSec=3",
        "NoNewPrivileges=true",
        "PrivateTmp=true",
        "ProtectSystem=strict",
        "ProtectHome=true",
        "RestrictAddressFamilies=AF_INET AF_INET6",
        "RestrictSUIDSGID=true",
        "UMask=0077",
    ] + list(extra_service)
    return "\n".join(
        [
            marker or jkbridge_relay.SKILL_MANAGED_MARKER,
            "[Unit]",
            "Description=JK Bridge loopback relay",
            "Wants=network-online.target",
            "After=network-online.target",
            "",
            "[Service]",
        ]
        + service_lines
        + ["", "[Install]", "WantedBy=default.target", ""]
    )


class RelayValidationTests(unittest.TestCase):
    def test_valid_spec_is_canonical_and_url_is_loopback(self):
        spec = relay_spec(target_host="Service.Example.Test.")

        self.assertEqual("service.example.test", spec.target_host)
        self.assertEqual(
            "https://127.0.0.1:18877/dashboard?tab=jobs#active",
            spec.loopback_url,
        )
        self.assertEqual("jkbridge-relay-campus-app.service", jkbridge_relay.unit_name_for_slug(spec.slug))

    def test_rejects_unsafe_slugs_hosts_and_self_loop(self):
        for slug in ("../escape", "Upper", "two words", "a_b", "-bad"):
            with self.subTest(slug=slug), self.assertRaises(jkbridge_relay.RelayValidationError):
                relay_spec(slug=slug)
        for host in (
            "*",
            "0.0.0.0",
            "10.0.0.0/8",
            "https://example.test",
            "example.test:443",
            "example.test;touch",
            "2001:db8::1",
        ):
            with self.subTest(host=host), self.assertRaises(jkbridge_relay.RelayValidationError):
                relay_spec(target_host=host)
        with self.assertRaises(jkbridge_relay.RelayValidationError):
            relay_spec(target_host="127.0.0.1", relay_port=443, target_port=443)

    def test_rejects_invalid_ports_scheme_and_open_path(self):
        for port in (0, 65536, True, "8080"):
            with self.subTest(port=port), self.assertRaises(jkbridge_relay.RelayValidationError):
                relay_spec(relay_port=port)
        for scheme in ("HTTP", "tcp", "socks5", "udp"):
            with self.subTest(scheme=scheme), self.assertRaises(jkbridge_relay.RelayValidationError):
                relay_spec(scheme=scheme)
        for path in ("relative", "//external.test/path", "https://external.test", "/bad path", "/bad\npath"):
            with self.subTest(path=path), self.assertRaises(jkbridge_relay.RelayValidationError):
                relay_spec(open_path=path)


class RelayRenderingTests(unittest.TestCase):
    def test_unit_is_canonical_exact_tcp_and_loopback_only(self):
        unit = jkbridge_relay.render_systemd_unit(relay_spec(), Path("/opt/jk/bin/socat"))

        self.assertTrue(unit.startswith(jkbridge_relay.MANAGED_MARKER + "\n"))
        self.assertIn(
            "ExecStart=/opt/jk/bin/socat TCP4-LISTEN:18877,bind=127.0.0.1,reuseaddr,fork "
            "TCP:service.example.test:443,connect-timeout=10",
            unit,
        )
        self.assertIn("NoNewPrivileges=true", unit)
        self.assertNotIn("0.0.0.0", unit)
        self.assertNotIn("UDP", unit)
        self.assertNotIn("SOCKS", unit.upper())
        self.assertNotIn("*", unit)
        self.assertEqual(unit, jkbridge_relay.render_systemd_unit(relay_spec(), Path("/opt/jk/bin/socat")))


class RelayManagerTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.units = self.root / "config" / "systemd" / "user"
        self.backups = self.root / "state" / "backups"
        self.runner = FakeRunner()
        self.manager = jkbridge_relay.RelayManager(
            self.units,
            self.backups,
            socat_path=Path("/test/bin/socat"),
            systemctl_path="/test/bin/systemctl",
            runner=self.runner,
            dependency_checker=lambda _path: True,
            clock_ns=lambda: 123456789,
        )
        self.spec = relay_spec()

    def tearDown(self):
        self.temporary.cleanup()

    def test_create_plan_is_read_only_then_apply_uses_only_injected_runner(self):
        plan = self.manager.plan_install(self.spec)

        self.assertEqual(jkbridge_relay.PlanAction.CREATE, plan.action)
        self.assertFalse(self.units.exists())
        self.assertEqual([], self.runner.calls)

        result = self.manager.apply_install(plan)

        self.assertTrue(result.applied)
        self.assertIsNone(result.backup_path)
        text = plan.unit_path.read_text(encoding="utf-8")
        self.assertIn("bind=127.0.0.1", text)
        self.assertEqual(
            [
                ("/test/bin/systemctl", "--user", "daemon-reload"),
                (
                    "/test/bin/systemctl",
                    "--user",
                    "enable",
                    "--now",
                    "jkbridge-relay-campus-app.service",
                ),
            ],
            self.runner.calls,
        )

    def test_never_overwrites_unmanaged_unit(self):
        self.units.mkdir(parents=True)
        unit = self.units / jkbridge_relay.unit_name_for_slug(self.spec.slug)
        unit.write_text("[Service]\nExecStart=/bin/true\n", encoding="utf-8")

        plan = self.manager.plan_install(self.spec)

        self.assertEqual(jkbridge_relay.PlanAction.BLOCKED, plan.action)
        with self.assertRaises(jkbridge_relay.RelayConflictError):
            self.manager.apply_install(plan)
        self.assertEqual("[Service]\nExecStart=/bin/true\n", unit.read_text(encoding="utf-8"))
        self.assertEqual([], self.runner.calls)

    def test_symlinked_unit_is_blocked_and_target_unchanged(self):
        self.units.mkdir(parents=True)
        outside = self.root / "outside.service"
        outside.write_text("do not change\n", encoding="utf-8")
        unit = self.units / jkbridge_relay.unit_name_for_slug(self.spec.slug)
        unit.symlink_to(outside)

        plan = self.manager.plan_install(self.spec)

        self.assertEqual(jkbridge_relay.PlanAction.BLOCKED, plan.action)
        self.assertIn("symbolic link", plan.reason)
        self.assertEqual("do not change\n", outside.read_text(encoding="utf-8"))

    def test_stale_plan_is_rejected_before_process_or_write(self):
        original = relay_spec(target_port=8443)
        initial_plan = self.manager.plan_install(original)
        self.manager.apply_install(initial_plan)
        self.runner.calls.clear()
        update = self.manager.plan_install(self.spec)
        update.unit_path.write_text(
            jkbridge_relay.render_systemd_unit(relay_spec(target_port=9443), Path("/test/bin/socat")),
            encoding="utf-8",
        )

        with self.assertRaises(jkbridge_relay.RelayConflictError):
            self.manager.apply_install(update)

        self.assertEqual([], self.runner.calls)
        self.assertIn("9443", update.unit_path.read_text(encoding="utf-8"))

    def test_failed_update_restores_backup(self):
        old_spec = relay_spec(target_port=8443)
        self.manager.apply_install(self.manager.plan_install(old_spec))
        old_text = self.manager._path(old_spec.slug).read_text(encoding="utf-8")
        self.runner.calls.clear()
        self.runner.outcomes = [
            jkbridge_relay.CommandResult(0),
            jkbridge_relay.CommandResult(0),
            jkbridge_relay.CommandResult(1, stderr="simulated restart failure"),
            jkbridge_relay.CommandResult(0),
            jkbridge_relay.CommandResult(0),
        ]

        with self.assertRaises(jkbridge_relay.RelayCommandError):
            self.manager.apply_install(self.manager.plan_install(self.spec))

        self.assertEqual(old_text, self.manager._path(self.spec.slug).read_text(encoding="utf-8"))
        backups = list(self.backups.iterdir())
        self.assertEqual(1, len(backups))
        self.assertEqual(old_text, backups[0].read_text(encoding="utf-8"))
        restart = (
            "/test/bin/systemctl",
            "--user",
            "restart",
            self.manager._path(self.spec.slug).name,
        )
        self.assertEqual(2, self.runner.calls.count(restart))

    def test_successful_update_enables_then_restarts_changed_execstart(self):
        old_spec = relay_spec(target_port=8443)
        self.manager.apply_install(self.manager.plan_install(old_spec))
        self.runner.calls.clear()

        result = self.manager.apply_install(self.manager.plan_install(self.spec))

        self.assertTrue(result.applied)
        self.assertEqual(
            [
                ("/test/bin/systemctl", "--user", "daemon-reload"),
                (
                    "/test/bin/systemctl",
                    "--user",
                    "enable",
                    "jkbridge-relay-campus-app.service",
                ),
                (
                    "/test/bin/systemctl",
                    "--user",
                    "restart",
                    "jkbridge-relay-campus-app.service",
                ),
            ],
            self.runner.calls,
        )
        self.assertIn("service.example.test:443", result.plan.unit_path.read_text(encoding="utf-8"))

    def test_failed_create_removes_partial_managed_unit(self):
        self.runner.outcomes = [
            jkbridge_relay.CommandResult(0),
            jkbridge_relay.CommandResult(1, stderr="simulated enable failure"),
            jkbridge_relay.CommandResult(0),
        ]
        plan = self.manager.plan_install(self.spec)

        with self.assertRaises(jkbridge_relay.RelayCommandError):
            self.manager.apply_install(plan)

        self.assertFalse(plan.unit_path.exists())

    def test_missing_dependency_prevents_mutation(self):
        manager = jkbridge_relay.RelayManager(
            self.units,
            self.backups,
            socat_path=Path("/missing/socat"),
            systemctl_path="/test/bin/systemctl",
            runner=self.runner,
            dependency_checker=lambda _path: False,
        )
        plan = manager.plan_install(self.spec)

        with self.assertRaises(jkbridge_relay.RelayDependencyError):
            manager.apply_install(plan)

        self.assertFalse(plan.unit_path.exists())
        self.assertEqual([], self.runner.calls)

    def test_file_appearing_during_create_is_never_overwritten(self):
        plan = self.manager.plan_install(self.spec)

        def create_unmanaged_then_report_available(_path):
            plan.unit_path.parent.mkdir(parents=True, exist_ok=True)
            plan.unit_path.write_text("unmanaged concurrent unit\n", encoding="utf-8")
            return True

        manager = jkbridge_relay.RelayManager(
            self.units,
            self.backups,
            socat_path=Path("/test/bin/socat"),
            systemctl_path="/test/bin/systemctl",
            runner=self.runner,
            dependency_checker=create_unmanaged_then_report_available,
        )

        with self.assertRaises(jkbridge_relay.RelayConflictError):
            manager.apply_install(plan)

        self.assertEqual("unmanaged concurrent unit\n", plan.unit_path.read_text(encoding="utf-8"))
        self.assertEqual([], self.runner.calls)

    def test_delete_requires_confirmation_and_returns_plan(self):
        self.manager.apply_install(self.manager.plan_install(self.spec))
        self.runner.calls.clear()

        preview = self.manager.delete(self.spec.slug)

        self.assertFalse(preview.applied)
        self.assertEqual(jkbridge_relay.PlanAction.DELETE, preview.plan.action)
        self.assertTrue(preview.plan.requires_confirmation)
        self.assertTrue(preview.plan.unit_path.exists())
        self.assertEqual([], self.runner.calls)

        applied = self.manager.delete(self.spec.slug, confirmed=True)

        self.assertTrue(applied.applied)
        self.assertFalse(applied.plan.unit_path.exists())
        self.assertIsNotNone(applied.backup_path)
        self.assertEqual(
            [
                (
                    "/test/bin/systemctl",
                    "--user",
                    "disable",
                    "--now",
                    "jkbridge-relay-campus-app.service",
                ),
                ("/test/bin/systemctl", "--user", "daemon-reload"),
            ],
            self.runner.calls,
        )

    def test_delete_never_removes_unmanaged_unit_even_when_confirmed(self):
        self.units.mkdir(parents=True)
        unit = self.units / jkbridge_relay.unit_name_for_slug(self.spec.slug)
        unit.write_text("unmanaged\n", encoding="utf-8")

        preview = self.manager.delete(self.spec.slug)

        self.assertEqual(jkbridge_relay.PlanAction.BLOCKED, preview.plan.action)
        with self.assertRaises(jkbridge_relay.RelayConflictError):
            self.manager.delete(self.spec.slug, confirmed=True)
        self.assertEqual("unmanaged\n", unit.read_text(encoding="utf-8"))
        self.assertEqual([], self.runner.calls)

    def test_failed_delete_reload_restores_unit_and_reenables_it(self):
        self.manager.apply_install(self.manager.plan_install(self.spec))
        unit = self.manager._path(self.spec.slug)
        original = unit.read_text(encoding="utf-8")
        self.runner.calls.clear()
        self.runner.outcomes = [
            jkbridge_relay.CommandResult(0),
            jkbridge_relay.CommandResult(1, stderr="simulated reload failure"),
            jkbridge_relay.CommandResult(0),
            jkbridge_relay.CommandResult(0),
        ]

        with self.assertRaises(jkbridge_relay.RelayCommandError):
            self.manager.delete(self.spec.slug, confirmed=True)

        self.assertEqual(original, unit.read_text(encoding="utf-8"))
        self.assertIn(
            (
                "/test/bin/systemctl",
                "--user",
                "enable",
                "--now",
                "jkbridge-relay-campus-app.service",
            ),
            self.runner.calls,
        )


class RelayProbeAndFilesystemTests(unittest.TestCase):
    def test_probe_is_read_only_and_uses_injected_dependencies(self):
        runner = FakeRunner(
            [
                jkbridge_relay.CommandResult(0, ""),
                jkbridge_relay.CommandResult(0, "connected\n", ""),
            ]
        )
        spec = relay_spec()

        result = jkbridge_relay.probe_relay(
            spec,
            socat_path=Path("/test/bin/socat"),
            dependency_checker=lambda path: path == Path("/test/bin/socat"),
            runner=runner,
            ss_path="/test/bin/ss",
        )

        self.assertTrue(result.ready_to_install)
        self.assertEqual(
            ("/test/bin/ss", "-H", "-ltn", "sport = :18877"),
            runner.calls[0],
        )
        self.assertEqual("/test/bin/socat", runner.calls[1][0])
        self.assertIn("service.example.test:443", runner.calls[1][-1])

    def test_atomic_write_and_rollback_refuse_symlink(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            target = root / "target"
            target.write_text("safe\n", encoding="utf-8")
            link = root / "managed.service"
            link.symlink_to(target)

            with self.assertRaises(jkbridge_relay.RelaySafetyError):
                jkbridge_relay.atomic_write_text(link, "unsafe\n")
            self.assertEqual("safe\n", target.read_text(encoding="utf-8"))

    def test_backup_and_rollback_restore_exact_content(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            unit = root / "unit.service"
            unit.write_text("old\n", encoding="utf-8")
            backup = jkbridge_relay.create_backup(unit, root / "backups", clock_ns=lambda: 7)
            jkbridge_relay.atomic_write_text(unit, "new\n")

            jkbridge_relay.rollback_file(
                unit,
                backup,
                created=False,
                expected_sha256=hashlib.sha256(b"new\n").hexdigest(),
            )

            self.assertEqual("old\n", unit.read_text(encoding="utf-8"))
            self.assertEqual("old\n", backup.read_text(encoding="utf-8"))
            self.assertEqual(0o600, os.stat(unit).st_mode & 0o777)

    def test_rollback_refuses_to_overwrite_an_unexpected_change(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            unit = root / "unit.service"
            unit.write_text("old\n", encoding="utf-8")
            backup = jkbridge_relay.create_backup(unit, root / "backups")
            unit.write_text("changed elsewhere\n", encoding="utf-8")

            with self.assertRaises(jkbridge_relay.RelaySafetyError):
                jkbridge_relay.rollback_file(
                    unit,
                    backup,
                    created=False,
                    expected_sha256=hashlib.sha256(b"our transient content\n").hexdigest(),
                )

            self.assertEqual("changed elsewhere\n", unit.read_text(encoding="utf-8"))


class RelayAdoptionTests(unittest.TestCase):
    DEVICE_ONE = "1" * 32
    DEVICE_TWO = "2" * 32

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.units = self.root / "config" / "systemd" / "user"
        self.units.mkdir(parents=True)
        self.backups = self.root / "state" / "backups"
        self.runner = FakeRunner()
        self.manager = jkbridge_relay.RelayManager(
            self.units,
            self.backups,
            socat_path=Path("/test/bin/socat"),
            systemctl_path="/test/bin/systemctl",
            runner=self.runner,
            dependency_checker=lambda _path: True,
            clock_ns=lambda: 987654321,
            unit_search_dirs=(self.units,),
        )
        self.spec = relay_spec(target_host="10.20.30.40", target_port=9000)
        self.unit_name = "jkbridge-{}-relay.service".format(self.spec.slug)
        self.unit = self.units / self.unit_name
        self.original = skill_unit(self.spec)
        self.unit.write_text(self.original, encoding="utf-8")

    def tearDown(self):
        self.temporary.cleanup()

    def plan(self, **kwargs):
        return self.manager.plan_adopt_existing(
            self.spec,
            self.unit_name,
            self.unit,
            **kwargs,
        )

    def test_plan_is_read_only_complete_and_binds_affected_devices(self):
        plan = self.plan(affected_device_ids=(self.DEVICE_TWO, self.DEVICE_ONE, self.DEVICE_ONE))

        self.assertEqual(jkbridge_relay.AdoptionAction.ADOPT, plan.action)
        self.assertTrue(plan.requires_confirmation)
        self.assertEqual((self.DEVICE_ONE, self.DEVICE_TWO), plan.affected_device_ids)
        self.assertEqual([self.DEVICE_ONE, self.DEVICE_TWO], plan.to_dict()["affectedDeviceIds"])
        self.assertTrue(plan.state_binding_token.startswith("sha256:"))
        self.assertIn("--- " + self.unit_name + ".existing", plan.full_diff)
        self.assertIn("+++ " + self.unit_name + ".jkbridge-managed", plan.full_diff)
        self.assertIn(jkbridge_relay.MANAGED_MARKER, plan.full_diff)
        self.assertEqual(self.original, self.unit.read_text(encoding="utf-8"))
        self.assertEqual([], self.runner.calls)

    def test_apply_requires_confirmation_and_matching_state_token(self):
        plan = self.plan()

        with self.assertRaises(jkbridge_relay.RelayConfirmationError):
            self.manager.apply_adoption(plan)
        with self.assertRaises(jkbridge_relay.RelayConfirmationError):
            self.manager.apply_adoption(plan, confirmed=True, state_binding_token="sha256:wrong")

        self.assertEqual(self.original, self.unit.read_text(encoding="utf-8"))
        self.assertEqual([], self.runner.calls)

    def test_confirmed_apply_normalizes_and_then_reports_no_change(self):
        plan = self.plan(affected_device_ids=(self.DEVICE_ONE,))

        result = self.manager.apply_adoption(
            plan,
            confirmed=True,
            state_binding_token=plan.state_binding_token,
        )

        self.assertTrue(result.applied)
        self.assertIsNotNone(result.backup_path)
        self.assertEqual(self.original, result.backup_path.read_text(encoding="utf-8"))
        self.assertEqual(
            jkbridge_relay.render_systemd_unit(self.spec, Path("/test/bin/socat")),
            self.unit.read_text(encoding="utf-8"),
        )
        self.assertEqual(
            [
                ("/test/bin/systemctl", "--user", "daemon-reload"),
                ("/test/bin/systemctl", "--user", "try-restart", self.unit_name),
            ],
            self.runner.calls,
        )
        after = self.plan(affected_device_ids=(self.DEVICE_ONE,))
        self.assertEqual(jkbridge_relay.AdoptionAction.NO_CHANGE, after.action)
        self.assertFalse(after.requires_confirmation)

    def test_stale_plan_or_changed_affected_set_is_rejected(self):
        plan = self.plan(affected_device_ids=(self.DEVICE_ONE,))
        self.unit.write_text(self.original.replace("RestartSec=3", "RestartSec=4"), encoding="utf-8")

        with self.assertRaises(jkbridge_relay.RelayConflictError):
            self.manager.apply_adoption(
                plan,
                confirmed=True,
                state_binding_token=plan.state_binding_token,
            )

        self.assertEqual([], self.runner.calls)
        changed_context = self.manager.plan_adopt_existing(
            self.spec,
            self.unit_name,
            self.unit,
            affected_device_ids=(self.DEVICE_TWO,),
        )
        self.assertNotEqual(plan.state_binding_token, changed_context.state_binding_token)

    def test_state_change_during_dependency_check_is_not_overwritten(self):
        plan = self.plan()
        changed = self.original.replace("RestartSec=3", "RestartSec=5")

        def mutate_then_report_available(_path):
            self.unit.write_text(changed, encoding="utf-8")
            return True

        manager = jkbridge_relay.RelayManager(
            self.units,
            self.backups,
            socat_path=Path("/test/bin/socat"),
            systemctl_path="/test/bin/systemctl",
            runner=self.runner,
            dependency_checker=mutate_then_report_available,
            unit_search_dirs=(self.units,),
        )

        with self.assertRaises(jkbridge_relay.RelayConflictError):
            manager.apply_adoption(
                plan,
                confirmed=True,
                state_binding_token=plan.state_binding_token,
            )

        self.assertEqual(changed, self.unit.read_text(encoding="utf-8"))
        self.assertEqual([], self.runner.calls)

    def test_failed_apply_atomically_restores_and_try_restarts_legacy_unit(self):
        plan = self.plan()
        self.runner.outcomes = [
            jkbridge_relay.CommandResult(0),
            jkbridge_relay.CommandResult(1, stderr="simulated restart failure"),
            jkbridge_relay.CommandResult(0),
            jkbridge_relay.CommandResult(0),
        ]

        with self.assertRaises(jkbridge_relay.RelayCommandError):
            self.manager.apply_adoption(
                plan,
                confirmed=True,
                state_binding_token=plan.state_binding_token,
            )

        self.assertEqual(self.original, self.unit.read_text(encoding="utf-8"))
        self.assertEqual(2, self.runner.calls.count(("/test/bin/systemctl", "--user", "try-restart", self.unit_name)))

    def test_rejects_non_allowlisted_symlink_and_unfamiliar_marker(self):
        outside_name = "unrelated.service"
        outside = self.units / outside_name
        outside.write_text(self.original, encoding="utf-8")
        blocked_name = self.manager.plan_adopt_existing(self.spec, outside_name, outside)
        self.assertEqual(jkbridge_relay.AdoptionAction.BLOCKED, blocked_name.action)

        target = self.root / "outside-target.service"
        target.write_text(self.original, encoding="utf-8")
        self.unit.unlink()
        self.unit.symlink_to(target)
        blocked_link = self.plan()
        self.assertEqual(jkbridge_relay.AdoptionAction.BLOCKED, blocked_link.action)
        self.assertIn("symbolic link", blocked_link.reason)

        self.unit.unlink()
        self.unit.write_text(skill_unit(self.spec, marker="# created manually"), encoding="utf-8")
        blocked_marker = self.plan()
        self.assertEqual(jkbridge_relay.AdoptionAction.BLOCKED, blocked_marker.action)
        self.assertIn("provenance", blocked_marker.reason)

    def test_rejects_drop_ins_and_world_writable_units(self):
        drop_in = self.units / (self.unit_name + ".d")
        drop_in.mkdir()
        (drop_in / "override.conf").write_text(
            "[Service]\nEnvironment=LD_PRELOAD=/tmp/x\n", encoding="utf-8"
        )

        blocked_drop_in = self.plan()

        self.assertEqual(jkbridge_relay.AdoptionAction.BLOCKED, blocked_drop_in.action)
        self.assertIn("drop-in", blocked_drop_in.reason)
        for child in drop_in.iterdir():
            child.unlink()
        drop_in.rmdir()
        prefix_drop_in = self.units / "jkbridge-.service.d"
        prefix_drop_in.mkdir()
        blocked_prefix = self.plan()
        self.assertEqual(jkbridge_relay.AdoptionAction.BLOCKED, blocked_prefix.action)
        self.assertIn("drop-in", blocked_prefix.reason)
        prefix_drop_in.rmdir()
        self.unit.chmod(0o666)

        blocked_mode = self.plan()

        self.assertEqual(jkbridge_relay.AdoptionAction.BLOCKED, blocked_mode.action)
        self.assertIn("world writable", blocked_mode.reason)

    def test_rejects_non_equivalent_or_broadened_exec_forms(self):
        cases = {
            "target mismatch": skill_unit(
                self.spec,
                exec_start=(
                    "/test/bin/socat TCP4-LISTEN:18877,bind=127.0.0.1,reuseaddr,fork "
                    "TCP4:10.20.30.41:9000"
                ),
            ),
            "wildcard listener": skill_unit(
                self.spec,
                exec_start=(
                    "/test/bin/socat TCP4-LISTEN:18877,bind=0.0.0.0,reuseaddr,fork "
                    "TCP4:10.20.30.40:9000"
                ),
            ),
            "shell": skill_unit(self.spec, exec_start="/bin/sh -c /test/bin/socat"),
            "udp": skill_unit(
                self.spec,
                exec_start=(
                    "/test/bin/socat UDP4-LISTEN:18877,bind=127.0.0.1,reuseaddr,fork "
                    "UDP4:10.20.30.40:9000"
                ),
            ),
            "second exec": skill_unit(
                self.spec,
                extra_service=(
                    "ExecStart=/test/bin/socat TCP4-LISTEN:18878,bind=127.0.0.1,reuseaddr,fork TCP4:10.20.30.40:9000",
                ),
            ),
            "environment": skill_unit(self.spec, extra_service=("Environment=LD_PRELOAD=/tmp/x",)),
            "extra exec": skill_unit(self.spec, extra_service=("ExecStartPre=/bin/true",)),
        }
        for label, text in cases.items():
            with self.subTest(label=label):
                self.unit.write_text(text, encoding="utf-8")
                plan = self.plan()
                self.assertEqual(jkbridge_relay.AdoptionAction.BLOCKED, plan.action)
                self.assertEqual([], self.runner.calls)

    def test_managed_target_update_preview_keeps_all_affected_devices(self):
        canonical = self.manager._path(self.spec.slug)
        old_spec = relay_spec(target_host="10.20.30.40", target_port=8999)
        self.manager.apply_install(self.manager.plan_install(old_spec))
        self.runner.calls.clear()

        plan = self.manager.plan_install(
            self.spec,
            affected_device_ids=(self.DEVICE_TWO, self.DEVICE_ONE),
        )

        self.assertEqual(jkbridge_relay.PlanAction.UPDATE, plan.action)
        self.assertEqual(old_spec.relay_port, plan.spec.relay_port)
        self.assertEqual((self.DEVICE_ONE, self.DEVICE_TWO), plan.affected_device_ids)
        self.assertEqual([self.DEVICE_ONE, self.DEVICE_TWO], plan.to_dict()["affectedDeviceIds"])
        result = self.manager.apply_install(plan)
        self.assertEqual(plan.affected_device_ids, result.plan.affected_device_ids)
        self.assertTrue(canonical.exists())


if __name__ == "__main__":
    unittest.main()
