# Browser host

This is the same Avalonia editor as the desktop application, hosted by
`Avalonia.Browser` on .NET 11 WebAssembly. Deploy the contents of the published
`wwwroot` directory; its relative URLs support both a site root and a subdirectory
ending in `/`. No server API or account is required.

```sh
dotnet workload install wasm-tools
dotnet publish src/MusicMachine.Browser -c Release -o artifacts/browser
node --test src/MusicMachine.Browser/tests/*.test.js
```

## Audio

`BrowserAudioPlayer` uses the shared deterministic `SynthRenderer` to render a
snapshot, reconstructing earlier oscillator/effect state when seeking. It copies
32 KiB stereo PCM chunks through documented .NET `[JSImport]` interop into a
browser-owned Web Audio buffer. `AudioBufferSourceNode` performs playback,
resampling, and sample-timed looping. SDL is never called by the browser host.
Editing a playing song takes effect after Stop/Play, as on desktop.

Selected loops render only their selected arrangement range and preserve the
requested starting position. Playback buffers are limited to five minutes
(approximately 110 MiB of stereo float PCM); choose a smaller loop, export the
arrangement, or use the native application for longer playback. Reconstructing
state is limited to the first fifteen minutes of an arrangement, preventing an
accidental hours-long seek from locking the browser UI. Preparing a large
buffer can briefly pause the browser UI. Offline exports use the shared managed
WAV/QOA implementation, independently of audio permissions.

Browser sound permissions vary. Capture-phase pointer/keyboard handlers request
AudioContext activation inside a user gesture. A visible, keyboard-accessible
Enable sound notice explains blocked or interrupted output. Pending activation
is canceled by Stop; late promises cannot restart a released player. Position is
based on AudioContext time, so it pauses with suspended output. `pagehide` releases
all sources.

## Files and recovery

The shared editor uses Avalonia storage streams for open/save/download operations.
Its recovery hooks are stored in IndexedDB in this site's origin, and survive a
normal page reload. Recovery is best effort; private browsing, storage limits,
cleared site data, or a different browser/origin can remove or prevent it. Keep
important songs as downloaded `.song` files. No project data is uploaded.

## Verification

The zero-dependency Node tests cover PCM stereo copying, seeking, loop wrapping,
natural completion, repeated Play/Stop, cancellation during a pending audio
permission prompt, successful activation, permission rejection, size validation,
and page-close cleanup. Actual browser/Avalonia smoke tests remain essential in
addition to these bridge tests.

## Interop references

