#!/usr/bin/env python3
"""Render MusicMachine's SVG icon with Inkscape; validate PNG and ICO with Pillow.

Run from any directory: python assets/icon/render.py
Requires an existing Inkscape installation and the Pillow Python package.
No downloads, network requests, fonts, or AI image generation are involved.
"""
from pathlib import Path
import os
import shutil
import struct
import subprocess
import tempfile
import xml.etree.ElementTree as ET

from PIL import Image

ROOT = Path(__file__).resolve().parent
PNG_SIZES = (16, 24, 32, 48, 64, 128, 180, 192, 256, 512, 1024)
ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)


def main():
    inkscape = shutil.which("inkscape")
    if inkscape is None:
        raise SystemExit("Install Inkscape from its official source to regenerate these assets.")
    ET.parse(ROOT / "musicmachine.svg")
    with tempfile.TemporaryDirectory(prefix="musicmachine-icon-") as work:
        env = os.environ | {
            "XDG_CONFIG_HOME": str(Path(work) / "config"),
            "XDG_CACHE_HOME": str(Path(work) / "cache"),
        }
        for size in PNG_SIZES:
            subprocess.run([
                inkscape, str(ROOT / "musicmachine.svg"),
                "--export-type=png",
                f"--export-filename={ROOT / f'musicmachine-{size}.png'}",
                f"--export-width={size}", f"--export-height={size}",
            ], env=env, check=True, capture_output=True)
            with Image.open(ROOT / f"musicmachine-{size}.png") as png:
                assert png.size == (size, size)
                assert png.mode == "RGBA"
                assert png.getpixel((0, 0))[3] == 0

    # PNG-backed ICO entries preserve the exact directly rendered small images.
    # Supported by Windows Vista and later and current browser favicon loaders.
    frames = [(ROOT / f"musicmachine-{size}.png").read_bytes() for size in ICO_SIZES]
    offset = 6 + 16 * len(frames)
    entries = []
    for size, data in zip(ICO_SIZES, frames):
        dimension = size if size < 256 else 0
        entries.append(struct.pack("<BBBBHHII", dimension, dimension, 0, 0,
                                   1, 32, len(data), offset))
        offset += len(data)
    (ROOT / "musicmachine.ico").write_bytes(
        struct.pack("<HHH", 0, 1, len(frames)) + b"".join(entries) + b"".join(frames)
    )
    with Image.open(ROOT / "musicmachine.ico") as icon:
        assert icon.ico.sizes() == {(size, size) for size in ICO_SIZES}
        for size in ICO_SIZES:
            assert icon.ico.getimage((size, size)).size == (size, size)
    print(f"Validated SVG, {len(PNG_SIZES)} transparent PNGs, and {len(ICO_SIZES)} ICO frames.")


if __name__ == "__main__":
    main()
