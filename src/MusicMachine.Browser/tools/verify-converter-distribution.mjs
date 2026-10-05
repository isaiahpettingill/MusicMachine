// A publication prerequisite, not a legal certification: sources/notices must
// accompany these exact bytes, and no obsolete prebuilt GPL core may hitchhike.
import { readFile, readdir, stat } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { FFMPEG_PIN as pin } from '../wwwroot/conversion-policy.js';
import { validateManifest } from '../wwwroot/conversion-assets.js';
const vendor = fileURLToPath(new URL('../wwwroot/vendor/ffmpeg/', import.meta.url));
const versions = await readdir(vendor);
if (versions.length !== 1 || versions[0] !== pin.runtime) throw new Error('Remove obsolete generated converter versions before distribution. Only the source-built selected runtime may be published.');
const root = join(vendor, pin.runtime);
const manifest = validateManifest(JSON.parse(await readFile(join(root, 'manifest.json'))));
const hash = b => createHash('sha256').update(b).digest('hex');
for (const artifact of [manifest.js, ...manifest.wasm.chunks, manifest.source]) {
    if (!artifact || !Number.isSafeInteger(artifact.bytes) || artifact.bytes > 25 * 1024 * 1024 || !/^[a-zA-Z0-9.-]+$/.test(artifact.name)) throw new Error('Invalid source/artifact descriptor');
    const bytes = await readFile(join(root, artifact.name));
    if (bytes.length !== artifact.bytes || hash(bytes) !== artifact.sha256) throw new Error(`Distribution integrity failure: ${artifact.name}`);
}
if (manifest.source.name !== 'ffmpeg-audio-source.tar.xz' || manifest.license !== 'LGPL-2.1-or-later') throw new Error('Missing corresponding source/license');
const listing = execFileSync('tar', ['-tJf', join(root, manifest.source.name)], { encoding: 'utf8' });
for (const path of ['downloads/ffmpeg-9.0.2.tar.xz', 'tools/ffmpeg-audio/audio-adapter.c', 'tools/ffmpeg-audio/link.sh', 'relink/audio-adapter.o', 'emscripten-source.tar.xz', 'LGPL-2.1.txt', 'README.md'])
    if (!listing.split('\n').includes('./' + path)) throw new Error(`Source bundle missing ${path}`);
const unpack = name => execFileSync('tar', ['-xJOf', join(root, manifest.source.name), './' + name], { maxBuffer: 25 * 1024 * 1024 });
const provenance = JSON.parse(unpack('SOURCE-PROVENANCE.json'));
if (provenance.ffmpeg.sha256 !== pin.sourceSha256 || provenance.coreLicense !== 'LGPL-2.1-or-later' ||
    provenance.builtArtifacts['ffmpeg-core.wasm'].sha256 !== pin.wasmSha256 ||
    provenance.builtArtifacts['ffmpeg-core.js'].sha256 !== pin.jsSha256)
    throw new Error('Source provenance does not match the binary pins');
if (hash(unpack('downloads/ffmpeg-9.0.2.tar.xz')) !== pin.sourceSha256 ||
    hash(unpack('relink/audio-adapter.o')) !== provenance.builtArtifacts['audio-adapter.o'].sha256 ||
    hash(unpack('emscripten-source.tar.xz')) !== provenance.includedEmscriptenSource.sha256)
    throw new Error('Embedded source/relink artifact integrity failure');
for (const name of ['NOTICE.md', 'LGPL-2.1.txt', 'FFmpeg-LICENSE.md', 'Emscripten-LICENSE.txt', 'musl-COPYRIGHT.txt', 'compiler-rt-LICENSE.txt', 'MusicMachine-MIT.txt', 'SOURCE-PROVENANCE.json'])
    if ((await stat(join(root, name))).size < 10) throw new Error(`Missing notice: ${name}`);
console.log('Converter binary/chunks, source/relink bundle and notices verified; obsolete runtime versions absent.');
