// Optional reference integration: uses the installed native FFmpeg executable to
// generate synthetic fixtures and compare decodes, never any user audio.
import { mkdtemp, rm, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { execFileSync } from 'node:child_process';
import assert from 'node:assert/strict';
import factory from '../.ffmpeg-build/output/ffmpeg-core.js';
import { convertWithCore, waveFrameCount } from '../wwwroot/conversion-worker-core.js';
const temporary = await mkdtemp(join(tmpdir(), 'musicmachine-audio-formats-'));
const wasmBinary = await readFile(new URL('../.ffmpeg-build/output/ffmpeg-core.wasm', import.meta.url));
const formats = [['mp3', 'libmp3lame'], ['flac', 'flac'], ['ogg', 'libvorbis'], ['opus', 'libopus'], ['m4a', 'aac'], ['aac', 'aac'], ['aif', 'pcm_s16be'], ['aiff', 'pcm_s24be'], ['wma', 'wmav2'], ['caf', 'pcm_s16le']];
const results = [];
try {
    for (const [extension, encoder] of formats) {
        const inputPath = join(temporary, 'tone.' + extension), referencePath = join(temporary, extension + '.pcm');
        execFileSync('ffmpeg', ['-v', 'error', '-nostdin', '-y', '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=44100:duration=0.5', '-c:a', encoder, inputPath], { timeout: 15000 });
        execFileSync('ffmpeg', ['-v', 'error', '-nostdin', '-y', '-i', inputPath, '-ac', '1', '-ar', '48000', '-f', 's16le', referencePath], { timeout: 15000 });
        const core = await factory({ wasmBinary });
        const wave = await convertWithCore(core, 'tone.' + extension, new Uint8Array(await readFile(inputPath)));
        const frames = waveFrameCount(wave), reference = await readFile(referencePath);
        if (extension === 'm4a' && reference.length !== frames * 2) {
            // FFmpeg 9's MOV demuxer honors the trailing AAC discard/edit duration;
            // the installed FFmpeg 7 reference retains part of the final AAC frame.
            assert.equal(frames, 24000);
            assert.ok(reference.length / 2 >= frames && reference.length / 2 - frames <= 1115);
        } else assert.equal(frames * 2, reference.length, `${extension}: native reference sample count`);
        const actual = new DataView(wave.buffer, wave.byteOffset + 44, frames * 2);
        let maximumError = 0, squareError = 0, signalPower = 0;
        for (let i = 0; i < frames; i++) {
            const a = actual.getInt16(i * 2, true), b = reference.readInt16LE(i * 2), d = a - b;
            maximumError = Math.max(maximumError, Math.abs(d)); squareError += d * d; signalPower += b * b;
        }
        const snrDb = squareError ? 10 * Math.log10(signalPower / squareError) : 200;
        assert.ok(snrDb > 55, `${extension}: decoder differs materially from native FFmpeg (${snrDb} dB SNR)`);
        results.push({ extension, frames, nativeFrames: reference.length / 2, maximumError, snrDb: Number(snrDb.toFixed(2)) });
    }
    console.log(JSON.stringify({ nativeVersion: execFileSync('ffmpeg', ['-version'], { encoding: 'utf8' }).split('\n')[0], formats: results }, null, 2));
} finally { await rm(temporary, { recursive: true, force: true }); }
