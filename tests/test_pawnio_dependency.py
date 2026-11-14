"""Exercise PawnIO verification with inert bytes; never fetch or run an installer."""
from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "Get-PawnIO.ps1"
SIZE = 3_410_960
SHA256 = "1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032"
SIGNER = "F380DCC9F706E2756A5047B832FFE719E1BC35F5"
SOURCE = "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe"


def ps_quote(value: object) -> str:
    return "'" + str(value).replace("'", "''") + "'"


@unittest.skipUnless(os.name == "nt", "Windows PowerShell dependency-verification fixtures")
class PawnIODependencyChecks(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        candidates = list(dict.fromkeys(
            str(Path(path).resolve())
            for name in ("powershell.exe", "pwsh.exe")
            if (path := shutil.which(name))
        ))
        cls.shells: list[str] = []
        cls.blocked_shells: list[str] = []
        # Respect the host's script execution policy. These are inert local test
        # scripts; no ExecutionPolicy override or machine setting is used.
        with tempfile.TemporaryDirectory(prefix="GlimHUD-PawnIO-engine-") as temporary:
            probe = Path(temporary) / "probe.ps1"
            probe.write_text("Write-Output 'PawnIO fixture runner ready'", encoding="utf-8-sig")
            for shell in candidates:
                result = subprocess.run(
                    [shell, "-NoProfile", "-NonInteractive", "-File", str(probe)],
                    capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=10,
                )
                if result.returncode == 0 and "PawnIO fixture runner ready" in result.stdout:
                    cls.shells.append(shell)
                elif "AuthorizationManager" in result.stderr or "UnauthorizedAccess" in result.stderr:
                    cls.blocked_shells.append(shell)
                else:
                    raise RuntimeError("PowerShell fixture runner failed: " + result.stdout + result.stderr)
        if not cls.shells:
            raise unittest.SkipTest("PowerShell unavailable")

    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="GlimHUD-PawnIO-check-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

    def fixture(self, name: str) -> Path:
        root = self.root / name
        (root / "scripts").mkdir(parents=True)
        # Only the authoring script is copied. The repository's genuine signed
        # installer is never copied or made available to the fixture process.
        shutil.copyfile(SCRIPT, root / "scripts" / SCRIPT.name)
        return root

    @staticmethod
    def destination(root: Path) -> Path:
        return root / "tools" / "PawnIO"

    def seed(self, root: Path, *, size: int = SIZE, provenance: bool = True) -> None:
        destination = self.destination(root)
        destination.mkdir(parents=True, exist_ok=True)
        # Sparse inert data cannot execute as a PE program, even if a future
        # regression accidentally attempts to invoke it outside our cmdlet mocks.
        with (destination / "PawnIO_setup.exe").open("wb") as stream:
            stream.truncate(size)
        if provenance:
            self.write_provenance(root, {
                "project": "namazso/PawnIO.Setup", "version": "2.2.0",
                "source": SOURCE, "sha256": SHA256, "size": SIZE,
                "signerThumbprint": SIGNER, "signer": "namazso.eu",
            })

    def write_provenance(self, root: Path, value: dict[str, object]) -> None:
        path = self.destination(root) / "provenance.json"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(value), encoding="utf-8")

    def run_fixture(self, root: Path, shell: str, *, mode: str = "valid", verify_only: bool = False) -> dict[str, object]:
        result_path = root / "fixture-result.json"
        harness = root / "fixture.ps1"
        harness.write_text(
            "$ErrorActionPreference = 'Stop'\n"
            f"$global:GlimHudPawnMode = {ps_quote(mode)}\n"
            "$global:GlimHudPawnEvents = New-Object 'System.Collections.Generic.List[string]'\n"
            f"$global:GlimHudPawnHash = {ps_quote(SHA256)}\n"
            f"$global:GlimHudPawnSigner = {ps_quote(SIGNER)}\n"
            f"$global:GlimHudPawnSource = {ps_quote(SOURCE)}\n"
            "function Invoke-WebRequest {\n"
            "  param([string]$Uri, [string]$OutFile, [int]$TimeoutSec)\n"
            "  $global:GlimHudPawnEvents.Add('download')\n"
            "  if ($Uri -cne $global:GlimHudPawnSource) { throw 'Unexpected download source.' }\n"
            f"  $length = {SIZE}\n"
            "  if ($global:GlimHudPawnMode -eq 'wrong-size') { $length = 17 }\n"
            "  if ($global:GlimHudPawnMode -eq 'download-failure') { $length = 123 }\n"
            "  $stream = [IO.File]::Open($OutFile, [IO.FileMode]::Create, [IO.FileAccess]::Write)\n"
            "  try { $stream.SetLength($length) } finally { $stream.Dispose() }\n"
            "  if ($global:GlimHudPawnMode -eq 'download-failure') { throw 'Owned fixture download failed.' }\n"
            "}\n"
            "function Get-FileHash {\n"
            "  param([string]$LiteralPath, [string]$Algorithm)\n"
            "  $global:GlimHudPawnEvents.Add('hash')\n"
            "  if ($Algorithm -cne 'SHA256') { throw 'Unexpected hash algorithm.' }\n"
            "  if ($global:GlimHudPawnMode -eq 'real-wrong-hash') {\n"
            "    $pawnFixtureStream = [IO.File]::OpenRead($LiteralPath)\n"
            "    $pawnFixtureHasher = [Security.Cryptography.SHA256]::Create()\n"
            "    try {\n"
            "      $computed = $pawnFixtureHasher.ComputeHash($pawnFixtureStream)\n"
            "      return [pscustomobject]@{ Hash = ([BitConverter]::ToString($computed)).Replace('-', '') }\n"
            "    } finally { $pawnFixtureHasher.Dispose(); $pawnFixtureStream.Dispose() }\n"
            "  }\n"
            "  $hash = $global:GlimHudPawnHash.ToUpperInvariant()\n"
            "  if ($global:GlimHudPawnMode -eq 'wrong-hash') { $hash = '0' * 64 }\n"
            "  return [pscustomobject]@{ Hash = $hash }\n"
            "}\n"
            "function Get-AuthenticodeSignature {\n"
            "  param([string]$LiteralPath)\n"
            "  $global:GlimHudPawnEvents.Add('signature')\n"
            "  $thumb = $global:GlimHudPawnSigner.ToLowerInvariant()\n"
            "  $status = 'Valid'\n"
            "  if ($global:GlimHudPawnMode -eq 'wrong-signer') { $thumb = '0' * 40 }\n"
            "  if ($global:GlimHudPawnMode -eq 'invalid-signature') { $status = 'HashMismatch' }\n"
            "  return [pscustomobject]@{ Status = $status; SignerCertificate = [pscustomobject]@{ Thumbprint = $thumb } }\n"
            "}\n"
            "function Start-Process {\n"
            "  param([Parameter(ValueFromRemainingArguments=$true)][object[]]$Arguments)\n"
            "  $global:GlimHudPawnEvents.Add('EXECUTION-DENIED')\n"
            "  throw 'Installer execution is forbidden in dependency fixtures.'\n"
            "}\n"
            "function Invoke-Item {\n"
            "  param([Parameter(ValueFromRemainingArguments=$true)][object[]]$Arguments)\n"
            "  $global:GlimHudPawnEvents.Add('EXECUTION-DENIED')\n"
            "  throw 'Installer execution is forbidden in dependency fixtures.'\n"
            "}\n"
            "$exitCode = 0\n"
            "$errorMessage = $null\n"
            "try {\n"
            f"  & {ps_quote(root / 'scripts' / SCRIPT.name)} -DestinationDirectory {ps_quote(self.destination(root))}"
            + (" -VerifyOnly\n" if verify_only else "\n")
            + "} catch { $exitCode = 1; $errorMessage = $_.Exception.Message }\n"
            f"$resultPath = {ps_quote(result_path)}\n"
            "[ordered]@{ ExitCode = $exitCode; Error = $errorMessage; Events = @($global:GlimHudPawnEvents.ToArray()) } |\n"
            "  ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $resultPath -Encoding UTF8\n"
            "exit $exitCode\n",
            encoding="utf-8-sig",
        )
        process = subprocess.run(
            [shell, "-NoProfile", "-NonInteractive", "-File", str(harness)],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=20,
        )
        self.assertTrue(result_path.is_file(), process.stdout + process.stderr)
        result = json.loads(result_path.read_text(encoding="utf-8-sig"))
        self.assertEqual(process.returncode, result["ExitCode"], process.stdout + process.stderr)
        self.assertNotIn("EXECUTION-DENIED", result["Events"])
        return result

    def assert_rejected(self, result: dict[str, object], message: str) -> None:
        self.assertNotEqual(result["ExitCode"], 0)
        self.assertIn(message, str(result["Error"]))

    def test_verified_download_and_verify_only_round_trip(self) -> None:
        for index, shell in enumerate(self.shells):
            with self.subTest(shell=shell):
                root = self.fixture(f"positive-{index}")
                fetched = self.run_fixture(root, shell)
                self.assertEqual(fetched["ExitCode"], 0, fetched)
                self.assertEqual(fetched["Events"], ["download", "hash", "signature"])
                destination = self.destination(root)
                self.assertEqual((destination / "PawnIO_setup.exe").stat().st_size, SIZE)
                provenance = json.loads((destination / "provenance.json").read_text(encoding="utf-8-sig"))
                for field, expected in {"version": "2.2.0", "source": SOURCE, "sha256": SHA256,
                                        "size": SIZE, "signerThumbprint": SIGNER}.items():
                    self.assertEqual(provenance[field], expected)
                self.assertFalse((destination / "PawnIO_setup.exe.download").exists())
                verified = self.run_fixture(root, shell, verify_only=True)
                self.assertEqual(verified["ExitCode"], 0, verified)
                self.assertEqual(verified["Events"], ["hash", "signature"])

    def test_other_installed_engines_when_execution_policy_permits(self) -> None:
        if self.blocked_shells:
            self.skipTest("Script execution policy blocks: " + ", ".join(self.blocked_shells))
        self.assertTrue(self.shells)

    def test_verify_only_rejects_wrong_payload_size_before_hash_or_signature(self) -> None:
        for index, shell in enumerate(self.shells):
            with self.subTest(shell=shell):
                root = self.fixture(f"size-{index}")
                self.seed(root, size=SIZE - 1)
                result = self.run_fixture(root, shell, verify_only=True)
                self.assert_rejected(result, "complete official PawnIO")
                self.assertEqual(result["Events"], [])

    def test_verify_only_rejects_a_real_hash_mismatch(self) -> None:
        for index, shell in enumerate(self.shells):
            with self.subTest(shell=shell):
                root = self.fixture(f"hash-{index}")
                self.seed(root)
                result = self.run_fixture(root, shell, mode="real-wrong-hash", verify_only=True)
                self.assert_rejected(result, "SHA256 verification failed")
                self.assertEqual(result["Events"], ["hash"])

    def test_verify_only_rejects_wrong_publisher_or_invalid_signature(self) -> None:
        for index, shell in enumerate(self.shells):
            for mode in ("wrong-signer", "invalid-signature"):
                with self.subTest(shell=shell, mode=mode):
                    root = self.fixture(f"signature-{index}-{mode}")
                    self.seed(root)
                    result = self.run_fixture(root, shell, mode=mode, verify_only=True)
                    self.assert_rejected(result, "Authenticode signature or pinned publisher")
                    self.assertEqual(result["Events"], ["hash", "signature"])

    def test_verify_only_rejects_tampered_provenance_fields(self) -> None:
        alterations = {"version": "9.9.9", "sha256": "0" * 64, "source": "https://invalid.example/other.exe",
                       "signerThumbprint": "0" * 40, "size": SIZE + 1}
        for index, shell in enumerate(self.shells):
            for field, changed in alterations.items():
                with self.subTest(shell=shell, field=field):
                    root = self.fixture(f"provenance-{index}-{field}")
                    self.seed(root)
                    path = self.destination(root) / "provenance.json"
                    provenance = json.loads(path.read_text(encoding="utf-8-sig"))
                    provenance[field] = changed
                    self.write_provenance(root, provenance)
                    self.assert_rejected(self.run_fixture(root, shell, verify_only=True), "provenance does not match")

    def test_verify_only_never_repairs_missing_or_malformed_provenance(self) -> None:
        for index, shell in enumerate(self.shells):
            for malformed in (False, True):
                with self.subTest(shell=shell, malformed=malformed):
                    root = self.fixture(f"absent-provenance-{index}-{malformed}")
                    self.seed(root, provenance=False)
                    path = self.destination(root) / "provenance.json"
                    if malformed:
                        path.write_text("{not-json", encoding="utf-8")
                    result = self.run_fixture(root, shell, verify_only=True)
                    self.assertNotEqual(result["ExitCode"], 0)
                    self.assertNotIn("download", result["Events"])
                    self.assertEqual(path.read_text(encoding="utf-8") if malformed else path.exists(), "{not-json" if malformed else False)

    def test_failed_download_or_verification_leaves_no_final_or_partial_payload(self) -> None:
        for index, shell in enumerate(self.shells):
            for mode in ("download-failure", "wrong-size", "wrong-hash", "wrong-signer", "invalid-signature"):
                with self.subTest(shell=shell, mode=mode):
                    root = self.fixture(f"download-{index}-{mode}")
                    result = self.run_fixture(root, shell, mode=mode)
                    self.assertNotEqual(result["ExitCode"], 0)
                    self.assertIn("download", result["Events"])
                    destination = self.destination(root)
                    self.assertFalse((destination / "PawnIO_setup.exe").exists())
                    self.assertFalse((destination / "provenance.json").exists())
                    self.assertFalse(list(destination.glob("*.download")))

    def test_invalid_cached_installer_is_rejected_without_redownload_or_relabeling(self) -> None:
        for index, shell in enumerate(self.shells):
            with self.subTest(shell=shell):
                root = self.fixture(f"bad-cache-{index}")
                self.seed(root, size=17, provenance=False)
                result = self.run_fixture(root, shell)
                self.assert_rejected(result, "complete official PawnIO")
                self.assertEqual(result["Events"], [])
                self.assertEqual((self.destination(root) / "PawnIO_setup.exe").stat().st_size, 17)
                self.assertFalse((self.destination(root) / "provenance.json").exists())

    def test_verify_only_missing_payload_does_not_create_destination(self) -> None:
        for index, shell in enumerate(self.shells):
            with self.subTest(shell=shell):
                root = self.fixture(f"verify-absent-{index}")
                result = self.run_fixture(root, shell, verify_only=True)
                self.assert_rejected(result, "complete official PawnIO")
                self.assertEqual(result["Events"], [])
                self.assertFalse(self.destination(root).exists())


@unittest.skipUnless(os.name == "nt", "Windows PowerShell syntax compatibility")
class PawnIOWindowsPowerShellSyntaxChecks(unittest.TestCase):
    def test_windows_powershell5_can_parse_the_authoring_script(self) -> None:
        shell = shutil.which("powershell.exe")
        if not shell:
            self.skipTest("Windows PowerShell 5.1 unavailable")
        command = (
            "$tokens=$null; $parseErrors=$null; "
            f"[void][System.Management.Automation.Language.Parser]::ParseFile({ps_quote(SCRIPT)}, "
            "[ref]$tokens, [ref]$parseErrors); "
            "if($parseErrors.Count -gt 0){ $parseErrors | ForEach-Object { $_.Message }; exit 1 }; "
            "Write-Output 'Windows PowerShell syntax OK'"
        )
        result = subprocess.run(
            [shell, "-NoProfile", "-NonInteractive", "-Command", command],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=10,
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("Windows PowerShell syntax OK", result.stdout)


if __name__ == "__main__":
    unittest.main()
