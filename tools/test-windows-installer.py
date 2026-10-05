"""Exercise the real per-user NSIS setup without opening the editor."""
import ctypes
from ctypes import wintypes
import hashlib
import json
import os
import shutil
from pathlib import Path
import subprocess
import sys
import tempfile
import time
def verify_installer_source():
    """Portable structural checks only; never claim Windows execution coverage."""
    source = Path(__file__).with_name("windows-installer.nsi").read_text()
    section = source[source.index('Section "MusicMachine"'):source.index('Function un.onInit')]
    assert section.index("Call CheckInstallDirectory") < section.index("SetOverwrite on")
    assert section.index("Call CheckInstallDirectory") < section.index('${INSTALL_FILES}')
    for marker in (".git", "*.csproj", "*.sln", "*.slnx"):
        assert marker in source
    for evidence in ("ReadRegStr $0 HKCU", "MusicMachine.Desktop.exe", "release.json", "Uninstall.exe",
                     "MusicMachine per-user Windows installation v1", "$0.update-$2", "StrLen $3 $2"):
        assert evidence in source
    assert source.count("!insertmacro RequireUnelevatedToken ") == 2
    assert "GetTokenInformation(p r1, i 20" in source
    assert "FindFirstFileW" in source and "FindNextFileW" in source and "0x400" in source
    assert "Delete \"$INSTDIR\\.installer-owned\"" in source
    assert "RequestExecutionLevel user" in source
    print("Windows installer structural checks passed; no installer executed")


if "--source-check" in sys.argv:
    verify_installer_source()
    sys.exit(0)

if sys.platform != "win32":
    raise RuntimeError("Real Windows installer validation requires Windows; no runtime checks were run")
import winreg

def token_is_elevated(token=None):
    """Query TokenElevation; an API failure must never masquerade as unelevated."""
    elevation, returned = wintypes.DWORD(), wintypes.DWORD()
    if not advapi.GetTokenInformation(token or current_token, 20, ctypes.byref(elevation),
                                       ctypes.sizeof(elevation), ctypes.byref(returned)):
        raise ctypes.WinError(ctypes.get_last_error())
    if returned.value != ctypes.sizeof(elevation):
        raise RuntimeError("Unexpected Windows token elevation result")
    return bool(elevation.value)


def token_integrity_rid(token):
    """Read the mandatory integrity SID without changing the token."""
    returned = wintypes.DWORD()
    advapi.GetTokenInformation(token, 25, None, 0, ctypes.byref(returned))
    if ctypes.get_last_error() != 122 or not 0 < returned.value <= 65536:
        raise RuntimeError("Cannot inspect test token integrity")
    data = ctypes.create_string_buffer(returned.value)
    if not advapi.GetTokenInformation(token, 25, data, len(data), ctypes.byref(returned)):
        raise ctypes.WinError(ctypes.get_last_error())
    sid = ctypes.cast(data, ctypes.POINTER(wintypes.LPVOID)).contents.value
    count_pointer = advapi.GetSidSubAuthorityCount(sid)
    if not count_pointer or count_pointer.contents.value == 0:
        raise RuntimeError("Invalid test token integrity SID")
    rid = advapi.GetSidSubAuthority(sid, count_pointer.contents.value - 1)
    if not rid:
        raise RuntimeError("Cannot read test token integrity level")
    return rid.contents.value


def get_test_user_token():
    """Prefer an existing linked token; otherwise ask SAFER to restrict our own.

    NORMALUSER removes Administrator/Power User rights. Flags=0 preserves
    AppLocker/SRP policy checks. Never enable privileges or alter UAC, a token's
    elevation/integrity values, or any persistent account/security setting.
    https://learn.microsoft.com/windows/win32/api/winsafer/nf-winsafer-safercreatelevel
    https://learn.microsoft.com/windows/win32/api/winsafer/nf-winsafer-safercomputetokenfromlevel
    This fallback remains unverified until the genuine Windows suite passes.
    """
    token, returned = wintypes.HANDLE(), wintypes.DWORD()
    if advapi.GetTokenInformation(current_token, 19, ctypes.byref(token),
                                  ctypes.sizeof(token), ctypes.byref(returned)):
        if not token.value or returned.value != ctypes.sizeof(token):
            if token.value:
                kernel.CloseHandle(token)
            raise RuntimeError("WINDOWS INSTALLER VERIFICATION BLOCKED: malformed linked token")
        return token, "linked limited"
    level = wintypes.HANDLE()
    if not advapi.SaferCreateLevel(2, 0x20000, 1, ctypes.byref(level), None):
        raise RuntimeError(f"WINDOWS INSTALLER VERIFICATION BLOCKED: no linked token and "
                           f"SAFER NORMALUSER unavailable (Win32 {ctypes.get_last_error()}); "
                           "use a standard-user runner. No real install was verified.")
    try:
        if not advapi.SaferComputeTokenFromLevel(level, current_token, ctypes.byref(token), 0, None):
            raise RuntimeError(f"WINDOWS INSTALLER VERIFICATION BLOCKED: SAFER could not "
                               f"restrict the test token (Win32 {ctypes.get_last_error()})")
        if not token.value:
            raise RuntimeError("WINDOWS INSTALLER VERIFICATION BLOCKED: SAFER returned no token")
        return token, "SAFER NORMALUSER"
    finally:
        advapi.SaferCloseLevel(level)


