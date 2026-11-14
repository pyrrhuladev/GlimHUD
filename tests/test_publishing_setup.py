"""Exercise publishing setup against an isolated fake CLI, never GitHub or real secrets."""
from __future__ import annotations

import base64
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
MOCK_CLI = r'''
import base64, json, os, sys
from pathlib import Path
args = sys.argv[1:]
record = {"args": args}
mode = os.environ["GLIMHUD_SETUP_FIXTURE_MODE"]
if args[:2] == ["secret", "set"]:
    stdin_bytes = sys.stdin.buffer.read()
    record["stdin_matched"] = stdin_bytes.decode("utf-8", errors="replace").strip() == base64.b64encode(bytes(32)).decode()
    record["stdin_bom"] = stdin_bytes.startswith((b"\xef\xbb\xbf", b"\xff\xfe", b"\xfe\xff"))
    record["stdin_length"] = len(stdin_bytes)
with open(os.environ["GLIMHUD_SETUP_FIXTURE_LOG"], "a", encoding="utf-8") as log:
    log.write(json.dumps(record) + "\n")
if args == ["api", "user"]:
    if mode == "unauthenticated":
        print("gh: Bad credentials (HTTP 401)", file=sys.stderr); sys.exit(1)
    print(json.dumps({"login": "fixture-user"}))
elif args == ["api", "repos/pyrrhuladev/GlimHUD"]:
    print(json.dumps({"full_name": "pyrrhuladev/GlimHUD", "permissions": {"admin": mode != "nonadmin"}}))
elif len(args) >= 2 and "/contents/" in args[1]:
    if "update-public-key.txt" in args[1]:
        text = "different-key" if mode == "wrong-key" else os.environ["GLIMHUD_SETUP_FIXTURE_PUBLIC"]
    else:
        text = "<Project><PropertyGroup><Version>" + os.environ["GLIMHUD_SETUP_FIXTURE_VERSION"] + "</Version></PropertyGroup></Project>"
    print(json.dumps({"type": "file", "encoding": "base64", "content": base64.b64encode(text.encode()).decode()}))
elif args[:2] == ["api", "repos/pyrrhuladev/GlimHUD/pages"] and "--include" in args:
    if mode == "pages-denied":
        print("HTTP/2.0 403 Forbidden", flush=True)
        print("gh: Resource not accessible by integration (HTTP 403)", file=sys.stderr); sys.exit(1)
    if mode != "existing-pages":
        print('HTTP/2.0 404 Not Found\n\n{"message":"Not Found"}', flush=True)
        print("gh: Not Found (HTTP 404)", file=sys.stderr); sys.exit(1)
    print('HTTP/2.0 200 OK\n\n{"build_type":"legacy"}')
elif args[:2] == ["secret", "set"] and mode == "secret-write-failed":
    print("failed to fetch public key: HTTP 403: Resource not accessible by personal access token", file=sys.stderr)
    sys.exit(1)
elif "--method" in args and mode == "pages-write-failed":
    print("gh: Must have admin rights to Repository (HTTP 403)", file=sys.stderr)
    sys.exit(1)
elif args[:2] == ["workflow", "run"] and mode == "dispatch-failed":
    print("could not create workflow dispatch event: HTTP 422: Workflow does not have workflow_dispatch trigger", file=sys.stderr)
    sys.exit(1)
else:
    print("{}")
'''


