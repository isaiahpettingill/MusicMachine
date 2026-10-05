# Audio runtime contract

`SynthRenderer` is UI-independent and snapshots the song at construction. One owner calls `Render(Span<float>)`; interleaved stereo float32 output is always 48,000 Hz. `Song.SampleRate` is retained file metadata, not a resampling request in this version. The renderer returns the frame count and clears unused output. `MusicalFrames` is the exact arrangement duration; `TotalFrames` includes up to the maximum instrument release plus 50 ms. `Reset` and uninterrupted rendering allocate no managed memory. `Seek` silently reconstructs preceding oscillator, noise, automation and FX state and should be done before the audio callback starts.

## Notes and effects

- Empty rows sustain, `OFF` releases ADSR, `CUT` silences immediately
- Instrument headers persist across patterns/repeats; held voices keep their old instrument, and subsequent notes use the new one
- Each synth track is monophonic. New notes reset oscillator/envelope and have a 64-sample transition from the previous sample. Attack has a one-millisecond safety floor
- `Axy` cycles root, +x, +y semitones across equal thirds of this row, then expires at the next row
- `Vxx` is persistent linear volume `xx / 255`, multiplicative with track trim and automation
- `Gxx` releases the note at `xx / 255` of the current row; it can also be applied to a held note on an FX-only row
- `Uxx` / `Dxx` are row-local pitch slides at `xx` semitones per second; accumulated pitch remains until the next note/retrigger
- `Rxx` divides the current row into `xx` equal subdivisions, with retriggers between boundaries; `00` disables, maximum `20` hex (32). `R01` has no intermediate retrigger
- `T` / `TT` repeat a held note every one-third / one-sixth of a beat until a new note, OFF, CUT or gate. Retrigger restarts ADSR and pitch slide
- `S` delays notes on odd absolute arrangement rows by `Song.Swing` times one row. OFF, CUT and empty rows remain on-grid
- FX columns apply together. The file validator rejects duplicate commands and contradictory up/down slides. Blank FX are ignored

Oscillators include polyBLEP saw/square/pulse, sine, triangle, deterministic xorshift noise, interpolated PCM16 custom cycles and normalized-position wavetable interpolation. A stable biquad lowpass limits cutoff to 20 kHz at the fixed sample rate. ADSR/pitch-envelope times support 0–60,000 ms. Drum lanes synthesize kick, snare, hats, tom and clap; velocity is linear amplitude. Any solo synth track suppresses nonsolo synth tracks and drum lanes. Track gain changes and linearly interpolated dB automation are smoothed over 5 ms. Final tanh saturation protects output bounds. Native files validate controls; DSP also sanitizes nonfinite/out-of-range numeric controls as a final safety measure.

The renderer caps the expanded event schedule at two million events before allocation. Long repeated songs can use realtime section loops instead of expanding huge arrangements.

## Realtime

`AudioPlayer.Play(song, startFrame: 0, loop: false)` uses SDL2's native float32 callback. Playback position is the sample-render clock, potentially ahead of the speaker by the device buffer. Loop mode uses the model's inclusive start/exclusive end arrangement sections and precomputed state restore without callback allocation. Loop state is deterministic, but waveform-continuous seams are not guaranteed.

`Stop` pauses/closes the device and waits for callbacks before releasing its GC handle. `Dispose` is idempotent. Control-side play/stop serialize; the audio callback never takes the control lock. Missing library/device is an actionable `InvalidOperationException`, and offline export remains available. UI edits require restarting playback. SDL2 must be installed: Linux `libsdl2-2.0-0`; macOS SDL2/Homebrew; Windows SDL2.dll beside the app. SDL2 is loaded dynamically so NativeAOT does not require a managed audio wrapper.

## Offline and optional FFmpeg

`OfflineExporter.Render`, `WriteWav`, and `WriteQoa` use the same renderer. Their default is exact musical length for game-loop duration, with `includeTail: true` for finite music. Export is the full arrangement, not the editor's loop selection. Matching endpoints or a game-side crossfade are needed for seamless loops. WAV is PCM16 stereo. QOA is PCM16 stereo, fully managed, based on the official MIT reference codec; see `QOA-LICENSE.txt`. Destination replacement is atomic after success and cancellation leaves existing files intact.

`WriteFfmpegAsync(absoluteExecutablePath, destination, song, token)` is optional. It does not search PATH, download software, or invoke a shell. The caller explicitly configures a trusted FFmpeg executable. Generated little-endian float32 PCM streams via stdin. Supported destination suffixes are FLAC, MP3, Opus, Ogg and M4A; installed FFmpeg must include the corresponding encoder. Unsupported suffixes, a missing executable, cancellation, or encoder failure report an error. Output uses a temporary sibling file and is moved only after a successful exit; existing destination files are preserved on failure.

## Verification

Audio tests cover deterministic block rendering and immutable snapshots; A440 and transposition; every oscillator/drum voice; sustain/OFF/CUT; simultaneous FX and FX-only rows; triplets/swing; instrument persistence; automation; mute/solo/pan; seek/reset; bounded output; zero callback allocation; WAV headers/exact duration; cancellation safety; QOA frame/slice boundaries, malformed input and >35 dB sine SNR. A golden QOA fixture is produced by the official C reference encoder and decoded to a reference SHA-256.

External verification during implementation also decoded the generated 48,000-frame QOA with the official C decoder and byte-compared it with the managed decoder. A sine render measured 73.45 dB QOA SNR. SDL2 dummy output exercised repeated start/stop; this confirms lifecycle behavior, not sound heard from a physical device.