- [.NET WebAssembly JavaScript imports and exports](https://learn.microsoft.com/aspnet/core/client-side/dotnet-interop/)
- [.NET WebAssembly browser app host](https://learn.microsoft.com/aspnet/core/client-side/dotnet-interop/wasm-browser-app)
- [Web Audio buffer playback](https://developer.mozilla.org/docs/Web/API/AudioBufferSourceNode/start)
- [AudioContext resume](https://developer.mozilla.org/docs/Web/API/AudioContext/resume)

The host is also checked against the actual installed .NET 11 runtime's
`dotnet.d.ts`; its `RuntimeAPI` defines `setModuleImports`, `getConfig`, and
`runMain`. The WebAssembly build uses the managed runtime; desktop releases use
NativeAOT.

## Sampling and optional browser conversion (subsequent update)

WAV/QOA imports use the managed decoders without loading this converter. Other
supported inputs (MP3, FLAC, Ogg, Opus, M4A, AAC, AIFF, WMA and CAF) are decoded
locally by a disposable worker using a source-built, audio-only FFmpeg 9.0.2 core.
No audio is uploaded and no runtime CDN, account, SharedArrayBuffer or
cross-origin isolation is needed. Output is mono PCM16 WAV at 48 kHz, passed to
the same managed WAV parser. The browser footer links its license/source page.

Limits are enforced in both the host and native adapter: 32 MiB encoded input,
30 seconds / 1,440,000 decoded output frames, source audio up to 192 kHz/eight
channels, 256 MiB WASM linear memory, and two minutes for the whole job including
download. JS/interoperability buffers add bounded memory beyond the WASM maximum.
Overlong audio is rejected, never silently truncated. Exact 30-second output is
accepted; one extra decoded sample is rejected. Mono downmix averages channels.
Cancel, replacement, page close, errors and timeout terminate the worker. Request
IDs prevent stale results applying to newer input. The adapter frees all decoder
state/input allocations; successful jobs also terminate to release core memory.

### Pinned source build

The core contains stock FFmpeg built-in audio decoders/demuxers, libavutil and
libswresample. No external codec libraries, video encoders, network protocols,
filesystem or threading are enabled. FFmpeg source is unmodified. A small MIT
C adapter calls the library API directly; no ffmpeg.wasm CLI or wrapper is used.
It generates bounded PCM and the browser bridge constructs the WAV header.

The official FFmpeg 9.0.2 archive SHA-256 and release-signing fingerprint are in
`tools/ffmpeg-audio/sources.json`. Its signature was verified. Toolchain packages
are pinned to the official .NET 11 wasm-tools packages at 11.0.0-rc.1.26425.128.
The package names contain Emscripten 6.0.2, while the actual compiler correctly
identifies itself as Emscripten 6.0.3, commit
`6ea9c28c38cdd40c1032fa04400c9d16230ee180`. Both are recorded explicitly, including
package archive hashes and official Node 24.18.1 used during linking.

On Linux x64 with the pinned .NET SDK/workload and bash, make, Python, GnuPG,
tar/xz and Node installed:

```sh
dotnet workload install wasm-tools --skip-manifest-update
cd src/MusicMachine.Browser
npm ci --ignore-scripts
npm run build:ffmpeg
npm run vendor:ffmpeg
npm test
npm run test:runtime
npm run verify:distribution
npm run test:formats   # optional native FFmpeg comparison, requires ffmpeg
npm run test:browser   # requires Chromium/Chrome and ffmpeg
```

Set `DOTNET_ROOT` to the SDK directory if necessary. No Docker or unrecognized
binary download is used. Build preparation downloads only pinned official source
archives/signatures and, if the workload installer removed its original archives,
exact official NuGet package archives for digest verification. Native WAV/QOA
import and the application build remain independent of this optional build.

The final core is 1,306,282 WASM bytes plus 15,206 JS bytes. A clean rebuild in a
second directory produced byte-identical artifacts. Pins in conversion-policy.js
cover both artifact lengths and SHA-256 digests. The generic <=8 MiB chunk format
is retained; this smaller core needs only one part. Assets are fetched lazily
from the app's own origin, checked per-part and after reassembly, and cached by
runtime version. Cache hits are reverified; corruption causes replacement and
cache/storage denial falls back to verified loading. Old versions are cleared
only after the replacement is verified. No binary is checked into git.

The worker compiles the verified WASM bytes and supplies the generated loader's
`instantiateWasm` callback. This pinned Emscripten build does not enable the
optional `wasmBinary` incoming Module API, so passing that property alone is
ignored. Loader-initiated file resolution is rejected; no placeholder URL or
second, unverified WASM download is used. Compilation is asynchronous and the
instance callback is synchronous so initialization errors reject normally.

### Source, licenses and relinking

FFmpeg's selected configuration and runtime license report LGPL-2.1-or-later.
The handwritten adapter/bridge are MIT; Emscripten runtime/system-library notices
are retained separately. Disabling GPL flags alone was not used as the license
check: the configured components, runtime license, source headers and actual
linked inputs were inspected. The previous prebuilt npm core's missing historical
metadata was an evidence gap, not a conclusion of legal noncompliance; it is no
longer used or included in the generated distribution.

The same-origin `ffmpeg-audio-source.tar.xz` contains the original signed FFmpeg
source archive, complete adapter/build source, a relinkable adapter object,
selected build configuration, the installed Emscripten runtime/system-library
source, and notices. Its README explains rebuilding, relinking modified FFmpeg,
and replacing the browser pins. The source bundle and each runtime asset are
below Cloudflare Pages' 25 MiB per-file cap. Publication validates exact binary,
chunk, embedded source/relink hashes and notices, and rejects obsolete runtime
folders. Normal browser ZIP/Pages preparation includes these wwwroot files.
Source/notices must remain available with distributed binaries; this technical
check is not a legal certification or patent assessment.

### Verification

Passed locally: Browser C# Compile, 37 Node bridge/unit/helper tests, 9 actual-WASM/cache
integration tests, signed-source/toolchain checksum checks, a clean reproducible
rebuild, and synthetic conversions for all ten advertised extensions against
native FFmpeg. Differences were at least 94 dB SNR; the newer MOV decoder removes
trailing AAC padding retained by the older native reference. Tests cover corrupt
inputs/cache, chunk/whole-file integrity, cancel/error/timeout/stale worker
lifecycle, exact output limits and failed allocations at the hard memory maximum.
Runtime tests import the verified glue in an isolated directory with no adjacent
WASM file, then use the same initialization helper as the browser worker. This
prevents Node's filesystem fallback from masking browser loader regressions.

Actual Chromium worker/UI smoke and full Avalonia browser publish are still
unverified locally: this environment denies required Unix sockets, including an
escalated isolated Chromium attempt. No GUI reroute was used. The browser CI job
builds the pinned core, runs all checks and a real isolated Chrome worker smoke
(lazy download, cache reuse, cancellation/retry, same-origin requests, malformed
input and long-input rejection) before publication. Local tests do not establish
a pass for that actual-browser CI gate.

Primary references:
- [Official FFmpeg releases and signature fingerprint](https://ffmpeg.org/download.html)
- [FFmpeg license information](https://ffmpeg.org/legal.html)
- [Pinned official Emscripten source](https://github.com/emscripten-core/emscripten/tree/6ea9c28c38cdd40c1032fa04400c9d16230ee180)
- [Official .NET Emscripten packaging](https://github.com/dotnet/emsdk)
- [Cloudflare Pages asset limits](https://developers.cloudflare.com/pages/platform/limits/)

## Full published-app CI smoke

`tools/smoke-published-app.mjs` serves the actual prepared Pages output, not a
standalone JavaScript fixture. The browser job runs it after `prepare-pages`
and retains `browser-app-smoke` PNGs, request diagnostics and a JSON report even
when the smoke fails. It does not publish anything. The JSON also records native
key/modifier delivery and DOM focus before/after each shortcut, plus the final
input state on failure. This passive observer never cancels an event, restores
focus, invokes a managed command, or changes preferences. Pane shortcuts run
back-to-back without an intervening click or refocus, so losing keyboard input
remains a failure rather than being hidden by the harness.

```sh
CI=true CHROMIUM=/path/to/google-chrome \
  node src/MusicMachine.Browser/tools/smoke-published-app.mjs \
  artifacts/pages artifacts/browser-app-smoke
```

Use this only in an authorized CI/remote-builder environment. No local browser
retry or socket-restriction workaround was used here. The dependencies are Node
24 and an installed official Chrome/Chromium; there is no Playwright download.
The helper unit tests exercise only files/parsing/pixels and do not launch Chrome.

The smoke proves, when it passes:

- The exact CI commit's prepared .NET/Avalonia app starts without reported runtime,
  console or resource errors, using the real Pages MIME, gzip, COOP and COEP rules
- A real Avalonia canvas has substantial nonblank rendered pixels; screenshots
  are captured at startup, after pane changes/reload, and during playback
- CDP keyboard Ctrl+L/Ctrl+I events change shared-app pane preferences, persist
  across a real reload, and inverse keyboard toggles prove the new managed view
  consumed the persisted values rather than merely leaving localStorage intact
- Space starts and stops the actual demo through the shared transport and browser
  player. Native AudioBuffer contents are finite, stereo/48 kHz and non-silent;
  a native AnalyserNode sees a nonzero rendered signal and the genuine
  AudioContext clock advances. The original audio destination remains connected
- Ordinary app startup and synthesis do not download the optional converter

Audio instrumentation is injected only by the test through CDP. It calls the
original native Web Audio methods and adds a parallel analyser with an unconnected output;
it does not replace AudioContext, generate substitute audio, alter the app's
output branch, bypass autoplay permissions or use fake media devices. This
verifies actual Web Audio rendering, not sound heard through a physical speaker.

New builds expose `data-musicmachine-ready=true` only after successful managed
startup; failure is marked explicitly. For an already-built older artifact that
lacks this new marker, an explicitly authorized remote run may set
`MUSICMACHINE_REQUIRE_READY_SIGNAL=false`. That compatibility mode still requires
real rendered pixels, managed keyboard/persistence responses and genuine audio;
the JSON report records that the lifecycle marker was not required. Normal CI
requires the marker. No actual full-app Chrome pass is claimed from local syntax
or helper-unit checks; a remote/CI execution result is required.

The analyser observation follows the native API contract: [AnalyserNode works
with its output unconnected](https://developer.mozilla.org/en-US/docs/Web/API/AnalyserNode).
