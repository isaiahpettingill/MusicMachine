#!/usr/bin/env bash
# SPDX-License-Identifier: 0BSD
set -euo pipefail

# Filled by the Linux release job. Source checkouts support --archive.
RELEASE_REPOSITORY='@REPOSITORY@'
RELEASE_VERSION='@VERSION@'
RELEASE_SHA256='@SHA256@'

die() { printf 'MusicMachine: %s\n' "$*" >&2; exit 1; }
usage() {
    cat <<'EOF'
Usage: bash install-musicmachine.sh [--archive FILE] [--sha256 HASH]
       bash install-musicmachine.sh --uninstall

Installs MusicMachine for the current Linux user, without sudo.
The release script downloads its matching Linux x64 version with a pinned SHA-256.
--archive uses an already downloaded archive (also works from a source checkout).
--sha256 supplies an expected checksum for a local archive.
Download the latest installer to update. Re-run this file to repair. Songs are retained.
Uninstall with musicmachine-uninstall.
Locations: ${XDG_DATA_HOME:-$HOME/.local/share}/musicmachine and ~/.local/bin.
MUSICMACHINE_BIN_DIR can override the command directory.
EOF
}

archive=''; expected=''; uninstall=false
while (($#)); do
    case "$1" in
        --archive|--sha256)
            (($# >= 2)) || die "Missing value for $1"
            if [[ "$1" == --archive ]]; then archive="$2"; else expected="$2"; fi
            shift 2 ;;
        --uninstall) uninstall=true; shift ;;
        --repair) shift ;; # Accepted for older instructions; repair is automatic.
        --help|-h) usage; exit 0 ;;
        *) die "Unknown option: $1 (use --help)" ;;
    esac
