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
from test_windows_install_directory import WindowsInstallDirectoryTests

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
WINDOWS_GUI = module("test-windows-gui.py")
WINDOWS_CI = module("windows_ci_policy.py")

class ReleaseTests(unittest.TestCase):
    def test_windows_gui_png_preserves_color_and_dimensions(self):
        import struct
        import zlib
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / "native.png"
            WINDOWS_GUI.write_png(output, 2, 1, bytes((0, 0, 255, 0, 0, 255, 0, 0)))
            data = output.read_bytes()
            self.assertEqual(data[:8], b"\x89PNG\r\n\x1a\n")
            position, chunks = 8, {}
            while position < len(data):
                size = struct.unpack(">I", data[position:position + 4])[0]
                kind = data[position + 4:position + 8]
                content = data[position + 8:position + 8 + size]
                checksum = struct.unpack(">I", data[position + 8 + size:position + 12 + size])[0]
                self.assertEqual(checksum, zlib.crc32(kind + content) & 0xffffffff)
                chunks[kind] = content
                position += size + 12
            self.assertEqual(struct.unpack(">II", chunks[b"IHDR"][:8]), (2, 1))
            self.assertEqual(zlib.decompress(chunks[b"IDAT"]), bytes((0, 255, 0, 0, 0, 255, 0)))
            for width, height, pixels in ((0, 1, b""), (1, -1, b""), (1, 1, b"bad")):
                with self.assertRaises(ValueError):
                    WINDOWS_GUI.write_png(output, width, height, pixels)

    def test_windows_gui_compares_only_visible_pixels(self):
        before = bytes((0, 0, 255, 0, 0, 255, 0, 0))
        alpha_only = bytes((0, 0, 255, 255, 0, 255, 0, 255))
        changed = bytes((0, 0, 0, 0, 0, 255, 0, 0))
        self.assertEqual(WINDOWS_GUI.color_count(before), 2)
        self.assertEqual(WINDOWS_GUI.changed_pixels(before, alpha_only), 0)
        self.assertEqual(WINDOWS_GUI.changed_pixels(before, changed), 1)
        with self.assertRaises(ValueError):
            WINDOWS_GUI.changed_pixels(before, b"")

    def test_windows_gui_selects_exact_owned_modal_instead_of_main_framebuffer(self):
        windows = [
            {"hwnd": 100, "pid": 42, "visible": True, "title": "Untitled song · MusicMachine", "owner": 0},
            {"hwnd": 200, "pid": 42, "visible": True, "title": "Make a loop", "owner": 100},
            {"hwnd": 300, "pid": 43, "visible": True, "title": "Make a loop", "owner": 100},
            {"hwnd": 400, "pid": 42, "visible": True, "title": "Other dialog", "owner": 100},
            {"hwnd": 500, "pid": 42, "visible": False, "title": "Make a loop", "owner": 100},
            {"hwnd": 600, "pid": 42, "visible": True, "title": "Make a loop", "owner": 999},
        ]
        self.assertEqual(WINDOWS_GUI.select_window(windows, 42), 100)
        self.assertEqual(WINDOWS_GUI.select_window(windows, 42, "Make a loop", 100), 200)
        self.assertIsNone(WINDOWS_GUI.select_window(windows[2:], 42, "Make a loop", 100))
        with self.assertRaisesRegex(RuntimeError, "Ambiguous"):
            WINDOWS_GUI.select_window(windows + [dict(windows[1], hwnd=201)], 42, "Make a loop", 100)

    def test_windows_gui_wait_requires_observed_success_and_live_process(self):
        from unittest.mock import Mock
        process = Mock()
        process.poll.return_value = None
        self.assertEqual(WINDOWS_GUI.wait_for(lambda: 200, process, "not observed", timeout=0), 200)
        with self.assertRaisesRegex(RuntimeError, "not observed"):
            WINDOWS_GUI.wait_for(lambda: None, process, "not observed", timeout=0)
        process.poll.return_value = 1
        with self.assertRaisesRegex(RuntimeError, "Native GUI exited"):
            WINDOWS_GUI.wait_for(lambda: 200, process, "not observed", timeout=0)

    def test_windows_gui_smoke_refuses_non_windows(self):
        with patch.object(WINDOWS_GUI.sys, "platform", "linux"):
            with self.assertRaisesRegex(RuntimeError, "no GUI was tested"):
                WINDOWS_GUI.run(Path("unused"), Path("unused"))

    def test_fast_ci_omits_runtime_smoke_and_installer_gates(self):
        workflow = (TOOLS.parent / ".github/workflows/build.yml").read_text()
        for command in ("test-windows-gui.py", "test-windows-installer.py", "test-updater-helper.py",
                        "test-native-payload.py", "test_linux_installer.py", "test-windows-bootstrap.ps1",
                        "smoke-published-app.mjs", "test:browser", "test:formats", "test:runtime"):
            self.assertNotIn(command, workflow)
        self.assertIn("dotnet test tests/MusicMachine.Tests", workflow)
        self.assertIn("node --test", workflow)
        self.assertIn("tools/verify-native-payload.py", workflow)

    def test_windows_installer_guard_does_not_manufacture_token_properties(self):
        script = (TOOLS / "test-windows-installer.py").read_text()
        self.assertIn("if elevated or integrity > 0x2000:", script)
        self.assertIn("No real install was verified", script)
        self.assertNotIn("SetTokenInformation", script)
        self.assertNotIn("AdjustTokenPrivileges", script)
        self.assertNotIn("CreateRestrictedToken", script)

    def test_windows_ci_waiver_is_opt_in_and_only_matches_known_hosted_runner(self):
        environment = {"GITHUB_ACTIONS": "true", "RUNNER_ENVIRONMENT": "github-hosted", "RUNNER_OS": "Windows"}
        arguments = ["--allow-unsupported-ci-runner"]
        error = WINDOWS_CI.UnsupportedStandardUserRunner("SAFER NORMALUSER: elevation=False, integrity=0x3000")
        with tempfile.TemporaryDirectory() as temp, patch("builtins.print"):
            summary = Path(temp) / "summary.md"
            summary.write_text("Existing steps\n")
            complete = dict(environment, GITHUB_STEP_SUMMARY=str(summary))
            self.assertTrue(WINDOWS_CI.record_unsupported_runner_skip(error, arguments, complete))
            text = summary.read_text()
            self.assertTrue(text.startswith("Existing steps\n"))
            self.assertIn("SKIPPED", text)
            self.assertIn("**not verified**", text)
            self.assertIn("integrity=0x3000", text)
            self.assertFalse(WINDOWS_CI.record_unsupported_runner_skip(error, [], complete))
            for key in environment:
                incomplete = dict(complete)
                incomplete.pop(key)
                self.assertFalse(WINDOWS_CI.record_unsupported_runner_skip(error, arguments, incomplete))
            for replacement in ({"RUNNER_ENVIRONMENT": "self-hosted"}, {"RUNNER_OS": "Linux"}, {"GITHUB_ACTIONS": "false"}):
                self.assertFalse(WINDOWS_CI.record_unsupported_runner_skip(error, arguments, dict(complete, **replacement)))
            for failure in (RuntimeError("actual installation failed"), AssertionError("elevated setup accepted"), OSError("token API failed")):
                self.assertFalse(WINDOWS_CI.record_unsupported_runner_skip(failure, arguments, complete))
            self.assertEqual(text, summary.read_text(), "Rejected waiver requests must not append a skip")

    def test_windows_ci_waiver_cannot_catch_install_or_elevated_refusal_failures(self):
        import ast
        script = (TOOLS / "test-windows-installer.py").read_text()
        handlers = [node for node in ast.walk(ast.parse(script)) if isinstance(node, ast.ExceptHandler)
                    and isinstance(node.type, ast.Name) and node.type.id == "UnsupportedStandardUserRunner"]
        self.assertEqual(len(handlers), 1)
        self.assertLess(script.index('assert result.returncode == 2, "Elevated setup was not refused"'),
                        script.index("except UnsupportedStandardUserRunner as error:"))
        workflow = (TOOLS.parent / ".github/workflows/build.yml").read_text()
        self.assertNotIn("--allow-unsupported-ci-runner", workflow)
        self.assertNotIn("continue-on-error:", workflow)
        installer_step = workflow.split("      - name: Build Windows installer", 1)[1].split("      - name:", 1)[0]
        commands = re.findall(r"^          python .*\n(.*)", installer_step, re.M)
        self.assertEqual(len(commands), 1)
        self.assertTrue(all(line.strip() == "if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }" for line in commands),
                        "PowerShell must stop after each failed native command, not only the final one")

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
        self.assertEqual(len(downloads), 1)
        self.assertTrue(any("pattern: desktop-*" in step for step in downloads))
        for step in downloads:
            self.assertIn("pattern: desktop-*", step)
            self.assertNotIn("browser-wasm", step)
            self.assertNotIn("browser-app-smoke", step)
            self.assertNotIn("browser-candidate-diagnostics", step)
            self.assertNotIn("windows-gui-smoke", step)
        pages = (TOOLS.parent / ".github/workflows/pages.yml").read_text()
        self.assertNotIn("browser-candidate-diagnostics", pages)

    def test_native_release_is_independent_of_browser(self):
        workflow = (TOOLS.parent / ".github/workflows/build.yml").read_text()
        release = workflow.split("  release:\n", 1)[1]
        self.assertIn("needs: [prepare, desktop]", release)
        self.assertNotIn("needs: [prepare, desktop, browser]", release)
        self.assertNotIn("MusicMachine-browser-wasm.zip", MANIFEST.REQUIRED)
        self.assertEqual(len(MANIFEST.REQUIRED), 6)
        self.assertIn("tools/verify-native-payload.py artifacts/${{ matrix.rid }} ${{ matrix.rid }}", workflow)

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
            self.assertEqual(len(result["assets"]), 6)
            self.assertTrue(all("/v1.2.3/" in asset["url"] for asset in result["assets"]))
            self.assertEqual(len((root / "SHA256SUMS").read_text().splitlines()), 6)

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
