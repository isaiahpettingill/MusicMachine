// Injected only by CI/CDP before page startup. Every native method still executes;
// no fake AudioContext, replacement samples, autoplay bypass or virtual audio API.
export const audioObserverScript = `(() => {
    const state = { supported: !!(globalThis.AudioContext && globalThis.BaseAudioContext && globalThis.AudioBufferSourceNode),
        buffers: [], sources: [], starts: 0, stops: 0, ended: 0, analyserPeak: 0, contexts: [], errors: [] };
    const contexts = [], taps = new Map(), ids = new WeakMap();
    function contextId(context) { let id = ids.get(context); if (id == null) { id = contexts.length; contexts.push(context); ids.set(context, id); } return id; }
    globalThis.__musicMachineSmokeAudio = {
        snapshot() { return { ...state, contexts: contexts.map(c => ({ state: c.state, currentTime: c.currentTime, sampleRate: c.sampleRate, realTime: c instanceof AudioContext })), active: [...taps.values()].filter(t => !t.finished).length }; }
    };
    if (!state.supported) return;
    const createBuffer = BaseAudioContext.prototype.createBuffer;
    BaseAudioContext.prototype.createBuffer = function(...args) {
        const buffer = Reflect.apply(createBuffer, this, args); contextId(this);
        state.buffers.push({ frames: buffer.length, channels: buffer.numberOfChannels, sampleRate: buffer.sampleRate });
        return buffer;
    };
    const start = AudioBufferSourceNode.prototype.start, stop = AudioBufferSourceNode.prototype.stop;
    function cleanup(source) {
        const tap = taps.get(source); if (!tap || tap.finished) return;
        tap.finished = true; clearInterval(tap.timer);
        try { source.disconnect(tap.analyser); } catch {}
        try { tap.analyser.disconnect(); } catch {}
    }
    AudioBufferSourceNode.prototype.start = function(...args) {
        const context = this.context, buffer = this.buffer, id = contextId(context);
        let peak = 0, energy = 0, count = 0, finite = true;
        if (buffer) for (let ch = 0; ch < buffer.numberOfChannels; ch++) {
            const data = buffer.getChannelData(ch), end = Math.min(data.length, buffer.sampleRate * 2);
            for (let i = 0; i < end; i += 8) { const v = data[i]; finite &&= Number.isFinite(v); peak = Math.max(peak, Math.abs(v)); energy += v * v; count++; }
        }
        const record = { context: id, frames: buffer?.length ?? 0, channels: buffer?.numberOfChannels ?? 0,
            sampleRate: buffer?.sampleRate ?? 0, peak, rms: count ? Math.sqrt(energy / count) : 0, finite };
        // Add a native analyser as a parallel observation branch. The application's
        // original connection to the real destination is not replaced or muted.
        try {
            const analyser = context.createAnalyser(); analyser.fftSize = 2048;
            // AnalyserNode explicitly supports an unconnected output; this
            // avoids both audible duplication and a zero-gain optimizer shortcut.
            this.connect(analyser);
            const data = new Float32Array(analyser.fftSize), tap = { analyser, finished: false };
            tap.timer = setInterval(() => { analyser.getFloatTimeDomainData(data); for (const value of data) state.analyserPeak = Math.max(state.analyserPeak, Math.abs(value)); }, 40);
            taps.set(this, tap); this.addEventListener('ended', () => { state.ended++; cleanup(this); }, { once: true });
        } catch (error) { state.errors.push(String(error)); }
        const result = Reflect.apply(start, this, args); state.starts++; state.sources.push(record); return result;
    };
    AudioBufferSourceNode.prototype.stop = function(...args) { const result = Reflect.apply(stop, this, args); state.stops++; cleanup(this); return result; };
})();`;
