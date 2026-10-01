const keys = new Map([
    ['ShiftLeft', 1], ['ShiftRight', 2], ['ControlLeft', 3], ['ControlRight', 4],
    ['AltLeft', 5], ['AltRight', 6], ['MetaLeft', 7], ['MetaRight', 8], ['ContextMenu', 9],
    ['ArrowUp', 45], ['ArrowDown', 46], ['ArrowLeft', 47], ['ArrowRight', 48],
    ['Enter', 49], ['Escape', 50], ['Space', 51], ['Tab', 52], ['Backspace', 53],
    ['Insert', 54], ['Delete', 55], ['PageUp', 56], ['PageDown', 57],
    ['Home', 58], ['End', 59], ['CapsLock', 60], ['ScrollLock', 61],
    ['PrintScreen', 62], ['Pause', 63], ['NumLock', 64],
    ['NumpadDivide', 77], ['NumpadMultiply', 78], ['NumpadSubtract', 79],
    ['NumpadAdd', 80], ['NumpadDecimal', 81], ['NumpadEnter', 82],
    ['Backquote', 119], ['Minus', 120], ['Equal', 121], ['BracketLeft', 122],
    ['BracketRight', 123], ['Semicolon', 124], ['Quote', 125], ['Comma', 126],
    ['Period', 127], ['Slash', 128], ['Backslash', 129], ['IntlBackslash', 130],
]);
for (let index = 1; index <= 35; index++) keys.set(`F${index}`, 9 + index);
for (let index = 0; index <= 9; index++) {
    keys.set(`Digit${index}`, 109 + index);
    keys.set(`Numpad${index}`, 67 + index);
}
for (let index = 0; index < 26; index++) keys.set(`Key${String.fromCharCode(65 + index)}`, 83 + index);

const gamepadButtons = [5, 7, 6, 4, 12, 13, -1, -1, 10, 11, 8, 9, 0, 1, 2, 3];
const finiteAxis = value => Number.isFinite(value) ? Math.max(-1, Math.min(1, value)) : 0;
const mouseButton = button => button === 0 ? 0 : button === 2 ? 1 : button === 1 ? 2 : -1;

/** DOM ownership ends at the snapshot boundary; gameplay consumes LocalInputInterface. */
export class BrowserEngineInput {
    constructor(canvas, engine, gamepadToggle) {
        this.canvas = canvas;
        this.engine = engine;
        this.gamepadToggle = gamepadToggle;
        this.gamepadStatus = gamepadToggle.parentElement?.querySelector('[role="status"]');
        this.events = new AbortController();
        this.pointer = -1;
        this.composing = false;
        this.cursorX = 0;
        this.cursorY = 0;
    }

