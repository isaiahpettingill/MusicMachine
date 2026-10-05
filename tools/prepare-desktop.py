"""Stage documented, versioned native payloads and the pinned official SDL2 DLL."""
import hashlib
import io
import json
import os
from pathlib import Path
import re
import shutil
import sys
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parent.parent
SDL_URL = "https://github.com/libsdl-org/SDL/releases/download/release-2.32.10/SDL2-2.32.10-win32-x64.zip"
SDL_SHA256 = "6cf9706eefd0a4a06dc764007934d428afaf029fabdd408a9e646048c91e18fb"


def prepare(payload: Path, runtime: str):
    if runtime not in ("win-x64", "linux-x64"):
        raise ValueError("Only Windows x64 and Linux x64 are published")
    version = os.environ["MUSIC_RELEASE_VERSION"]
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise ValueError("Invalid release version")
    executable = "MusicMachine.Desktop" + (".exe" if runtime == "win-x64" else "")
    if not (payload / executable).is_file():
        raise ValueError("Missing published native executable")
    # NativeAOT creates external debug symbols by default; do not ship these.
    for p in payload.rglob("*"):
        if p.is_symlink():
            raise ValueError(f"Unexpected symlink: {p}")
        if p.is_file() and p.suffix.lower() in (".pdb", ".dbg", ".map"):
            p.unlink()
    for name in ("LICENSE", "README.md"):
        shutil.copy2(ROOT / name, payload)
    for name in ("docs", "licenses", "examples"):
        shutil.copytree(ROOT / name, payload / name, dirs_exist_ok=True)
    for name in ("musicmachine.svg", "musicmachine.ico"):
        shutil.copy2(ROOT / "assets/icon" / name, payload)
    if runtime == "win-x64":
        with urllib.request.urlopen(SDL_URL, timeout=120) as response:
            data = response.read()
        if hashlib.sha256(data).hexdigest() != SDL_SHA256:
            raise ValueError("Official SDL2 archive checksum mismatch")
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            matches = [name for name in archive.namelist() if name == "SDL2.dll" or name.endswith("/SDL2.dll")]
            if len(matches) != 1:
                raise ValueError("Expected one SDL2.dll in official x64 archive")
            (payload / "SDL2.dll").write_bytes(archive.read(matches[0]))
    metadata = {"schema": 1, "product": "MusicMachine", "version": version, "runtime": runtime,
                "commit": os.environ.get("GITHUB_SHA", ""), "executable": executable,
                "repository": os.environ.get("GITHUB_REPOSITORY", "isaiahpettingill/MusicMachine")}
    (payload / "release.json").write_text(json.dumps(metadata, indent=2) + "\n")
    total = sum(p.stat().st_size for p in payload.rglob("*") if p.is_file())
    if total > 160 * 1024 * 1024:
        raise ValueError("Desktop payload exceeds 160 MiB size budget")
    print(f"Prepared {runtime} {version}: {total / 1048576:.2f} MiB")


if __name__ == "__main__":
    prepare(Path(sys.argv[1]).resolve(), sys.argv[2])
