// Integration tests require: npm ci --ignore-scripts && npm run vendor:ffmpeg.
// Executes the actual WASM core in a Node worker-like environment, NOT a browser.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadConverterAssets, sha256 } from '../wwwroot/conversion-assets.js';
import { CONVERSION_LIMITS as limits, FFMPEG_PIN as pin } from '../wwwroot/conversion-policy.js';
import { initializeCore, convertWithCore, waveFrameCount } from '../wwwroot/conversion-worker-core.js';
const runtime = new URL(`../wwwroot/vendor/ffmpeg/${pin.runtime}/`, import.meta.url);
const manifest = JSON.parse(await readFile(new URL('manifest.json', runtime)));
const cache = new Map();
let requested = [];
const cacheStorage = { async open() { return { async match(k) { return cache.get(k)?.clone(); }, async put(k, v) { cache.set(k, v); }, async delete(k) { cache.delete(k); } }; }, async keys() { return []; } };
async function fetchImpl(url) { requested.push(new URL(url).pathname); return new Response(await readFile(new URL(new URL(url).pathname.split('/').pop(), runtime))); }
const settings = { baseUrl: 'https://local.test/ffmpeg/', fetchImpl, cacheStorage };
let loaded;
test('real chunks reconstruct the pinned WASM and a verified cache avoids asset downloads', async () => {
    loaded = await loadConverterAssets(settings);
    assert.equal(await sha256(loaded.wasm), pin.wasmSha256); assert.equal(requested.length, 3);
    requested = []; const again = await loadConverterAssets(settings);
    assert.equal(await sha256(again.wasm), pin.wasmSha256); assert.equal(requested.length, 1);
    assert.equal(requested[0], '/ffmpeg/manifest.json');
});
test('corrupted cache entries are deleted and redownloaded; corrupted network bytes fail closed', async () => {
    const url = 'https://local.test/ffmpeg/ffmpeg-core.wasm.part00'; cache.set(url, new Response(new Uint8Array(5)));
    requested = []; await loadConverterAssets(settings); assert.deepEqual(requested, ['/ffmpeg/manifest.json', '/ffmpeg/ffmpeg-core.wasm.part00']);
    await assert.rejects(loadConverterAssets({ ...settings, cacheStorage: undefined, async fetchImpl(url) {
        if (String(url).endsWith('.part00')) return new Response(new Uint8Array(Math.min(limits.chunkBytes, pin.wasmBytes)));
        return fetchImpl(url);
    } }), /SHA-256/);
});
test('chunk reconstruction rejects wrong total digest even when each supplied chunk hash matches', async () => {
    const modified = structuredClone(manifest), first = await readFile(new URL(modified.wasm.chunks[0].name, runtime)); first[20] ^= 1;
    modified.wasm.chunks[0].sha256 = await sha256(first);
    await assert.rejects(loadConverterAssets({ ...settings, cacheStorage: undefined, async fetchImpl(url) {
        if (String(url).endsWith('manifest.json')) return new Response(JSON.stringify(modified));
        if (String(url).endsWith('.part00')) return new Response(first);
        return fetchImpl(url);
    } }), /Reassembled/);
});
test('aborting an asset download stops before the next chunk and never returns a runtime', async () => {
    const controller = new AbortController();
    await assert.rejects(loadConverterAssets({ ...settings, cacheStorage: undefined, signal: controller.signal, onProgress() { controller.abort(); } }), { name: 'AbortError' });
});
let factory;
async function createCore() {
    if (!factory) {
        // Import the exact verified glue with no adjacent WASM file. A fallback
        // filesystem read must not hide broken in-memory initialization again.
        const isolated = await mkdtemp(join(tmpdir(), 'musicmachine-core-loader-'));
        try {
            const script = join(isolated, 'ffmpeg-core.mjs');
            await writeFile(script, loaded.js);
            factory = (await import(pathToFileURL(script).href)).default;
        } finally { await rm(isolated, { recursive: true, force: true }); }
    }
    return initializeCore(factory, loaded.wasm);
}
test('source-built WASM decodes MP3 and FLAC; license/configuration and heap bounds agree', async () => {
    for (const extension of ['mp3', 'flac']) {
        const core = await createCore();
        assert.equal(core.UTF8ToString(core._mm_version()), '9.0.2');
        assert.equal(core.UTF8ToString(core._mm_license()), 'LGPL version 2.1 or later');
        const configuration = core.UTF8ToString(core._mm_configuration());
        assert.ok(configuration.includes('--disable-network')); assert.ok(configuration.includes('--disable-gpl'));
        assert.ok(!configuration.includes('--enable-libx264')); assert.ok(!configuration.includes('--enable-libx265'));
        const input = new Uint8Array(await readFile(new URL(`../tests/fixtures/sine-440hz.${extension}`, import.meta.url)));
        const output = await convertWithCore(core, `sine.${extension}`, input);
        assert.equal(waveFrameCount(output), 24000); assert.ok(output.subarray(44).some(b => b !== 0));
        assert.equal(core._malloc(limits.memoryBytes), 0, 'The full 256 MiB request must fail beyond the link-time memory maximum');
        assert.ok(core.HEAPU8.length <= limits.memoryBytes);
    }
});
test('actual decoder rejects malformed input and can decode again without stale output', async () => {
    const core = await createCore();
    await assert.rejects(convertWithCore(core, 'broken.mp3', new Uint8Array([1, 2, 3])), /decode|corrupt|unsupported/);
    const input = new Uint8Array(await readFile(new URL('../tests/fixtures/sine-440hz.flac', import.meta.url)));
    assert.equal(waveFrameCount(await convertWithCore(core, 'valid.flac', input)), 24000);
});
function pcmWave(frames, rate = 48000, channels = 1) {
    const input = new Uint8Array(44 + frames * channels * 2), view = new DataView(input.buffer);
    for (const [offset, value] of [[0, 'RIFF'], [8, 'WAVE'], [12, 'fmt '], [36, 'data']]) [...value].forEach((c, i) => input[offset + i] = c.charCodeAt(0));
    view.setUint32(4, input.length - 8, true); view.setUint32(16, 16, true); view.setUint16(20, 1, true); view.setUint16(22, channels, true);
    view.setUint32(24, rate, true); view.setUint32(28, rate * channels * 2, true); view.setUint16(32, channels * 2, true); view.setUint16(34, 16, true); view.setUint32(40, frames * channels * 2, true);
    return input;
}
test('actual decoder accepts exactly 30 seconds and rejects one extra decoded sample', async () => {
    const core = await createCore();
    assert.equal(waveFrameCount(await convertWithCore(core, 'at-limit.mp3', pcmWave(limits.frames))), limits.frames);
    await assert.rejects(convertWithCore(core, 'over-limit.mp3', pcmWave(limits.frames + 1)), /30 seconds/);
});
test('native adapter independently rejects invalid dimensions and oversized input lengths', async () => {
    const core = await createCore();
    await assert.rejects(convertWithCore(core, 'rate.mp3', pcmWave(1000, 384000)), /192 kHz/);
    await assert.rejects(convertWithCore(core, 'channels.mp3', pcmWave(1000, 48000, 9)), /eight channels/);
    const pointer = core._malloc(1);
    try { assert.equal(core._mm_decode(pointer, limits.inputBytes + 1), -2); } finally { core._free(pointer); }
});
test('one, two, six and eight channels downmix to an arithmetic mono average', async () => {
    const core = await createCore();
    for (const channels of [1, 2, 6, 8]) {
        const input = pcmWave(480, 48000, channels), samples = new DataView(input.buffer);
        for (let offset = 44; offset < input.length; offset += 2) samples.setInt16(offset, 8000, true);
        const output = await convertWithCore(core, 'channels.caf', input);
        assert.equal(waveFrameCount(output), 480);
        assert.equal(new DataView(output.buffer).getInt16(44, true), 8000);
    }
});
