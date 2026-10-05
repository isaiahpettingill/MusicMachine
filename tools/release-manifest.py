"""Verify the complete platform set and produce downloadable checksum metadata."""
import hashlib
import json
import os
from pathlib import Path
import sys

REQUIRED = ["MusicMachine-win-x64-setup.exe", "MusicMachine-win-x64.zip",
            "MusicMachine-linux-x64.tar.gz", "install-musicmachine.sh", "install-musicmachine.ps1",
            "MusicMachine-browser-wasm.zip", "MusicMachine-source.zip"]


def create(directory: Path):
    version = os.environ["MUSIC_RELEASE_VERSION"]
    repository = os.environ["GITHUB_REPOSITORY"]
    assets = []
    for name in REQUIRED:
        path = directory / name
        if not path.is_file() or path.stat().st_size == 0:
            raise ValueError(f"Missing release asset: {name}")
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        assets.append({"name": name, "size": path.stat().st_size, "sha256": digest,
                       "url": f"https://github.com/{repository}/releases/download/v{version}/{name}"})
    (directory / "release.json").write_text(json.dumps({"schema": 1, "updaterProtocol": 1, "product": "MusicMachine",
        "version": version, "commit": os.environ["GITHUB_SHA"], "assets": assets}, indent=2) + "\n")
    (directory / "SHA256SUMS").write_text("".join(f"{a['sha256']}  {a['name']}\n" for a in assets))


if __name__ == "__main__":
    create(Path(sys.argv[1]))