def run_with_test_user_token():
    """Test-only re-exec; every token property is checked, no bypass or skip."""
    token, source = get_test_user_token()
    try:
        elevated, integrity = token_is_elevated(token), token_integrity_rid(token)
        print(f"Windows test token: source={source}, elevated={elevated}, integrity=0x{integrity:04x}", flush=True)
        if elevated or integrity > 0x2000:
            raise RuntimeError(f"WINDOWS INSTALLER VERIFICATION BLOCKED: {source} token "
                               f"has elevation={elevated}, integrity=0x{integrity:04x}; a genuine "
                               "standard-user Windows runner/session is required. GitHub-hosted "
                               "Windows runs as administrator with UAC disabled. No token properties, "
                               "accounts or security policy were changed. No real install was verified.")
        class StartupInfo(ctypes.Structure):
            _fields_ = [("cb", wintypes.DWORD), ("lpReserved", wintypes.LPWSTR),
                        ("lpDesktop", wintypes.LPWSTR), ("lpTitle", wintypes.LPWSTR),
                        ("dwX", wintypes.DWORD), ("dwY", wintypes.DWORD),
                        ("dwXSize", wintypes.DWORD), ("dwYSize", wintypes.DWORD),
                        ("dwXCountChars", wintypes.DWORD), ("dwYCountChars", wintypes.DWORD),
                        ("dwFillAttribute", wintypes.DWORD), ("dwFlags", wintypes.DWORD),
                        ("wShowWindow", wintypes.WORD), ("cbReserved2", wintypes.WORD),
                        ("lpReserved2", ctypes.POINTER(ctypes.c_byte)),
                        ("hStdInput", wintypes.HANDLE), ("hStdOutput", wintypes.HANDLE),
                        ("hStdError", wintypes.HANDLE)]
        class ProcessInfo(ctypes.Structure):
            _fields_ = [("hProcess", wintypes.HANDLE), ("hThread", wintypes.HANDLE),
                        ("dwProcessId", wintypes.DWORD), ("dwThreadId", wintypes.DWORD)]
        advapi.CreateProcessWithTokenW.argtypes = [wintypes.HANDLE, wintypes.DWORD,
            wintypes.LPCWSTR, wintypes.LPWSTR, wintypes.DWORD, wintypes.LPVOID,
            wintypes.LPCWSTR, ctypes.POINTER(StartupInfo), ctypes.POINTER(ProcessInfo)]
        advapi.CreateProcessWithTokenW.restype = wintypes.BOOL
        command = ctypes.create_unicode_buffer(subprocess.list2cmdline([
            sys.executable, str(Path(__file__).resolve()), str(setup), str(payload), "--unelevated-child"
        ]))
        startup, process = StartupInfo(), ProcessInfo()
        startup.cb = ctypes.sizeof(startup)
        # LOGON_WITH_PROFILE keeps HKCU tied to this same user's loaded profile.
        if not advapi.CreateProcessWithTokenW(token, 1, sys.executable, command,
                0, None, str(Path.cwd()), ctypes.byref(startup), ctypes.byref(process)):
            error = ctypes.get_last_error()
            raise RuntimeError(
                f"WINDOWS INSTALLER VERIFICATION BLOCKED: cannot launch {source} "
                f"token (Win32 {error}); use a standard-user runner. Real installation "
                "has not been verified."
            )
        try:
            result = kernel.WaitForSingleObject(process.hProcess, 600_000)
            if result != 0:
                kernel.TerminateProcess(process.hProcess, 124)
                kernel.WaitForSingleObject(process.hProcess, 10_000)
                raise RuntimeError("Standard-user Windows installer test timed out or could not be observed")
            code = wintypes.DWORD()
            if not kernel.GetExitCodeProcess(process.hProcess, ctypes.byref(code)):
                raise ctypes.WinError(ctypes.get_last_error())
            if code.value:
                raise RuntimeError(f"Standard-user Windows installer test failed ({code.value})")
        finally:
            kernel.CloseHandle(process.hThread)
            kernel.CloseHandle(process.hProcess)
    finally:
        kernel.CloseHandle(token)


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

