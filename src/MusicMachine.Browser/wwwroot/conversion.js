import { CONVERSION_LIMITS as limits, conversionExtension } from './conversion-policy.js';
import { waveFrameCount } from './conversion-worker-core.js';
// No core import or runtime download occurs until convert() is requested. Each job
// owns a disposable worker; cancellation can interrupt synchronous codec execution.
export function createAudioConverter({ createWorker = () => new Worker(new URL('./conversion-worker.js', import.meta.url), { type: 'module' }),
    setTimer = globalThis.setTimeout, clearTimer = globalThis.clearTimeout, onProgress = () => {} } = {}) {
    let active = null;
    const abortError = () => new DOMException('Audio conversion canceled.', 'AbortError');
    function cancel(id) { if (active && (id == null || id === active.id)) active.finish(abortError()); }
    function convert(id, name, bytes) {
        cancel();
        return new Promise((resolve, reject) => {
            let worker, timer, settled = false;
            const job = { id, finish(error, result) {
                if (settled) return;
                settled = true; clearTimer(timer);
                if (worker) { worker.onmessage = null; worker.onerror = null; worker.onmessageerror = null; worker.terminate(); }
                if (active === job) active = null;
                if (error) reject(error); else resolve(result);
            } };
            try {
                if (!Number.isSafeInteger(id) || id < 1) throw new Error('Invalid conversion request.');
                conversionExtension(name);
                if (!(bytes instanceof Uint8Array) || !bytes.length || bytes.length > limits.inputBytes) throw new Error('Choose audio of 32 MiB or less.');
                worker = createWorker(); active = job;
                timer = setTimer(() => job.finish(new Error('Audio conversion timed out after two minutes. Try a smaller file or desktop conversion.')), limits.timeoutMs);
                worker.onerror = event => { event.preventDefault?.(); job.finish(new Error('The audio converter could not start or ran out of memory. Try again or use desktop conversion.')); };
                worker.onmessageerror = () => job.finish(new Error('The audio converter returned an unreadable response.'));
                worker.onmessage = ({ data }) => {
                    if (active !== job || data?.id !== id || settled) return;
                    try {
                        if (data.type === 'progress') { onProgress({ id, phase: data.phase, progress: Math.max(0, Math.min(1, Number(data.progress) || 0)) }); return; }
                        if (data.type === 'error') { job.finish(new Error(String(data.message).slice(0, 512))); return; }
                        if (data.type !== 'result') throw new Error('The audio converter returned an unexpected response.');
                        waveFrameCount(data.bytes); job.finish(null, data.bytes);
                    } catch (error) { job.finish(error); }
                };
                // Keep the .NET caller's array usable. Only transfer this single bounded copy.
                const copy = bytes.slice();
                worker.postMessage({ id, name, bytes: copy }, [copy.buffer]);
            } catch (error) { job.finish(error); }
        });
    }
    async function convertBase64(id, name, bytes) {
        const result = await convert(id, name, bytes);
        // Promise<byte[]> is not supported by .NET's source-generated JS interop.
        // WAV output is at most 2.9 MB; bound both this encoding and the managed decode.
        const chunks = [];
        for (let offset = 0; offset < result.length; offset += 32768)
            chunks.push(String.fromCharCode(...result.subarray(offset, offset + 32768)));
        return btoa(chunks.join(''));
    }
    return { convert, convertBase64, cancel, dispose: () => cancel() };
}
