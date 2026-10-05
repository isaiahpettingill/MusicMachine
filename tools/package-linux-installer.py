"""Package a version-pinned HTTPS installer; no signing secret is required."""
import hashlib
import os
from pathlib import Path
import re


def package(archive: Path, output: Path, repository: str, version: str) -> None:
    if not re.fullmatch(r"[\w.-]+/[\w.-]+", repository):
        raise ValueError("A GitHub owner/repository is required")
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise ValueError("A stable release version is required")
    checksum = hashlib.sha256(archive.read_bytes()).hexdigest()
    template = Path(__file__).with_name("install-linux.sh").read_text()
    for key, value in {"@REPOSITORY@": repository, "@VERSION@": version, "@SHA256@": checksum}.items():
        template = template.replace(key, value)
    output.write_text(template, newline="\n")
    output.chmod(0o755)
    archive.with_name(archive.name + ".sha256").write_text(f"{checksum}  {archive.name}\n", newline="\n")


if __name__ == "__main__":
    package(Path("MusicMachine-linux-x64.tar.gz"), Path("install-musicmachine.sh"),
            os.environ["GITHUB_REPOSITORY"], os.environ["MUSIC_RELEASE_VERSION"])
