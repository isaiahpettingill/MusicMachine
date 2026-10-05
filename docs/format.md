# MusicMachine binary formats, version 1

`.song` and `.instrument` are single CBOR documents, not JSON or ZIP files. Both use an explicit reflection-free `System.Formats.Cbor` codec, suitable for NativeAOT. Song files embed complete instrument definitions, custom waves and wavetable frames. They never depend on a file path or the continued existence of a global preset.

`SongFile` and `InstrumentFile` expose `Write`, `Read`, `ReadAsync`, `Validate`, `Clone`, `Save` and `Load`. The asynchronous `ReadAsync(Stream, CancellationToken)` APIs work with browser storage streams and non-seekable sources, read from the current position, and leave stream ownership with the caller. They reject known oversize streams before reading, cap buffering at 16 MiB, and use one extra probe byte to reject unknown-length oversize streams. Cancellation propagates as `OperationCanceledException`. File saves serialize and validate first, then flush a temporary file in the destination directory and rename it over the destination. Failed serialization cannot replace the previous file. The temporary file is removed on handled failures. Rename atomicity and crash durability ultimately depend on the destination filesystem; network shares may provide weaker guarantees.

## Compatibility and validation

All maps use nonnegative integer field keys. Writers emit keys in ascending order and use definite-length maps, arrays and strings. Re-encoding a known model is byte deterministic, including IEEE-754 double values and list order. This is deterministic application encoding, not a claim of RFC canonical-CBOR shortest-float encoding.

The root kind marker and version, the song's four collection fields, and every instrument/track/pattern ID are required. New optional fields can be introduced in version 1: readers skip unknown keys, including bounded nested values. Unknown fields are not preserved when an older application resaves a file. A change in existing field meaning, enum meaning, or required data requires a new version. Readers reject unsupported root or embedded-instrument versions rather than guessing. Enum numbers below are permanent.

Readers reject malformed/truncated CBOR, duplicate or negative field keys, trailing bytes, indefinite containers, unknown sample encoding, invalid enum values, nonfinite numeric values, broken references, duplicate IDs/track lanes, invalid loop ranges and mismatched pattern row/step counts. Invalid input raises `SongFormatException` with a useful explanation. There is no silent truncation of tracks, notes or waves.

Limits are enforced before allocating declared collections and again against the full model:

| Item | Limit |
|---|---:|
| File size | 16 MiB |
| Instruments | 256 |
| Tracks | 64 |
| Patterns | 256 |
| Rows per pattern | 1–1,024 |
| Drum lanes per pattern | 32 |
| Total stored note rows + drum steps | 262,144 |
| Arrangement sections | 1,024 |
| Repeats per section | 1–128 |
| Total expanded playback rows | 1,000,000 |
| FX columns per note | 16 |
| Automation points per track | 8,192 |
| Custom-wave/frame samples | 4,096 |
| Wavetable frames per instrument | 64 |
| IDs / track / instrument / pattern names | 128 UTF-16 code units |
| Song title / author | 256 UTF-16 code units |
| Fields in a known map | 256 |
| Unknown-value nesting | 32 |
| Values in one unknown field | 65,536 |

Tempo is 20–400 BPM; rows/beat one of 1, 2, 4, 8 or 16 and beats/bar 1–16; swing 0–0.75; stored sample rate 8–192 kHz; pan −1…+1; track, instrument and master gains −96…+12 dB. The current renderer uses 48 kHz; `SampleRate` is stored for a future selectable-rate engine. Meter is stored as `BeatsPerBar/BeatUnit`, where the numerator is 1–16 and denominator is 2, 4, 8 or 16. BPM and `RowsPerBeat` always count quarter notes, so changing the denominator changes bar grouping, never row duration: 7/8 contains 3.5 quarter notes and, at four rows/quarter, 14 rows per bar. Older version-1 files without the optional denominator field default to 4. Loop end is exclusive. IDs are opaque case-sensitive strings, never filenames. Songs may have zero melodic tracks but require at least one embedded instrument, one pattern and one arrangement section.

