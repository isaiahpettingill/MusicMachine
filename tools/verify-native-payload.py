"""Read-only, cross-platform checks on staged NativeAOT release payloads.

Usage: python tools/verify-native-payload.py artifacts/linux-x64 linux-x64
Run after prepare-desktop.py and before packaging. This structural gate complements
the real native export/GUI smoke tests; it does not prove runtime behavior or an
exact trimming/optimization setting. Uses only Python's standard library.
"""
import argparse
import json
from pathlib import Path
import re
import struct
import sys


EXECUTABLES = {"win-x64": "MusicMachine.Desktop.exe", "linux-x64": "MusicMachine.Desktop"}
RUNTIME_LIBRARY = re.compile(r"(?:lib)?(?:coreclr|hostfxr|hostpolicy)(?:\.dll|\.dylib|\.so(?:\..*)?)$", re.I)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def region(data, offset, length):
    require(0 <= offset <= len(data) and 0 <= length <= len(data) - offset,
            "Truncated or invalid native binary header")
    return data[offset:offset + length]


def unpack(fmt, data, offset):
    return struct.unpack(fmt, region(data, offset, struct.calcsize(fmt)))


def verify_pe(data, *, executable=False):
    """Require AMD64 PE32+ with no IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR."""
    require(region(data, 0, 2) == b"MZ", "Expected Windows PE native binary")
    pe, = unpack("<I", data, 0x3c)
    require(pe >= 0x40 and region(data, pe, 4) == b"PE\0\0", "Invalid PE signature")
    machine, sections, _, symbols, symbol_count, optional_size, flags = unpack("<HHIIIHH", data, pe + 4)
    require(machine == 0x8664, "Expected Windows x64 (AMD64) binary")
    require(flags & 2 and (not executable or not flags & 0x2000), "Expected PE executable image")
    optional = region(data, pe + 24, optional_size)
    require(optional_size >= 112 and unpack("<H", optional, 0)[0] == 0x20b,
            "Expected PE32+ optional header")
    directories, = unpack("<I", optional, 108)
    require(112 + directories * 8 <= optional_size, "Invalid PE data directory table")
    if directories > 14:
        clr_address, clr_size = unpack("<II", optional, 112 + 14 * 8)
        require(clr_address == 0 and clr_size == 0, "Managed CLR header is forbidden in NativeAOT payload")
    require(sections > 0, "Missing PE sections")
    section_table = region(data, pe + 24 + optional_size, sections * 40)
    if executable:
        require(symbols == 0 and symbol_count == 0, "Main PE contains unstripped COFF symbols")
        for offset in range(0, len(section_table), 40):
            name = section_table[offset:offset + 8].split(b"\0", 1)[0]
            require(not name.startswith((b".debug", b".zdebug", b".stab")),
                    "Main PE contains embedded debug sections")


