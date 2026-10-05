// Real single-thread Web Worker smoke test, independent of the Avalonia publish.
// Uses a new headless Chromium profile; never touches an existing browser session.
import { createServer } from 'node:http';
import { readFile, mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve, extname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawn, execFileSync } from 'node:child_process';
import assert from 'node:assert/strict';
import { FFMPEG_PIN as pin, CONVERSION_LIMITS as limits } from '../wwwroot/conversion-policy.js';
const root = fileURLToPath(new URL('../wwwroot/', import.meta.url));
const temporary = await mkdtemp(join(tmpdir(), 'musicmachine-conversion-smoke-'));
const requests = [];
const networkRequests = [];
const networkSetup = [];
let browser, server;
const html = `<!doctype html><title>MusicMachine isolated conversion smoke</title><script type="module">
import {createAudioConverter} from './conversion.js';
import {waveFrameCount} from './conversion-worker-core.js';
window.smoke = async () => {
 const progress=[]; const bridge=createAudioConverter({onProgress:p=>progress.push(p)});
 const before=performance.getEntriesByType('resource').filter(r=>r.name.includes('/vendor/')).length;
 if(before) throw new Error('Runtime loaded eagerly');
 const input=new Uint8Array(await (await fetch('/short.mp3')).arrayBuffer());
 const first=await bridge.convert(1,'tone.mp3',input);
 const second=await bridge.convertBase64(2,'tone.mp3',input);
 const canceled=bridge.convert(3,'tone.mp3',input); bridge.cancel(3);
 let cancel=false; try {await canceled;} catch(e){cancel=e.name==='AbortError';}
 const restored=await bridge.convert(4,'tone.mp3',input);
 let invalid=false; try {await bridge.convert(5,'bad.mp3',new Uint8Array([1,2,3]));}catch(e){invalid=true;}
 const long=new Uint8Array(await (await fetch('/long.mp3')).arrayBuffer());
 let tooLong=false; try {await bridge.convert(6,'long.mp3',long);}catch(e){tooLong=e.message.includes('30 seconds');}
 bridge.dispose();
 return {before,frames:waveFrameCount(first),base64Bytes:atob(second).length,retryFrames:waveFrameCount(restored),cancel,invalid,tooLong,progress:progress.length,crossOriginIsolated};
};
</script>`;
try {
    for (const [name, duration] of [['short', '0.5'], ['long', '31']])
        execFileSync('ffmpeg', ['-v', 'error', '-nostdin', '-y', '-f', 'lavfi', '-i', `sine=frequency=440:sample_rate=44100:duration=${duration}`, '-c:a', 'libmp3lame', join(temporary, `${name}.mp3`)]);
    server = createServer(async (req, res) => {
        try {
            const pathname = decodeURIComponent(new URL(req.url, 'http://localhost').pathname);
            requests.push(pathname);
            if (pathname === '/') { res.writeHead(200, { 'content-type': 'text/html' }); res.end(html); return; }
            const isFixture = ['/short.mp3', '/long.mp3'].includes(pathname);
            const path = resolve(isFixture ? temporary : root, '.' + pathname);
            if (!path.startsWith((isFixture ? temporary : root.replace(/\/$/, '')) + '/')) throw new Error('Path rejected');
            const bytes = await readFile(path);
            res.writeHead(200, { 'content-type': extname(path) === '.js' ? 'text/javascript' : extname(path) === '.json' ? 'application/json' : 'application/octet-stream', 'content-length': bytes.length }); res.end(bytes);
        } catch { res.writeHead(404); res.end(); }
    });
    await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
    const origin = `http://127.0.0.1:${server.address().port}`;
    browser = spawn(process.env.CHROMIUM ?? 'chromium', ['--headless', '--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--disable-background-networking', '--no-first-run', `--user-data-dir=${join(temporary, 'profile')}`, '--remote-debugging-pipe', 'about:blank'], { stdio: ['ignore', 'ignore', 'pipe', 'pipe', 'pipe'] });
    let stderr = '', buffer = '', id = 0;
    const pending = new Map();
    browser.stderr.on('data', b => { stderr = (stderr + b).slice(-12000); });
    browser.on('error', e => { for (const p of pending.values()) p.reject(e); });
    browser.on('exit', code => { for (const p of pending.values()) { clearTimeout(p.timer); p.reject(new Error(`Chromium exited ${code}: ${stderr}`)); } pending.clear(); });
    for (const pipe of [browser.stdio[3], browser.stdio[4]]) pipe.on('error', () => {});
    browser.stdio[4].on('data', b => {
        buffer += b.toString();
        let index;
        while ((index = buffer.indexOf('\0')) >= 0) {
            const msg = JSON.parse(buffer.slice(0, index)); buffer = buffer.slice(index + 1);
            if (msg.method === 'Network.requestWillBeSent') networkRequests.push(msg.params.request.url);
            if (msg.method === 'Target.attachedToTarget')
                networkSetup.push(command('Network.enable', {}, msg.params.sessionId).catch(() => {}));
            const p = pending.get(msg.id); if (!p) continue;
            pending.delete(msg.id); clearTimeout(p.timer); if (msg.error) p.reject(new Error(JSON.stringify(msg.error))); else p.resolve(msg.result);
        }
    });
    function command(method, params = {}, sessionId) {
        return new Promise((resolve, reject) => {
            const commandId = ++id;
            const timer = setTimeout(() => { pending.delete(commandId); reject(new Error(`CDP ${method} timed out: ${stderr}`)); }, 180000);
            pending.set(commandId, { resolve, reject, timer }); browser.stdio[3].write(JSON.stringify({ id: commandId, method, params, ...(sessionId ? { sessionId } : {}) }) + '\0');
        });
    }
    await command('Target.setAutoAttach', { autoAttach: true, waitForDebuggerOnStart: false, flatten: true });
    const { targetId } = await command('Target.createTarget', { url: origin });
    const { sessionId } = await command('Target.attachToTarget', { targetId, flatten: true });
    await command('Runtime.enable', {}, sessionId);
    await command('Network.enable', {}, sessionId);
    await command('Target.setAutoAttach', { autoAttach: true, waitForDebuggerOnStart: false, flatten: true }, sessionId);
    const { result, exceptionDetails } = await command('Runtime.evaluate', { expression: `new Promise((resolve,reject)=>{let n=0;const timer=setInterval(()=>{if(window.smoke){clearInterval(timer);window.smoke().then(resolve,reject)}else if(++n>100){clearInterval(timer);reject(Error('Smoke page did not load'))}},50)})`, awaitPromise: true, returnByValue: true }, sessionId);
    if (exceptionDetails) throw new Error(JSON.stringify(exceptionDetails));
    const value = result.value;
    assert.equal(value.before, 0); assert.equal(value.frames, 24000); assert.equal(value.retryFrames, 24000);
    assert.equal(value.cancel, true); assert.equal(value.invalid, true); assert.equal(value.tooLong, true); assert.equal(value.crossOriginIsolated, false);
    const assetRequests = requests.filter(p => /\.part\d+$|\/ffmpeg-core\.js$/.test(p));
    assert.equal(assetRequests.length, 1 + Math.ceil(pin.wasmBytes / limits.chunkBytes), 'Runtime assets should download once, then use the verified cache');
    await Promise.all(networkSetup);
    assert.equal(networkRequests.filter(url => /^https?:/.test(url) && new URL(url).origin !== origin).length, 0, 'Conversion must not request a third-party origin');
    console.log(JSON.stringify({ ...value, observedNetworkRequests: networkRequests.length, assetRequests, manifestRequests: requests.filter(p => p.endsWith('manifest.json')).length }, null, 2));
} finally {
    browser?.kill('SIGTERM');
    if (server) await new Promise(resolve => server.close(resolve));
    await rm(temporary, { recursive: true, force: true, maxRetries: 3, retryDelay: 100 });
}
