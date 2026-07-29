"""Process-boundary security checks for scripts/jkbridge_agent.py.

The module may be merged before the Agent implementation. In that state the
whole class skips cleanly. Once the production script is present, these are
release-gate assertions and an incompatible CLI is a failure.
"""

from __future__ import annotations

import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
AGENT = ROOT / "scripts" / "jkbridge_agent.py"


@unittest.skipUnless(AGENT.is_file(), "jkbridge Agent has not been implemented yet")
class AgentCliBlackBoxTests(unittest.TestCase):
    maxDiff = 4000

    def run_agent(self, sandbox: Path, *arguments: str) -> subprocess.CompletedProcess[str]:
        sandbox = sandbox.resolve(strict=True)
        home = sandbox / "home"
        config = sandbox / "config"
        state = sandbox / "state"
        runtime = sandbox / "runtime"
        for path in (home, config, state, runtime):
            path.mkdir(mode=0o700, exist_ok=True)
            self.assertFalse(path.is_symlink())
            self.assertTrue(path.is_dir())
            self.assertTrue(path.resolve().is_relative_to(sandbox))

        environment = {
            "HOME": str(home),
            "XDG_CONFIG_HOME": str(config),
            "XDG_STATE_HOME": str(state),
            "XDG_RUNTIME_DIR": str(runtime),
            "PATH": os.environ.get("PATH", "/usr/bin:/bin"),
            "LANG": "C.UTF-8",
            "LC_ALL": "C.UTF-8",
            # A safe implementation does not dump arbitrary environment values.
            "JKBRIDGE_TEST_SECRET_MARKER": "JKBA_SECRET_MUST_NOT_APPEAR_7f9c",
        }
        return subprocess.run(
            [sys.executable, str(AGENT), *arguments],
            cwd=sandbox,
            env=environment,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            timeout=5,
            check=False,
        )

    def combined_output(self, result: subprocess.CompletedProcess[str]) -> str:
        return result.stdout + "\n" + result.stderr

    def test_top_level_help_is_non_mutating_redacted_and_documents_lifecycle(self):
        with tempfile.TemporaryDirectory() as temporary:
            sandbox = Path(temporary)

            result = self.run_agent(sandbox, "--help")

            output = self.combined_output(result)
            self.assertEqual(0, result.returncode, output)
            self.assertNotIn("JKBA_SECRET_MUST_NOT_APPEAR_7f9c", output)
            self.assertNotIn(str(Path.home()), output)
            for command in ("bootstrap", "recovery-code", "serve", "status"):
                self.assertIn(command, output, "missing lifecycle command " + command)
            files = [path for path in sandbox.rglob("*") if path.is_file()]
            self.assertEqual([], files, "--help must not create Agent state")

    def test_subcommand_help_exposes_safe_test_path_overrides_without_mutation(self):
        # Security and CI tests need to inject paths. Requiring explicit options
        # also prevents tests from accidentally reaching the operator's files.
        with tempfile.TemporaryDirectory() as temporary:
            sandbox = Path(temporary)
            help_outputs = []
            for command in ("bootstrap", "recovery-code", "serve", "status"):
                with self.subTest(command=command):
                    result = self.run_agent(sandbox, command, "--help")
                    output = self.combined_output(result)
                    help_outputs.append(output)
                    self.assertEqual(0, result.returncode, output)
                    self.assertNotIn("JKBA_SECRET_MUST_NOT_APPEAR_7f9c", output)
                    self.assertNotIn(str(Path.home()), output)

            # The exact spelling may evolve, but a persistent Agent needs an
            # explicit state-root override documented by its help. argparse
            # intentionally shows global options only in top-level help and
            # requires them before the selected subcommand.
            top_level = self.run_agent(sandbox, "--help")
            self.assertEqual(0, top_level.returncode, self.combined_output(top_level))
            aggregate = "\n".join(
                [self.combined_output(top_level), *help_outputs]
            )
            self.assertRegex(aggregate, r"--(?:state-dir|data-dir|test-root)\b")
            files = [path for path in sandbox.rglob("*") if path.is_file()]
            self.assertEqual([], files, "subcommand --help must not create Agent state")

    def test_unknown_operation_fails_without_environment_or_traceback_leak(self):
        with tempfile.TemporaryDirectory() as temporary:
            result = self.run_agent(Path(temporary), "definitely-not-an-operation")

            output = self.combined_output(result)
            self.assertNotEqual(0, result.returncode, output)
            self.assertNotIn("JKBA_SECRET_MUST_NOT_APPEAR_7f9c", output)
            self.assertNotIn("Traceback (most recent call last)", output)
            self.assertNotIn(str(Path.home()), output)


if __name__ == "__main__":
    unittest.main()
