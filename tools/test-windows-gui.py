"""Smoke the actual published Windows window, rendering, help, resize and shutdown.

This is deliberately separate from the standard-user installer test. A successful
GUI smoke under a hosted runner's token does not prove installer/update privileges.
Only this script's child process, window and temporary application data are used.
"""
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import time
import traceback
import zlib


def write_png(path, width, height, bgra):
    """Save a top-down 32-bit GDI framebuffer without optional image packages."""
    if width <= 0 or height <= 0 or len(bgra) != width * height * 4:
        raise ValueError("Invalid framebuffer dimensions")
    rgb = bytearray(width * height * 3)
    rgb[0::3], rgb[1::3], rgb[2::3] = bgra[2::4], bgra[1::4], bgra[0::4]
    rows = b"".join(b"\0" + rgb[y * width * 3:(y + 1) * width * 3] for y in range(height))
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xffffffff)
    Path(path).write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
                          + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b""))


def color_count(frame):
    # Alpha is often unset in a GDI framebuffer and is not image content.
    return len({frame[p:p + 3] for p in range(0, len(frame), 4)})


def changed_pixels(before, after):
    if len(before) != len(after) or len(before) % 4:
        raise ValueError("Cannot compare different framebuffer sizes")
    return sum(before[p:p + 3] != after[p:p + 3] for p in range(0, len(before), 4))


class Windows:
    def __init__(self):
        self.user = ctypes.WinDLL("user32", use_last_error=True)
        self.gdi = ctypes.WinDLL("gdi32", use_last_error=True)
        self.callback = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
        def bind(dll, name, result, args):
            function = getattr(dll, name)
            function.restype, function.argtypes = result, args
            return function
        bind(self.user, "SetProcessDPIAware", wintypes.BOOL, [])
        self.user.SetProcessDPIAware()
        bind(self.user, "EnumWindows", wintypes.BOOL, [self.callback, wintypes.LPARAM])
        bind(self.user, "GetWindowThreadProcessId", wintypes.DWORD, [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)])
        bind(self.user, "IsWindowVisible", wintypes.BOOL, [wintypes.HWND])
        bind(self.user, "GetWindowTextW", ctypes.c_int, [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int])
        bind(self.user, "GetClientRect", wintypes.BOOL, [wintypes.HWND, ctypes.POINTER(wintypes.RECT)])
        bind(self.user, "SetWindowPos", wintypes.BOOL, [wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wintypes.UINT])
        bind(self.user, "SetForegroundWindow", wintypes.BOOL, [wintypes.HWND])
        bind(self.user, "PostMessageW", wintypes.BOOL, [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM])
        bind(self.user, "SendMessageTimeoutW", wintypes.LPARAM, [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM, wintypes.UINT, wintypes.UINT, ctypes.POINTER(ctypes.c_size_t)])
        bind(self.user, "GetDC", wintypes.HDC, [wintypes.HWND])
        bind(self.user, "ReleaseDC", ctypes.c_int, [wintypes.HWND, wintypes.HDC])
        bind(self.gdi, "CreateCompatibleDC", wintypes.HDC, [wintypes.HDC])
        bind(self.gdi, "CreateDIBSection", wintypes.HBITMAP, [wintypes.HDC, wintypes.LPVOID, wintypes.UINT, ctypes.POINTER(wintypes.LPVOID), wintypes.HANDLE, wintypes.DWORD])
        bind(self.gdi, "SelectObject", wintypes.HANDLE, [wintypes.HDC, wintypes.HANDLE])
        bind(self.gdi, "BitBlt", wintypes.BOOL, [wintypes.HDC, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wintypes.HDC, ctypes.c_int, ctypes.c_int, wintypes.DWORD])
        bind(self.gdi, "DeleteObject", wintypes.BOOL, [wintypes.HANDLE])
        bind(self.gdi, "DeleteDC", wintypes.BOOL, [wintypes.HDC])
        bind(self.gdi, "GdiFlush", wintypes.BOOL, [])

    def window(self, pid):
        found = []
        @self.callback
        def visit(hwnd, _):
            owner = wintypes.DWORD()
            self.user.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
            title = ctypes.create_unicode_buffer(1024)
            self.user.GetWindowTextW(hwnd, title, len(title))
            if owner.value == pid and self.user.IsWindowVisible(hwnd) and "MusicMachine" in title.value:
                found.append(hwnd)
            return True
        if not self.user.EnumWindows(visit, 0):
            raise ctypes.WinError(ctypes.get_last_error())
        return found[0] if found else None

    def responsive(self, hwnd):
        result = ctypes.c_size_t()
        # WM_NULL with SMTO_ABORTIFHUNG, scoped to our process's verified window.
        if not self.user.SendMessageTimeoutW(hwnd, 0, 0, 0, 2, 5000, ctypes.byref(result)):
            raise RuntimeError("The native window stopped responding")

    def resize(self, hwnd, width, height):
        # Keep our test window visible while capturing only its client area.
        if not self.user.SetWindowPos(hwnd, -1, 0, 0, width, height, 0):
            raise ctypes.WinError(ctypes.get_last_error())
        self.user.SetForegroundWindow(hwnd)

    def key(self, hwnd, key, scan):
        for message, flags in ((0x100, 1), (0x101, 0xc0000001)):
            if not self.user.PostMessageW(hwnd, message, key, flags | scan << 16):
                raise ctypes.WinError(ctypes.get_last_error())

    def capture(self, hwnd, destination):
        self.responsive(hwnd)
        rect = wintypes.RECT()
        if not self.user.GetClientRect(hwnd, ctypes.byref(rect)):
            raise ctypes.WinError(ctypes.get_last_error())
        width, height = rect.right, rect.bottom
        if not 800 <= width <= 4096 or not 500 <= height <= 4096:
            raise RuntimeError(f"Unexpected native window size: {width}x{height}")
        # A real window DC observes the production rendering path, unlike
        # rendering Avalonia controls into a separate test-only bitmap.
        source = self.user.GetDC(hwnd)
        target = self.gdi.CreateCompatibleDC(source)
        bits = wintypes.LPVOID()
        header = ctypes.create_string_buffer(struct.pack("<IiiHHIIiiII", 40, width, -height, 1, 32, 0, width * height * 4, 0, 0, 0, 0))
        bitmap = self.gdi.CreateDIBSection(source, header, 0, ctypes.byref(bits), None, 0)
        previous = self.gdi.SelectObject(target, bitmap) if bitmap and target else None
        try:
            if not source or not target or not bitmap or not bits.value or not previous:
                raise RuntimeError("Cannot allocate the native screenshot")
            if not self.gdi.BitBlt(target, 0, 0, width, height, source, 0, 0, 0x00cc0020):
                raise ctypes.WinError(ctypes.get_last_error())
            self.gdi.GdiFlush()
            frame = ctypes.string_at(bits, width * height * 4)
            write_png(destination, width, height, frame)
            colors = color_count(frame)
            if colors < 64:
                raise RuntimeError(f"Native client framebuffer is blank or incomplete ({colors} colors); see {destination}")
            return frame, {"width": width, "height": height, "colors": colors, "screenshot": Path(destination).name}
        finally:
            if previous: self.gdi.SelectObject(target, previous)
            if bitmap: self.gdi.DeleteObject(bitmap)
            if target: self.gdi.DeleteDC(target)
            if source: self.user.ReleaseDC(hwnd, source)