## Song root

| Key | Value |
|---:|---|
| 0 | Text `MusicMachine.song` |
| 1 | Integer version, currently 1 |
| 2 | Title text |
| 3 | Author text |
| 4 | Quarter-note BPM, float64 |
| 5 | Rows per quarter note, integer |
| 6 | Meter numerator (beats per bar), integer |
| 7 | Swing, float64 |
| 8 | Sample rate, integer |
| 9 | Deterministic noise seed, uint32 |
| 10 | Master volume dB, float64 |
| 11 | Loop start section, integer |
| 12 | Loop end section, exclusive integer |
| 13 | Array of embedded instrument maps |
| 14 | Array of track maps |
| 15 | Array of pattern maps |
| 16 | Array of arrangement-section maps |
| 17 | Producer hint text; ignored by reader |
| 18 | Optional meter denominator (`BeatUnit`), integer 2/4/8/16; default 4 |

## Instrument root and embedded instrument

An `.instrument` root has keys `0: "MusicMachine.instrument"`, `1: 1`, `2: embedded instrument map`.

| Embedded key | Value |
|---:|---|
| 0 | ID text |
| 1 | Name text |
| 2 | Waveform enum |
| 3 | Drum-kind enum |
| 4 | Pulse width, float64, 0.01–0.99 |
| 5 | Detune cents, float64, −1200…+1200 |
| 6 | Initial phase, float64, 0–1 |
| 7 | Instrument gain dB, float64 |
| 8 | Four float64 values `[attackMs, decayMs, sustain, releaseMs]` |
| 9 | Pitch-envelope semitones, float64, −96…+96 |
| 10 | Pitch-envelope time milliseconds, float64 |
| 11 | Low-pass cutoff Hz, float64, 20–96,000 |
| 12 | Filter resonance, float64, 0–0.99 |
| 13 | Custom waveform byte string |
| 14 | Array of waveform byte strings |
| 15 | Wavetable position, float64, 0–1 |
| 16 | `IsLocal`, Boolean |
| 17 | Embedded schema version, integer 1 |
| 18 | Sample encoding text `pcm16le` |

Wave bytes are signed 16-bit little-endian PCM samples, with even byte counts. A custom oscillator needs at least two samples; a wavetable needs at least one frame and every frame needs at least two samples. Wavetable frames can have different lengths. Wave samples describe one periodic cycle; they are not external audio samples. Envelope times are 0–60,000 ms, sustain 0–1.

Stable waveform values: `0 Sine`, `1 Triangle`, `2 Saw`, `3 Square`, `4 Pulse`, `5 Noise`, `6 Custom`, `7 Wavetable`.

Stable drum values: `0 None`, `1 Kick`, `2 Snare`, `3 ClosedHat`, `4 OpenHat`, `5 Tom`, `6 Clap`.

Factory presets are freshly allocated by `InstrumentLibrary.CreatePresets()`. `CreateLocalCopy()` round-trips the binary codec, issues a new ID, and marks the copy local. Neither parameter edits nor waveform-array edits to a local instrument can modify a global preset or another song. `ImportToSong()` adds such an independent copy. The `IsLocal` flag is a UI provenance hint; every song embeds every definition regardless of this flag.

## Sampling extraction

The Sampling workspace adds no schema keys and no external-file references. It imports a bounded clip into transient UI state, selects and resamples a representative single period to 128 signed PCM16 samples, removes DC, repairs the seam and offers reversible shaping. Applying commits either `CustomWave` or a new `Wavetable` frame through the same validated `SongEditor.Change` transaction and history as hand-drawn waves. The original audio, file path, selection and pitch-analysis confidence are not serialized. Instruments remain self-contained and compatible with the existing schema version.

Pitch detection is a bounded monophonic estimate, not transcription. A manual period can produce a new timbre from unpitched material. Extracted waveforms are synthesized at the note's requested pitch; they never promise preservation of the original recording or its duration.

## Tracks, patterns and arrangement

