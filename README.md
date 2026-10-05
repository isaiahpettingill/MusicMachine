# MusicMachine

A native, keyboard-first chiptune studio for game music, without emulating a particular sound chip. Built with .NET 11, Avalonia, and NativeAOT.

MusicMachine reopens your last saved project, or starts with an empty song. **File → Open demo song** opens **Neon Orchard**, an original 30-second example. Unsaved recovery is offered separately. Nothing needs an account or internet connection.

## Build and run

Install the .NET SDK pinned by `global.json` (11.0.100-rc.1.26425.128), then:

```sh
dotnet run --project src/MusicMachine.Desktop
```

Realtime audio uses SDL2. On Linux install `libsdl2-2.0-0`; the Windows release bundles the official SDL2 runtime. For a Windows source build, put SDL2.dll beside the executable. WAV, FLAC and QOA export work without SDL2 or an audio device. The Linux desktop uses X11 or XWayland.

```sh
dotnet test tests/MusicMachine.Tests
dotnet publish src/MusicMachine.Desktop -c Release -r linux-x64 -o artifacts/linux-x64
```

NativeAOT needs the platform's native compiler/linker prerequisites. Publishing on each target OS is recommended. For other architectures use the corresponding .NET runtime identifier. The source is cross-platform; check the validation report for platforms actually exercised.

## Browser build

The browser uses the same Avalonia editor and synthesis engine with WebAudio playback, native browser file pickers/downloads, and IndexedDB recovery. An optional, same-origin FFmpeg WebAssembly runtime converts other sampling inputs; external FFmpeg export is desktop-only.

```sh
dotnet workload install wasm-tools
dotnet publish src/MusicMachine.Browser -c Release -o artifacts/browser
```

Serve `artifacts/browser/wwwroot` over HTTP. Cloudflare Pages serves the application at `/` and Windows/Linux installers at `/download/`. Browser playback buffers up to five minutes and limits seek preparation to the first fifteen minutes; longer projects can be exported or played natively.

## Clean workspace

File, Edit, View, Pattern, Track, Instrument and Transport menus hold infrequent commands. Material icon buttons keep playback and pane controls compact, with tooltips and keyboard shortcuts.

Use the center selector for Tracker, Drums, Instrument, Arrangement, Automation or Sampling. Ctrl+L toggles the library and Ctrl+I toggles the inspector; the instrument editor opens in a two-column center layout when space allows. The inspector has collapsible sections. Theme and pane/workspace preferences persist locally (browser preferences use localStorage).

**View → Theme** offers exactly the ComicEditor choices: Solarized light, Solarized dark, Catppuccin Mocha, Catppuccin Latte, Dark and Gruvbox. Mocha is the default. **View → FX columns** hides or shows tracker effect columns without changing the music or selected track.

## Make a loop

- Click a Tracker note cell. Type `F`, `F#`, `F4`, or `F#4`, then Enter. An omitted octave follows the closest earlier note in that track, or defaults to 4. Arrows navigate; Tab changes track; Delete clears; Esc cancels an unfinished edit
- Empty rows sustain. `OFF` releases the envelope; `CUT` stops immediately. `F#4T` and `F#4TT` repeat a held note in eighth/sixteenth triplets. `F#4 S` applies swing to odd rows. Hover a cell for an explanation
- Shift-click selects cells. Ctrl+C/Ctrl+V copy/paste tab-separated blocks transactionally. Paste rejects invalid notes and out-of-bounds blocks without partial changes
- Press **F2** or choose **View → FX reference** to search effect names, adjust ordinary decimal parameters, see examples and insert into the selected row. The right pane can switch between this reference and the instrument editor, or collapse with Ctrl+I. Selecting a note cell preserves its pitch and adds/updates a same-row FX; selecting an FX cell replaces that cell
- FX columns run together with the note: `A37` arpeggio, `VC0` volume, `G80` gate, `U02`/`D02` pitch slide, `R04` retrigger. The exact scales and persistence are in [format.md](docs/format.md)
- Select a sound in the library, then **Track → Insert instrument change** to insert a section header without consuming a row. **Instrument → Make local copy** creates an independent preset and assigns it to the selected track
- Shape amplitude/pitch envelopes, filtering and oscillators in the sound panel. Draw custom waveforms, add wavetable frames, and audition sounds
- Program synthesized percussion in **Drums**. Click steps; right-click for accents; adjust lane controls or generate a starting groove
- In **Arrangement**, reuse and reorder patterns, edit relative track trims and pans, mute/solo tracks, and draw volume automation. Playback loop markers are section boundaries
- Space plays/stops. Ctrl+S saves, Ctrl+Shift+S saves as, Ctrl+O opens, Ctrl+Z undoes, Ctrl+Shift+Z redoes. F1 opens help

The project is a self-contained binary CBOR `.song`; reusable presets are CBOR `.instrument` files. No JSON/ZIP substitution, external preset dependency, or reflection-based serializer is involved. Writes are atomic, history tracks saved content, and unsaved edits receive a recovery snapshot. Desktop startup remembers the saved file path; browser startup keeps a separate IndexedDB copy of the last opened/saved song. New resets the next launch to blank. Missing files fall back to blank with a notice, and retained unsaved recovery stays available in the File menu. See [startup and recovery](docs/startup.md).

