# Startup, last project, and recovery

A first launch opens an empty pattern with no notes or drum steps. New songs have numbered tracks with no instruments assigned. Create instruments from the Instrument menu. Save instrument keeps a reusable copy in the local instrument history; Export also writes an .instrument file. **File → Open demo song** explicitly opens an example song as an unsaved example; saving that example makes it an ordinary named project.

Startup priority is:

1. An updater resume session restores its song, dirty state, and workspace first
2. An explicit desktop command-line `.song` path opens instead of a remembered project; unrelated unsaved recovery is retained and identified in the status bar
3. On an ordinary launch, an available unsaved recovery prompts **Recover** or **Keep for later**; the latter leaves the last clean project open
4. Otherwise the last successfully opened or saved project reopens
5. With no startup target, or a missing/corrupt target, the editor starts blank; a failed restore has a status-bar explanation

On desktop, the startup target is the absolute `.song` path in the local MusicMachine data directory. The file is read again on launch, so changes made outside the editor are included. A stale target is kept until a successful Open/Save or New replaces it.

In a browser, the last clean project is a CBOR snapshot in IndexedDB. No local filesystem handle or continuing file permission is assumed. Opening/importing a song or completing Save updates this clean snapshot; subsequent edits go to separate unsaved recovery. Browser storage is local to that browser profile and origin. Clearing site data, private browsing, quotas, or browser policies can prevent persistence; storage errors are reported, and downloading a `.song` remains the durable portable copy.

**File → New** honors the normal Save/Discard/Cancel prompt and intentionally clears the startup target. Later unsaved edits to that blank document still receive recovery snapshots. Cancelled or failed file selection, parsing, writing, or final stream disposal does not replace the active project, its path, or its last-clean target. An explicit Save selected in a discard prompt is an independent successful save even if the subsequent Open is cancelled.

Recovery entries have independent session keys. Opening another project, keeping recovery for later, saving a different project, or closing a clean window only clears the current session's recovery. **File → Recover unsaved song…** can return to retained copies; multiple copies can be browsed with **Next copy**. Recovery is validated before replacing the current song, adopted into a new durable session copy, and remains dirty until saved. Unreadable recovery is retained. Autosave writes and cleanup are serialized so a delayed write cannot recreate a discarded document after New or Open. Undoing back to the saved content clears a stale current-session snapshot.

The previous `recovery.song`/IndexedDB `song` slot remains readable for upgrades. Updater preparation preserves an unrelated legacy recovery before writing its fallback snapshot. Update session files are not consumed by restoring them.

Only serialized song content is persisted. Sampling source clips, selection, and shaping workspace are transient; only a waveform actually applied to an instrument belongs to the song.
