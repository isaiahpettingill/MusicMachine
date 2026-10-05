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
