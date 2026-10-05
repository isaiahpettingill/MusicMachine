import { dotnet } from './_framework/dotnet.js';
import { createAudioBridge } from './audio.js';
import { createRecoveryStore } from './recovery.js';


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

try {
    const runtime = await dotnet.withDiagnosticTracing(false).create();
    runtime.setModuleImports('musicmachine', { audio, recovery: createRecoveryStore(), preferences });
    await runtime.runMain(runtime.getConfig().mainAssemblyName, [globalThis.location.href]);
} catch (error) {
    console.error('MusicMachine startup failed', error);
    const status = document.getElementById('loading-status');
    if (status) status.textContent = 'MusicMachine could not start. Reload this page or try a current Chrome, Edge, Firefox or Safari browser.';
}
