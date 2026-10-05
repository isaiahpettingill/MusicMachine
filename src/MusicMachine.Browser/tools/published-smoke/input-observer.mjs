// CI-only passive diagnostics. Never focus an element, cancel an event, invoke a
// managed command, or modify preferences: the smoke must exercise native input.
function installInputObserver() {
    const events = [], limit = 160;
    let sequence = 0;
    const describe = element => element ? { tag: element.tagName ?? (element === window ? 'WINDOW' : null), id: element.id ?? null,
        inApp: !!element.nodeType && !!document.querySelector('#out')?.contains(element) } : null;
    const readFocus = () => ({ hasFocus: document.hasFocus(), activeElement: describe(document.activeElement) });
    const record = event => {
        const item = { sequence: ++sequence, type: event.type, code: event.code ?? null, key: event.key ?? null,
            ctrl: !!event.ctrlKey, shift: !!event.shiftKey, alt: !!event.altKey, meta: !!event.metaKey,
            repeat: !!event.repeat, trusted: event.isTrusted, target: describe(event.target),
            defaultPreventedAtCapture: event.defaultPrevented, ...readFocus() };
        events.push(item); if (events.length > limit) events.shift();
        // Observe post-dispatch state as well: asynchronous managed dispatch can
        // call preventDefault too late for a browser-owned shortcut.
        setTimeout(() => { item.defaultPreventedAfterDispatch = event.defaultPrevented; item.focusAfterDispatch = readFocus(); }, 0);
    };
    for (const type of ['keydown', 'keyup', 'focusin', 'focusout']) document.addEventListener(type, record, true);
    for (const type of ['focus', 'blur']) window.addEventListener(type, record);
    globalThis.__musicMachineSmokeInput = Object.freeze({ snapshot: () => ({ sequence, ...readFocus(), events: events.map(event => ({ ...event })) }) });
}
export const inputObserverScript = `(${installInputObserver.toString()})();`;