Track map: `0 ID`, `1 name`, `2 default instrument ID`, `3 volume dB`, `4 pan`, `5 muted`, `6 solo`, `7 #RRGGBB color`, `8 automation array`. Each automation point is `[absolute song row, additional dB]`; rows are unique and strictly increasing. The engine interpolates dB between points and smooths gain changes.

Pattern map: `0 ID`, `1 name`, `2 row count`, `3 pattern-track array`, `4 drum-lane array`. A pattern-track map has `0 track ID` and `1 note-event array`. Every present track has exactly the pattern length; an absent track means a silent/no-event lane. No extra timing slots are inserted by FX or timing modifiers.

Drum-lane map: `0 name`, `1 instrument ID`, `2 gain dB`, `3 pan`, `4 muted`, `5 byte string of velocities`. Every step occupies one pattern row: `0` is no hit, `1…255` is velocity. Each lane uses the same fully editable synth-instrument model as melodic tracks; drum synthesis is selected by the instrument's drum kind.

Arrangement-section map: `0 pattern ID`, `1 repeat count`, `2 transpose in semitones (−48…+48)`. Patterns can be reused by reference; editing one updates every instance in the arrangement. Loop boundaries address sections.

## Notes and FX

Note-event map: `0 kind`, `1 MIDI pitch`, `2 timing`, `3 instrument ID or null`, `4 array of FX strings`. Kind values: `0 Empty`, `1 Note`, `2 Off`, `3 Cut`. Timing values: `0 Straight`, `1 TripletEighth`, `2 TripletSixteenth`, `3 Swing`.

MIDI 60 is C4. Literal input accepts A–G, an optional sharp, and an optional octave −1…9 within MIDI 0…127: `F`, `F#`, `F4`, `F#4`. Internal spaces between pitch and octave (`F# 4`) are rejected; spaces before a timing suffix are allowed. Omitted octave inherits the preceding pitched row's octave; with no preceding pitched row it is 4. `OFF` releases the envelope, `CUT` stops immediately, and an empty row sustains the previous note. Instrument IDs on rows act as section headers and persist until another header.

`F#4T` retriggers held notes every eighth-note triplet (beat/3). `F#4TT` retriggers every sixteenth-note triplet (beat/6). `F#4 S` applies swing to odd absolute rows by delaying the trigger `Swing × rowDuration`. Modifiers occupy the existing row. They require a pitched note; OFF and CUT remain on-grid. A gate or following note can end triplet retriggering earlier.

FX columns are simultaneous operations on a row, never additional time slots. Hex digits are case-insensitive. Empty columns are allowed. Duplicate command letters and simultaneous up/down slides are rejected with a visible validation error.

| Command | Meaning |
|---|---|
| `Axy` | Cycle root, root+x and root+y semitones in three equal parts of the current row |
| `Vxx` | Persistent channel volume multiplier xx/255; VFF is unity |
| `Gxx` | Gate the new note after xx/255 of its row; G00 stops at the trigger |
| `Uxx` / `Dxx` | Slide pitch up/down by xx semitones/second during this row |
| `Rxx` | Retrigger in xx equal row subdivisions, bounded 0–32 (R00 disables, R01 is one ordinary trigger) |

Pitch timing and FX are stored independently. All notes, gates, automation, instrument headers and drums are evaluated by the same renderer used for realtime playback and offline exports.

## Edit history

`SongEditor.Change(Action<Song>)` runs the action against an independent validated candidate. Exceptions or validation failures leave the document, dirty state and history unchanged. Successful content changes increment `Revision`, push undo state and clear redo. No-op edits do none of those things. Undo/redo restore complete codec snapshots, including instruments and automation. History is bounded to 128 states and approximately 64 MiB per direction. `MarkSaved()` records current bytes; dirty state compares content rather than revision numbers, so undoing exactly to a saved state makes the document clean. `Load()` clears history and marks the new document clean. UI writes must use `Change`; the exposed mutable `Song` object is a read model outside the transaction callback.
