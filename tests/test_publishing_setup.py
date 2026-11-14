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
    record["stdin_matched"] = sys.stdin.read().strip() == base64.b64encode(bytes(32)).decode()
with open(os.environ["GLIMHUD_SETUP_FIXTURE_LOG"], "a", encoding="utf-8") as log:
    log.write(json.dumps(record) + "\n")
if args == ["api", "user"]:
    if mode == "unauthenticated": sys.exit(1)
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
        print("HTTP/2.0 403 Forbidden"); sys.exit(1)
    if mode != "existing-pages":
        print("HTTP/2.0 404 Not Found"); sys.exit(1)
    print('HTTP/2.0 200 OK\n\n{"build_type":"legacy"}')
else:
    print("{}")
'''


@unittest.skipUnless(os.name == "nt", "The prepared publishing helper runs on Windows.")
class PublishingSetupChecks(unittest.TestCase):
    def setUp(self) -> None:
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
        command = [shutil.which("pwsh"), "-NoProfile", "-File", str(ROOT / "scripts/Enable-Publishing.ps1"),
                   "-GitHubCliPath", str(self.launcher), "-KeyDirectory", str(self.keys), "-StartRelease"]
        if what_if:
            command.append("-WhatIf")
        result = subprocess.run(command, capture_output=True, text=True, env=self.environment, timeout=30, check=False)
        calls = [json.loads(line) for line in self.log.read_text(encoding="utf-8").splitlines()] if self.log.exists() else []
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
        self.assertIn("POST", mutations[1]["args"])
        self.assertEqual(mutations[2]["args"][:3], ["workflow", "run", "release.yml"])
        self.assertNotIn(base64.b64encode(bytes(32)).decode(), result.stdout + result.stderr + json.dumps(calls))

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
                self.assertEqual(self.mutations(calls), [])

    def test_whatif_only_reads(self) -> None:
        result, calls = self.run_helper("new-pages", what_if=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.mutations(calls), [])


if __name__ == "__main__":
    unittest.main()
