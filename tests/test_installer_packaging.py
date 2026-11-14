"""Safety contracts for per-user installers; these checks never install software."""
from __future__ import annotations

import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "packaging" / "installer" / "GlimHUD.iss"


def setup_values() -> dict[str, str]:
    section = ""
    result: dict[str, str] = {}
    for raw in SCRIPT.read_text(encoding="utf-8-sig").splitlines():
        line = raw.strip()
        if not line or line.startswith((";", "#")):
            continue
        if line.startswith("[") and line.endswith("]"):
            section = line.lower()
        elif section == "[setup]" and "=" in line:
            key, value = line.split("=", 1)
            result[key.lower()] = value
    return result


class InstallerSafetyContracts(unittest.TestCase):
    def test_installs_per_user_without_elevation_or_startup_changes(self) -> None:
        values = setup_values()
        self.assertEqual(values["privilegesrequired"], "lowest")
        self.assertNotIn("privilegesrequiredoverridesallowed", values)
        self.assertEqual(values["defaultdirname"], r"{localappdata}\Programs\GlimHUD")
        content = SCRIPT.read_text(encoding="utf-8-sig").lower()
        self.assertNotIn("[registry]", content)
        self.assertNotIn("{autostartup}", content)
        self.assertNotIn("{commonstartup}", content)

    def test_preserves_user_preferences_and_only_closes_hud(self) -> None:
        values = setup_values()
        self.assertEqual(values["closeapplications"], "yes")
        self.assertEqual(values["closeapplicationsfilter"], "GlimHUD.exe")
        self.assertEqual(values["restartapplications"], "no")
        self.assertEqual(values["appmutex"], r"Local\GlimHUD.Desktop")
        self.assertNotIn("[uninstalldelete]", SCRIPT.read_text(encoding="utf-8-sig").lower())

    def test_install_marker_and_silent_restart_require_explicit_request(self) -> None:
        marker = ROOT / "packaging" / "installer" / "installed.marker"
        self.assertEqual(marker.read_text(encoding="utf-8-sig").strip(), "GlimHUD installed v1")
        content = SCRIPT.read_text(encoding="utf-8-sig")
        self.assertIn("skipifsilent unchecked runasoriginaluser", content)
        self.assertIn("skipifnotsilent runasoriginaluser; Check: ShouldRestartAfterUpdate", content)
        self.assertIn("ExpandConstant('{param:GLIMHUDRESTART|0}') = '1'", content)


@unittest.skipUnless(os.name == "nt", "Windows PowerShell validation")
class InstallerInputChecks(unittest.TestCase):
    def setUp(self) -> None:
        self.shell = shutil.which("pwsh") or shutil.which("powershell")
        if not self.shell:
            self.skipTest("PowerShell unavailable")
        self.temporary = tempfile.TemporaryDirectory(prefix="GlimHUD-installer-check-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.input = self.root / "publish"
        self.input.mkdir()

    def run_build(self, output: Path) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [self.shell, "-NoProfile", "-NonInteractive", "-File",
             str(ROOT / "scripts" / "Build-Installer.ps1"),
             "-InputDirectory", str(self.input), "-OutputDirectory", str(output)],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30,
        )

    def test_rejects_output_inside_publish_without_mutation(self) -> None:
        output = self.input / "nested-output"
        result = self.run_build(output)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("outside InputDirectory", result.stdout + result.stderr)
        self.assertFalse(output.exists())

    def test_incomplete_publish_never_generates_an_installer(self) -> None:
        output = self.root / "installer"
        result = self.run_build(output)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("complete self-contained", result.stdout + result.stderr)
        self.assertFalse(output.exists())

    def test_private_update_key_blocks_packaging_before_compilation(self) -> None:
        (self.input / "NetSparkle_Ed25519.priv").write_text("private-key-fixture", encoding="utf-8")
        output = self.root / "installer"
        result = self.run_build(output)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("private key or environment material", result.stdout + result.stderr)
        self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()