kernel = ctypes.WinDLL("kernel32", use_last_error=True)
advapi = ctypes.WinDLL("advapi32", use_last_error=True)
kernel.GetCurrentProcess.restype = wintypes.HANDLE
kernel.CloseHandle.argtypes = [wintypes.HANDLE]
kernel.CloseHandle.restype = wintypes.BOOL
kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
kernel.WaitForSingleObject.restype = wintypes.DWORD
kernel.GetExitCodeProcess.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
kernel.GetExitCodeProcess.restype = wintypes.BOOL
kernel.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]
kernel.TerminateProcess.restype = wintypes.BOOL
advapi.OpenProcessToken.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE)]
advapi.OpenProcessToken.restype = wintypes.BOOL
advapi.GetTokenInformation.argtypes = [wintypes.HANDLE, ctypes.c_int, wintypes.LPVOID,
                                     wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
advapi.GetTokenInformation.restype = wintypes.BOOL
advapi.GetSidSubAuthorityCount.argtypes = [wintypes.LPVOID]
advapi.GetSidSubAuthorityCount.restype = ctypes.POINTER(ctypes.c_ubyte)
advapi.GetSidSubAuthority.argtypes = [wintypes.LPVOID, wintypes.DWORD]
advapi.GetSidSubAuthority.restype = ctypes.POINTER(wintypes.DWORD)
advapi.SaferCreateLevel.argtypes = [wintypes.DWORD, wintypes.DWORD, wintypes.DWORD,
                                   ctypes.POINTER(wintypes.HANDLE), wintypes.LPVOID]
advapi.SaferCreateLevel.restype = wintypes.BOOL
advapi.SaferComputeTokenFromLevel.argtypes = [wintypes.HANDLE, wintypes.HANDLE,
    ctypes.POINTER(wintypes.HANDLE), wintypes.DWORD, wintypes.LPVOID]
advapi.SaferComputeTokenFromLevel.restype = wintypes.BOOL
advapi.SaferCloseLevel.argtypes = [wintypes.HANDLE]
advapi.SaferCloseLevel.restype = wintypes.BOOL
current_token = wintypes.HANDLE()
if not advapi.OpenProcessToken(kernel.GetCurrentProcess(), 0x000B, ctypes.byref(current_token)):
    raise ctypes.WinError(ctypes.get_last_error())
try:
    elevated = token_is_elevated()
    if "--unelevated-child" in sys.argv and elevated:
        raise RuntimeError("Test child is still elevated; no installation attempted")
    if not elevated and token_integrity_rid(current_token) > 0x2000:
        raise RuntimeError("Test token is above medium integrity; no installation attempted")
    if elevated:
        # Prove the real setup refuses all elevated modes before attempting the
        # genuine standard-user runtime suite. No installer payload may appear.
        with tempfile.TemporaryDirectory(prefix="musicmachine-elevation-test-") as temp:
            for mode in ("", "/STAGE", "/REGISTER"):
                destination = Path(temp) / "Must Not Install"
                result = subprocess.run(f'"{setup}" /S {mode} /D={destination}', timeout=120)
                assert result.returncode == 2, "Elevated setup was not refused"
                assert not destination.exists(), "Elevated setup wrote an installation directory"
        run_with_test_user_token()
        print("Windows setup elevated refusal and genuine standard-user-token runtime suite verified")
        sys.exit(0)
finally:
    kernel.CloseHandle(current_token)
# IsUserAnAdmin checks effective token membership, not unfiltered account groups.
if ctypes.windll.shell32.IsUserAnAdmin():
    raise RuntimeError("Windows installer test still has an effective administrator token")

with tempfile.TemporaryDirectory(prefix="musicmachine-setup-test-") as temp:
    root = Path(temp) / "App With Spaces"
    stage = Path(temp) / "Update Stage With Spaces"
    missing = Path(temp) / "Missing Stage"
    # All rejection cases use our disposable root after the clean-runner checks.
    for marker in ("README.md", "LICENSE", ".git", "Foreign.csproj", "Foreign.slnx"):
        foreign = Path(temp) / ("Foreign " + marker.replace(".", "_"))
        foreign.mkdir()
        (foreign / marker).write_bytes(b"unrelated data must survive")
        for mode in ("", "/STAGE"):
            result = subprocess.run(f'"{setup}" /S {mode} /D={foreign}', timeout=120)
            assert result.returncode == 2, "Unrelated/source directory was not refused"
            assert (foreign / marker).read_bytes() == b"unrelated data must survive"
            assert set(p.name for p in foreign.iterdir()) == {marker}, "Rejected target was changed"
    empty = Path(temp) / "Existing Empty Folder"
    empty.mkdir()
    subprocess.run(f'"{setup}" /S /STAGE /D={empty}', check=True, timeout=120)
    assert (empty / "MusicMachine.Desktop.exe").is_file(), "Empty destination was refused"
    assert (empty / ".installer-owned").read_text() == "MusicMachine per-user Windows installation v1"
    result = subprocess.run(f'"{setup}" /S /REGISTER /D={missing}', timeout=120)
    assert result.returncode != 0, "Register accepted a missing payload"
    result = subprocess.run(f'"{setup}" /S /STAGE /REGISTER /D={missing}', timeout=120)
    assert result.returncode != 0, "Mutually exclusive installer modes were accepted"
    subprocess.run(f'"{setup}" /S /STAGE /D={stage}', check=True, timeout=120)
    assert (stage / "MusicMachine.Desktop.exe").is_file()
    assert (stage / "Uninstall.exe").is_file()
    assert not shortcut.exists(), "Stage-only install created shell integration"
    try:
        entry = winreg.OpenKey(winreg.HKEY_CURRENT_USER, key, 0, registry_access)
    except FileNotFoundError:
        pass
    else:
        entry.Close()
        raise AssertionError("Stage-only install changed registration")
    stage_readme = stage / "README.md"
    stage_readme.write_text("Register must not copy payload files")
    staged_song = stage / "keep-my-song.song"
    staged_song.write_bytes(b"retain this song")
    subprocess.run(f'"{setup}" /S /REGISTER /D={stage}', check=True, timeout=120)
    assert stage_readme.read_text() == "Register must not copy payload files"
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, key, 0, registry_access) as entry:
        assert winreg.QueryValueEx(entry, "InstallLocation")[0] == str(stage)
        assert winreg.QueryValueEx(entry, "DisplayVersion")[0] == json.loads((payload / "release.json").read_text())["version"]
    assert shortcut.is_file(), "Registration omitted the Start menu shortcut"
    subprocess.run(f'"{stage / "Uninstall.exe"}" /S _?={stage}', check=True, timeout=120)
    assert not (stage / "MusicMachine.Desktop.exe").exists()
    assert staged_song.read_bytes() == b"retain this song"
    assert not shortcut.exists()
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
        if attempt == 0:
            # Migrate the first released installer, which did not write a marker.
            (root / ".installer-owned").unlink()
            token = "a" * 32
            upgrade_stage = Path(str(root) + ".update-" + token)
            shutil.copytree(root, upgrade_stage)
            (upgrade_stage / ".update-token").write_text(token)
            subprocess.run(f'"{setup}" /S /STAGE /D={upgrade_stage}', check=True, timeout=120)
            assert (upgrade_stage / ".installer-owned").is_file(), "Legacy staged upgrade was refused"
            invalid_stage = Path(str(root) + ".update-" + "b" * 32)
            shutil.copytree(root, invalid_stage)
            (invalid_stage / ".update-token").write_text("c" * 32)
            result = subprocess.run(f'"{setup}" /S /STAGE /D={invalid_stage}', timeout=120)
            assert result.returncode == 2, "Mismatched stage token was accepted"
            assert not (invalid_stage / ".installer-owned").exists()
            # Even registered paths must refuse source-checkout markers.
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
    try:
        entry = winreg.OpenKey(winreg.HKEY_CURRENT_USER, key, 0, registry_access)
    except FileNotFoundError:
        pass
    else:
        entry.Close()
        raise AssertionError("Uninstall registry entry remains")
print("Windows setup install/reinstall/export/uninstall verified")
