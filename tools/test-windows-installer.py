"""Exercise the real per-user NSIS setup without opening the editor."""
import hashlib
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import winreg

setup, payload = map(lambda p: Path(p).resolve(), sys.argv[1:3])
key = r"Software\Microsoft\Windows\CurrentVersion\Uninstall\MusicMachine"
registry_access = winreg.KEY_READ | winreg.KEY_WOW64_32KEY
try:
    existing = winreg.OpenKey(winreg.HKEY_CURRENT_USER, key, 0, registry_access)
except FileNotFoundError:
    pass
else:
    existing.Close()
    raise RuntimeError("Refusing to overwrite an existing MusicMachine installation; use a clean CI runner")
shortcut = Path(os.environ["APPDATA"]) / "Microsoft/Windows/Start Menu/Programs/MusicMachine.lnk"
if shortcut.exists():
    raise RuntimeError("Refusing to overwrite an existing MusicMachine shortcut; use a clean CI runner")
with tempfile.TemporaryDirectory(prefix="musicmachine-setup-test-") as temp:
    root = Path(temp) / "App With Spaces"
    root.mkdir()  # Existing empty destinations remain supported.
    for marker in ("README.md", "LICENSE", ".git", "Foreign.csproj", "Foreign.slnx"):
        foreign = Path(temp) / ("Foreign " + marker.replace(".", "_"))
        foreign.mkdir()
        (foreign / marker).write_bytes(b"unrelated data must survive")
        result = subprocess.run(f'"{setup}" /S /D={foreign}', timeout=120)
        assert result.returncode == 2, "Unrelated/source directory was not refused"
        assert (foreign / marker).read_bytes() == b"unrelated data must survive"
        assert set(p.name for p in foreign.iterdir()) == {marker}, "Rejected target was changed"
    for attempt in range(2):
        # NSIS requires its final /D= argument to remain unquoted even with spaces.
        # Passing a raw command line on Windows avoids list2cmdline quoting it.
        subprocess.run(f'"{setup}" /S /D={root}', check=True, timeout=120)
        for source in payload.rglob("*"):
            if source.is_file():
                installed = root / source.relative_to(payload)
                assert installed.is_file(), f"Installer missed {source.name}"
                assert hashlib.sha256(installed.read_bytes()).digest() == hashlib.sha256(source.read_bytes()).digest(), source.name
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, key, 0, registry_access) as entry:
            assert winreg.QueryValueEx(entry, "InstallLocation")[0] == str(root)
        assert (root / ".installer-owned").read_text() == "MusicMachine per-user Windows installation v1"
        if attempt == 0:
            (root / ".installer-owned").unlink()  # A registered legacy install is still recognized.
            (root / ".git").mkdir()
            result = subprocess.run(f'"{setup}" /S /D={root}', timeout=120)
            assert result.returncode == 2, "Registered source checkout was overwritten"
            (root / ".git").rmdir()
    user_song = root / "My song.song"
    user_song.write_bytes(b"retained user file")
    subprocess.run([sys.executable, str(Path(__file__).with_name("test-native-payload.py")), str(root)], check=True)
    # _?= executes the uninstaller in-place so waiting tracks the actual operation.
    subprocess.run(f'"{root / "Uninstall.exe"}" /S _?={root}', check=True, timeout=120)
    deadline = time.monotonic() + 15
    while (root / "MusicMachine.Desktop.exe").exists() and time.monotonic() < deadline:
        time.sleep(0.2)
    assert not (root / "MusicMachine.Desktop.exe").exists(), "Uninstall left executable"
    assert user_song.read_bytes() == b"retained user file", "Uninstall removed user song"
    assert not (root / ".installer-owned").exists(), "Uninstall left the ownership marker"
    try:
        entry = winreg.OpenKey(winreg.HKEY_CURRENT_USER, key, 0, registry_access)
    except FileNotFoundError:
        pass
    else:
        entry.Close()
        raise AssertionError("Uninstall registry entry remains")
print("Windows setup install/reinstall/export/uninstall verified")
