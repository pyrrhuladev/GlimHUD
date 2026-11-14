"""Package the portable build with the complete editable project; no SDK/cache/logs."""
from __future__ import annotations

import argparse
import hashlib
import json
import xml.etree.ElementTree as ET
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

SOURCE_DIRECTORIES = ("src", "tests", "scripts", "tools", "Notices", "docs", "packaging", "site", ".github")
ROOT_FILES = (
    "LICENSE", "README.md", "CHANGELOG.md", "THIRD-PARTY-NOTICES.md", "CONTRIBUTING.md", "SECURITY.md",
    "GlimHUD.slnx", "global.json", ".gitignore", ".editorconfig",
)
REQUIRED_NOTICES = (
    "Notices/LibreHardwareMonitor.txt", "Notices/NAudio.txt", "Notices/HidSharp.txt",
    "Notices/BlackSharp.Core.txt", "Notices/DiskInfoToolkit.txt", "Notices/RAMSPDToolkit-NDD.txt",
    "Notices/Mono.Posix.NETStandard.txt", "Notices/Microsoft.NET.txt", "Notices/Microsoft.NET-ThirdParty.txt",
    "Notices/NetSparkle.txt", "Notices/NetSparkle.Chaos.NaCl.txt",
    "Notices/PawnIO.Modules.txt", "Notices/PawnIO.Modules.LGPL-2.1.txt", "Notices/PawnIO.Modules.Upstream-README.txt",
    "Notices/PawnIO.Setup.txt",
)
PAWNIO_SETUP_SHA256 = "1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032"
EXCLUDED_DIRECTORIES = {
    "bin", "obj", ".git", ".codex", ".tools", ".packages", "__pycache__", ".vs",
    "artifacts", "node_modules", "nvidia corporation",
}
EXCLUDED_EXTENSIONS = {
    ".pdb", ".pyc", ".pyo", ".log", ".dmp", ".pfx", ".p12", ".key", ".priv", ".cer", ".crt",
    ".pem", ".msix", ".msixbundle", ".msixupload", ".user", ".suo", ".tmp", ".download",
}


def allowed(relative_path: Path) -> bool:
    """Apply the same secret/build exclusions to source and published files."""
    if EXCLUDED_DIRECTORIES.intersection(part.casefold() for part in relative_path.parts):
        return False
    name = relative_path.name.casefold()
    if name in {"id_rsa", "id_dsa", "id_ecdsa", "id_ed25519"}:
        return False
    if name == ".env" or (name.startswith(".env.") and name != ".env.example"):
        return False
    return relative_path.suffix.casefold() not in EXCLUDED_EXTENSIONS


def require_file(path: Path) -> None:
    if not path.is_file() or path.stat().st_size == 0:
        raise ValueError(f"Required release file is missing or empty: {path}")


def require_notices(folder: Path) -> None:
    for name in ("LICENSE", "THIRD-PARTY-NOTICES.md", *REQUIRED_NOTICES):
        require_file(folder / name)
    for executable in (folder / "tools/PresentMon").glob("PresentMon*.exe"):
        if "service" not in executable.name.casefold():
            require_file(executable.parent / "LICENSE.txt")
            require_file(executable.parent / "provenance.json")
    pawnio = folder / "tools/PawnIO/PawnIO_setup.exe"
    require_file(pawnio)
    require_file(pawnio.parent / "provenance.json")
    if hashlib.sha256(pawnio.read_bytes()).hexdigest() != PAWNIO_SETUP_SHA256:
        raise ValueError("Bundled PawnIO setup does not match the pinned official 2.2.0 binary.")


def collect_entries(root: Path, binary: Path) -> list[tuple[Path, str]]:
    require_file(binary / "GlimHUD.exe")
    require_notices(root)
    require_notices(binary)
    entries: list[tuple[Path, str]] = []
    for path in sorted(binary.rglob("*")):
        if (path.is_file() and not path.is_symlink() and path.resolve().is_relative_to(binary.resolve())
                and allowed(path.relative_to(binary))):
            entries.append((path, path.relative_to(binary).as_posix()))
    for name in SOURCE_DIRECTORIES:
        for path in sorted((root / name).rglob("*")):
            relative_path = path.relative_to(root)
            if (path.is_file() and not path.is_symlink() and path.resolve().is_relative_to(root.resolve())
                    and allowed(relative_path)):
                entries.append((path, "Source/" + relative_path.as_posix()))
    for name in ROOT_FILES:
        require_file(root / name)
        entries.append((root / name, "Source/" + name))
    preview = root / "artifacts/preview/GlimHUD.backgrounds.png"
    if preview.is_file():
        entries.append((preview, "Design-Preview.png"))
    return entries


def create_archive(root: Path, binary: Path, output: Path, version: str) -> int:
    if output.resolve().is_relative_to(binary.resolve()):
        raise ValueError("The release archive must be outside the published binary directory.")
    entries = collect_entries(root, binary)
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_suffix(".zip.tmp")
    with ZipFile(temporary, "w", ZIP_DEFLATED, compresslevel=9) as archive:
        for path, name in entries:
            archive.write(path, name)
        archive.comment = f"GlimHUD {version} - Windows x64 portable app and editable source. See README.md.".encode("ascii")
    temporary.replace(output)
    with ZipFile(output) as archive:
        error = archive.testzip()
        if error:
            raise ValueError("Archive verification failed: " + error)
    return len(entries)


def main() -> None:
    root = Path(__file__).resolve().parents[1]
    version = ET.parse(root / "src/GlimHUD/GlimHUD.csproj").findtext("PropertyGroup/Version", "0.1.0")
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", type=Path, default=root / "artifacts/GlimHUD-win-x64")
    parser.add_argument("--output", type=Path, default=root / f"artifacts/GlimHUD-{version}.zip")
    args = parser.parse_args()
    binary = args.binary.resolve()
    output = args.output.resolve()
    try:
        files = create_archive(root, binary, output, version)
    except ValueError as error:
        raise SystemExit(str(error)) from error
    print(json.dumps({"path": str(output), "files": files, "bytes": output.stat().st_size}))


if __name__ == "__main__":
    main()
