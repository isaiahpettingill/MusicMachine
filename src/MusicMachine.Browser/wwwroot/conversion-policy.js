// Shared by the host, worker, vendoring tool and tests. No runtime is loaded here.
export const CONVERSION_LIMITS = Object.freeze({
    inputBytes: 32 * 1024 * 1024,
    seconds: 30,
    sampleRate: 48000,
    frames: 1440000,
    outputBytes: 1440001 * 2 + 4096,
    memoryBytes: 256 * 1024 * 1024,
    timeoutMs: 120000,
    chunkBytes: 8 * 1024 * 1024
});
export const FFMPEG_PIN = Object.freeze({
    version: '9.0.2',
    runtime: 'audio-ffmpeg-9.0.2-emscripten-6.0.3-v1',
    sourceSha256: '8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e',
    jsBytes: 15206,
    jsSha256: '5432cad56ccafd2cf54fa392146c17524e278f9ac1aeccda8a2de95aba1cd519',
    wasmBytes: 1306282,
    wasmSha256: '87590ed10f4af23258ef8f51bee791d7deba28665724284bf29239576e39f468'
});
export const CONVERSION_EXTENSIONS = Object.freeze(['mp3', 'flac', 'ogg', 'opus', 'm4a', 'aac', 'aif', 'aiff', 'wma', 'caf']);
export function conversionExtension(name) {
    const extension = String(name).split('.').pop().toLowerCase();
    if (!CONVERSION_EXTENSIONS.includes(extension)) throw new Error('Choose WAV, QOA, MP3, FLAC, Ogg, Opus, M4A, AAC, AIFF, WMA or CAF audio.');
    return extension;
}
