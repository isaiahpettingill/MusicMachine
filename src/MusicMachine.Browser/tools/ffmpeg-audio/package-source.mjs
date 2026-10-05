import { readFile, writeFile, mkdir, cp, rm, stat } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';
import { execFileSync } from 'node:child_process';
const root = fileURLToPath(new URL('../../', import.meta.url));
const pins = JSON.parse(await readFile(new URL('./sources.json', import.meta.url)));
const base = join(root, '.ffmpeg-build'), stage = join(base, 'source-package'), output = join(base, 'output');
await rm(stage, { recursive: true, force: true }); await mkdir(stage, { recursive: true });
const sdk = join(process.env.DOTNET_ROOT ?? '/workspace/shared/dotnet-11', 'packs', 'Microsoft.NET.Runtime.Emscripten.6.0.2.Sdk.linux-x64', pins.toolchain.packageVersion, 'tools/emscripten');
const tarOptions = ['--sort=name', '--mtime=@1789689600', '--owner=0', '--group=0', '--numeric-owner', '--format=gnu'];
execFileSync('tar', [...tarOptions, '--exclude=./__pycache__', '--exclude=*/__pycache__', '--exclude=*.pyc', '--exclude=./cache', '--exclude=./.emscripten', '-cJf', join(stage, 'emscripten-source.tar.xz'), '-C', sdk, '.'], { env: { ...process.env, XZ_OPT: '-9 --threads=1', TZ: 'UTC', LC_ALL: 'C' } });
await cp(join(base, 'downloads'), join(stage, 'downloads'), { recursive: true });
await cp(join(root, 'tools/ffmpeg-audio'), join(stage, 'tools/ffmpeg-audio'), { recursive: true });
await cp(join(root, 'tools/build-ffmpeg-audio.sh'), join(stage, 'tools/build-ffmpeg-audio.sh'));
await mkdir(join(stage, 'relink'), { recursive: true });
await cp(join(output, 'audio-adapter.o'), join(stage, 'relink/audio-adapter.o'));
await mkdir(join(stage, 'licenses'), { recursive: true });
for (const name of ['NOTICE.md', 'LGPL-2.1.txt', 'FFmpeg-LICENSE.md', 'Emscripten-LICENSE.txt', 'musl-COPYRIGHT.txt', 'compiler-rt-LICENSE.txt', 'MusicMachine-MIT.txt'])
    {
    await cp(join(root, 'licenses', name), join(stage, name));
    await cp(join(root, 'licenses', name), join(stage, 'licenses', name));
}
await mkdir(join(stage, 'build-configuration'), { recursive: true });
for (const name of ['config.h', 'config_components.h', 'ffbuild/config.mak'])
    await cp(join(base, 'source/ffmpeg-9.0.2', name), join(stage, 'build-configuration', name.replaceAll('/', '-')));
const configuration = await readFile(join(base, 'source/ffmpeg-9.0.2/config.h'), 'utf8');
for (const option of ['GPL', 'NONFREE', 'NETWORK', 'LIBX264', 'LIBX265'])
    if (!new RegExp(`^#define CONFIG_${option} 0$`, 'm').test(configuration))
        throw new Error(`Audio-only build audit failed: ${option}`);
