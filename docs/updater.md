# Desktop updates

Packaged Windows x64 and Linux x64 releases can check for stable releases from `isaiahpettingill/MusicMachine`. The Help menu provides **Check for updates**, **Download update**, and **Restart and install** as separate actions. An optional availability check runs 15 seconds after startup, then every four hours. Disable it in the update dialog. Checks never install or download a package automatically. Close/Cancel interrupts network and preparation work.

The browser build has no desktop updater. Save your song before reloading the hosted website. Development builds, source checkouts, unsupported runtimes, elevated Windows processes, and system-owned or unsafe installation directories do not update themselves.

## Trust and package contract

GitHub HTTPS and matching SHA-256/byte length protect transport and downloaded-file integrity. **Packages are not Authenticode-signed or independently publisher-signed.** There is no private signing key, and a checksum from the same release does not provide independent publisher authentication.

Only exact versioned URLs under `https://github.com/isaiahpettingill/MusicMachine/releases/download/vX.Y.Z/` are accepted from the GitHub stable-release API and schema-1 `release.json`. Metadata is bounded and versions, runtime, commit, names, sizes, hashes, duplicate fields, and archive paths/types are checked. Downloads use temporary files, cancellation and deadlines; incomplete downloads never replace a verified cached package. Redirects are limited to GitHub's HTTPS release-serving hosts.

A payload's `release.json` identifies its product, version, full commit, runtime, executable and repository. Windows registered installations use `MusicMachine-win-x64-setup.exe` only when the online manifest advertises `updaterProtocol: 1`. The setup's `/STAGE` mode must write the new payload/uninstaller without registry or shortcut side effects. `/REGISTER` refreshes the current-user registration after the swap. Portable Windows installs use `MusicMachine-win-x64.zip`. Linux uses `MusicMachine-linux-x64.tar.gz`. Archives unpack directly into their payload root, with optional `./` tar prefixes.

## Restart and recovery

Restart offers Save, Preserve, or Cancel for a dirty song. Save cancellation cancels restart. Song contents, saved/unsaved state, filename, selected pattern/instrument, and supported workspace are snapshotted before the editor exits. Normal crash recovery also receives a plain `.song` copy. Imported Sampling audio must be applied to an instrument first; transient imported audio and undo history are not song data and are not restored. If a source clip is loaded, restarting requires a separate warning confirmation, even for a clean song. Active sample import or analysis must finish or be cancelled first.

A copied native host runs as the update helper, acknowledges readiness, and waits for both explicit approval and the original process to exit. Its per-install exclusive lock prevents concurrent helpers from racing. The replacement is staged next to the installation so directory moves remain on the same volume. Unrelated files are copied forward; arbitrary symlinks, special files, foreign-writable Linux paths, and unsafe source/build locations are refused. The Linux installer's recognized `current` alias keeps pointing to the same physical application path.

The helper retains the old installation in a uniquely named `.previous-<token>` sibling, rolls back ordinary apply/startup failures, and restarts with the song snapshot. It never kills a running editor just because startup acknowledgment is slow. Recovery files and the previous application are intentionally retained after success. Failed/cancelled preparation can therefore leave recovery or work files in MusicMachine's local application-data `updates` folder; these are not silently wiped. If space is needed, close MusicMachine, verify your songs are saved, then manually remove old update folders/backups you no longer need.

The directory replacement uses two same-volume renames with rollback on reported errors. It is **not a power-loss transaction**: if the helper or machine stops between those renames, the previous application may need to be restored manually from its retained sibling backup. Never delete that backup until the updated application starts successfully.

## Verification

`dotnet test tests/MusicMachine.Tests -c Release` covers metadata/runtime selection, HTTP failures/cancellation, bad lengths/hashes, path traversal, archive links/special files/bombs, safe staging, apply retry/rollback, concurrent helper exclusion, unrelated-file preservation, durable recovery and restart approval. Headless UI checks cover browser/development gating, preference persistence, repeated dialogs, cancellation, and recovery-preserving shutdown. These tests use temporary fixture installs. Actual Windows NSIS integration remains a Windows-CI responsibility; no updater test should replace a user's running app.

After publishing a Linux native executable, `python3 tools/test-updater-helper.py /path/to/MusicMachine.Desktop` exercises the real native helper against disposable synthetic installs. It verifies successful swap/startup acknowledgment and early-startup failure rollback. Restart targets are shell fixtures, so no GUI or real installed application is launched or replaced.
