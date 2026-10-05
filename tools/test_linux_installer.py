"""Regression-test per-user installer operations using disposable fake payloads."""
import io
import os
from pathlib import Path
import subprocess
import tarfile
import tempfile
import unittest

TOOLS = Path(__file__).resolve().parent

class LinuxInstallerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="musicmachine installer ")
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.data = self.base / "data $with 'quotes'"
        self.bin = self.base / "bin with spaces"
        self.env = dict(os.environ, XDG_DATA_HOME=str(self.data), MUSICMACHINE_BIN_DIR=str(self.bin))
        self.archive = self.base / "MusicMachine-linux-x64.tar.gz"
        self.make_archive("one")

    def make_archive(self, version, extra=None):
        files = {"MusicMachine.Desktop": f'#!/bin/sh\nprintf "{version}:%s\\n" "$*"\n',
                 "musicmachine.svg": f"<svg xmlns='http://www.w3.org/2000/svg'><title>{version}</title></svg>",
                 "LICENSE": "MIT"}
        files.update(extra or {})
        with tarfile.open(self.archive, "w:gz") as tar:
            for name, content in files.items():
                entry = tarfile.TarInfo(name)
                data = content.encode()
                entry.size, entry.mode = len(data), 0o644
                tar.addfile(entry, io.BytesIO(data))

    def run_installer(self, *args, success=True):
        result = subprocess.run(["bash", str(TOOLS / "install-linux.sh"), *map(str, args)], env=self.env, capture_output=True, text=True)
        self.assertEqual(result.returncode == 0, success, result.stdout + result.stderr)
        return result

    def command(self, name, *args):
        return subprocess.check_output([str(self.bin / name), *args], env=self.env, text=True)

    def test_install_upgrade_reinstall_uninstall(self):
        self.run_installer("--archive", self.archive)
        self.assertEqual(self.command("musicmachine", "two words"), "one:two words\n")
        desktop = self.data / "applications/io.github.isaiahpettingill.MusicMachine.desktop"
        self.assertIn("StartupWMClass=MusicMachine", desktop.read_text())
        self.assertIn("Categories=AudioVideo;Audio;Midi;", desktop.read_text())
        song = self.data / "example.song"
        song.write_text("keep")
        self.make_archive("two")
        self.run_installer("--archive", self.archive)
        self.run_installer("--archive", self.archive)
        self.assertEqual(self.command("musicmachine"), "two:\n")
        self.assertEqual(len(list((self.data / "musicmachine/releases").iterdir())), 1)
        self.command("musicmachine-uninstall")
        self.assertFalse(desktop.exists())
        self.assertFalse((self.data / "musicmachine").exists())
        self.assertEqual(song.read_text(), "keep")
        self.run_installer("--uninstall")

    def test_bad_checksum_preserves_existing(self):
        self.run_installer("--archive", self.archive)
        self.make_archive("two")
        self.run_installer("--archive", self.archive, "--sha256", "0" * 64, success=False)
        self.assertEqual(self.command("musicmachine"), "one:\n")

    def test_unmanaged_directory_preserved(self):
        root = self.data / "musicmachine"
        root.mkdir(parents=True)
        (root / "mine").write_text("keep")
        self.run_installer("--archive", self.archive, success=False)
        self.assertEqual((root / "mine").read_text(), "keep")

    def test_unrelated_launcher_preserved(self):
        self.bin.mkdir()
        (self.bin / "musicmachine").write_text("keep")
        self.run_installer("--archive", self.archive, success=False)
        self.assertEqual((self.bin / "musicmachine").read_text(), "keep")

    def test_traversal_and_symlink_archives_rejected(self):
        self.make_archive("bad", {"../escape": "bad"})
        self.run_installer("--archive", self.archive, success=False)
        self.assertFalse((self.data / "escape").exists())
        with tarfile.open(self.archive, "w:gz") as tar:
            link = tarfile.TarInfo("link")
            link.type, link.linkname = tarfile.SYMTYPE, "/tmp"
            tar.addfile(link)
        self.run_installer("--archive", self.archive, success=False)

    def test_download_uses_exact_version_and_rejects_tampering(self):
        import importlib.util
        spec = importlib.util.spec_from_file_location("packager", TOOLS / "package-linux-installer.py")
        packager = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(packager)
        installer = self.base / "pinned-installer.sh"
        packager.package(self.archive, installer, "example/MusicMachine", "1.2.3")
        mock = self.base / "mock-bin"
        mock.mkdir()
        curl = mock / "curl"
        curl.write_text('#!/bin/bash\nwhile (($#)); do\n if [[ "$1" == --output ]]; then output="$2"; shift 2; else url="$1"; shift; fi\ndone\nprintf "%s" "$url" > "$REQUEST_LOG"\ncp "$TEST_ARCHIVE" "$output"\n')
        curl.chmod(0o755)
        env = dict(self.env, PATH=str(mock) + ":" + os.environ["PATH"], TEST_ARCHIVE=str(self.archive), REQUEST_LOG=str(self.base / "url"))
        result = subprocess.run(["bash", str(installer)], env=env, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((self.base / "url").read_text(), "https://github.com/example/MusicMachine/releases/download/v1.2.3/MusicMachine-linux-x64.tar.gz")
        self.make_archive("tampered")
        result = subprocess.run(["bash", str(installer)], env=env, capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("checksum mismatch", result.stderr)
        self.assertEqual(self.command("musicmachine"), "one:\n")

    def test_missing_required_file_rejected(self):
        with tarfile.open(self.archive, "w:gz"):
            pass
        self.run_installer("--archive", self.archive, success=False)
        self.assertFalse((self.data / "musicmachine").exists())

    def test_uninstall_keeps_replaced_launcher(self):
        self.run_installer("--archive", self.archive)
        command = self.bin / "musicmachine"
        command.unlink()
        command.write_text("user replacement")
        self.command("musicmachine-uninstall")
        self.assertEqual(command.read_text(), "user replacement")

if __name__ == "__main__":
    unittest.main()