if (!/^#define HAVE_THREADS 0$/m.test(configuration)) throw new Error('Threaded FFmpeg build is not supported');
const configPath = join(stage, 'build-configuration/ffbuild-config.mak');
let portableConfig = await readFile(configPath, 'utf8');
portableConfig = portableConfig.replaceAll(root.replace(/\/$/, ''), '$MUSICMACHINE_SOURCE_ROOT')
    .replaceAll(process.env.DOTNET_ROOT ?? '/workspace/shared/dotnet-11', '$DOTNET_ROOT');
await writeFile(configPath, portableConfig);
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const sources = { ...pins, verifiedAt: '2026-10-05', adapterLicense: 'MIT', coreLicense: 'LGPL-2.1-or-later',
    builtArtifacts: Object.fromEntries(await Promise.all(['ffmpeg-core.js', 'ffmpeg-core.wasm', 'audio-adapter.o'].map(async name => [name, { bytes: (await stat(join(output, name))).size, sha256: digest(await readFile(join(output, name))) }]))),
    includedEmscriptenSource: { name: 'emscripten-source.tar.xz', sha256: digest(await readFile(join(stage, 'emscripten-source.tar.xz'))) },
    patches: [], build: 'tools/build-ffmpeg-audio.sh', configure: 'tools/ffmpeg-audio/configure.sh', link: 'tools/ffmpeg-audio/link.sh',
    bounds: { inputBytes: 33554432, outputFrames: 1440000, channels: 1, sampleRate: 48000, maximumWasmMemory: 268435456 },
    priorReleasedCore: 'The previous @ffmpeg/core npm binary had incomplete historical dependency evidence. This is not a finding of license noncompliance; this source-built alternative uses independently supplied actual inputs.' };
await writeFile(join(stage, 'SOURCE-PROVENANCE.json'), JSON.stringify(sources, null, 2) + '\n');
await writeFile(join(root, 'licenses/SOURCE-PROVENANCE.json'), JSON.stringify(sources, null, 2) + '\n');
await writeFile(join(stage, 'README.md'), `# Rebuild or relink MusicMachine's audio converter\n\nThis bundle contains FFmpeg's original signed 9.0.2 source archive, unchanged;\nMusicMachine's MIT adapter/build source and adapter object; selected build config;\nand Emscripten compiler/runtime/system-library source from the exact official SDK.\n\nUse Linux x64, bash, Python 3, make, Node, tar/xz and GnuPG. Install the three\nofficial .NET toolchain packages listed in tools/ffmpeg-audio/sources.json into\n$DOTNET_ROOT/packs (the original build used the .NET 11 wasm-tools workload).\nTheir archive digests and exact Emscripten source commit are pinned and checked.\nNo Docker, third-party encoder libraries or npm runtime package is needed.\n\nCopy downloads/* into .ffmpeg-build/downloads/ and run:\n\n    bash tools/build-ffmpeg-audio.sh\n\nOutput: .ffmpeg-build/output/ffmpeg-core.js and ffmpeg-core.wasm.\nThe script verifies pinned archives/signatures, extracts stock FFmpeg, configures\nonly the listed built-in audio decoders/demuxers, and links the supplied adapter.\nThe original core's 256 MiB maximum is set at link time, not patched afterward.\nTo relink modified FFmpeg, change the source/pins intentionally, build its static\nlibraries, then run tools/ffmpeg-audio/link.sh. The adapter source is complete;\nrelink/audio-adapter.o is also provided for relinking without recompiling it:\n\n    MUSICMACHINE_ADAPTER_OBJECT="$PWD/relink/audio-adapter.o" bash tools/ffmpeg-audio/link.sh\n\nAll build/source files can be modified and rebuilt under their included licenses.\n\nIn the MusicMachine repository, update wwwroot/conversion-policy.js's version,\nbyte sizes and SHA-256 pins to your replacement core, regenerate vendor assets\nwith npm run vendor:ffmpeg, then run npm test and npm run test:runtime. The runtime\nmanifest integrity checks are security checks, not a prohibition on relinking.\n\nThe build has no FFmpeg source patches or external codec dependencies. Configure\ndisables GPL, nonfree, version3, autodetection, network, filesystem protocols,\nvideo encoders and threading. FFmpeg reports LGPL version 2.1 or later. Keep\nsource, notices and relinking materials available with distributed binaries.\n`);
execFileSync('tar', [...tarOptions, '-cJf', join(output, 'ffmpeg-audio-source.tar.xz'), '-C', stage, '.'], { env: { ...process.env, XZ_OPT: '-9 --threads=1', TZ: 'UTC', LC_ALL: 'C' } });
const size = (await stat(join(output, 'ffmpeg-audio-source.tar.xz'))).size;
if (size > 25 * 1024 * 1024) throw new Error('Source bundle exceeds Pages cap; split it before distribution');
console.log(`Packaged corresponding build inputs, notices and relinking materials (${size} bytes).`);
