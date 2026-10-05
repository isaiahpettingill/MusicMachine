import importlib.util
import io
import json
import os
import re
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

TOOLS = Path(__file__).resolve().parent

def module(filename):
    spec = importlib.util.spec_from_file_location(filename, TOOLS / filename)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result

VERSION = module("ci-version.py")
MANIFEST = module("release-manifest.py")
INSTALLER = module("package-linux-installer.py")
DESKTOP = module("prepare-desktop.py")

class ReleaseTests(unittest.TestCase):
    def test_first_release(self):
        self.assertEqual(VERSION.choose_version("0.1.0", "abc", "refs/heads/main", "push", [], {}), ("0.1.0", True))

    def test_next_patch_reserves_drafts_and_tags(self):
        releases = [{"tag_name": "v0.1.2", "target_commitish": "old", "draft": True}]
        self.assertEqual(VERSION.choose_version("0.1.0", "new", "refs/heads/main", "push", releases, {"v0.1.5": "other"}), ("0.1.6", True))

    def test_published_commit_is_immutable(self):
        releases = [{"tag_name": "v0.1.2", "target_commitish": "main", "draft": False}]
        self.assertEqual(VERSION.choose_version("0.1.0", "abc", "refs/heads/main", "push", releases, {"v0.1.2": "abc"}), ("0.1.2", False))

    def test_pull_request_never_publishes(self):
        self.assertEqual(VERSION.choose_version("0.1.0", "abc", "refs/pull/1/merge", "pull_request", [], {}), ("0.1.0", False))

    def test_validation_branches_never_publish(self):
        for event in ("push", "workflow_dispatch"):
            self.assertEqual(VERSION.choose_version("0.1.0", "abc", "refs/heads/build/musicmachine-initial", event, [], {}), ("0.1.0", False))

    def test_publication_requires_current_main_and_rejects_invalid_responses(self):
        with patch.dict(os.environ, GITHUB_SHA="a" * 40, GITHUB_REPOSITORY="example/MusicMachine"):
            with patch.object(VERSION.subprocess, "check_output", return_value="a" * 40 + "\n"):
                self.assertTrue(VERSION.current_main())
            with patch.object(VERSION.subprocess, "check_output", return_value="b" * 40 + "\n"):
                self.assertFalse(VERSION.current_main())
            with patch.object(VERSION.subprocess, "check_output", return_value="unexpected"):
                with self.assertRaises(ValueError):
                    VERSION.current_main()

    def test_release_does_not_collect_diagnostic_artifacts(self):
        workflow = (TOOLS.parent / ".github/workflows/build.yml").read_text()
        release = workflow.split("  release:\n", 1)[1]
        downloads = re.findall(r"uses: actions/download-artifact@v4.*?(?=^      -|\Z)", release, re.M | re.S)
        self.assertEqual(len(downloads), 2)
        self.assertTrue(any("pattern: desktop-*" in step for step in downloads))
        self.assertTrue(any("name: browser-wasm" in step for step in downloads))
        for step in downloads:
            self.assertTrue("pattern:" in step or "name: browser-wasm" in step)
            self.assertNotIn("browser-app-smoke", step)

    def test_version_validation(self):
        for value in ("1.0", "../test", "1.2.3-rc1"):
            with self.assertRaises(ValueError):
                VERSION.version_tuple(value)

    def test_stamp_preserves_other_properties(self):
        with patch.dict(os.environ, MUSIC_RELEASE_VERSION="1.2.3"):
            root = ET.fromstring(VERSION.stamped(b"<Project><PropertyGroup><Version>0.1.0</Version><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>"))
        self.assertEqual(root.findtext(".//Version"), "1.2.3")
        self.assertEqual(root.findtext(".//TargetFramework"), "net11.0")

    def test_complete_manifest_required(self):
        with tempfile.TemporaryDirectory() as temp, patch.dict(os.environ, MUSIC_RELEASE_VERSION="1.2.3", GITHUB_SHA="abc", GITHUB_REPOSITORY="example/MusicMachine"):
            root = Path(temp)
            with self.assertRaises(ValueError):
                MANIFEST.create(root)
            for name in MANIFEST.REQUIRED:
                (root / name).write_bytes(b"fixture:" + name.encode())
            MANIFEST.create(root)
            result = json.loads((root / "release.json").read_text())
            self.assertEqual(result["commit"], "abc")
            self.assertEqual(result["updaterProtocol"], 1)
            self.assertEqual(len(result["assets"]), 7)
            self.assertTrue(all("/v1.2.3/" in asset["url"] for asset in result["assets"]))
            self.assertEqual(len((root / "SHA256SUMS").read_text().splitlines()), 7)

    def test_installed_metadata_advertises_updater_protocol(self):
        with tempfile.TemporaryDirectory() as temp, patch.dict(os.environ, MUSIC_RELEASE_VERSION="1.2.3", GITHUB_SHA="a" * 40, GITHUB_REPOSITORY="example/MusicMachine"):
            root = Path(temp)
            (root / "MusicMachine.Desktop").write_bytes(b"harmless payload fixture")
            (root / "MusicMachine.Desktop.dbg").write_bytes(b"debug symbols")
            DESKTOP.prepare(root, "linux-x64")
            metadata = json.loads((root / "release.json").read_text())
            self.assertEqual(metadata["updaterProtocol"], 1)
            self.assertEqual(metadata["runtime"], "linux-x64")
            self.assertEqual(metadata["executable"], "MusicMachine.Desktop")
            self.assertFalse((root / "MusicMachine.Desktop.dbg").exists())

    def test_linux_installer_is_version_and_checksum_pinned(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            archive = root / "MusicMachine-linux-x64.tar.gz"
            archive.write_bytes(b"archive")
            output = root / "installer.sh"
            INSTALLER.package(archive, output, "example/MusicMachine", "1.2.3")
            text = output.read_text()
            self.assertIn("RELEASE_VERSION='1.2.3'", text)
            self.assertNotIn("@SHA256@", text)
            self.assertNotIn("/releases/latest/download", text)
            with self.assertRaises(ValueError):
                INSTALLER.package(archive, output, "example/MusicMachine", "bad")

if __name__ == "__main__":
    unittest.main()
