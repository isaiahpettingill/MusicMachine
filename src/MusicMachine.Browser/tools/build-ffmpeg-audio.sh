#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
if [[ -d downloads ]]; then
    mkdir -p .ffmpeg-build/downloads
    cp downloads/* .ffmpeg-build/downloads/
fi
node tools/ffmpeg-audio/fetch-sources.mjs
source tools/ffmpeg-audio/emsdk-env.sh
emcc --version | head -1 | grep -F '6.0.3 (6ea9c28c38cdd40c1032fa04400c9d16230ee180)'
[[ "$("$DOTNET_EMSCRIPTEN_NODE_JS" --version)" == v24.18.1 ]]
mkdir -p .ffmpeg-build/source .ffmpeg-build/output
if [[ ! -f .ffmpeg-build/source/ffmpeg-9.0.2/configure ]]; then
    tar -xJf .ffmpeg-build/downloads/ffmpeg-9.0.2.tar.xz -C .ffmpeg-build/source
fi
bash tools/ffmpeg-audio/configure.sh
bash tools/ffmpeg-audio/link.sh
node tools/ffmpeg-audio/package-source.mjs
