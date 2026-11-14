"""Exercise the real feed publisher with official NetSparkle signatures and temporary keys."""
from __future__ import annotations

import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
SPARKLE = "{http://www.andymatuschak.org/xml-namespaces/sparkle}"


class UpdateFeedChecks(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.pwsh = shutil.which("pwsh")
        cls.tool = ROOT / ".tools/netsparkle-appcast/netsparkle-generate-appcast.exe"
        if not cls.pwsh or not cls.tool.is_file():
            raise unittest.SkipTest("Install the pinned NetSparkle appcast tool to run signing integration checks.")
        cls.environment = os.environ.copy()
        cls.environment["DOTNET_ROLL_FORWARD"] = "LatestMajor"
        if (ROOT / ".tools/dotnet/dotnet.exe").is_file():
            cls.environment["DOTNET_ROOT"] = str(ROOT / ".tools/dotnet")
        for key in ("SPARKLE_PRIVATE_KEY", "SPARKLE_PUBLIC_KEY", "NETSPARKLE_PRIVATE_KEY"):
            cls.environment.pop(key, None)

    def setUp(self) -> None:
        temporary = tempfile.TemporaryDirectory(prefix="GlimHUD-update-feed-")
        self.addCleanup(temporary.cleanup)
        self.folder = Path(temporary.name)
        self.keys = self.folder / "keys"
        self.keys.mkdir()
        result = self.cli("--generate-keys", "--key-path", str(self.keys))
        self.assertEqual(result.returncode, 0, "Temporary signing-key generation failed.")
        self.public_key = self.keys / "NetSparkle_Ed25519.pub"
        self.installer = self.folder / "GlimHUD-1.2.3-Setup-x64.exe"
        self.installer.write_bytes(b"MZ\0Fixture signed payload\r\n" * 4)
        self.output = self.folder / "published"

    def cli(self, *arguments: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run([str(self.tool), *arguments], capture_output=True, text=True,
                              env=self.environment, timeout=30, check=False)

    def publish(self, *, key_directory: bool = True) -> subprocess.CompletedProcess[str]:
        command = [self.pwsh, "-NoProfile", "-File", str(ROOT / "scripts/Build-UpdateFeed.ps1"),
                   "-InstallerPath", str(self.installer), "-Version", "1.2.3",
                   "-OutputDirectory", str(self.output), "-PublicKeyPath", str(self.public_key),
                   "-AppCastToolPath", str(self.tool), "-PublishedAt", "2026-11-14T12:00:00Z"]
        if key_directory:
            command += ["-KeyDirectory", str(self.keys)]
        return subprocess.run(command, capture_output=True, text=True, env=self.environment,
                              timeout=45, check=False)

    def verifies(self, path: Path, signature: str) -> bool:
        # AppCastGenerator 2.9.0 exits zero even for rejected signatures.
        result = self.cli("--verify", str(path), "--signature", signature,
                          "--key-path", str(self.keys))
        return result.returncode == 0 and result.stdout.strip() == "Signature valid"

    def test_feed_and_installer_are_signed_and_tampering_is_rejected(self) -> None:
        result = self.publish()
        self.assertEqual(result.returncode, 0, result.stderr)
        feed = self.output / "appcast.xml"
        signature = (self.output / "appcast.xml.signature").read_text(encoding="utf-8")
        self.assertTrue(self.verifies(feed, signature))
        enclosure = ET.parse(feed).find("./channel/item/enclosure")
        self.assertIsNotNone(enclosure)
        self.assertEqual(enclosure.attrib[SPARKLE + "version"], "1.2.3")
        self.assertEqual(enclosure.attrib["url"],
                         "https://github.com/pyrrhuladev/GlimHUD/releases/download/v1.2.3/GlimHUD-1.2.3-Setup-x64.exe")
        self.assertEqual(int(enclosure.attrib["length"]), self.installer.stat().st_size)
        self.assertTrue(self.verifies(self.installer, enclosure.attrib[SPARKLE + "signature"]))
        feed.write_bytes(feed.read_bytes() + b"\n<!-- changed -->")
        self.installer.write_bytes(self.installer.read_bytes() + b"changed")
        self.assertFalse(self.verifies(feed, signature))
        self.assertFalse(self.verifies(self.installer, enclosure.attrib[SPARKLE + "signature"]))

    def test_wrong_pinned_key_prevents_publication(self) -> None:
        other_keys = self.folder / "other-keys"
        other_keys.mkdir()
        self.cli("--generate-keys", "--key-path", str(other_keys))
        self.public_key = other_keys / "NetSparkle_Ed25519.pub"
        result = self.publish()
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.output / "appcast.xml").exists())
        self.assertFalse((self.output / "appcast.xml.signature").exists())

    def test_no_private_key_fails_closed(self) -> None:
        result = self.publish(key_directory=False)
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.output / "appcast.xml").exists())

    def test_existing_metadata_cannot_be_replaced(self) -> None:
        result = self.publish()
        self.assertEqual(result.returncode, 0, result.stderr)
        feed = self.output / "appcast.xml"
        original = feed.read_bytes()
        self.assertNotEqual(self.publish().returncode, 0)
        self.assertEqual(feed.read_bytes(), original)


if __name__ == "__main__":
    unittest.main()
