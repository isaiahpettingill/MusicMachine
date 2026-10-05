import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, rm, symlink } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { deflateSync, gzipSync } from 'node:zlib';
import { parsePagesHeaders, readPreparedAsset, viewPreferences, inspectPng, requireRenderedCanvas, renderedDifference } from '../tools/published-smoke/support.mjs';
import { audioObserverScript } from '../tools/published-smoke/audio-observer.mjs';

test('prepared server honors actual Pages COOP/COEP and compressed-WASM headers without decoding content', async () => {
    const root = await mkdtemp(join(tmpdir(), 'musicmachine-static-unit-'));
    try {
        await mkdir(join(root, '_framework'));
        const compressed = gzipSync(Buffer.from('real wasm payload'));
        await writeFile(join(root, 'index.html'), '<canvas></canvas>');
        await writeFile(join(root, '_framework/app.wasm.gz'), compressed);
        await writeFile(join(root, '_framework/app.wasm'), Buffer.from('wasm'));
        await writeFile(join(root, 'main.js'), 'export {};');
        const rules = parsePagesHeaders('/*\n  Cross-Origin-Opener-Policy: same-origin\n  Cross-Origin-Embedder-Policy: require-corp\n  X-Content-Type-Options: nosniff\n/\n  Cache-Control: no-cache\n/_framework/app.wasm.gz\n  Content-Type: application/gzip\n  Cache-Control: public, max-age=31536000, immutable, no-transform\n');
        const html = await readPreparedAsset(root, '/', rules);
        assert.equal(html.status, 200); assert.match(html.headers['content-type'], /^text\/html/);
        assert.equal(html.headers['cross-origin-opener-policy'], 'same-origin'); assert.equal(html.headers['cross-origin-embedder-policy'], 'require-corp');
        assert.equal(html.headers['cache-control'], 'no-cache');
        const gzip = await readPreparedAsset(root, '/_framework/app.wasm.gz', rules);
        assert.deepEqual(gzip.body, compressed); assert.equal(gzip.headers['content-type'], 'application/gzip');
        assert.equal(gzip.headers['content-encoding'], undefined);
        assert.equal((await readPreparedAsset(root, '/_framework/app.wasm', rules)).headers['content-type'], 'application/wasm');
        assert.match((await readPreparedAsset(root, '/main.js?v=1', rules)).headers['content-type'], /^text\/javascript/);
        assert.equal((await readPreparedAsset(root, '/missing.js', rules)).status, 404);
    } finally { await rm(root, { recursive: true, force: true }); }
});
test('prepared server refuses traversal, configuration files and symlink escape', async () => {
    const parent = await mkdtemp(join(tmpdir(), 'musicmachine-static-unit-')), root = join(parent, 'site');
    try {
        await mkdir(root); await writeFile(join(parent, 'secret.txt'), 'not a site resource');
        await symlink(join(parent, 'secret.txt'), join(root, 'escape.txt'));
        for (const path of ['/../secret.txt', '/%2e%2e/secret.txt', '/..%2fsecret.txt', '/_headers', '/_worker.js', '/.env', '/escape.txt', '/x\\secret'])
            assert.equal((await readPreparedAsset(root, path, [])).status, 403, path);
        assert.equal((await readPreparedAsset(root, '/%zz', [])).status, 400);
    } finally { await rm(parent, { recursive: true, force: true }); }
});
test('preference observation uses persisted values and actual app defaults', () => {
    assert.deepEqual(viewPreferences(null), { library: true, inspector: false, theme: 'catppuccin-mocha', workspace: 'Tracker' });
    assert.deepEqual(viewPreferences('library=false\ninspector=true\ntheme=dark\nworkspace=Sampling\n'), { library: false, inspector: true, theme: 'dark', workspace: 'Sampling' });
});
function crc32(bytes) { let crc = 0xffffffff; for (const byte of bytes) { crc ^= byte; for (let i = 0; i < 8; i++) crc = (crc >>> 1) ^ (0xedb88320 & -(crc & 1)); } return (crc ^ 0xffffffff) >>> 0; }
function png(width, height, colorful) {
    const chunk = (name, bytes) => { const body = Buffer.concat([Buffer.from(name), bytes]), out = Buffer.alloc(body.length + 8); out.writeUInt32BE(bytes.length); body.copy(out, 4); out.writeUInt32BE(crc32(body), out.length - 4); return out; };
    const header = Buffer.alloc(13); header.writeUInt32BE(width); header.writeUInt32BE(height, 4); header[8] = 8; header[9] = 6;
    const data = Buffer.alloc(height * (width * 4 + 1));
    for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) { const at = y * (width * 4 + 1) + 1 + x * 4; data[at] = colorful ? x % 256 : 30; data[at + 1] = colorful ? y % 256 : 30; data[at + 2] = colorful ? (x + y) % 256 : 30; data[at + 3] = 255; }
    return Buffer.concat([Buffer.from('89504e470d0a1a0a', 'hex'), chunk('IHDR', header), chunk('IDAT', deflateSync(data)), chunk('IEND', Buffer.alloc(0))]);
}
test('pixel verifier accepts rendered detail and rejects a large blank canvas', () => {
    const detail = inspectPng(png(800, 500, true)); requireRenderedCanvas(detail); assert.ok(detail.uniqueColors > 100);
    assert.throws(() => requireRenderedCanvas(inspectPng(png(800, 500, false))), /blank/);
    assert.throws(() => inspectPng(Buffer.from('not png')), /Not a PNG/);
    assert.throws(() => requireRenderedCanvas(inspectPng(png(10, 10, true))), /blank/);
    assert.equal(renderedDifference(png(800, 500, true), png(800, 500, true)), 0);
    assert.ok(renderedDifference(png(800, 500, true), png(800, 500, false)) > 0.9);
});
test('audio observer is syntactically valid and observes native methods rather than supplying audio', () => {
    assert.doesNotThrow(() => new Function(audioObserverScript));
    assert.match(audioObserverScript, /Reflect\.apply\(start, this, args\)/);
    assert.match(audioObserverScript, /getFloatTimeDomainData/);
    assert.doesNotMatch(audioObserverScript, /OfflineAudioContext|fakeAudio|globalThis\.AudioContext\s*=/);
});

test('real demo shortcut holds and releases Control and Shift in the right order', async () => {
    const { keyEvents } = await import('../tools/published-smoke/key-events.mjs');
    const events = keyEvents('KeyD', 'D', 68, true, true);
    assert.deepEqual(events.map(e => [e.type, e.code, e.modifiers]), [
        ['rawKeyDown', 'ControlLeft', 2], ['rawKeyDown', 'ShiftLeft', 10],
        ['rawKeyDown', 'KeyD', 10], ['keyUp', 'KeyD', 10],
        ['keyUp', 'ShiftLeft', 2], ['keyUp', 'ControlLeft', 0]
    ]);
    assert.deepEqual(keyEvents('Space', ' ', 32).map(e => [e.type, e.modifiers]), [['rawKeyDown', 0], ['keyUp', 0]]);
    assert.equal(keyEvents('KeyI', 'i', 73, true).length, 4);
});