    install() {
        const signal = this.events.signal, canvas = this.canvas;
        const owns = () => !document.hidden && document.hasFocus() && document.activeElement === canvas && !this.composing;
        this.gamepadToggle.addEventListener('change', () => {
            if (this.gamepadStatus) this.gamepadStatus.textContent = '';
        }, { signal });
        canvas.addEventListener('keydown', event => {
            if (!owns() || event.isComposing || event.altKey || event.metaKey) return;
            const key = keys.get(event.code);
            if (key === undefined) return;
            this.engine.InputKey(key, true);
            if (event.key.length === 1 && !event.ctrlKey) this.engine.InputText(event.key);
            if (event.code !== 'Tab') event.preventDefault();
        }, { signal });
        canvas.addEventListener('keyup', event => {
            const key = keys.get(event.code);
            if (key === undefined) return;
            this.engine.InputKey(key, false);
            if (owns()) event.preventDefault();
        }, { signal });
        canvas.addEventListener('compositionstart', () => {
            this.composing = true;
            this.engine.ResetInput();
        }, { signal });
        canvas.addEventListener('compositionend', event => {
            this.composing = false;
            if (typeof event.data === 'string') this.engine.InputText(event.data.slice(0, 64));
        }, { signal });
        canvas.addEventListener('pointerdown', event => {
            const button = mouseButton(event.button);
            if (button < 0 || document.hidden) return;
            canvas.focus({ preventScroll: true });
            if (button === 0) {
                if (this.pointer !== -1) return;
                this.pointer = event.pointerId;
                try { canvas.setPointerCapture(event.pointerId); }
                catch { this.pointer = -1; return; }
            }
            this.publishPointer(event);
            this.engine.InputMouseButton(button, true);
            if (button === 0 && this.engine.GetInputCaptureDesired()) {
                try {
                    const request = canvas.requestPointerLock?.();
                    if (request && typeof request.catch === 'function') void request.catch(() => {});
                } catch { /* Pointer lock remains optional. */ }
            }
            event.preventDefault();
        }, { signal });
        canvas.addEventListener('pointermove', event => {
            if ((event.pointerId === this.pointer || event.pointerType === 'mouse') && owns())
                this.publishPointer(event);
        }, { signal });
        const release = event => {
            const button = mouseButton(event.button);
            if (button >= 0) this.engine.InputMouseButton(button, false);
            if (event.pointerId === this.pointer) {
                this.pointer = -1;
                if (canvas.hasPointerCapture(event.pointerId)) canvas.releasePointerCapture(event.pointerId);
            }
        };
        canvas.addEventListener('pointerup', release, { signal });
        const cancel = event => {
            if (event.pointerId !== this.pointer) return;
            this.pointer = -1;
            this.engine.InputMouseButton(0, false);
        };
        canvas.addEventListener('pointercancel', cancel, { signal });
        canvas.addEventListener('lostpointercapture', cancel, { signal });
        canvas.addEventListener('contextmenu', event => { if (owns()) event.preventDefault(); }, { signal });
        canvas.addEventListener('wheel', event => {
            if (!owns()) return;
            const step = event.deltaMode === WheelEvent.DOM_DELTA_LINE ? 3
                : event.deltaMode === WheelEvent.DOM_DELTA_PAGE ? 1 : 100;
            // DOM deltaY is positive down; the engine's wheel Y is positive up.
            this.engine.InputScroll(event.deltaX / step, -event.deltaY / step);
            event.preventDefault();
        }, { signal, passive: false });
        canvas.addEventListener('blur', () => this.reset(), { signal });
        document.addEventListener('visibilitychange', () => { if (document.hidden) this.reset(); }, { signal });
    }

    publishPointer(event) {
        const bounds = this.canvas.getBoundingClientRect();
        if (bounds.width <= 0 || bounds.height <= 0) return;
        if (document.pointerLockElement === this.canvas) {
            this.cursorX = Math.max(0, Math.min(bounds.width, this.cursorX + event.movementX));
            this.cursorY = Math.max(0, Math.min(bounds.height, this.cursorY + event.movementY));
        } else {
            this.cursorX = event.clientX - bounds.left;
            this.cursorY = event.clientY - bounds.top;
        }
        this.engine.InputPointer(this.cursorX, this.cursorY);
    }

    publish() {
        const focused = !document.hidden && document.hasFocus() && document.activeElement === this.canvas;
        let connected = false, mask = 0, lt = 0, rt = 0, lx = 0, ly = 0, rx = 0, ry = 0;
        if (focused && this.gamepadToggle.checked) {
            let pads;
            try { pads = navigator.getGamepads?.(); }
            catch (error) {
                this.gamepadToggle.checked = false;
                if (this.gamepadStatus) this.gamepadStatus.textContent = `Gamepad unavailable: ${error.message ?? error}`;
            }
            if (!pads && this.gamepadToggle.checked) {
                this.gamepadToggle.checked = false;
                if (this.gamepadStatus) this.gamepadStatus.textContent = 'Gamepad API unavailable.';
            }
            let pad;
            if (pads) {
                for (let index = 0; index < pads.length; index++) {
                    if (pads[index]?.connected && pads[index].mapping === 'standard') {
                        pad = pads[index];
                        break;
                    }
                }
            }
            if (pad) {
                connected = true;
                for (let index = 0; index < gamepadButtons.length; index++)
                    if (gamepadButtons[index] >= 0 && pad.buttons[index]?.pressed)
                        mask |= 1 << gamepadButtons[index];
                lt = pad.buttons[6]?.value ?? 0;
                rt = pad.buttons[7]?.value ?? 0;
                lx = finiteAxis(pad.axes[0]); ly = -finiteAxis(pad.axes[1]);
                rx = finiteAxis(pad.axes[2]); ry = -finiteAxis(pad.axes[3]);
            }
        }
        this.engine.PublishInput(focused, document.pointerLockElement === this.canvas,
            connected, mask, lt, rt, lx, ly, rx, ry);
    }

    reset() {
        this.pointer = -1;
        this.engine.ResetInput();
    }

    dispose() {
        this.events.abort();
        this.reset();
    }
}
