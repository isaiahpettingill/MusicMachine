#!/usr/bin/env bash
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
source "$HERE/emsdk-env.sh"
SOURCE="$ROOT/.ffmpeg-build/source/ffmpeg-9.0.2"
OUTPUT="${1:-$ROOT/.ffmpeg-build/output}"
mkdir -p "$OUTPUT"
ADAPTER="${MUSICMACHINE_ADAPTER_OBJECT:-$OUTPUT/audio-adapter.o}"
if [[ -z "${MUSICMACHINE_ADAPTER_OBJECT:-}" ]]; then
    emcc "$HERE/audio-adapter.c" -I"$SOURCE" -Oz -ffile-prefix-map="$ROOT"=. -c -o "$ADAPTER"
fi
[[ -r "$ADAPTER" ]]
emcc "$ADAPTER" -Oz \
    -Wl,--start-group "$SOURCE/libavformat/libavformat.a" "$SOURCE/libavcodec/libavcodec.a" \
    "$SOURCE/libswresample/libswresample.a" "$SOURCE/libavutil/libavutil.a" -Wl,--end-group \
    -sMODULARIZE -sEXPORT_ES6 -sEXPORT_NAME=createAudioCore -sENVIRONMENT=worker,node \
    -sINITIAL_MEMORY=33554432 -sMAXIMUM_MEMORY=268435456 -sALLOW_MEMORY_GROWTH \
    -sFILESYSTEM=0 -sABORTING_MALLOC=0 -sSTACK_SIZE=1048576 \
    -sEXPORTED_FUNCTIONS='["_malloc","_free","_mm_decode","_mm_pcm","_mm_version","_mm_license","_mm_configuration"]' \
    -sEXPORTED_RUNTIME_METHODS='["HEAPU8","UTF8ToString"]' \
    -ffile-prefix-map="$ROOT"=. -o "$OUTPUT/ffmpeg-core.js"
