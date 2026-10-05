"""Build the NSIS setup from the same published payload as the portable ZIP."""

import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile


def nsis_text(value: str) -> str:
    return value.replace("$", "$$").replace('"', '$\\"')


def package(payload: Path, output: Path) -> None:
    payload, output = payload.resolve(), output.resolve()
    metadata = json.loads((payload / "release.json").read_text(encoding="utf-8"))
    version = metadata["version"]
    if metadata["runtime"] != "win-x64" or not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise ValueError("Expected a versioned win-x64 release payload")
    if not (payload / "MusicMachine.Desktop.exe").is_file():
        raise ValueError("Missing desktop executable")
    compiler = shutil.which("makensis") or shutil.which("makensis.exe")
    if compiler is None:
        candidate = Path("C:/Program Files (x86)/NSIS/makensis.exe")
        if candidate.is_file():
            compiler = str(candidate)
    if compiler is None:
        raise RuntimeError("NSIS makensis is required to build the Windows installer")
    root = Path(__file__).resolve().parent.parent
    shutil.copy2(root / "assets/icon/musicmachine.ico", payload)
    files = sorted(p for p in payload.rglob("*") if p.is_file())
    if any(p.is_symlink() for p in payload.rglob("*")):
        raise ValueError("Release payload cannot contain symbolic links")
    with tempfile.TemporaryDirectory(prefix="musicmachine-nsis-") as temp:
        folder = Path(temp)
        install, uninstall = [], []
        directories = set()
        for file in files:
            relative = file.relative_to(payload)
            parent = str(relative.parent).replace("/", "\\")
            name = str(relative).replace("/", "\\")
            install += [
                f'SetOutPath "$INSTDIR\\{nsis_text(parent)}"',
                f'File "{nsis_text(str(file))}"',
            ]
            uninstall.append(f'Delete "$INSTDIR\\{nsis_text(name)}"')
            directories.update(relative.parents)
        for directory in sorted(directories, key=lambda p: len(p.parts), reverse=True):
            if directory != Path("."):
                name = str(directory).replace("/", "\\")
                uninstall.append(f'RMDir "$INSTDIR\\{nsis_text(name)}"')
        for name, lines in [("install.nsh", install), ("uninstall.nsh", uninstall)]:
            (folder / name).write_text("\n".join(lines) + "\n", encoding="utf-8")
        output.parent.mkdir(parents=True, exist_ok=True)
        definitions = {
            "VERSION": version,
            "OUTPUT": str(output),
            "ICON": str(root / "assets/icon/musicmachine.ico"),
            "LICENSE_FILE": str(root / "LICENSE"),
            "INSTALL_FILES": str(folder / "install.nsh"),
            "UNINSTALL_FILES": str(folder / "uninstall.nsh"),
            "SIZE_KB": str(sum(f.stat().st_size for f in files) // 1024 + 1),
        }
        subprocess.run(
            [
                compiler,
                "/V2" if sys.platform == "win32" else "-V2",
                *[f"{'/' if sys.platform == 'win32' else '-'}D{k}={v}" for k, v in definitions.items()],
                str(root / "tools/windows-installer.nsi"),
            ],
            check=True,
        )


if __name__ == "__main__":
    package(Path(sys.argv[1]), Path(sys.argv[2]))
