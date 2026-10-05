import test from 'node:test';
import assert from 'node:assert/strict';
import { createAudioConverter } from '../wwwroot/conversion.js';
import { initializeCore, waveFrameCount, convertWithCore } from '../wwwroot/conversion-worker-core.js';
import { CONVERSION_LIMITS as limits, FFMPEG_PIN as pin } from '../wwwroot/conversion-policy.js';
import { validateManifest, readBoundedResponse } from '../wwwroot/conversion-assets.js';
function wav(frames = 2) {
    const result = new Uint8Array(44 + frames * 2), view = new DataView(result.buffer);
    const text = (at, s) => [...s].forEach((x, i) => { result[at + i] = x.charCodeAt(0); });
    text(0, 'RIFF'); view.setUint32(4, result.length - 8, true); text(8, 'WAVE'); text(12, 'fmt ');
    view.setUint32(16, 16, true); view.setUint16(20, 1, true); view.setUint16(22, 1, true); view.setUint32(24, 48000, true);
    view.setUint32(28, 96000, true); view.setUint16(32, 2, true); view.setUint16(34, 16, true); text(36, 'data'); view.setUint32(40, frames * 2, true);
    return result;
}
function setup() {
    const workers = [], timers = new Map(), progress = []; let nextTimer = 0;
    const bridge = createAudioConverter({
        createWorker() { const w = { terminated: 0, messages: [], terminate() { this.terminated++; }, postMessage(m) { this.messages.push(m); } }; workers.push(w); return w; },
        setTimer(f) { timers.set(++nextTimer, f); return nextTimer; }, clearTimer(id) { timers.delete(id); },
        onProgress(data) { progress.push(data); }
    });
    return { bridge, workers, timers, progress };
}
test('core initialization instantiates supplied WASM through the generated loader callback', async () => {
    const wasm = new Uint8Array([0, 97, 115, 109, 1, 0, 0, 0]);
    let received;
    const core = await initializeCore(async options => {
        assert.throws(() => options.locateFile('ffmpeg-core.wasm'), /unverified runtime/);
        const exports = options.instantiateWasm({}, (instance, module) => {
            assert.ok(instance instanceof WebAssembly.Instance);
            assert.ok(module instanceof WebAssembly.Module);
            received = instance;
        });
        assert.equal(exports, received.exports);
        return received;
    }, wasm);
    assert.equal(core, received);
});
test('invalid WASM and loader callback errors reject initialization without hanging', async () => {
    let called = false;
    await assert.rejects(initializeCore(() => { called = true; }, new Uint8Array()), WebAssembly.CompileError);
    assert.equal(called, false);
    const wasm = new Uint8Array([0, 97, 115, 109, 1, 0, 0, 0]);
    await assert.rejects(initializeCore(options => new Promise(resolve => {
        options.instantiateWasm({}, () => { throw new Error('Initialization failed'); });
        resolve();
    }), wasm), /Initialization failed/);
});
test('creating bridge and native-format rejection do not start or download a runtime', async () => {
    const { bridge, workers } = setup(); assert.equal(workers.length, 0);
    await assert.rejects(bridge.convert(1, 'native.wav', new Uint8Array([1])), /Choose WAV/);
    assert.equal(workers.length, 0);
});
test('conversion transfers a copy, reports progress and terminates after success', async () => {
    const { bridge, workers, timers, progress } = setup(); const source = new Uint8Array([1, 2]);
    const result = bridge.convert(1, 'input.MP3', source); const w = workers[0];
    assert.notEqual(w.messages[0].bytes, source); assert.deepEqual(w.messages[0].bytes, source);
    w.onmessage({ data: { id: 1, type: 'progress', phase: 'download', progress: 0.5 } });
    w.onmessage({ data: { id: 1, type: 'result', bytes: wav() } });
    assert.equal(waveFrameCount(await result), 2); assert.equal(w.terminated, 1); assert.equal(timers.size, 0); assert.equal(progress.length, 1);
});
test('cancel interrupts a worker and ignores late messages without canceling its successor', async () => {
    const { bridge, workers } = setup(); const first = bridge.convert(1, 'a.mp3', new Uint8Array([1])); const rejected = assert.rejects(first, { name: 'AbortError' });
    const late = workers[0].onmessage;
    const second = bridge.convert(2, 'b.flac', new Uint8Array([2])); await rejected;
    bridge.cancel(1); late({ data: { id: 1, type: 'result', bytes: wav() } });
    assert.equal(workers[1].terminated, 0);
    workers[1].onmessage({ data: { id: 2, type: 'result', bytes: wav() } }); await second;
    assert.equal(workers[0].terminated, 1); assert.equal(workers[1].terminated, 1);
});
test('dispose, timeout, worker error and message error always settle and terminate', async () => {
    for (const mode of ['dispose', 'timeout', 'error', 'messageerror']) {
        const { bridge, workers, timers } = setup(); const task = bridge.convert(1, 'a.mp3', new Uint8Array([1]));
        const check = assert.rejects(task);
        if (mode === 'dispose') bridge.dispose();
        if (mode === 'timeout') [...timers.values()][0]();
        if (mode === 'error') workers[0].onerror({ preventDefault() {} });
        if (mode === 'messageerror') workers[0].onmessageerror();
        await check; assert.equal(workers[0].terminated, 1); assert.equal(timers.size, 0);
    }
});
test('invalid worker output and oversized input fail boundedly', async () => {
    const { bridge, workers } = setup();
    await assert.rejects(bridge.convert(1, 'a.mp3', new Uint8Array(limits.inputBytes + 1)), /32 MiB/); assert.equal(workers.length, 0);
    const task = bridge.convert(2, 'a.mp3', new Uint8Array([1])); const check = assert.rejects(task, /Converted audio/);
    workers[0].onmessage({ data: { id: 2, type: 'result', bytes: new Uint8Array(2) } }); await check;
    assert.equal(workers[0].terminated, 1);
});
test('WAV validation rejects overlong output without accepting cropped data', () => {
    assert.equal(waveFrameCount(wav(limits.frames)), limits.frames);
    assert.throws(() => waveFrameCount(wav(limits.frames + 1)), /30 seconds/);
    const broken = wav(); broken[22] = 2; assert.throws(() => waveFrameCount(broken), /mono/);
});
function manifest() {
    return { schema: 1, runtime: pin.runtime, version: pin.version, memoryBytes: limits.memoryBytes,
        js: { name: 'ffmpeg-core.js', bytes: pin.jsBytes, sha256: pin.jsSha256 },
        wasm: { bytes: pin.wasmBytes, sha256: pin.wasmSha256, chunks: Array.from({ length: Math.ceil(pin.wasmBytes / limits.chunkBytes) }, (_, i) => ({ name: `ffmpeg-core.wasm.part0${i}`, bytes: Math.min(limits.chunkBytes, pin.wasmBytes - i * limits.chunkBytes), sha256: 'a'.repeat(64) })) } };
}
test('manifest enforces pinned runtime, entire artifact digests, sizes, ordered same-directory chunks', () => {
    validateManifest(manifest());
    for (const change of [m => m.runtime = 'untrusted', m => m.wasm.sha256 = 'b'.repeat(64), m => m.wasm.chunks[0].name = 'ffmpeg-core.wasm.part01',
        m => m.wasm.chunks[0].name = '../external', m => m.wasm.chunks[0].bytes++, m => m.wasm.chunks.push(m.wasm.chunks[0]), m => m.js.sha256 = 'b'.repeat(64)]) {
        const m = manifest(); change(m); assert.throws(() => validateManifest(m), /integrity/);
    }
});
test('stream download rejects oversized, truncated and aborted responses', async () => {
    await assert.rejects(readBoundedResponse(new Response(new Uint8Array(11)), 10), /declared size/);
    await assert.rejects(readBoundedResponse(new Response(new Uint8Array(2), { headers: { 'content-length': '12' } }), 10), /too large/);
    await assert.rejects(readBoundedResponse(new Response('', { status: 404 }), 10), /404/);
    const controller = new AbortController(); controller.abort();
    await assert.rejects(readBoundedResponse(new Response('a'), 10, controller.signal), { name: 'AbortError' });
    assert.equal((await readBoundedResponse(new Response(new Uint8Array(3)), 10)).length, 3);
});
test('audio adapter frees its input allocation after success and native errors', async () => {
    for (const nativeResult of [2, -1, -3, -4, -5, -6]) {
        const freed = []; const core = { HEAPU8: new Uint8Array(100), onProgress: null,
            _malloc() { return 8; }, _free(p) { freed.push(p); }, _mm_pcm() { return 32; },
            _mm_decode() { return nativeResult; } };
        if (nativeResult > 0) assert.equal(waveFrameCount(await convertWithCore(core, 'a.mp3', new Uint8Array([1]))), 2);
        else await assert.rejects(convertWithCore(core, 'a.mp3', new Uint8Array([1])));
        assert.deepEqual(freed, [8]); assert.equal(core.onProgress, null);
    }
});
test('audio adapter rejects failed allocations and malformed native results', async () => {
    for (const nativeResult of [0, NaN, limits.frames + 1]) {
        let freed = 0; const core = { HEAPU8: new Uint8Array(100), _malloc: () => 8, _free() { freed++; }, _mm_decode: () => nativeResult };
        await assert.rejects(convertWithCore(core, 'a.mp3', new Uint8Array([1])), /invalid sample count/); assert.equal(freed, 1);
    }
    await assert.rejects(convertWithCore({ _malloc: () => 0 }, 'a.mp3', new Uint8Array([1])), /memory/);
});
test('worker construction failures and explicit worker errors settle with no stale timer', async () => {
    const broken = createAudioConverter({ createWorker() { throw new Error('Worker unavailable'); } });
    await assert.rejects(broken.convert(1, 'a.mp3', new Uint8Array([1])), /Worker unavailable/);
    const { bridge, workers, timers } = setup(); const task = bridge.convert(2, 'a.mp3', new Uint8Array([1]));
    workers[0].onmessage({ data: { id: 2, type: 'error', message: 'Download failed' } });
    await assert.rejects(task, /Download failed/); assert.equal(workers[0].terminated, 1); assert.equal(timers.size, 0);
});
test('managed bridge base64 encodes verified WAV and does not expose a Promise<byte[]> ABI', async () => {
    const { bridge, workers } = setup(); const task = bridge.convertBase64(1, 'a.mp3', new Uint8Array([1]));
    const expected = wav(20000); workers[0].onmessage({ data: { id: 1, type: 'result', bytes: expected } });
    assert.deepEqual(new Uint8Array(Buffer.from(await task, 'base64')), expected);
});

test('codec progress cannot flood the parent queue; phase and completion remain immediate', async () => {
    const { throttleProgress } = await import('../wwwroot/conversion-worker-core.js');
    const values = []; let time = 0;
    const progress = throttleProgress(value => values.push(value), () => time);
    for (let i = 0; i < 100000; i++) progress({ phase: 'convert', progress: .1 });
    assert.equal(values.length, 1);
    time = 49; progress({ phase: 'convert', progress: .2 }); assert.equal(values.length, 1);
    time = 50; progress({ phase: 'convert', progress: .3 }); assert.equal(values.length, 2);
    progress({ phase: 'done', progress: 1 }); assert.equal(values.length, 3);
    assert.equal(values[2].phase, 'done');
});
