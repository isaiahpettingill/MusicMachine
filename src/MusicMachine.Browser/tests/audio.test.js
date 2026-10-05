import test from 'node:test';
import assert from 'node:assert/strict';
import { createAudioBridge } from '../wwwroot/audio.js';

function setup(initialState = 'running') {
    const sources = [];
    const notices = [];
    let resolveResume, rejectResume;
    const context = {
        state: initialState, currentTime: 0, destination: {},
        addEventListener() {},
        createBuffer(channels, frames, sampleRate) {
            const data = Array.from({ length: channels }, () => new Float32Array(frames));
            return { getChannelData(channel) { return data[channel]; }, duration: frames / sampleRate, data };
        },
        createBufferSource() {
            const source = { connected: false, stopped: false, starts: 0,
                connect() { this.connected = true; }, disconnect() { this.connected = false; },
                start(when, offset) { this.starts++; this.offset = offset; }, stop() { this.stopped = true; } };
            sources.push(source); return source;
        },
        resume() { return new Promise((resolve, reject) => { resolveResume = resolve; rejectResume = reject; }); }
    };
    const bridge = createAudioBridge({ createContext: () => context, showNotice: text => notices.push(text) });
    return { bridge, context, sources, notices,
        resume() { context.state = 'running'; resolveResume(); }, reject() { rejectResume(new Error('Permission blocked')); } };
}
const tick = () => new Promise(resolve => setTimeout(resolve, 0));

test('PCM bytes preserve stereo channels, offsets and unaligned typed arrays', () => {
    const { bridge, sources } = setup();
    const id = bridge.createBuffer(4, 48000);
    const bytes = new Uint8Array(17), samples = new DataView(bytes.buffer, 1);
    [0.25, -0.5, 0.75, -1].forEach((value, index) => samples.setFloat32(index * 4, value, true));
    bridge.writeBuffer(id, bytes.subarray(1), 1, 2);
    bridge.start(id, 0, false);
    assert.deepEqual([...sources[0].buffer.data[0]], [0, 0.25, 0.75, 0]);
    assert.deepEqual([...sources[0].buffer.data[1]], [0, -0.5, -1, 0]);
});

test('seek offset and loop position follow AudioContext sample time', () => {
    const { bridge, context, sources } = setup();
    const id = bridge.createBuffer(48000, 48000);
    context.currentTime = 10;
    bridge.start(id, 24000, true);
    assert.equal(sources[0].offset, 0.5);
    assert.equal(sources[0].loopEnd, 1);
    context.currentTime = 10.75;
    assert.equal(bridge.position(id), 12000);
    assert.equal(bridge.isPlaying(id), true);
});

test('normal completion marks playback stopped and preserves final position', () => {
    const { bridge, sources } = setup();
    const id = bridge.createBuffer(48000, 48000);
    bridge.start(id, 0, false);
    sources[0].onended();
    assert.equal(bridge.isPlaying(id), false);
    assert.equal(bridge.position(id), 48000);
    assert.equal(sources[0].connected, false);
});

test('repeated Play/Stop releases source and buffer, with no stale ended callback', () => {
    const { bridge, sources } = setup();
    for (let iteration = 0; iteration < 20; iteration++) {
        const id = bridge.createBuffer(48000, 48000);
        bridge.start(id, 0, false);
        bridge.release(id);
        bridge.release(id);
        assert.equal(bridge.isPlaying(id), false);
        assert.equal(sources.at(-1).buffer, null);
        assert.equal(sources.at(-1).onended, null);
        assert.equal(sources.at(-1).stopped, true);
    }
});

test('Stop cancels a pending browser unlock without starting a late source', async () => {
    const { bridge, sources, resume, notices } = setup('suspended');
    const id = bridge.createBuffer(48000, 48000);
    bridge.start(id, 0, false);
    assert.equal(bridge.isPlaying(id), true);
    assert.equal(bridge.position(id), 0);
    assert.match(notices[0], /Enable sound/);
    bridge.release(id);
    resume(); await tick();
    assert.equal(sources.length, 0);
    assert.equal(bridge.isPlaying(id), false);
});

test('successful unlock starts the pending source exactly once', async () => {
    const { bridge, sources, resume } = setup('suspended');
    const id = bridge.createBuffer(48000, 48000);
    bridge.start(id, 12000, false);
    resume(); await tick();
    assert.equal(sources.length, 1);
    assert.equal(sources[0].starts, 1);
    assert.equal(sources[0].offset, 0.25);
});

test('permission failure stops the transport with actionable error', async () => {
    const { bridge, reject, notices } = setup('suspended');
    const id = bridge.createBuffer(48000, 48000);
    bridge.start(id, 0, false);
    reject(); await tick();
    assert.equal(bridge.isPlaying(id), false);
    assert.match(bridge.error(id), /Permission blocked/);
    assert.match(notices.at(-1), /sound permission and output device/);
});

test('invalid buffers/chunks are rejected and pagehide releases every source', () => {
    const { bridge, sources } = setup();
    assert.throws(() => bridge.createBuffer(48000 * 301, 48000), /five-minute/);
    const id = bridge.createBuffer(4, 48000);
    assert.throws(() => bridge.writeBuffer(id, new Uint8Array(8), 4, 1), /Invalid audio chunk/);
    bridge.start(id, 0, true);
    assert.throws(() => bridge.start(id, 0, false), /already playing/);
    bridge.stopAll();
    assert.equal(sources[0].buffer, null);
    assert.equal(bridge.isPlaying(id), false);
});


test('a device start failure disconnects its failed source immediately', () => {
    const { bridge, context, sources } = setup();
    const create = context.createBufferSource.bind(context);
    context.createBufferSource = () => { const source = create(); source.start = () => { throw new Error('Output unavailable'); }; return source; };
    const id = bridge.createBuffer(48000, 48000);
    assert.throws(() => bridge.start(id, 0, false), /Output unavailable/);
    assert.equal(bridge.isPlaying(id), false);
    assert.equal(sources[0].connected, false);
    assert.equal(sources[0].buffer, null);
    assert.match(bridge.error(id), /Output unavailable/);
});