## Extract a sound in Sampling

Choose **Sampling** in the center workspace or **File → Import audio for sampling**. WAV and QOA import directly on desktop and in the browser. Drag the source waveform, or set selection bounds numerically, then choose **Find stable cycle**. The detector suggests a representative monophonic period (40–2,000 Hz) and reports confidence. Silence, noisy material and short regions have explicit warnings; for unpitched sounds, adjust the cycle start and period and choose **Extract manual cycle**.

Smooth, drive and sine-blend controls always derive from the original extraction, so reset is lossless. Audition at C4, then create a new instrument, replace the selected waveform, or append a wavetable frame. Applying is an ordinary undoable song edit; replacing a shared instrument affects all its notes. Only the extracted 128-sample periodic waveform is stored. Source clips are transient, and this is waveform synthesis, not full-recording playback or preservation.

Imports are limited to 32 MiB, 30 seconds and 1,440,000 source frames, with strict decoder validation and cancellation. Higher-rate clips can hit the frame cap before 30 seconds. Desktop **File → Configure FFmpeg** accepts an explicitly selected, already installed executable for best-effort conversion of other audio formats; MusicMachine never searches PATH or installs it. Browser builds can provide an optional local FFmpeg WebAssembly conversion runtime; WAV/QOA import remains independent of it. Unsupported, too-long, malformed or cancelled imports preserve the previous extraction.

## Export for Godot

Choose **File → Export audio** for built-in PCM16 WAV, lossless FLAC or QOA on desktop and in the browser. Exports use the same deterministic 48 kHz stereo engine as realtime playback and include the complete arrangement at exact musical length. Loop markers control playback; they do not silently crop the exported arrangement. Compose compatible endpoints or crossfade in your game to prevent a loop seam. Natural release-tail export is also available through the audio API.

Built-in song rendering uses fixed-size audio buffers. Cancel remains available while rendering; browser/non-local saves enter a short Finalizing phase before committing. A storage-provider failure or closing the browser tab during that final save can leave an incomplete destination.

WAV is the baseline interchange format: copy it into your Godot project, set the desired loop import/playback settings, and use an AudioStreamPlayer. `.song` and `.instrument` are MusicMachine source formats, not built-in Godot resources. QOA requires a compatible Godot version or decoder; do not assume every Godot release imports standalone `.qoa` files.

Optional **FFmpeg…** export accepts an explicit, trusted local executable path for MP3/Opus/Ogg/M4A. Nothing is bundled or downloaded, and codec support depends on that FFmpeg build. Cancellation and failures preserve an existing output file.

Headless export is available without opening the UI:

```sh
dotnet run --project src/MusicMachine.Desktop -- --render-demo neon-orchard.wav
dotnet run --project src/MusicMachine.Desktop -- --export song.song music.qoa
```

## Architecture

The layout follows the actual architectural patterns in the author's [ComicEditor](https://github.com/isaiahpettingill/comic_editor) and [Vibe Harder](https://github.com/isaiahpettingill/vibe-harder): a thin platform host, reusable Avalonia UI, explicit editing state and history, NativeAOT-friendly serialization, named theme colors, compact editor chrome, separate durable data and background work.

- **MusicMachine.Core**: song/instrument model, explicit versioned CBOR, validators, note grammar, snapshots/history, presets/demo
- **MusicMachine.Audio**: deterministic synthesis, sample-accurate sequencing, allocation-free audio rendering, SDL2 device lifecycle, WAV/FLAC/QOA export, bounded sample import and optional FFmpeg conversion
- **MusicMachine.App**: shared Avalonia controls, tracker, sound designer, arrangement/automation, drums, waveform sampling, file workflows, desktop updates and recovery
- **MusicMachine.Desktop**: platform startup and headless export entry point
- **MusicMachine.Browser**: WebAssembly host, real Web Audio output, local recovery and optional source-built audio conversion
- **MusicMachine.Tests**: model corruption/roundtrip tests, editing/parser regressions, DSP timing/determinism/allocation tests, audio codec interoperability fixtures

See [the format and timing contract](docs/format.md) and [audio backend documentation](src/MusicMachine.Audio/README.md). The engine has no UI dependency and can support a future Godot adapter; none is required for rendered audio.

## Desktop updates

Windows and Linux release packages offer **Help → Check for updates**. Availability checks can run automatically; downloading and restarting require your choice. Downloads are size/hash verified, and unsaved songs receive durable recovery snapshots before replacement. See [updater behavior and recovery limits](docs/updater.md). Installer scripts and per-user setup are described in [release documentation](docs/releases.md).

## Scope and known limits

This is a standalone synthesizer, not a chip emulator or VST host. Instruments and sample buffers are synthesized; full-sample playback and tracker-format import are not included. Sampling imports short clips only to extract embedded periodic waveforms. Output is currently fixed at 48 kHz/PCM16 for WAV/FLAC/QOA. Editing stops realtime playback before updating the snapshot. Grid refinement preserves event onsets by inserting empty rows; row-relative FX are measured in the newly selected row duration. Coarsening must preserve all events or be rejected. Linux verification does not substitute for Windows runtime testing.

The repository's existing MIT license is preserved. QOA interoperability follows the public reference specification; attribution is included in the audio project.
