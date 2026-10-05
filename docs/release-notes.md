Native Windows x64 and Linux x64 builds.

- Windows: run `MusicMachine-win-x64-setup.exe` for a per-user installation, or extract the portable ZIP. SDL2 is included. No .NET installation is needed.
- Linux: download `install-musicmachine.sh`, inspect it, then run `bash install-musicmachine.sh`. The installer uses a version-pinned archive and SHA-256, installs without sudo, and adds an application-menu launcher. It needs an x64 glibc system compatible with Ubuntu 24.04, X11/XWayland, SDL2, fontconfig and standard desktop libraries.
- Browser: the same editing/synthesis core is built and tested independently. Cloudflare Pages deploys only a browser build that passes its own basic tests and build; a browser failure does not delay native releases. Browser build ZIPs are retained as CI artifacts.
- `release.json` gives exact commit, download URLs, sizes and SHA-256 hashes. `SHA256SUMS` verifies the six main artifacts. The source ZIP includes the release version stamp.

The Windows package is not Authenticode-signed. Check the official repository and checksum if Windows warns about an unknown publisher. Checksums protect integrity, not an independent publisher identity. No silent auto-update or new credential setup is installed.
