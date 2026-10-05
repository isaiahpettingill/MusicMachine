#!/usr/bin/env bash
# Source this file. Only the pinned official .NET toolchain is used by this build.
set -euo pipefail
PACKS="${DOTNET_ROOT:-/workspace/shared/dotnet-11}/packs"
VERSION=11.0.0-rc.1.26425.128
SDK="$PACKS/Microsoft.NET.Runtime.Emscripten.6.0.2.Sdk.linux-x64/$VERSION/tools"
export DOTNET_EMSCRIPTEN_LLVM_ROOT="$SDK/bin"
export DOTNET_EMSCRIPTEN_BINARYEN_ROOT="$SDK"
export DOTNET_EMSCRIPTEN_NODE_JS="$PACKS/Microsoft.NET.Runtime.Emscripten.6.0.2.Node.linux-x64/$VERSION/tools/bin/node"
export EM_CACHE="$PACKS/Microsoft.NET.Runtime.Emscripten.6.0.2.Cache.linux-x64/$VERSION/tools/emscripten/cache"
export PATH="$SDK/emscripten:$SDK/bin:$PATH"
export SOURCE_DATE_EPOCH=1789689600 TZ=UTC LC_ALL=C
export PYTHONDONTWRITEBYTECODE=1