def verify_elf(data, *, executable=False):
    """Inspect ELF64 headers/sections without readelf, retaining dynamic symbols."""
    require(region(data, 0, 7) == b"\x7fELF\x02\x01\x01", "Expected little-endian ELF64 binary")
    kind, machine, version, _, phoff, shoff, _, ehsize, phentsize, phnum, shentsize, shnum, shstrndx = unpack(
        "<HHIQQQIHHHHHH", data, 16)
    require(machine == 62 and version == 1 and kind in (2, 3), "Expected Linux x64 executable/shared object")
    require(ehsize == 64, "Invalid ELF header size")
    require(phnum > 0 and phentsize >= 56, "Missing ELF program headers")
    region(data, phoff, phnum * phentsize)
    require(any(unpack("<II", data, phoff + i * phentsize)[0] == 1
                and unpack("<II", data, phoff + i * phentsize)[1] & 1 for i in range(phnum)),
            "Missing executable ELF load segment")
    # Section headers are optional in a completely stripped ELF image.
    if shoff == 0:
        require(shnum == 0 and shstrndx == 0, "Invalid missing ELF section table")
        return
    require(shentsize >= 64, "Invalid ELF section header size")

    def section(index):
        return unpack("<IIQQQQIIQQ", data, shoff + index * shentsize)

    first = section(0)
    if shnum == 0:  # ELF extended section count/index encodings.
        shnum = first[5]
    if shstrndx == 0xffff:
        shstrndx = first[6]
    require(shnum > 0 and 0 < shstrndx < shnum, "Invalid ELF section name table")
    region(data, shoff, shnum * shentsize)
    names_section = section(shstrndx)
    require(names_section[1] == 3, "Expected ELF section-name string table")
    names = region(data, names_section[4], names_section[5])
    for index in range(shnum):
        entry = section(index)
        require(entry[0] < len(names), "Invalid ELF section name offset")
        end = names.find(b"\0", entry[0])
        require(end >= 0, "Unterminated ELF section name")
        name = names[entry[0]:end]
        require(not name.startswith((b".debug", b".zdebug", b".stab")) and name != b".gnu_debugdata",
                f"ELF contains embedded debug section {name!r}")
        # Vendor .so files can retain static symbols; do not alter these libraries.
        # SHT_DYNSYM (.dynsym), unwind data and debuglink references are permitted.
        if executable:
            require(entry[1] != 2 and name != b".symtab", "Main ELF contains unstripped static symbols")


def verify(payload: Path, runtime: str):
    require(runtime in EXECUTABLES, "Only win-x64 and linux-x64 payloads are supported")
    require(payload.is_dir() and not payload.is_symlink(), "Expected a staged payload directory")
    metadata_path = payload / "release.json"
    require(not metadata_path.is_symlink(), "Unexpected symlink: release.json")
    metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
    require(isinstance(metadata, dict) and metadata.get("runtime") == runtime,
            "Release runtime does not match expected runtime")
    executable = EXECUTABLES[runtime]
    require(metadata.get("executable") == executable, "Unexpected release executable")
    require((payload / executable).is_file(), "Missing native executable")
    files, total, native = 0, 0, 0
    for path in sorted(payload.rglob("*")):
        relative = path.relative_to(payload)
        require(not path.is_symlink(), f"Unexpected symlink: {relative}")
        if path.is_dir():
            continue
        require(path.is_file(), f"Unexpected non-file payload entry: {relative}")
        name = path.name.lower()
        require(not (name == "musicmachine.desktop.dll" or RUNTIME_LIBRARY.fullmatch(name)
                     or name.endswith((".deps.json", ".runtimeconfig.json", ".runtimeconfig.dev.json",
                                       ".pdb", ".dbg", ".map"))),
                f"Forbidden managed runtime or debug payload: {relative}")
        files += 1
        total += path.stat().st_size
        is_main = relative == Path(executable)
        # Native dependencies must remain bundled. Inspect their format, never strip/remove them.
        is_binary = is_main or name.endswith((".dll", ".exe")) or re.search(r"\.so(?:\..*)?$", name)
        if is_binary:
            try:
                data = path.read_bytes()
                if runtime == "win-x64":
                    verify_pe(data, executable=is_main)
                else:
                    verify_elf(data, executable=is_main)
            except ValueError as error:
                raise ValueError(f"{relative}: {error}") from error
            native += 1
    return {"runtime": runtime, "files": files, "native_binaries": native, "bytes": total}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("payload", type=Path)
    parser.add_argument("runtime", choices=EXECUTABLES)
    args = parser.parse_args()
    try:
        result = verify(args.payload, args.runtime)
    except (OSError, ValueError) as error:
        parser.exit(1, f"Native payload verification failed: {error}\n")
    print(f"Verified {result['runtime']}: {result['native_binaries']} native binaries, "
          f"{result['files']} files, {result['bytes'] / 1048576:.2f} MiB unpacked; "
          "no managed runtime/debug payload (compressed size is reported by packaging)")


if __name__ == "__main__":
    main()
