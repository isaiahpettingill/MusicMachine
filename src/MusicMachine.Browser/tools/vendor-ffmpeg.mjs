import { readFile, mkdir, writeFile, readdir, stat, cp } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { resolve, join } from 'node:path';
import { createHash } from 'node:crypto';
import { FFMPEG_PIN as pin, CONVERSION_LIMITS as limits } from '../wwwroot/conversion-policy.js';
const root = fileURLToPath(new URL('../', import.meta.url));
const built = join(root, '.ffmpeg-build/output');
const output = resolve(process.argv[2] ?? join(root, 'wwwroot/vendor/ffmpeg', pin.runtime));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
async function artifact(name, length, sha) {
    const bytes = await readFile(join(built, name)).catch(e => { throw new Error(`Missing audio-only core. Run npm run build:ffmpeg first. ${e.message}`); });
    if (bytes.length !== length || hash(bytes) !== sha) throw new Error(`Pinned audio core mismatch: ${name}. Rebuild from the pinned source/toolchain, or intentionally update the reviewed pins.`);
    return bytes;
}
const js = await artifact('ffmpeg-core.js', pin.jsBytes, pin.jsSha256);
const wasm = await artifact('ffmpeg-core.wasm', pin.wasmBytes, pin.wasmSha256);
if (!WebAssembly.validate(wasm)) throw new Error('Invalid audio WASM');
const source = await readFile(join(built, 'ffmpeg-audio-source.tar.xz'));
const sourcePins = JSON.parse(await readFile(join(root, 'licenses/SOURCE-PROVENANCE.json')));
if (sourcePins.ffmpeg.sha256 !== pin.sourceSha256 || sourcePins.builtArtifacts['ffmpeg-core.wasm'].sha256 !== pin.wasmSha256 || sourcePins.coreLicense !== 'LGPL-2.1-or-later')
    throw new Error('Source bundle provenance does not match the selected core');
await mkdir(output, { recursive: true });
const chunks = [];
for (let offset = 0; offset < wasm.length; offset += limits.chunkBytes) {
    const bytes = wasm.subarray(offset, offset + limits.chunkBytes);
    const name = `ffmpeg-core.wasm.part${String(chunks.length).padStart(2, '0')}`;
    await writeFile(join(output, name), bytes); chunks.push({ name, bytes: bytes.length, sha256: hash(bytes) });
}
await writeFile(join(output, 'ffmpeg-core.js'), js);
await writeFile(join(output, 'ffmpeg-audio-source.tar.xz'), source);
const manifest = { schema: 1, runtime: pin.runtime, version: pin.version, memoryBytes: limits.memoryBytes,
    js: { name: 'ffmpeg-core.js', bytes: js.length, sha256: hash(js) },
    wasm: { bytes: wasm.length, sha256: hash(wasm), chunks },
    source: { name: 'ffmpeg-audio-source.tar.xz', bytes: source.length, sha256: hash(source) },
    license: 'LGPL-2.1-or-later' };
await writeFile(join(output, 'manifest.json'), JSON.stringify(manifest, null, 2) + '\n');
for (const name of ['NOTICE.md', 'LGPL-2.1.txt', 'FFmpeg-LICENSE.md', 'Emscripten-LICENSE.txt', 'musl-COPYRIGHT.txt', 'compiler-rt-LICENSE.txt', 'MusicMachine-MIT.txt', 'SOURCE-PROVENANCE.json'])
    await cp(join(root, 'licenses', name), join(output, name));
for (const name of await readdir(output))
    if ((await stat(join(output, name))).size > 25 * 1024 * 1024) throw new Error(`Asset exceeds Pages' 25 MiB cap: ${name}`);
console.log(`Vendored source-built FFmpeg ${pin.version}: ${wasm.length} WASM bytes; ${chunks.length} part(s) of <= ${limits.chunkBytes} bytes; ${source.length} source-bundle bytes.`);