done
[[ $(uname -s) == Linux ]] || die 'This installer requires Linux.'
[[ -n ${HOME:-} && "$HOME" == /* ]] || die 'HOME must be an absolute path.'
data_home="${XDG_DATA_HOME:-$HOME/.local/share}"
[[ "$data_home" == /* ]] || data_home="$HOME/.local/share"
bin_home="${MUSICMACHINE_BIN_DIR:-$HOME/.local/bin}"
[[ "$bin_home" == /* ]] || die 'MUSICMACHINE_BIN_DIR must be absolute.'
# Desktop Exec paths cannot contain '='; exclude control characters as well.
for path in "$data_home" "$bin_home"; do
    [[ "$path" != *$'\n'* && "$path" != *$'\r'* && "$path" != *$'\t'* && "$path" != *=* ]] || die 'Unsupported character in installation path.'
done
data_home=$(realpath -m -- "$data_home")
bin_home=$(realpath -m -- "$bin_home")
root="$data_home/musicmachine"
desktop="$data_home/applications/io.github.isaiahpettingill.MusicMachine.desktop"
icon="$data_home/icons/hicolor/scalable/apps/io.github.isaiahpettingill.MusicMachine.svg"
marker='MusicMachine per-user installation v1'
[[ ! -L "$root" ]] || die "Installation directory is a symlink: $root"
if [[ -e "$root" ]]; then
    [[ -f "$root/.installer-owned" && $(cat "$root/.installer-owned") == "$marker" ]] || die "Refusing to replace an unmanaged directory: $root"
fi

destinations=("$bin_home/musicmachine" "$bin_home/musicmachine-uninstall" "$desktop" "$icon")
targets=("$root/launch" "$root/uninstall" "$root/musicmachine.desktop" "$root/current/musicmachine.svg")
owned_destination() {
    local destination="$1" target="$2"
    if [[ -L "$destination" && $(readlink -- "$destination") == "$target" ]]; then return 0; fi
    if ! "$uninstall" && [[ -d "$root" && ( "$destination" == "$desktop" || "$destination" == "$icon" ) ]]; then return 0; fi
    [[ ( "$destination" == "$desktop" || "$destination" == "$icon" ) && ! -L "$destination" && -f "$destination" && -f "$target" ]] && cmp -s -- "$destination" "$target"
}
refresh_desktop() {
    if command -v update-mime-database >/dev/null && [[ -d "$data_home/mime" ]]; then update-mime-database "$data_home/mime" >/dev/null 2>&1 || true; fi
    if command -v update-desktop-database >/dev/null; then update-desktop-database "$data_home/applications" >/dev/null 2>&1 || true; fi
    if command -v gtk-update-icon-cache >/dev/null; then gtk-update-icon-cache -f -t "$data_home/icons/hicolor" >/dev/null 2>&1 || true; fi
    # update-desktop-database updates MIME associations, not Plasma's application menu.
    local cache_tool
    for cache_tool in kbuildsycoca6 kbuildsycoca5; do
        if command -v "$cache_tool" >/dev/null; then
            if ! "$cache_tool" --noincremental > /dev/null 2>&1; then
                printf 'Plasma menu refresh was unavailable. Run %s --noincremental in your desktop session.\n' "$cache_tool" >&2
            fi
            break
        fi
    done
}
if "$uninstall"; then
    [[ -d "$root" ]] || { printf 'MusicMachine is not installed here.\n'; exit 0; }
    for i in "${!destinations[@]}"; do
        if owned_destination "${destinations[i]}" "${targets[i]}"; then
            rm -- "${destinations[i]}"
        fi
    done
    rm -rf -- "$root"
    refresh_desktop
    printf 'MusicMachine uninstalled. Songs outside the application directory were retained.\n'
    exit 0
fi
[[ $(uname -m) == x86_64 ]] || die 'This release provides Linux x64 only.'
for tool in tar sha256sum mktemp install readlink; do command -v "$tool" >/dev/null || die "Required command not found: $tool"; done
for i in "${!destinations[@]}"; do
    destination="${destinations[i]}"
    if [[ -e "$destination" || -L "$destination" ]]; then
        owned_destination "$destination" "${targets[i]}" || die "Refusing to replace an unrelated file: $destination"
    fi
done

mkdir -p -- "$data_home"
stage=$(mktemp -d "$data_home/.musicmachine-install.XXXXXXXX")
trap 'rm -rf -- "$stage"' EXIT
if [[ -z "$archive" ]]; then
    [[ "$RELEASE_REPOSITORY" != @* && "$RELEASE_REPOSITORY" == */* ]] || die 'Use the installer from a GitHub release, or supply --archive FILE.'
    url="https://github.com/$RELEASE_REPOSITORY/releases/download/v$RELEASE_VERSION/MusicMachine-linux-x64.tar.gz"
    archive="$stage/MusicMachine-linux-x64.tar.gz"
    printf 'Downloading MusicMachine %s…\n' "$RELEASE_VERSION"
    download() {
        if command -v curl >/dev/null; then
            curl --fail --location --retry 3 --proto '=https' --proto-redir '=https' --tlsv1.2 --output "$2" "$1"
        elif command -v wget >/dev/null; then
            wget --https-only -O "$2" "$1"
        else die 'Install curl or wget, or use --archive FILE.'; fi
    }
    download "$url" "$archive"
    expected="$RELEASE_SHA256"
fi
[[ -f "$archive" ]] || die "Archive not found: $archive"
actual=$(sha256sum < "$archive"); actual=${actual%% *}
if [[ -n "$expected" ]]; then
    [[ "$expected" =~ ^[0-9a-fA-F]{64}$ ]] || die 'Invalid SHA-256 checksum.'
    [[ "${actual,,}" == "${expected,,}" ]] || die 'Archive checksum mismatch; installation was not changed.'
