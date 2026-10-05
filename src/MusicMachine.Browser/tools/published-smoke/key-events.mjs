// CDP key sequences used only by the real published-app smoke.
export function keyEvents(code, key, number, ctrl = false, shift = false) {
    const event = (type, code, key, number, modifiers) => ({ type, code, key, windowsVirtualKeyCode: number, nativeVirtualKeyCode: number, modifiers });
    const events = [], modifiers = (ctrl ? 2 : 0) | (shift ? 8 : 0);
    if (ctrl) events.push(event('rawKeyDown', 'ControlLeft', 'Control', 17, 2));
    if (shift) events.push(event('rawKeyDown', 'ShiftLeft', 'Shift', 16, modifiers));
    events.push(event('rawKeyDown', code, key, number, modifiers), event('keyUp', code, key, number, modifiers));
    if (shift) events.push(event('keyUp', 'ShiftLeft', 'Shift', 16, ctrl ? 2 : 0));
    if (ctrl) events.push(event('keyUp', 'ControlLeft', 'Control', 17, 0));
    return events;
}
