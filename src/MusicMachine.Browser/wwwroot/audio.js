// .NET imports this object's methods through setModuleImports("musicmachine", ...).
// PCM transfers are bounded, copied into AudioBuffer once, and never retained on the managed heap.
export function createAudioBridge({ createContext, showNotice = () => {}, hideNotice = () => {} } = {}) {
    let context;
    let nextId = 0;
    const buffers = new Map();
    const blockedMessage = 'Sound is paused by your browser. Click Enable sound, check this tab’s sound permission and your output device, then press Play again if needed.';
    const fail = (entry, error) => {
        if (!buffers.has(entry.id)) return;
        entry.pending = false;
        if (entry.source) {
            entry.source.onended = null;
            try { entry.source.stop(); } catch { /* A failed source may never have started. */ }
            entry.source.disconnect();
            entry.source.buffer = null;
            entry.source = null;
        }
        entry.error = `${error?.message || 'The audio output could not start'}. Check the browser sound permission and output device, then press Play again.`;
        showNotice(entry.error);
    };
    function getContext() {
        if (!context || context.state === 'closed') {
            const AudioContextType = globalThis.AudioContext || globalThis.webkitAudioContext;
            if (!createContext && !AudioContextType) throw new Error('This browser does not support Web Audio. Use a current Chrome, Edge, Firefox or Safari, or export WAV/QOA');
            context = createContext ? createContext() : new AudioContextType({ latencyHint: 'interactive' });
            context.addEventListener('statechange', () => {
                if (context.state === 'running') hideNotice();
                else if ([...buffers.values()].some(entry => entry.pending || entry.source)) showNotice(blockedMessage);
            });
        }
        return context;
    }
    async function unlock() {
        try {
            const ctx = getContext();
            if (ctx.state !== 'running') await ctx.resume();
            if (ctx.state === 'running') hideNotice();
        } catch (error) {
            showNotice(`Could not enable sound: ${error?.message || 'audio output unavailable'}. Check this tab’s sound permission and your output device.`);
        }
    }
    function position(id) {
        const entry = buffers.get(id);
        if (!entry) return 0;
        if (entry.ended) return entry.frames;
        if (!entry.source) return entry.startFrame;
        const frame = entry.startFrame + Math.max(0, Math.floor((context.currentTime - entry.startedAt) * entry.sampleRate));
        return entry.loop ? frame % entry.frames : Math.min(frame, entry.frames);
    }
    function release(id) {
        const entry = buffers.get(id);
        if (!entry) return;
        // Delete first: an already-resolving resume promise or late ended event cannot resurrect it.
        buffers.delete(id);
        entry.pending = false;
        if (entry.source) {
            entry.source.onended = null;
            try { entry.source.stop(); } catch { /* Already ended. */ }
            entry.source.disconnect();
            entry.source.buffer = null;
            entry.source = null;
        }
        entry.buffer = null;
        if (!buffers.size) hideNotice();
    }
    return {
        unlock,
        createBuffer(frames, sampleRate) {
            if (!Number.isInteger(frames) || frames <= 0 || frames > sampleRate * 300) throw new Error('Browser audio buffer exceeds the five-minute playback limit');
            const ctx = getContext();
            const id = ++nextId;
            const buffer = ctx.createBuffer(2, frames, sampleRate);
            buffers.set(id, { id, buffer, frames, sampleRate, startFrame: 0, startedAt: 0, source: null, loop: false, pending: false, ended: false, error: null });
            return id;
        },
        writeBuffer(id, pcm, offset, frames) {
            const entry = buffers.get(id);
            if (!entry || entry.source || entry.pending) throw new Error('The audio buffer is unavailable or already playing');
            if (!Number.isInteger(offset) || !Number.isInteger(frames) || offset < 0 || frames < 0 || offset + frames > entry.frames || pcm.byteLength < frames * 8) throw new Error('Invalid audio chunk');
            // .NET byte[] is a Uint8Array. DataView also accepts non-four-byte-aligned byte offsets.
            const samples = new DataView(pcm.buffer, pcm.byteOffset, frames * 8);
            const left = entry.buffer.getChannelData(0), right = entry.buffer.getChannelData(1);
            for (let frame = 0; frame < frames; frame++) {
                left[offset + frame] = samples.getFloat32(frame * 8, true);
                right[offset + frame] = samples.getFloat32(frame * 8 + 4, true);
            }
        },
        start(id, startFrame, loop) {
            const entry = buffers.get(id);
            if (!entry) throw new Error('The audio buffer was released');
            if (entry.source || entry.pending) throw new Error('The audio buffer is already playing');
            const ctx = getContext();
            entry.startFrame = Math.max(0, Math.min(startFrame, entry.frames - 1));
            entry.loop = loop;
            entry.pending = true;
            entry.ended = false;
            entry.error = null;
            const play = () => {
                if (!buffers.has(id) || !entry.pending) return;
                if (ctx.state !== 'running') { showNotice(blockedMessage); return; }
                const source = ctx.createBufferSource();
                source.buffer = entry.buffer;
                source.loop = loop;
                source.loopStart = 0;
                source.loopEnd = entry.frames / entry.sampleRate;
                source.connect(ctx.destination);
                source.onended = () => {
                    if (!buffers.has(id) || entry.source !== source) return;
                    entry.ended = true;
                    entry.source = null;
                    source.disconnect();
                    source.buffer = null;
                };
                entry.startedAt = ctx.currentTime;
                entry.source = source;
                entry.pending = false;
                source.start(0, entry.startFrame / entry.sampleRate);
                hideNotice();
            };
            if (ctx.state === 'running') {
                try { play(); } catch (error) { fail(entry, error); throw error; }
            } else {
                showNotice(blockedMessage);
                // resume() must be initiated within a gesture; the capture-phase handler usually
                // already did this. Pending Play is cancellable while a browser awaits permission.
                ctx.resume().then(play).catch(error => fail(entry, error));
            }
        },
        release,
        position,
        isPlaying(id) { const entry = buffers.get(id); return !!entry && !entry.error && (entry.pending || !!entry.source) && !entry.ended; },
        error(id) { return buffers.get(id)?.error ?? null; },
        stopAll() { for (const id of buffers.keys()) release(id); }
    };
}
