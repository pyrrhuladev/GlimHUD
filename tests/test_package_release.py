"""Release fixtures verify exported source completeness and private-file exclusions."""
from __future__ import annotations

import sys
import hashlib
import tempfile
import unittest
from pathlib import Path
from zipfile import ZipFile
from unittest.mock import patch

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from package_release import REQUIRED_NOTICES, ROOT_FILES, create_archive  # noqa: E402


class ReleaseArchiveChecks(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="GlimHUD-release-check-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name) / "source"
        self.binary = Path(self.temporary.name) / "published"
        self.output = Path(self.temporary.name) / "release.zip"
        for relative in ROOT_FILES:
            self.write(self.root, relative)
        for folder in (self.root, self.binary):
            for relative in ("LICENSE", "THIRD-PARTY-NOTICES.md", *REQUIRED_NOTICES):
                self.write(folder, relative)
        self.write(self.binary, "GlimHUD.exe")
        pawnio = b"Pinned official setup fixture - never executed."
        self.expected_pawnio = patch("package_release.PAWNIO_SETUP_SHA256", hashlib.sha256(pawnio).hexdigest())
        self.expected_pawnio.start()
        self.addCleanup(self.expected_pawnio.stop)
        for folder in (self.root, self.binary):
            setup = self.write(folder, "tools/PawnIO/PawnIO_setup.exe")
            setup.write_bytes(pawnio)
            self.write(folder, "tools/PawnIO/provenance.json")

    @staticmethod
    def write(folder: Path, relative: str) -> Path:
        path = folder / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("Release fixture: " + relative, encoding="utf-8")
        return path

    def archive_names(self) -> set[str]:
        create_archive(self.root, self.binary, self.output, "0.3.0")
        with ZipFile(self.output) as archive:
            self.assertIsNone(archive.testzip())
            return set(archive.namelist())

    def test_complete_editable_project_and_mit_license(self) -> None:
        extra_files = (
            "src/GlimHUD/Program.cs", "tests/Program.cs", "scripts/Build.ps1",
            "tools/README.md", "docs/privacy-policy.md", "packaging/AppxManifest.xml",
            ".github/workflows/ci.yml", "src/.env.example",
        )
        for relative in extra_files:
            self.write(self.root, relative)
        names = self.archive_names()
        for relative in (*ROOT_FILES, *extra_files, *REQUIRED_NOTICES):
            self.assertIn("Source/" + relative, names)
        self.assertIn("LICENSE", names)
        self.assertIn("GlimHUD.exe", names)

    def test_private_files_and_build_outputs_never_enter_archive(self) -> None:
        excluded = (
            "src/.env", "src/.env.production", "scripts/publisher.pfx", "scripts/cert.P12",
            "tools/private.key", "tools/public.cer", "tools/cert.pem", "tools/error.log",
            "tests/crash.dmp", "tests/__pycache__/fixture.pyc", "src/GlimHUD/obj/cache.json",
            "src/GlimHUD/bin/Debug/fixture.exe", "scripts/.codex/private.json",
            "tools/.git/config", "tools/NVIDIA Corporation/umdlogs/log.dat", "tools/staging.msix",
            "scripts/NetSparkle_Ed25519.priv", "tools/SIGNING.PRIV", "tools/id_ed25519", "tools/id_rsa",
        )
        for folder in (self.root, self.binary):
            for relative in excluded:
                self.write(folder, relative)
        names = self.archive_names()
        for relative in excluded:
            self.assertNotIn(relative, names)
            self.assertNotIn("Source/" + relative, names)

    def test_public_update_key_can_be_distributed(self) -> None:
        public_key = "src/GlimHUD/Assets/update-public-key.pub"
        for folder in (self.root, self.binary):
            self.write(folder, public_key)
        names = self.archive_names()
        self.assertIn(public_key, names)
        self.assertIn("Source/" + public_key, names)

    def test_missing_source_license_stops_release(self) -> None:
        (self.root / "LICENSE").unlink()
        with self.assertRaisesRegex(ValueError, "LICENSE"):
            self.archive_names()
        self.assertFalse(self.output.exists())

    def test_missing_update_crypto_license_stops_release(self) -> None:
        (self.binary / "Notices/NetSparkle.Chaos.NaCl.txt").unlink()
        with self.assertRaisesRegex(ValueError, "NetSparkle.Chaos.NaCl"):
            self.archive_names()
        self.assertFalse(self.output.exists())

    def test_missing_embedded_pawnio_notice_stops_release(self) -> None:
        for folder in (self.root, self.binary):
            for name in (
                "Notices/PawnIO.Modules.txt", "Notices/PawnIO.Modules.LGPL-2.1.txt",
                "Notices/PawnIO.Modules.Upstream-README.txt",
            ):
                with self.subTest(folder=folder.name, notice=name):
                    (folder / name).unlink()
                    with self.assertRaisesRegex(ValueError, "PawnIO.Modules"):
                        self.archive_names()
                    self.assertFalse(self.output.exists())
                    self.write(folder, name)

    def test_missing_driver_redistribution_notice_stops_release(self) -> None:
        (self.binary / "Notices/PawnIO.Setup.txt").unlink()
        with self.assertRaisesRegex(ValueError, "PawnIO.Setup"):
            self.archive_names()
        self.assertFalse(self.output.exists())

    def test_missing_or_changed_bundled_pawnio_setup_stops_release(self) -> None:
        installer = self.binary / "tools/PawnIO/PawnIO_setup.exe"
        installer.unlink()
        with self.assertRaisesRegex(ValueError, "PawnIO_setup"):
            self.archive_names()
        installer.write_bytes(b"Changed setup fixture")
        with self.assertRaisesRegex(ValueError, "pinned official"):
            self.archive_names()
        self.assertFalse(self.output.exists())

    def test_missing_published_notice_stops_release(self) -> None:
        (self.binary / REQUIRED_NOTICES[0]).unlink()
        with self.assertRaisesRegex(ValueError, "LibreHardwareMonitor"):
            self.archive_names()

    def test_versioned_presentmon_requires_its_notice_and_provenance(self) -> None:
        for folder in (self.root, self.binary):
            self.write(folder, "tools/PresentMon/PresentMon-2.6.0-x64.exe")
        with self.assertRaisesRegex(ValueError, "LICENSE.txt"):
            self.archive_names()
        for folder in (self.root, self.binary):
            self.write(folder, "tools/PresentMon/LICENSE.txt")
        with self.assertRaisesRegex(ValueError, "provenance.json"):
            self.archive_names()
        for folder in (self.root, self.binary):
            self.write(folder, "tools/PresentMon/provenance.json")
        names = self.archive_names()
        self.assertIn("tools/PresentMon/PresentMon-2.6.0-x64.exe", names)
        self.assertIn("Source/tools/PresentMon/LICENSE.txt", names)

    def test_output_inside_binary_directory_is_rejected(self) -> None:
        with self.assertRaisesRegex(ValueError, "outside"):
            create_archive(self.root, self.binary, self.binary / "release.zip", "0.3.0")


if __name__ == "__main__":
    unittest.main()
