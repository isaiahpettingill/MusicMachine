"""Portable binary fixtures keep the release gate testable on Windows and Linux."""
import importlib.util
import json
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).with_name("verify-native-payload.py")
SPEC = importlib.util.spec_from_file_location("verify_native_payload", SCRIPT)
VERIFY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VERIFY)


def pe(*, machine=0x8664, clr=(0, 0), dll=False):
    data = bytearray(512)
    data[:2] = b"MZ"
    struct.pack_into("<I", data, 0x3c, 0x80)
    data[0x80:0x84] = b"PE\0\0"
    struct.pack_into("<HHIIIHH", data, 0x84, machine, 1, 0, 0, 0, 240, 0x22 | (0x2000 if dll else 0))
    struct.pack_into("<H", data, 0x98, 0x20b)
    struct.pack_into("<I", data, 0x98 + 108, 16)
    struct.pack_into("<II", data, 0x98 + 112 + 14 * 8, *clr)
    data[0x188:0x190] = b".text\0\0\0"
    return data


def elf(*, machine=62, sections=()):
    names = b"\0.shstrtab\0" + b"".join(name + b"\0" for name, _ in sections)
    count, shoff = len(sections) + 2, 128
    names_offset = shoff + count * 64
    data = bytearray(names_offset + len(names))
    data[:7] = b"\x7fELF\x02\x01\x01"
    struct.pack_into("<HHIQQQIHHHHHH", data, 16, 3, machine, 1, 0, 64, shoff, 0, 64, 56, 1, 64, count, 1)
    struct.pack_into("<IIQQQQQQ", data, 64, 1, 5, 0, 0, 0, len(data), len(data), 4096)
    struct.pack_into("<IIQQQQIIQQ", data, shoff + 64, 1, 3, 0, 0, names_offset, len(names), 0, 0, 1, 0)
    offset = len(b"\0.shstrtab\0")
    for index, (name, kind) in enumerate(sections, 2):
        struct.pack_into("<IIQQQQIIQQ", data, shoff + index * 64, offset, kind, 0, 0, 0, 0, 0, 0, 1, 0)
        offset += len(name) + 1
    data[names_offset:] = names
    return data


class NativePayloadTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def payload(self, runtime):
        executable = VERIFY.EXECUTABLES[runtime]
        (self.root / "release.json").write_text(json.dumps({"runtime": runtime, "executable": executable}))
        (self.root / executable).write_bytes(pe() if runtime == "win-x64" else elf())
        return self.root

    def test_windows_allows_native_vendor_libraries(self):
        root = self.payload("win-x64")
        for name in ("SDL2.dll", "libSkiaSharp.dll", "libHarfBuzzSharp.dll", "av_libglesv2.dll"):
            (root / name).write_bytes(pe(dll=True))
        self.assertEqual(VERIFY.verify(root, "win-x64")["native_binaries"], 5)

    def test_linux_preserves_dynamic_symbols_unwind_and_vendor_static_symbols(self):
        root = self.payload("linux-x64")
        (root / "MusicMachine.Desktop").write_bytes(elf(sections=((b".dynsym", 11), (b".eh_frame", 1),
                                                                 (b".gnu_debuglink", 1), (b"__managedcode", 1))))
        vendor = root / "libSkiaSharp.so"
        original = elf(sections=((b".symtab", 2), (b".dynsym", 11)))
        vendor.write_bytes(original)
        self.assertEqual(VERIFY.verify(root, "linux-x64")["native_binaries"], 2)
        self.assertEqual(vendor.read_bytes(), original)

    def test_rejects_forbidden_files_recursively_and_case_insensitively(self):
        root = self.payload("linux-x64")
        nested = root / "nested"
        nested.mkdir()
        for name in ("MusicMachine.Desktop.DLL", "coreclr.dll", "libcoreclr.so", "hostfxr.dll",
                     "libhostfxr.so.8", "libhostpolicy.so", "HOSTPOLICY.DLL", "app.deps.json",
                     "app.runtimeconfig.json", "app.runtimeconfig.dev.json", "symbols.PDB", "app.DBG", "app.map"):
            with self.subTest(name=name):
                path = nested / name
                path.write_bytes(b"forbidden")
                with self.assertRaisesRegex(ValueError, "Forbidden"):
                    VERIFY.verify(root, "linux-x64")
                path.unlink()

    def test_rejects_managed_header_in_main_or_arbitrary_dll(self):
        root = self.payload("win-x64")
        for clr in ((1234, 72), (1234, 0), (0, 72)):
            with self.subTest(clr=clr), self.assertRaisesRegex(ValueError, "CLR header"):
                VERIFY.verify_pe(pe(clr=clr), executable=True)
        (root / "AnotherAssembly.dll").write_bytes(pe(clr=(1234, 72), dll=True))
        with self.assertRaisesRegex(ValueError, "AnotherAssembly.dll.*CLR header"):
            VERIFY.verify(root, "win-x64")

    def test_rejects_wrong_architecture_and_binary_format(self):
        for data, check in ((pe(machine=0x14c), VERIFY.verify_pe), (pe(machine=0xaa64), VERIFY.verify_pe),
                            (elf(machine=183), VERIFY.verify_elf), (elf(), VERIFY.verify_pe), (pe(), VERIFY.verify_elf)):
            with self.subTest(check=check.__name__), self.assertRaises(ValueError):
                check(data, executable=True)
        for index, value in ((4, 1), (5, 2), (6, 0)):
            data = elf()
            data[index] = value
            with self.assertRaises(ValueError):
                VERIFY.verify_elf(data)

    def test_rejects_dll_as_main_executable_and_coff_symbols(self):
        with self.assertRaisesRegex(ValueError, "PE executable"):
            VERIFY.verify_pe(pe(dll=True), executable=True)
        data = pe()
        struct.pack_into("<II", data, 0x84 + 8, 400, 2)
        with self.assertRaisesRegex(ValueError, "COFF symbols"):
            VERIFY.verify_pe(data, executable=True)

    def test_rejects_embedded_elf_debug_sections(self):
        for section in (b".debug_info", b".zdebug_info", b".stab", b".stabstr", b".gnu_debugdata"):
            for main in (False, True):
                with self.subTest(section=section, main=main), self.assertRaisesRegex(ValueError, "debug section"):
                    VERIFY.verify_elf(elf(sections=((section, 1),)), executable=main)

    def test_main_elf_static_symbols_are_rejected_by_type_and_name(self):
        for name, kind in ((b".symtab", 2), (b"renamed_symbols", 2), (b".symtab", 1)):
            with self.subTest(name=name), self.assertRaisesRegex(ValueError, "static symbols"):
                VERIFY.verify_elf(elf(sections=((name, kind),)), executable=True)

    def test_elf_can_be_fully_sectionless_or_use_extended_numbering(self):
        data = elf()
        struct.pack_into("<Q", data, 40, 0)
        struct.pack_into("<HH", data, 60, 0, 0)
        VERIFY.verify_elf(data, executable=True)
        data = elf()
        struct.pack_into("<HH", data, 60, 0, 0xffff)
        struct.pack_into("<Q", data, 128 + 32, 2)
        struct.pack_into("<I", data, 128 + 40, 1)
        VERIFY.verify_elf(data, executable=True)

    def test_truncated_and_out_of_range_headers_fail_closed(self):
        for data, check in ((pe(), VERIFY.verify_pe), (elf(), VERIFY.verify_elf)):
            for length in (0, 2, 16, 63, 100, len(data) - 200):
                with self.subTest(check=check.__name__, length=length), self.assertRaises(ValueError):
                    check(data[:length], executable=True)
        data = pe()
        struct.pack_into("<I", data, 0x3c, 0xffffffff)
        with self.assertRaises(ValueError):
            VERIFY.verify_pe(data)
        data = pe()
        struct.pack_into("<I", data, 0x98 + 108, 0xffffffff)
        with self.assertRaises(ValueError):
            VERIFY.verify_pe(data)
        data = elf()
        struct.pack_into("<Q", data, 40, 0xffffffffffffffff)
        with self.assertRaises(ValueError):
            VERIFY.verify_elf(data)

    def test_rejects_missing_main_and_misleading_metadata(self):
        root = self.payload("linux-x64")
        with self.assertRaisesRegex(ValueError, "runtime"):
            VERIFY.verify(root, "win-x64")
        for executable in ("../outside", "elsewhere", "/MusicMachine.Desktop"):
            (root / "release.json").write_text(json.dumps({"runtime": "linux-x64", "executable": executable}))
            with self.assertRaisesRegex(ValueError, "executable"):
                VERIFY.verify(root, "linux-x64")
        self.payload("linux-x64")
        (root / "MusicMachine.Desktop").unlink()
        with self.assertRaisesRegex(ValueError, "Missing native executable"):
            VERIFY.verify(root, "linux-x64")

    def test_rejects_symlinks_without_reading_the_target(self):
        root = self.payload("linux-x64")
        try:
            (root / "link").symlink_to(root / "MusicMachine.Desktop")
        except (OSError, NotImplementedError):
            self.skipTest("Symlink creation unavailable for this runner")
        with self.assertRaisesRegex(ValueError, "symlink"):
            VERIFY.verify(root, "linux-x64")

    def test_cli_success_and_failure_exit_codes(self):
        root = self.payload("linux-x64")
        command = [sys.executable, str(SCRIPT), str(root), "linux-x64"]
        result = subprocess.run(command, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("Verified linux-x64", result.stdout)
        (root / "app.pdb").write_bytes(b"symbols")
        result = subprocess.run(command, capture_output=True, text=True)
        self.assertEqual(result.returncode, 1)
        self.assertIn("Forbidden", result.stderr)
        self.assertNotIn("Traceback", result.stderr)


if __name__ == "__main__":
    unittest.main()
