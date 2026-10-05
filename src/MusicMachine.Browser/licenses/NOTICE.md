# MusicMachine optional browser audio converter

This software uses code of FFmpeg 9.0.2, licensed under LGPL version 2.1 or later.
The exact official source archive, all MusicMachine adapter/build source, a
relinkable adapter object, build configuration, and the Emscripten runtime/system
library source are available beside this notice in ffmpeg-audio-source.tar.xz.
The source bundle includes rebuild/relink instructions. FFmpeg itself is unmodified.

This audio-only build enables built-in audio decoders and demuxers, libavutil,
and libswresample. No external codec libraries, video encoders, GPL/nonfree
components, network protocols, filesystem, or threading are enabled. The build's
configuration and runtime avcodec_license() report LGPL-2.1-or-later. The full
FFmpeg license notices are retained in the source archive and FFmpeg-LICENSE.md;
see LGPL-2.1.txt for the full applicable license.

MusicMachine's small C adapter and handwritten browser bridge use the repository's
MIT license (MusicMachine-MIT.txt). Emscripten 6.0.3 generated runtime/glue is under
its MIT/University of Illinois-NCSA terms (Emscripten-LICENSE.txt), with applicable
system-library notices retained (including musl and compiler-rt). No ffmpeg.wasm
wrapper or its GPL-enabled prebuilt core is part of this runtime.

The WebAssembly core is statically linked. The provided adapter source/object and
build scripts let you modify FFmpeg and rebuild/relink it. You may replace the
core and regenerate the manifest/digest pins for your modified MusicMachine build;
see the source bundle README. MusicMachine does not restrict reverse engineering
for debugging modifications to the library. The application and converter are
provided without warranty. Source availability is separate from any patent or
jurisdiction-specific distribution considerations; this notice is not legal advice.

Source and toolchain versions, SHA-256 digests and the official FFmpeg signature
fingerprint are recorded in SOURCE-PROVENANCE.json. The prior npm release's missing
historical dependency metadata was an evidence gap, not a legal determination.
This build avoids that ambiguity by supplying its actual selected build inputs.