def run(payload, output):
    if sys.platform != "win32":
        raise RuntimeError("Real native-window smoke requires Windows; no GUI was tested")
    output.mkdir(parents=True, exist_ok=True)
    metadata = json.loads((payload / "release.json").read_text())
    if metadata["runtime"] != "win-x64":
        raise ValueError("Expected the actual published Windows x64 payload")
    executable = payload / metadata["executable"]
    windows, report = Windows(), {"runtime": metadata["runtime"], "commit": metadata.get("commit"), "stages": []}
    try:
        with tempfile.TemporaryDirectory(prefix="musicmachine-gui-") as temporary:
            env = dict(os.environ, MUSICMACHINE_DATA=temporary)
            # Repeated launches exercise clean close and saved view restoration.
            for launch in range(2):
                with (output / f"launch-{launch}.log").open("w") as log:
                    process = subprocess.Popen([str(executable)], env=env, stdout=log, stderr=log)
                    hwnd = None
                    try:
                        deadline = time.monotonic() + 30
                        while not hwnd and time.monotonic() < deadline:
                            if process.poll() is not None:
                                raise RuntimeError(f"Native GUI exited during startup ({process.returncode})")
                            hwnd = windows.window(process.pid)
                            time.sleep(0.1)
                        if not hwnd:
                            raise RuntimeError("Native GUI did not create a visible MusicMachine window")
                        for index, size in enumerate(((1000, 740), (900, 660))):
                            windows.resize(hwnd, *size)
                            time.sleep(2)
                            frame, details = windows.capture(hwnd, output / f"launch-{launch}-size-{index}.png")
                            report["stages"].append({"launch": launch, "stage": "window-resize", **details})
                        windows.key(hwnd, 0x70, 0x3b)  # F1: real keyboard event opens help.
                        time.sleep(1)
                        help_frame, details = windows.capture(hwnd, output / f"launch-{launch}-help.png")
                        changed = changed_pixels(frame, help_frame)
                        if changed < 1000:
                            raise RuntimeError("F1 did not visibly open the native help overlay")
                        report["stages"].append({"launch": launch, "stage": "help-open", "changedPixels": changed, **details})
                        windows.key(hwnd, 0x1b, 0x01)  # Escape: dismiss overlay, leave document clean.
                        time.sleep(1)
                        closed_frame, details = windows.capture(hwnd, output / f"launch-{launch}-help-closed.png")
                        if changed_pixels(help_frame, closed_frame) < 1000:
                            raise RuntimeError("Escape did not visibly dismiss the native help overlay")
                        report["stages"].append({"launch": launch, "stage": "help-close", **details})
                        windows.responsive(hwnd)
                        if not windows.user.PostMessageW(hwnd, 0x10, 0, 0):  # WM_CLOSE
                            raise ctypes.WinError(ctypes.get_last_error())
                        if process.wait(timeout=20) != 0:
                            raise RuntimeError(f"Native GUI exited with failure ({process.returncode})")
                    finally:
                        if process.poll() is None:
                            process.kill()
                            process.wait(timeout=10)
        report["passed"] = True
        print("Actual Windows NativeAOT GUI rendered, resized, opened/dismissed help and closed cleanly twice")
    except BaseException:
        report["passed"] = False
        report["error"] = traceback.format_exc()
        raise
    finally:
        (output / "report.json").write_text(json.dumps(report, indent=2))


if __name__ == "__main__":
    run(Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve())
