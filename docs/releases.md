# Releases and Cloudflare Pages

The pipeline follows [ComicEditor](https://github.com/isaiahpettingill/comic_editor): platform-native AOT builds, tested installers, an immutable GitHub release and a separate trusted-build Pages deployment. MusicMachine publishes Windows x64 and Linux x64 only.

## Artifacts and installation

Every successful `main` build allocates the next `0.1.x` release, beginning at `0.1.0`. Tags `vX.Y.Z` on the current `main` commit request an explicit version; pull requests and `build/**` validation branches build and test without publishing or deploying. Rerunning an already published commit will not overwrite its release. A partial upload stays draft until all Windows, Linux and browser artifacts pass checks. New pushes cancel superseded runs in the same release group. The release job rechecks the current `main` SHA before uploading and before publishing, so an obsolete build cannot publish after a newer commit. Validation branches have separate concurrency groups.

- `MusicMachine-win-x64-setup.exe`: current-user NSIS installer, Start menu shortcut and Windows uninstall entry. No administrator rights required. It bundles the unmodified official SDL2 2.32.10 x64 runtime after checking upstream's pinned SHA-256. Windows packages are not Authenticode-signed.
- `install-musicmachine.ps1`: Windows bootstrap for the same per-user setup. Inspect it, then run it under your existing PowerShell policy; it does not change execution policy or elevate. `-Version X.Y.Z` pins a release, `-Silent` requests silent setup, and `-InstallDirectory` chooses an absolute destination. It verifies the official versioned setup URL, byte length and SHA-256 before execution.
- `MusicMachine-win-x64.zip`: portable equivalent, including SDL2, icons, docs, examples, licenses and `release.json`.
- `MusicMachine-linux-x64.tar.gz`: native Linux payload. Built on Ubuntu 24.04; requires compatible glibc (2.39 or newer), X11/XWayland, SDL2 and desktop libraries. On Ubuntu: `sudo apt install libsdl2-2.0-0 libx11-6 libice6 libsm6 libfontconfig1 libicu74`.
- `install-musicmachine.sh`: install without sudo into `${XDG_DATA_HOME:-$HOME/.local/share}/musicmachine` and `~/.local/bin`. Downloads its exact version and checks its embedded SHA-256. Alternatively: `bash install-musicmachine.sh --archive MusicMachine-linux-x64.tar.gz --sha256 HASH`. Re-run the latest release's installer to upgrade; `musicmachine-uninstall` removes the managed app and shell integration. Keep songs outside the managed installation directory.
- `MusicMachine-browser-wasm.zip`: portable published browser app.
- `MusicMachine-source.zip`: exact source commit with the matching version stamp.
- `release.json` and `SHA256SUMS`: exact commit, asset sizes, versioned URLs and checksums. On Linux verify with `sha256sum --check SHA256SUMS`; on Windows compare `Get-FileHash FILE -Algorithm SHA256` with the manifest.

Native CI smoke-tests real WAV export on both operating systems. Windows CI installs, reinstalls and uninstalls the actual setup, verifying payload hashes and retained user files. Linux regression tests cover installs, updates, malformed archives, unrelated files and checksums. Headless export does not prove sound-device access on every machine.

## Pages deployment

`Deploy browser to Cloudflare Pages` accepts only a successful trusted `Build and release` run whose commit belongs to `main`. Automatic deployment skips superseded commits; pull-request artifacts never receive deployment credentials. The exact prepared browser artifact is deployed to the existing `music-machine` Pages project or created under the already authorized account. No paid service or billing plan is configured.

The app lives at `/`; `/download/` provides desktop installers and source. `/build.json` identifies the exact deployed version/commit. Files above Pages' 25 MiB limit are gzip-compressed and loaded with the browser's `DecompressionStream`, checking .NET's SHA-256 integrity value before execution. The complete browser ZIP is preserved separately.

GitHub Actions needs these secrets in this repository or the `cloudflare-pages` environment:

- `CLOUDFLARE_API_TOKEN` with access to Pages in the selected account
- Optional `CLOUDFLARE_ACCOUNT_ID` secret or repository variable override. CI otherwise uses the verified account that hosts ComicEditor; the account ID is a non-secret routing identifier

If the token is missing, deployment is skipped cleanly and native release/build checks continue. The owner can configure the token through GitHub's secure settings. Do not paste token values into issues or chat, copy another repository's credentials, or broaden permissions automatically. GitHub releases use the built-in `GITHUB_TOKEN`; no PAT or signing key is required.

To retry deployment without rebuilding, run the Pages workflow manually with the successful build's numeric run ID. The final step checks production HTTP responses, favicon, download links and the expected commit. Browser editing/audio QA is a separate end-to-end check.

## Updater installer integration

`updaterProtocol: 1` is present in both the online release manifest and per-install `release.json` for these updater-aware packages. Before running a Windows setup in staging mode, the app updater must require protocol 1 in the verified target release manifest, then verify it again in the staged payload. Older setup executables must not be probed with unknown flags because they can perform a normal installation.

The Windows setup supports `/S /STAGE /D=ABSOLUTE_PATH` to unpack the complete new payload and its current exact-file uninstaller without changing shortcuts or registry. `/S /REGISTER /D=ABSOLUTE_PATH` refreshes the registration and Start menu link of an already-staged payload without copying files. These modes are mutually exclusive. NSIS requires `/D=` to be last and unquoted even when the path has spaces. Registered in-app updates should stage the verified setup so the uninstaller stays consistent; portable installations can use the ZIP.

Linux's managed `current` link and `releases/build.<id>/app` layout remain stable. The app updater owns download validation, same-volume staging, backup/rollback, process shutdown and restart. It must not follow arbitrary installation symlinks or overwrite an unrelated directory.
