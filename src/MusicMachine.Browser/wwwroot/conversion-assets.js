import { FFMPEG_PIN as pin, CONVERSION_LIMITS as limits } from './conversion-policy.js';
const cachePrefix = 'musicmachine-ffmpeg-';
const hex = bytes => Array.from(new Uint8Array(bytes), value => value.toString(16).padStart(2, '0')).join('');
export async function sha256(bytes, cryptoApi = globalThis.crypto) {
    return hex(await cryptoApi.subtle.digest('SHA-256', bytes));
}
export function validateManifest(manifest) {
    const fail = () => { throw new Error('Audio converter manifest failed integrity validation. Reload or contact the site owner.'); };
    if (manifest?.schema !== 1 || manifest.runtime !== pin.runtime || manifest.version !== pin.version || manifest.memoryBytes !== limits.memoryBytes) fail();
    const js = manifest.js, wasm = manifest.wasm;
    if (js?.name !== 'ffmpeg-core.js' || js.bytes !== pin.jsBytes || js.sha256 !== pin.jsSha256 ||
        wasm?.bytes !== pin.wasmBytes || wasm.sha256 !== pin.wasmSha256 || !Array.isArray(wasm.chunks) ||
        wasm.chunks.length !== Math.ceil(pin.wasmBytes / limits.chunkBytes)) fail();
    let total = 0;
    for (let index = 0; index < wasm.chunks.length; index++) {
        const chunk = wasm.chunks[index];
        if (chunk?.name !== `ffmpeg-core.wasm.part${String(index).padStart(2, '0')}` ||
            chunk.bytes !== Math.min(limits.chunkBytes, pin.wasmBytes - total) || !/^[a-f0-9]{64}$/.test(chunk.sha256)) fail();
        total += chunk.bytes;
    }
    if (total !== pin.wasmBytes) fail();
    return manifest;
}
export async function readBoundedResponse(response, maxBytes, signal) {
    signal?.throwIfAborted();
    if (!response.ok) throw new Error(`Audio converter download failed (${response.status}). Try again when online.`);
    const length = response.headers.get('Content-Length');
    if (length && Number(length) > maxBytes) throw new Error('Audio converter asset is too large.');
    if (!response.body?.getReader) throw new Error('This browser does not support streamed converter downloads.');
    const reader = response.body.getReader();
    const bytes = new Uint8Array(maxBytes);
    let offset = 0;
    try {
        while (true) {
            signal?.throwIfAborted();
            const { value, done } = await reader.read();
            if (done) break;
            if (value.length > maxBytes - offset) throw new Error('Audio converter asset exceeds its declared size.');
            bytes.set(value, offset); offset += value.length;
        }
        signal?.throwIfAborted();
        return offset === maxBytes ? bytes : bytes.slice(0, offset);
    } finally { await reader.cancel().catch(() => {}); reader.releaseLock(); }
}
export async function loadConverterAssets({ baseUrl = new URL(`./vendor/ffmpeg/${pin.runtime}/`, import.meta.url),
    fetchImpl = globalThis.fetch, cacheStorage = globalThis.caches, cryptoApi = globalThis.crypto,
    signal, onProgress = () => {} } = {}) {
    if (!cryptoApi?.subtle) throw new Error('Audio conversion needs a secure HTTPS browser connection.');
    const base = new URL(baseUrl);
    let cache;
    try { cache = await cacheStorage?.open(cachePrefix + pin.runtime); } catch { /* Private browsing and quotas are optional. */ }
    const request = url => fetchImpl(url, { signal, credentials: 'omit', mode: 'same-origin', redirect: 'error', cache: 'no-cache' });
    const manifestResponse = await request(new URL('manifest.json', base));
    if (manifestResponse.status === 404) throw new Error('This build does not include the optional browser converter. Import WAV or QOA, or use desktop FFmpeg conversion.');
    const manifestBytes = await readBoundedResponse(manifestResponse, 65536, signal);
    let manifest;
    try { manifest = validateManifest(JSON.parse(new TextDecoder().decode(manifestBytes))); }
    catch { throw new Error('Audio converter is unavailable or its manifest is invalid. Native WAV and QOA import still work.'); }
    let loaded = 0;
    async function asset(info) {
        const url = new URL(info.name, base).href;
        let bytes;
        try {
            const cached = await cache?.match(url);
            if (cached) {
                bytes = await readBoundedResponse(cached, info.bytes, signal);
                if (bytes.length !== info.bytes || await sha256(bytes, cryptoApi) !== info.sha256) { bytes = null; await cache.delete(url); }
            }
        } catch (error) { signal?.throwIfAborted(); bytes = null; }
        if (!bytes) {
            bytes = await readBoundedResponse(await request(url), info.bytes, signal);
            if (bytes.length !== info.bytes || await sha256(bytes, cryptoApi) !== info.sha256)
                throw new Error('Audio converter download failed its SHA-256 integrity check. Try again.');
            signal?.throwIfAborted();
            try { await cache?.put(url, new Response(bytes)); } catch { /* Cache is only an optimization. */ }
        }
        signal?.throwIfAborted();
        loaded += bytes.length; onProgress({ phase: 'download', progress: loaded / (pin.jsBytes + pin.wasmBytes) });
        return bytes;
    }
    const js = await asset(manifest.js);
    const wasm = new Uint8Array(pin.wasmBytes);
    let offset = 0;
    for (const chunk of manifest.wasm.chunks) { const bytes = await asset(chunk); wasm.set(bytes, offset); offset += bytes.length; }
    if (await sha256(wasm, cryptoApi) !== pin.wasmSha256) throw new Error('Reassembled audio converter failed its SHA-256 integrity check.');
    signal?.throwIfAborted();
    try {
        for (const key of await cacheStorage?.keys() ?? [])
            if (key.startsWith(cachePrefix) && key !== cachePrefix + pin.runtime) await cacheStorage.delete(key);
    } catch { /* No cache access is required to convert. */ }
    return { js, wasm };
}
