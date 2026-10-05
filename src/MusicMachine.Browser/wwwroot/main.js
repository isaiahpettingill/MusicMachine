import { dotnet } from './_framework/dotnet.js';
import { createAudioBridge } from './audio.js';
import { createProjectStore } from './recovery.js';
import { createAudioConverter } from './conversion.js';


function applyViewTheme(text) {
    const match = /^theme=(.+)$/m.exec(text ?? '');
    document.body.dataset.theme = match?.[1]?.trim() ?? 'catppuccin-mocha';
}
const preferences = {
    load() { try { const text = localStorage.getItem('musicmachine.views'); applyViewTheme(text); return text; } catch { return null; } },
    save(text) { localStorage.setItem('musicmachine.views', text); applyViewTheme(text); }
};
preferences.load();
const notice = document.getElementById('audio-notice');
const noticeText = document.getElementById('audio-notice-text');
const audio = createAudioBridge({
    showNotice(text) { noticeText.textContent = text; notice.hidden = false; },
    hideNotice() { notice.hidden = true; }
});
// These run before Avalonia's input handler, preserving browser user activation for Web Audio.
document.addEventListener('pointerdown', () => void audio.unlock(), { capture: true, passive: true });
document.addEventListener('keydown', () => void audio.unlock(), { capture: true });
document.getElementById('enable-audio').addEventListener('click', () => void audio.unlock());
document.getElementById('dismiss-audio').addEventListener('click', () => { notice.hidden = true; });
window.addEventListener('pagehide', () => audio.stopAll());
const conversion = createAudioConverter();
window.addEventListener('pagehide', () => conversion.dispose());

try {
    const runtime = await dotnet.withDiagnosticTracing(false).create();
    runtime.setModuleImports('musicmachine', { audio, projectStorage: createProjectStore(), preferences, conversion });
    await runtime.runMain(runtime.getConfig().mainAssemblyName, [globalThis.location.href]);
    // An observable lifecycle signal, not a test-only substitute for UI readiness.
    document.documentElement.dataset.musicmachineReady = 'true';
    window.dispatchEvent(new Event('musicmachine-ready'));
} catch (error) {
    document.documentElement.dataset.musicmachineReady = 'failed';
    console.error('MusicMachine startup failed', error);
    const status = document.getElementById('loading-status');
    if (status) status.textContent = 'MusicMachine could not start. Reload this page or try a current Chrome, Edge, Firefox or Safari browser.';
}
