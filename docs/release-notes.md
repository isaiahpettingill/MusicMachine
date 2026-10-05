Native Windows x64 and Linux x64 builds, plus the browser app.

- Windows: run `MusicMachine-win-x64-setup.exe` for a per-user installation, or extract the portable ZIP. SDL2 is included. No .NET installation is needed.
- Linux: download `install-musicmachine.sh`, inspect it, then run `bash install-musicmachine.sh`. The installer uses a version-pinned archive and SHA-256, installs without sudo, and adds an application-menu launcher. It needs an x64 glibc system compatible with Ubuntu 24.04, X11/XWayland, SDL2, fontconfig and standard desktop libraries.
- Browser: the Cloudflare Pages deployment runs after this release pipeline succeeds. The website has the same editing/synthesis core; browser file and audio capabilities differ from desktop.
- `release.json` gives exact commit, download URLs, sizes and SHA-256 hashes. `SHA256SUMS` verifies the six main artifacts. The source ZIP includes the release version stamp.

The Windows package is not Authenticode-signed. Check the official repository and checksum if Windows warns about an unknown publisher. Checksums protect integrity, not an independent publisher identity. No silent auto-update or new credential setup is installed.