@unittest.skipUnless(os.name == "nt", "The prepared publishing helper runs on Windows.")
class PublishingSetupChecks(unittest.TestCase):
    shell_name = "pwsh"

    def setUp(self) -> None:
        self.shell = shutil.which(self.shell_name)
        if not self.shell:
            self.skipTest(f"{self.shell_name} is unavailable.")
        temporary = tempfile.TemporaryDirectory(prefix="GlimHUD-setup-fixture-")
        self.addCleanup(temporary.cleanup)
        self.folder = Path(temporary.name)
        self.log = self.folder / "calls.jsonl"
        self.keys = self.folder / "keys"
        self.keys.mkdir()
        public = (ROOT / "src/GlimHUD/Assets/update-public-key.txt").read_text(encoding="utf-8").strip()
        (self.keys / "NetSparkle_Ed25519.pub").write_text(public, encoding="utf-8")
        (self.keys / "NetSparkle_Ed25519.priv").write_text(base64.b64encode(bytes(32)).decode(), encoding="utf-8")
        mock = self.folder / "fake_gh.py"
        mock.write_text(MOCK_CLI, encoding="utf-8")
        self.launcher = self.folder / "gh-fixture.cmd"
        self.launcher.write_text(f'@echo off\n"{sys.executable}" "{mock}" %*\n', encoding="utf-8")
        self.environment = os.environ.copy()
        self.environment.update({
            "GLIMHUD_SETUP_FIXTURE_LOG": str(self.log),
            "GLIMHUD_SETUP_FIXTURE_PUBLIC": public,
            "GLIMHUD_SETUP_FIXTURE_VERSION": ET.parse(ROOT / "src/GlimHUD/GlimHUD.csproj").findtext("./PropertyGroup/Version"),
            "GH_CONFIG_DIR": str(self.folder / "empty-cli-config"),
        })

    def run_helper(self, mode: str, *, what_if: bool = False) -> tuple[subprocess.CompletedProcess[str], list[dict]]:
        self.environment["GLIMHUD_SETUP_FIXTURE_MODE"] = mode
        def ps_string(value: str | Path) -> str:
            return "'" + str(value).replace("'", "''") + "'"

        # Reproduce hosted WinPS5's UTF-8 BOM setting. The helper must use no-BOM
        # stdin for secrets while restoring the caller's original encoding object.
        invoke = (f"& {ps_string(ROOT / 'scripts/Enable-Publishing.ps1')} "
                  f"-GitHubCliPath {ps_string(self.launcher)} -KeyDirectory {ps_string(self.keys)} "
                  "-StartRelease" + (" -WhatIf" if what_if else ""))
        wrapper = ("$OutputEncoding = [Text.UTF8Encoding]::new($true); "
                   "$originalEncoding = $OutputEncoding; $fixtureExit = 0; "
                   f"try {{ {invoke} }} "
                   "catch { [Console]::Error.WriteLine($_.ToString()); $fixtureExit = 1 } "
                   "finally { if (-not [object]::ReferenceEquals($originalEncoding, $OutputEncoding)) { "
                   "[Console]::Error.WriteLine('CALLER_OUTPUT_ENCODING_CHANGED'); $fixtureExit = 2 } }; "
                   "exit $fixtureExit")
        # Limit execution-policy relaxation to this isolated fixture subprocess.
        command = [self.shell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", wrapper]
        result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace",
                                env=self.environment, timeout=30, check=False)
        calls = [json.loads(line) for line in self.log.read_text(encoding="utf-8").splitlines()] if self.log.exists() else []
        self.assertNotIn(base64.b64encode(bytes(32)).decode(), result.stdout + result.stderr + json.dumps(calls))
        self.assertNotIn("CALLER_OUTPUT_ENCODING_CHANGED", result.stdout + result.stderr)
        return result, calls

    @staticmethod
    def mutations(calls: list[dict]) -> list[dict]:
        return [call for call in calls if call["args"][0] != "api" or "--method" in call["args"]]

    def test_new_pages_secret_stdin_and_release_dispatch(self) -> None:
        result, calls = self.run_helper("new-pages")
        self.assertEqual(result.returncode, 0, result.stderr)
        mutations = self.mutations(calls)
        self.assertEqual(len(mutations), 3)
        self.assertEqual(mutations[0]["args"][:2], ["secret", "set"])
        self.assertTrue(mutations[0]["stdin_matched"])
        self.assertFalse(mutations[0]["stdin_bom"])
        self.assertIn(mutations[0]["stdin_length"], (44, 45, 46))
        self.assertIn("POST", mutations[1]["args"])
        self.assertEqual(mutations[2]["args"][:3], ["workflow", "run", "release.yml"])

    def test_existing_pages_uses_put(self) -> None:
        result, calls = self.run_helper("existing-pages")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("PUT", self.mutations(calls)[1]["args"])

    def test_auth_admin_remote_key_and_pages_denials_never_mutate(self) -> None:
        for mode in ("unauthenticated", "nonadmin", "wrong-key", "pages-denied"):
            with self.subTest(mode=mode):
                self.log.unlink(missing_ok=True)
                result, calls = self.run_helper(mode)
                self.assertNotEqual(result.returncode, 0)
                self.assertGreater(len(calls), 0, result.stderr)
                self.assertEqual(calls[0]["args"], ["api", "user"])
                self.assertEqual(self.mutations(calls), [])

    def test_whatif_only_reads(self) -> None:
        result, calls = self.run_helper("new-pages", what_if=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.mutations(calls), [])

    def test_write_failures_stop_subsequent_mutations(self) -> None:
        for mode, expected_count in (("secret-write-failed", 1), ("pages-write-failed", 2), ("dispatch-failed", 3)):
            with self.subTest(mode=mode):
                self.log.unlink(missing_ok=True)
                result, calls = self.run_helper(mode)
                self.assertNotEqual(result.returncode, 0)
                mutations = self.mutations(calls)
                self.assertEqual(len(mutations), expected_count)
                self.assertEqual(mutations[0]["args"][:2], ["secret", "set"])
                self.assertTrue(mutations[0]["stdin_matched"])
                self.assertFalse(mutations[0]["stdin_bom"])
                if expected_count > 1:
                    self.assertIn("POST", mutations[1]["args"])
                if expected_count > 2:
                    self.assertEqual(mutations[2]["args"][:3], ["workflow", "run", "release.yml"])


class WindowsPowerShell51PublishingSetupChecks(PublishingSetupChecks):
    """Windows PowerShell turns native stderr into errors unlike PowerShell 7."""

    shell_name = "powershell"


if __name__ == "__main__":
    unittest.main()
