// CI-only end-to-end smoke of the actual prepared/published Avalonia application.
// Never launches locally, never replaces Web Audio, and never uses an existing profile.
import { createServer } from 'node:http';
import { readFile, writeFile, mkdir, mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { resolve, join } from 'node:path';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import assert from 'node:assert/strict';
import { parsePagesHeaders, readPreparedAsset, viewPreferences, inspectPng, requireRenderedCanvas, renderedDifference, startupExpression, acceptStartup } from './published-smoke/support.mjs';
import { audioObserverScript } from './published-smoke/audio-observer.mjs';
import { inputObserverScript } from './published-smoke/input-observer.mjs';
import { keyEvents } from './published-smoke/key-events.mjs';

if (process.env.CI !== 'true') throw new Error('Published-app browser smoke is CI-only. Do not use it to bypass a local browser/socket restriction.');
if (!process.argv[2]) throw new Error('Usage: node smoke-published-app.mjs <prepared-pages-directory> [evidence-directory]');
const root = resolve(process.argv[2]), evidence = resolve(process.argv[3] ?? 'artifacts/browser-app-smoke');
if (evidence === root || evidence.startsWith(root + '/')) throw new Error('Evidence must be outside the published site.');
const rules = parsePagesHeaders(await readFile(join(root, '_headers'), 'utf8'));
const build = JSON.parse(await readFile(join(root, 'build.json'), 'utf8'));
if (process.env.GITHUB_SHA) assert.equal(build.commit, process.env.GITHUB_SHA, 'Prepared app must match this CI commit');
await readFile(join(root, 'index.html'));
await mkdir(evidence, { recursive: true });
const temporary = await mkdtemp(join(tmpdir(), 'musicmachine-published-smoke-'));
const requests = [], network = [], failures = [], warnings = [], pending = new Map();
const requireReadySignal = process.env.MUSICMACHINE_REQUIRE_READY_SIGNAL !== 'false';
const report = { build, passed: false, checks: [], screenshots: [], keyboard: [], proof: 'Real published Avalonia app, native Chrome Web Audio and keyboard events. No physical speaker/heard-audio claim.' };
let server, browser, sessionId, stderr = '', messageId = 0, incoming = '', closing = false, navigating = false;
let exited = false;
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
function checkFailures() { if (failures.length) throw new Error(failures.join('\n')); }
function command(method, params = {}, session = sessionId, timeout = 30000) {
    if (exited) return Promise.reject(new Error('Chrome already exited: ' + stderr));
    return new Promise((resolve, reject) => {
        const id = ++messageId;
        const timer = setTimeout(() => { pending.delete(id); reject(new Error(`${method} timed out`)); }, timeout);
        pending.set(id, { resolve, reject, timer });
        browser.stdio[3].write(JSON.stringify({ id, method, params, ...(session ? { sessionId: session } : {}) }) + '\0', error => {
            if (!error) return; const item = pending.get(id); if (!item) return;
            pending.delete(id); clearTimeout(timer); reject(error);
        });
    });
}
async function evaluate(expression) {
    const result = await command('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true }, sessionId, 120000);
    if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
    return result.result.value;
}
async function until(label, expression, accept = value => !!value, timeout = 120000) {
    const start = Date.now(); let last;
    while (Date.now() - start < timeout) {
        checkFailures(); last = await evaluate(expression);
        if (accept(last)) return last;
        await delay(120);
    }
    throw new Error(`${label} timed out; last observed ${JSON.stringify(last)}`);
}
const canvasExpression = `(() => { const list=[...document.querySelectorAll('#out canvas')].filter(c=>c.width>=600&&c.height>=400&&c.getBoundingClientRect().width>=600); const c=list[0]; if(!c)return null; const r=c.getBoundingClientRect(); return {x:r.x,y:r.y,width:r.width,height:r.height,bitmapWidth:c.width,bitmapHeight:c.height}; })()`;
async function ready() {
    // Legacy compatibility still requires canvas pixels, managed keyboard
    // responses and real audio; current builds require the managed ready signal.
    await until(requireReadySignal ? 'App runtime readiness' : 'Legacy Avalonia host',
        startupExpression(requireReadySignal), value => acceptStartup(value, requireReadySignal));
    report.startupSignalRequired = requireReadySignal;
    const canvas = await until('Avalonia rendering canvas', canvasExpression);
    await evaluate('new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))');
    assert.equal(await evaluate('crossOriginIsolated'), true, 'Serve the real Pages COOP/COEP headers');
    assert.equal(await evaluate(`!!document.querySelector('#out.avalonia-container')`), true, 'Use the real Avalonia host');
    return canvas;
}
async function screenshot(name, canvas, waitForPaintMs = 0) {
    const deadline = Date.now() + waitForPaintMs;
    while (true) {
    checkFailures();
    const shot = await command('Page.captureScreenshot', { format: 'png', fromSurface: true, captureBeyondViewport: false,
        clip: { x: Math.max(0, canvas.x), y: Math.max(0, canvas.y), width: Math.min(1440 - canvas.x, canvas.width), height: Math.min(900 - canvas.y, canvas.height), scale: 1 } });
    const bytes = Buffer.from(shot.data, 'base64'); await writeFile(join(evidence, name), bytes);
    const pixels = inspectPng(bytes);
    try { requireRenderedCanvas(pixels); } catch (error) { if (Date.now() >= deadline) throw error; await delay(350); continue; }
    const sha256 = createHash('sha256').update(bytes).digest('hex'); report.screenshots.push({ name, sha256, pixels }); return sha256;
    }
}
async function focusApp(canvas) {
    await evaluate(`document.querySelector('#out').focus()`);
    const x = canvas.x + Math.min(canvas.width * 0.4, 420), y = canvas.y + Math.min(canvas.height * 0.28, 220);
    await command('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', clickCount: 1 });
    await command('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'left', clickCount: 1 });
}
async function key(code, key, number, ctrl = false, shift = false) {
    const before = await evaluate('__musicMachineSmokeInput.snapshot()');
    const entry = { code, key, ctrl, shift, before }; report.keyboard.push(entry);
    assert.equal(before.activeElement?.inApp, true, `App must retain keyboard focus before ${code}; see report.keyboard`);
    for (const event of keyEvents(code, key, number, ctrl, shift)) await command('Input.dispatchKeyEvent', event);
    entry.after = await evaluate('__musicMachineSmokeInput.snapshot()');
    const received = entry.after.events.filter(event => event.sequence > before.sequence && event.code === code);
    assert.ok(received.some(event => event.type === 'keydown' && event.target?.inApp && event.ctrl === ctrl && event.shift === shift),
        `Native ${code} keydown must reach the Avalonia host with expected modifiers; see report.keyboard`);
    assert.ok(received.some(event => event.type === 'keyup'), `Native ${code} keyup must arrive; see report.keyboard`);
}
async function prefs() { return viewPreferences(await evaluate(`localStorage.getItem('musicmachine.views')`)); }
async function expectPref(name, value) { await until(`Preference ${name}=${value}`, `localStorage.getItem('musicmachine.views')`, text => viewPreferences(text)[name] === value, 10000); }
try {
    server = createServer(async (req, res) => {
        try {
            if (!['GET', 'HEAD'].includes(req.method)) { res.writeHead(405); res.end(); return; }
            const item = await readPreparedAsset(root, req.url, rules);
            requests.push({ path: req.url, status: item.status, bytes: item.body.length, contentType: item.headers['content-type'] });
            res.writeHead(item.status, item.headers); res.end(req.method === 'HEAD' ? undefined : item.body);
        } catch (error) { failures.push(`Static asset server: ${error.message}`); res.writeHead(500); res.end(); }
    });
    await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
    const origin = `http://127.0.0.1:${server.address().port}`;
    browser = spawn(process.env.CHROMIUM ?? 'google-chrome', ['--headless', '--no-sandbox', '--disable-dev-shm-usage', '--disable-background-networking', '--no-first-run',
        '--window-size=1440,900', `--user-data-dir=${join(temporary, 'profile')}`, '--remote-debugging-pipe', 'about:blank'], { stdio: ['ignore', 'ignore', 'pipe', 'pipe', 'pipe'] });
    browser.stderr.on('data', bytes => { stderr = (stderr + bytes).slice(-30000); });
    browser.on('error', error => { exited = true; for (const entry of pending.values()) { clearTimeout(entry.timer); entry.reject(error); } pending.clear(); });
    browser.on('exit', (code, signal) => {
        exited = true;
        if (!closing) failures.push(`Chrome exited (${code ?? signal}): ${stderr}`);
        for (const entry of pending.values()) { clearTimeout(entry.timer); entry.reject(new Error('Chrome exited: ' + stderr)); } pending.clear();
    });
    for (const pipe of [browser.stdio[3], browser.stdio[4]]) pipe.on('error', () => {});
    browser.stdio[4].on('data', bytes => {
        incoming += bytes.toString(); let end;
        while ((end = incoming.indexOf('\0')) >= 0) {
            let message;
            try { message = JSON.parse(incoming.slice(0, end)); } catch (error) { failures.push(`Invalid CDP message: ${error.message}`); incoming = incoming.slice(end + 1); continue; }
            incoming = incoming.slice(end + 1);
            const entry = pending.get(message.id);
            if (entry) { pending.delete(message.id); clearTimeout(entry.timer); if (message.error) entry.reject(new Error(JSON.stringify(message.error))); else entry.resolve(message.result); continue; }
            if (closing) continue;
            const data = message.params;
            if (message.method === 'Runtime.exceptionThrown') failures.push('Runtime exception: ' + JSON.stringify(data.exceptionDetails));
            if (message.method === 'Runtime.consoleAPICalled' && ['error', 'assert'].includes(data.type)) failures.push('Console error: ' + data.args.map(a => a.description ?? a.value).join(' '));
            if (message.method === 'Log.entryAdded' && data.entry.level === 'error') failures.push('Browser log error: ' + data.entry.text);
            if (message.method === 'Log.entryAdded' && data.entry.level === 'warning') warnings.push(data.entry.text);
            if (message.method === 'Network.responseReceived') {
                const response = data.response; network.push({ url: response.url, status: response.status, mimeType: response.mimeType });
                if (response.status >= 400) failures.push(`Resource ${response.status}: ${response.url}`);
                if (/^https?:/.test(response.url) && new URL(response.url).origin !== origin) failures.push('Unexpected external app resource: ' + response.url);
            }
            if (message.method === 'Network.loadingFailed' && !(navigating && data.canceled)) failures.push('Resource loading failed: ' + JSON.stringify(data));
        }
    });
    const { targetId } = await command('Target.createTarget', { url: 'about:blank' }, undefined);
    ({ sessionId } = await command('Target.attachToTarget', { targetId, flatten: true }, undefined));
    await command('Page.enable'); await command('Page.bringToFront'); await command('Runtime.enable'); await command('Network.enable'); await command('Log.enable');
    await command('Emulation.setDeviceMetricsOverride', { width: 1440, height: 900, deviceScaleFactor: 1, mobile: false });
    await command('Page.addScriptToEvaluateOnNewDocument', { source: audioObserverScript });
    await command('Page.addScriptToEvaluateOnNewDocument', { source: inputObserverScript });
    navigating = true; await command('Page.navigate', { url: origin + '/' });
    let canvas = await ready(); navigating = false; await delay(300); checkFailures();
    const initial = await prefs(); const initialImage = await screenshot('01-startup.png', canvas, 30000);
    report.checks.push('Actual .NET/Avalonia startup, production resource loader/headers and nonblank rendered canvas');

    await focusApp(canvas); await key('KeyL', 'l', 76, true); await expectPref('library', !initial.library);
    await key('KeyI', 'i', 73, true); await expectPref('inspector', !initial.inspector);
    const toggled = await prefs(); await delay(250);
    const toggledImage = await screenshot('02-panes-toggled.png', canvas);
    assert.notEqual(toggledImage, initialImage, 'Keyboard pane changes must also change the rendered image');
    report.panePixelChange = renderedDifference(await readFile(join(evidence, '01-startup.png')), await readFile(join(evidence, '02-panes-toggled.png')));
    assert.ok(report.panePixelChange > 0.01, 'Pane toggles must visibly change at least one percent of canvas samples, not just a focus outline');
    report.preferences = { initial, toggled };

    navigating = true; await command('Page.reload', { ignoreCache: false }); canvas = await ready(); navigating = false;
    assert.deepEqual(await prefs(), toggled, 'Preferences persist across a real page reload');
    await focusApp(canvas); await key('KeyL', 'l', 76, true); await expectPref('library', initial.library);
    await key('KeyI', 'i', 73, true); await expectPref('inspector', initial.inspector);
    // The second inverse toggle proves the new managed MainView read persisted
    // pane values; reading localStorage alone would not establish that.
    report.preferences.restored = await prefs(); await delay(250);
    await screenshot('03-reloaded-and-restored.png', canvas);
    report.checks.push('Ctrl+L/Ctrl+I routed through shared app, persist through reload and restore through keyboard');

    await focusApp(canvas);
    // Fresh storage starts with an empty song. Open the actual built-in example
    // through the shared menu shortcut before expecting a non-silent signal.
    const projectStoreAvailable = await evaluate(`import('./recovery.js').then(module => typeof module.createProjectStore === 'function')`);
    if (projectStoreAvailable) {
        assert.equal(await evaluate(`import('./recovery.js').then(module => module.createProjectStore().list('recovery-'))`), '', 'Fresh blank startup must not create dirty recovery');
        await key('KeyD', 'D', 68, true, true);
        await until('Explicit demo selection and durable unsaved snapshot', `import('./recovery.js').then(module => module.createProjectStore().list('recovery-'))`, keys => typeof keys === 'string' && keys.length > 0, 15000);
        await delay(150);
        await screenshot('03b-explicit-demo.png', canvas);
        report.checks.push('Fresh blank startup and explicit demo command create an independent recovery snapshot');
    } else {
        assert.equal(requireReadySignal, false, 'Current build must expose project persistence');
        report.checks.push('Legacy artifact compatibility: demo is the existing startup song');
    }
    const before = await evaluate('__musicMachineSmokeAudio.snapshot()');
    assert.equal(before.supported, true, 'Native Web Audio must be available');
    await key('Space', ' ', 32);
    const playing = await until('Real demo audio rendering', '__musicMachineSmokeAudio.snapshot()', state => state.starts > before.starts && state.analyserPeak > 0.00001, 45000);
    assert.equal(playing.errors.length, 0, 'Native analyser observation must succeed');
    const source = playing.sources.at(-1);
    assert.ok(source.frames > 0 && source.channels === 2 && source.sampleRate === 48000 && source.finite && source.peak > 0.00001 && source.rms > 0.000001, 'Shared synth must fill a nonempty finite stereo PCM buffer');
    const time = playing.contexts[source.context].currentTime;
    assert.equal(playing.contexts[source.context].state, 'running');
    assert.equal(playing.contexts[source.context].realTime, true, 'Use a native realtime AudioContext, not offline or substitute audio');
    await delay(250);
    const advancing = await evaluate('__musicMachineSmokeAudio.snapshot()');
    assert.ok(advancing.contexts[source.context].currentTime > time, 'Real AudioContext clock must advance');
    await screenshot('04-playing.png', canvas);
    await key('Space', ' ', 32);
    const stopped = await until('Shared app Stop', '__musicMachineSmokeAudio.snapshot()', state => state.stops > playing.stops && state.active === 0, 10000);
    report.audio = { before, playing, stopped };
    report.checks.push('Space Play/Stop, genuine 48 kHz stereo AudioBuffer content, native analyser signal and advancing AudioContext clock');
    assert.equal(requests.filter(r => r.path.includes('/vendor/ffmpeg/')).length, 0, 'Ordinary app startup/playback must not download the optional converter');
    assert.ok(network.some(r => r.url.includes('/_framework/')), 'The actual published .NET framework must have loaded');
    assert.ok(requests.some(r => r.path.endsWith('.wasm') || r.path.endsWith('.wasm.gz')), 'Actual WASM resources must load');
    await delay(300); checkFailures(); report.passed = true;
    console.log('Published Avalonia application smoke passed: startup, rendering, keyboard persistence and real Web Audio Play/Stop.');
} catch (error) {
    report.failure = error.stack ?? String(error);
    if (browser && !exited && sessionId) {
        try { report.failureInput = await evaluate('__musicMachineSmokeInput?.snapshot()'); } catch {}
        try { const shot = await command('Page.captureScreenshot', { format: 'png', fromSurface: true }); await writeFile(join(evidence, 'failure.png'), Buffer.from(shot.data, 'base64')); } catch {}
    }
    console.error(report.failure); process.exitCode = 1;
} finally {
    report.failures = failures; report.warnings = warnings; report.requests = requests; report.network = network;
    await writeFile(join(evidence, 'report.json'), JSON.stringify(report, null, 2) + '\n');
    await writeFile(join(evidence, 'chrome-stderr.txt'), stderr);
    closing = true;
    if (browser && !exited) { browser.kill('SIGTERM'); await Promise.race([new Promise(resolve => browser.once('exit', resolve)), delay(3000)]); if (!exited) browser.kill('SIGKILL'); }
    for (const entry of pending.values()) { clearTimeout(entry.timer); entry.reject(new Error('Smoke finished')); } pending.clear();
    server?.closeAllConnections(); if (server) await new Promise(resolve => server.close(resolve));
    await rm(temporary, { recursive: true, force: true, maxRetries: 3, retryDelay: 100 });
}