fi
# Published archives contain only regular files and directories, never links.
tar -tzf "$archive" > "$stage/entries"
while IFS= read -r entry; do
    [[ "$entry" != /* && "/$entry/" != */../* ]] || die 'Unsafe archive path.'
done < "$stage/entries"
tar -tvzf "$archive" > "$stage/details"
if grep -q '^[^-d]' "$stage/details"; then die 'Archive contains unsupported links or special files.'; fi
mkdir "$stage/app"
tar -xzf "$archive" --no-same-owner --no-same-permissions -C "$stage/app"
for file in MusicMachine.Desktop musicmachine.svg; do
    [[ -f "$stage/app/$file" ]] || die "Archive is missing $file"
done
chmod +x "$stage/app/MusicMachine.Desktop"
if command -v ldd >/dev/null; then
    for binary in "$stage/app/MusicMachine.Desktop" "$stage/app/"*.so; do
        [[ -f "$binary" ]] || continue
        dependencies=$(ldd "$binary" 2>&1 || true)
        if [[ "$dependencies" == *'not found'* ]]; then
            printf '%s\n' "$dependencies" >&2
            die 'Missing Linux libraries. See the Linux installation section in README.md; the existing installation was not changed.'
        fi
    done
fi

install -m 755 -- "${BASH_SOURCE[0]}" "$stage/installer.sh"
mkdir -p -- "$root/releases" "$bin_home" "$(dirname "$desktop")" "$(dirname "$icon")"
printf '%s\n' "$marker" > "$root/.installer-owned"
version=$(mktemp -d "$root/releases/build.XXXXXXXX")
mv -- "$stage/app" "$version/app"
old=$(readlink "$root/current" 2>/dev/null || true)
ln -s "$version/app" "$stage/current"
mv -Tf -- "$stage/current" "$root/current"
# Wrappers retain their actual paths, including spaces, without editing shell profiles.
printf '#!/usr/bin/env bash\nexec %q "$@"\n' "$root/current/MusicMachine.Desktop" > "$root/launch"
install -m 755 -- "$stage/installer.sh" "$root/installer.sh"
printf '#!/usr/bin/env bash\nexport XDG_DATA_HOME=%q\nexport MUSICMACHINE_BIN_DIR=%q\nexec bash %q --uninstall\n' "$data_home" "$bin_home" "$root/installer.sh" > "$root/uninstall"
chmod 755 "$root/launch" "$root/uninstall"
# Exec uses Desktop Entry quoting, which is distinct from shell quoting.
exec_path="$root/launch"
exec_path=${exec_path//\\/\\\\}; exec_path=${exec_path//\"/\\\"}
exec_path=${exec_path//\$/\\\$}; exec_path=${exec_path//\`/\\\`}
exec_path=${exec_path//\\/\\\\}; exec_path=${exec_path//%/%%}
cat > "$root/musicmachine.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=MusicMachine
GenericName=Chiptune Studio
Comment=Compose and export original game music
Exec="$exec_path"
Icon=io.github.isaiahpettingill.MusicMachine
Terminal=false
Categories=AudioVideo;Audio;Midi;
Keywords=music;chiptune;tracker;synthesizer;
StartupNotify=true
StartupWMClass=MusicMachine
X-MusicMachine-Managed=true
EOF
chmod 644 "$root/musicmachine.desktop"
for i in "${!destinations[@]}"; do
    if [[ "${destinations[i]}" == "$desktop" || "${destinations[i]}" == "$icon" ]]; then
        # Stable regular files let desktop environments refresh the launcher and icon.
        install -m 644 -- "${targets[i]}" "$stage/link"
    else
        ln -s -- "${targets[i]}" "$stage/link"
    fi
    mv -Tf -- "$stage/link" "${destinations[i]}"
done
refresh_desktop
# Only remove the previous managed payload; keep documents and font caches elsewhere.
if [[ "$old" == "$root/releases/"*/app && "$old" != "$version/app" ]]; then
    previous=${old%/app}
    [[ $(dirname "$previous") == "$root/releases" && ! -L "$previous" ]] && rm -rf -- "$previous"
fi
printf 'Installed MusicMachine. Open it from your application menu or run:\n  %s\nUninstall: %s\nDownload the latest installer to update.\n' "$bin_home/musicmachine" "$bin_home/musicmachine-uninstall"
case ":$PATH:" in *":$bin_home:"*) ;; *) printf 'For terminal commands, add %s to PATH.\n' "$bin_home" ;; esac
