import { CONVERSION_LIMITS as limits, conversionExtension } from './conversion-policy.js';

export function waveFrameCount(bytes) {
    if (!(bytes instanceof Uint8Array) || bytes.length < 44 || bytes.length > limits.outputBytes) throw new Error('Converted audio exceeds the supported size.');
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const text = at => String.fromCharCode(...bytes.subarray(at, at + 4));
    if (text(0) !== 'RIFF' || text(8) !== 'WAVE' || view.getUint32(4, true) + 8 !== bytes.length) throw new Error('Converter returned an invalid WAV file.');
    let frames = 0, format = false, data = false;
    for (let offset = 12; offset + 8 <= bytes.length;) {
        const length = view.getUint32(offset + 4, true), start = offset + 8, end = start + length;
        if (end > bytes.length) throw new Error('Converter returned a truncated WAV file.');
        if (text(offset) === 'fmt ') {
            if (format || length < 16 || view.getUint16(start, true) !== 1 || view.getUint16(start + 2, true) !== 1 ||
                view.getUint32(start + 4, true) !== limits.sampleRate || view.getUint16(start + 12, true) !== 2 || view.getUint16(start + 14, true) !== 16)
                throw new Error('Converter must return mono 48 kHz PCM16 WAV.');
            format = true;
        }
        if (text(offset) === 'data') {
            if (data || length % 2) throw new Error('Converter returned invalid PCM samples.');
            data = true; frames = length / 2;
        }
        offset = end + (length & 1);
    }
    if (!format || !data || frames === 0) throw new Error('No usable audio was found in this file.');
    if (frames > limits.frames) throw new Error('Sampling accepts at most 30 seconds. Trim the source audio and try again.');
    return frames;
}
// This adapter is built from pinned stock FFmpeg sources. It exposes one bounded
// audio operation; no command-line parser, filesystem, protocol or codec encoder.
export async function convertWithCore(core, name, input, onProgress = () => {}) {
    conversionExtension(name);
    if (!(input instanceof Uint8Array) || !input.length || input.length > limits.inputBytes) throw new Error('Choose audio of 32 MiB or less.');
    let pointer = 0;
    const previousProgress = core.onProgress;
    try {
        pointer = core._malloc(input.length);
        if (!pointer) throw new Error('This audio needs too much browser memory. Try a smaller file.');
        core.HEAPU8.set(input, pointer);
        core.onProgress = progress => onProgress({ phase: 'convert', progress: Math.max(0, Math.min(0.99, progress)) });
        const frames = core._mm_decode(pointer, input.length);
        if (!Number.isInteger(frames)) throw new Error('Audio converter returned an invalid sample count.');
        if (frames < 0) {
            const messages = {
                '-1': 'Could not decode this audio file. It may be corrupt or unsupported.',
                '-2': 'Choose audio of 32 MiB or less.',
                '-3': 'Sampling accepts at most 30 seconds. Trim the source audio and try again.',
                '-4': 'Sampling supports audio up to 192 kHz and eight channels with a consistent format.',
                '-5': 'This audio needs too much browser memory. Try a smaller file.',
                '-6': 'Audio conversion timed out. Try a smaller file or desktop conversion.'
            };
            throw new Error(messages[frames] ?? 'Audio conversion failed.');
        }
        if (frames < 1 || frames > limits.frames) throw new Error('Audio converter returned an invalid sample count.');
        const start = core._mm_pcm(), size = frames * 2;
        if (!Number.isSafeInteger(start) || start < 0 || start > core.HEAPU8.length - size) throw new Error('Audio converter returned an invalid sample buffer.');
        const output = new Uint8Array(44 + size), view = new DataView(output.buffer);
        const text = (at, value) => { for (let i = 0; i < value.length; i++) output[at + i] = value.charCodeAt(i); };
        text(0, 'RIFF'); view.setUint32(4, output.length - 8, true); text(8, 'WAVE'); text(12, 'fmt ');
        view.setUint32(16, 16, true); view.setUint16(20, 1, true); view.setUint16(22, 1, true);
        view.setUint32(24, limits.sampleRate, true); view.setUint32(28, limits.sampleRate * 2, true);
        view.setUint16(32, 2, true); view.setUint16(34, 16, true); text(36, 'data'); view.setUint32(40, size, true);
        output.set(core.HEAPU8.subarray(start, start + size), 44);
        waveFrameCount(output); onProgress({ phase: 'done', progress: 1 });
        return output;
    } finally {
        core.onProgress = previousProgress;
        if (pointer) core._free(pointer);
    }
}

// Codec packetization is untrusted. Bound progress messages independently of the
// WASM heap, while keeping phase transitions and completion immediately visible.
export function throttleProgress(report, now = () => performance.now()) {
    let previousTime = -Infinity, previousPhase;
    return value => {
        const currentTime = now();
        if (value.phase === previousPhase && value.progress < 1 && currentTime - previousTime < 50) return;
        previousTime = currentTime; previousPhase = value.phase;
        report(value);
    };
}
