#!/usr/bin/env bash
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
source "$HERE/emsdk-env.sh"
cd "$ROOT/.ffmpeg-build/source/ffmpeg-9.0.2"
emconfigure ./configure \
    --target-os=none --arch=wasm32 --enable-cross-compile --disable-asm \
    --disable-autodetect --disable-network --disable-iconv --disable-gpl --disable-nonfree --disable-version3 \
    --disable-everything --disable-programs --disable-doc --disable-debug --disable-stripping \
    --disable-pthreads --disable-w32threads --disable-os2threads \
    --disable-avdevice --disable-avfilter --disable-swscale \
    --enable-avcodec --enable-avformat --enable-swresample --enable-small \
    --cc=emcc --cxx=em++ --ar=emar --nm=emnm --ranlib=emranlib --optflags=-Oz \
    --enable-demuxer=aac,aiff,asf,caf,flac,matroska,mov,mp3,ogg,wav \
    --enable-decoder=aac,aac_fixed,ac3,eac3,alac,flac,mp1,mp2,mp3,mp3float,opus,vorbis,wmav1,wmav2,wmapro,wmalossless,pcm_s8,pcm_u8,pcm_s16le,pcm_s16be,pcm_s24le,pcm_s24be,pcm_s32le,pcm_s32be,pcm_f32le,pcm_f32be,pcm_f64le,pcm_f64be,pcm_alaw,pcm_mulaw \
    --enable-parser=aac,aac_latm,ac3,flac,mpegaudio,opus,vorbis
emmake make -j"${FFMPEG_JOBS:-4}"
