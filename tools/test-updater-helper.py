"""Headless Linux NativeAOT updater smoke test using disposable synthetic installs.

Usage: python3 tools/test-updater-helper.py /path/to/published/MusicMachine.Desktop
The helper is real. Both restarted application targets are harmless shell fixtures;
no GUI or real installed application is started/replaced. All state stays in a temp tree.
"""
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import sys
import tempfile
import uuid

OLD = "a" * 40
NEW = "b" * 40


def metadata(path, version, commit):
    (path / "release.json").write_text(json.dumps({
        "schema": 1, "product": "MusicMachine", "version": version,
        "runtime": "linux-x64", "commit": commit,
        "executable": "MusicMachine.Desktop", "updaterProtocol": 1,
        "repository": "isaiahpettingill/MusicMachine",
    }))


def executable(path, script):
    path.write_text("#!/bin/sh\nset -eu\n" + script + "\n")
    path.chmod(0o755)


def scenario(native, fail):
    with tempfile.TemporaryDirectory(prefix="musicmachine-helper-fixture-") as folder:
        root = Path(folder)
        token = uuid.uuid4().hex
        target = root / "app"
        prepared = root / ("app.update-" + token)
        backup = root / ("app.previous-" + token)
        work = root / "data" / "updates" / ("install-" + token)
        helper = work / "helper"
        for directory in (target, prepared, helper):
            directory.mkdir(parents=True, mode=0o700)
        metadata(target, "0.1.0", OLD)
        metadata(prepared, "0.2.0", NEW)
        old_restart = work / "old-restarted"
        executable(target / "MusicMachine.Desktop", "printf old > " + shlex.quote(str(old_restart)))
        if fail:
            executable(prepared / "MusicMachine.Desktop", "exit 23")
        else:
            executable(prepared / "MusicMachine.Desktop", "printf %s " + shlex.quote(token) + " > " + shlex.quote(str(work / "startup.ready")))
        for directory in (target, prepared):
            (directory / "user-song.song").write_bytes(b"unrelated user song")
        (prepared / ".update-token").write_text(token)
        # Fixtures represent already-approved, durable preparation. The original process is gone.
        plan = {
            "Token": token, "Target": str(target), "Runtime": "linux-x64",
            "Version": "0.2.0", "Commit": NEW, "PreviousVersion": "0.1.0",
            "PreviousCommit": OLD, "ParentProcess": 2147483647,
            "ParentStartUtcTicks": 1, "InstallerSha256": None, "InstallerSize": 0,
        }
        (work / "plan.json").write_text(json.dumps(plan))
        (work / "apply.approved").write_text(token)
        (work / "resume.song").write_bytes(b"recovery fixture is never consumed by helper")
        for source in native.parent.iterdir():
            if source.is_file() and (source == native or source.suffix in (".so", ".dll")):
                shutil.copy2(source, helper / source.name)
        env = dict(os.environ, MUSICMACHINE_DATA=str(root / "data"))
        result = subprocess.run([str(helper / native.name), "--apply-update", str(work / "plan.json")],
                                env=env, capture_output=True, text=True, timeout=20)
        expected = 1 if fail else 0
        assert result.returncode == expected, (result.returncode, result.stdout, result.stderr)
        current = json.loads((target / "release.json").read_text())
        assert current["commit"] == (OLD if fail else NEW)
        assert (target / "user-song.song").read_bytes() == b"unrelated user song"
        assert (work / "resume.song").exists()
        assert (work / "helper.ready").read_text() == token
        log = (work / "install.log").read_text()
        if fail:
            assert log.startswith("Update failed:")
            assert not backup.exists()
            assert prepared.exists()
            # Restart is intentionally asynchronous; wait briefly for the harmless old fixture.
            import time
            deadline = time.monotonic() + 3
            while not old_restart.exists() and time.monotonic() < deadline:
                time.sleep(0.01)
            assert old_restart.read_text() == "old"
        else:
            assert log.startswith("Update installed and song restored.")
            assert json.loads((backup / "release.json").read_text())["commit"] == OLD
            assert not old_restart.exists()
        print("PASS: helper startup-failure rollback" if fail else "PASS: helper apply, startup acknowledgment and retained backup")


def main():
    if sys.platform != "linux":
        raise SystemExit("This shell-fixture helper smoke test is Linux-only.")
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    native = Path(sys.argv[1]).resolve()
    if native.name != "MusicMachine.Desktop" or not native.is_file():
        raise SystemExit("Pass the freshly published Linux MusicMachine.Desktop executable.")
    scenario(native, False)
    scenario(native, True)


if __name__ == "__main__":
    main()
