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
        this.contacts = new Int32Array(10).fill(-1);
        this.composing = false;
        this.cursorX = 0;
        this.cursorY = 0;
        this.textElement = null;
        this.textEvents = null;
        this.textGeneration = 0;
        this.textVersion = 0;
        this.textLabelVersion = 0;
        this.textComposing = false;
        this.textCommitPending = false;
        this.textConflict = false;
        this.compositionBaseValue = '';
        this.textCommitSequence = 0;
        this.textEpoch = 0;
        this.textPosition = { hidden: false, left: NaN, top: NaN, width: NaN, height: NaN,
            clipTop: NaN, clipRight: NaN, clipBottom: NaN, clipLeft: NaN };
        this.textCursor = -1;
        this.controlElement = null;
        this.controlEvents = null;
        this.controlGeneration = 0;
        this.controlLabelVersion = 0;
        this.controlPosition = { hidden: false, left: NaN, top: NaN, width: NaN, height: NaN,
            clipTop: NaN, clipRight: NaN, clipBottom: NaN, clipLeft: NaN };
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
            this.cancelPointers();
            this.engine.ResetInput();
        }, { signal });
        canvas.addEventListener('compositionend', event => {
            this.composing = false;
            if (typeof event.data === 'string' && event.data.length <= 64)
                this.engine.InputText(event.data);
        }, { signal });
        canvas.addEventListener('pointerdown', event => {
            if (event.pointerType === 'touch') {
                if (document.hidden || this.contacts.includes(event.pointerId)) return;
                const slot = this.contacts.indexOf(-1);
                if (slot < 0 || !Number.isInteger(event.pointerId) || event.pointerId < 0 || event.pointerId > 2147483647) return;
                canvas.focus({ preventScroll: true });
                if (!owns()) return;
                try { canvas.setPointerCapture(event.pointerId); }
                catch { return; }
                this.contacts[slot] = event.pointerId;
                if (!this.publishContact(event, 0)) this.releaseContact(event.pointerId);
                event.preventDefault();
                return;
            }
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
            if (event.pointerType === 'touch') {
                if (this.contacts.includes(event.pointerId) && owns()) this.publishContact(event, 1);
                return;
            }
            if ((event.pointerId === this.pointer || event.pointerType === 'mouse') && owns())
                this.publishPointer(event);
        }, { signal });
        const release = event => {
            if (event.pointerType === 'touch') {
                if (this.contacts.includes(event.pointerId)) {
                    this.publishContact(event, 2);
                    this.releaseContact(event.pointerId);
                }
                return;
            }
            const button = mouseButton(event.button);
            if (button >= 0) this.engine.InputMouseButton(button, false);
            if (event.pointerId === this.pointer) {
                this.pointer = -1;
                if (canvas.hasPointerCapture(event.pointerId)) canvas.releasePointerCapture(event.pointerId);
            }
        };
        canvas.addEventListener('pointerup', release, { signal });
        const cancel = event => {
            if (this.contacts.includes(event.pointerId)) {
                this.publishContact(event, 3);
                this.releaseContact(event.pointerId);
                return;
            }
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
        canvas.addEventListener('blur', () => {
            this.cancelPointers();
            this.engine.ResetInput();
        }, { signal });
        window.addEventListener('blur', () => {
            this.cancelPointers();
            this.engine.ResetInput();
        }, { signal });
        document.addEventListener('visibilitychange', () => { if (document.hidden) this.reset(); }, { signal });
    }

    publishContact(event, phase) {
        const bounds = this.canvas.getBoundingClientRect();
        if (bounds.width <= 0 || bounds.height <= 0) return false;
        // The bridge transports contacts only. Engine UI decides capture and action mappings.
        return this.engine.InputContact(event.pointerId, phase,
            (event.clientX - bounds.left) * this.canvas.width / bounds.width,
            (event.clientY - bounds.top) * this.canvas.height / bounds.height);
    }

    releaseContact(id) {
        const slot = this.contacts.indexOf(id);
        if (slot < 0) return;
        this.contacts[slot] = -1;
        if (this.canvas.hasPointerCapture(id)) this.canvas.releasePointerCapture(id);
    }

    cancelPointers() {
        for (let index = 0; index < this.contacts.length; index++) {
            const id = this.contacts[index];
            if (id >= 0) this.releaseContact(id);
        }
        const pointer = this.pointer;
        this.pointer = -1;
        if (pointer >= 0 && this.canvas.hasPointerCapture(pointer)) this.canvas.releasePointerCapture(pointer);
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
        // The render viewport uses canvas backing pixels, not CSS layout pixels.
        this.engine.InputPointer(
            Math.max(0, Math.min(this.canvas.width, this.cursorX * this.canvas.width / bounds.width)),
            Math.max(0, Math.min(this.canvas.height, this.cursorY * this.canvas.height / bounds.height)));
    }

    publish() {
        const focused = this.ownsFocus();
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
        this.cancelPointers();
        this.removeTextElement();
        this.removeAccessibleControl();
        this.engine.ResetInput();
    }

    dispose() {
        this.events.abort();
        this.reset();
    }

    ownsFocus() {
        return !document.hidden && document.hasFocus() &&
            (document.activeElement === this.canvas || document.activeElement === this.textElement ||
                document.activeElement === this.controlElement);
    }

    /** Called after the engine frame so focused UI and projected bounds are current. */
    syncTextFocus() {
        this.syncAccessibleControl();
        const generation = this.engine.RefreshTextInput();
        if (!generation) {
            this.removeTextElement();
            return;
        }
        if (generation !== this.textGeneration)
            this.createTextElement(generation);

        const element = this.textElement;
        if (!element) return;
        const version = this.engine.GetTextInputContentVersion();
        if ((this.textComposing || this.textCommitPending) && version !== this.textVersion) {
            if (this.engine.GetTextInputValue() !== this.compositionBaseValue)
                this.textConflict = true;
        } else if (!this.textComposing && !this.textCommitPending && version !== this.textVersion) {
            this.textVersion = version;
            const value = this.engine.GetTextInputValue();
            if (element.value !== value) {
                element.value = value;
                this.textCursor = -1;
            }
        }
        const cursor = this.engine.GetTextInputCursor();
        if (!this.textComposing && !this.textCommitPending &&
            (document.activeElement !== element || cursor !== this.textCursor) &&
            (element.selectionStart !== cursor || element.selectionEnd !== cursor))
            element.setSelectionRange(cursor, cursor);
        if (!this.textComposing && !this.textCommitPending) this.textCursor = cursor;
        element.readOnly = this.engine.GetTextInputReadOnly();
        const labelVersion = this.engine.GetTextInputLabelVersion();
        if (labelVersion !== this.textLabelVersion) {
            this.textLabelVersion = labelVersion;
            element.setAttribute('aria-label', this.engine.GetTextInputLabel() || 'Engine text input');
        }
        this.positionTextElement();
    }

    createTextElement(generation) {
        const focusWasOnCanvas = document.activeElement === this.canvas ||
            document.activeElement === this.textElement;
        this.removeTextElement();
        const singleLine = this.engine.GetTextInputSingleLine();
        const element = document.createElement(singleLine ? 'input' : 'textarea');
        if (singleLine) element.type = 'text';
        element.setAttribute('aria-label', this.engine.GetTextInputLabel() || 'Engine text input');
        this.textLabelVersion = this.engine.GetTextInputLabelVersion();
        element.setAttribute('autocomplete', 'off');
        element.maxLength = 16384;
        element.readOnly = this.engine.GetTextInputReadOnly();
        element.style.position = 'fixed';
        element.style.zIndex = '2147483647';
        element.style.boxSizing = 'border-box';
        element.style.background = 'transparent';
        element.style.color = 'transparent';
        element.style.caretColor = '#90c9ef';
        element.style.border = '0';
        element.style.outline = '2px solid #90c9ef';
        // Native font metrics can differ from engine glyph metrics; the DOM caret
        // and focus ring aid editing/accessibility but do not claim glyph-pixel alignment.
        element.style.fontSize = '16px';
        element.style.resize = 'none';
        element.style.pointerEvents = 'none';
        element.value = this.engine.GetTextInputValue();
        this.textVersion = this.engine.GetTextInputContentVersion();
        const cursor = this.engine.GetTextInputCursor();
        element.setSelectionRange(cursor, cursor);
        this.textCursor = cursor;
        const controller = new AbortController();
        const signal = controller.signal;
        this.textEvents = controller;
        this.textElement = element;
        this.textGeneration = generation;
        this.textComposing = false;
        this.textCommitPending = false;
        this.textConflict = false;
        const epoch = ++this.textEpoch;
        element.addEventListener('compositionstart', () => {
            this.textCommitSequence++;
            this.textComposing = true;
            this.textCommitPending = false;
            this.compositionBaseValue = this.engine.GetTextInputValue();
            this.textConflict = element.value !== this.compositionBaseValue;
            this.engine.ResetInput();
        }, { signal });
        element.addEventListener('compositionend', () => {
            this.textComposing = false;
            this.textCommitPending = true;
            const commit = ++this.textCommitSequence;
            setTimeout(() => {
                if (epoch !== this.textEpoch || commit !== this.textCommitSequence) return;
                this.textCommitPending = false;
                if (this.textConflict || this.engine.GetTextInputValue() !== this.compositionBaseValue ||
                    this.engine.GetTextInputContentVersion() !== this.textVersion)
                    this.restoreTextElement();
                else
                    this.applyTextElement();
                this.textConflict = false;
            }, 0);
        }, { signal });
        element.addEventListener('input', event => {
            if (!event.isComposing && !this.textComposing && !this.textCommitPending)
                this.applyTextElement();
        }, { signal });
        element.addEventListener('select', () => {
            if (!this.textComposing && !this.textCommitPending && this.textGeneration === generation) {
                const cursor = element.selectionDirection === 'backward'
                    ? element.selectionStart : element.selectionEnd;
                if (this.engine.SelectTextInput(generation, this.textVersion, cursor))
                    this.textCursor = cursor;
                else if (this.engine.GetTextInputContentVersion() !== this.textVersion)
                    this.restoreTextElement();
            }
        }, { signal });
        element.addEventListener('keydown', event => {
            if (event.isComposing || this.textComposing) return;
            if (this.textCommitPending) {
                if (event.key === 'Enter' || event.key === 'Escape') event.preventDefault();
                return;
            }
            if (event.key === 'Enter' && singleLine) {
                if (!this.engine.ActOnTextInput(generation, this.textVersion, true) &&
                    this.engine.GetTextInputContentVersion() !== this.textVersion)
                    this.restoreTextElement();
                event.preventDefault();
            } else if (event.key === 'Escape') {
                if (this.engine.ActOnTextInput(generation, this.textVersion, false))
                    event.preventDefault();
                else if (this.engine.GetTextInputContentVersion() !== this.textVersion)
                    this.restoreTextElement();
            }
        }, { signal });
        document.body.appendChild(element);
        this.positionTextElement();
        if (focusWasOnCanvas && !this.textPosition.hidden) element.focus({ preventScroll: true });
    }

    applyTextElement() {
        const element = this.textElement;
        if (!element || !this.textGeneration || this.textComposing) return;
        if (this.textPosition.hidden) {
            this.restoreTextElement();
            return;
        }
        if (this.engine.GetTextInputContentVersion() !== this.textVersion) {
            this.restoreTextElement();
            return;
        }
        const accepted = this.engine.EditTextInput(this.textGeneration, this.textVersion, element.value,
            element.selectionStart, element.selectionEnd);
        const value = this.engine.GetTextInputValue();
        if (!accepted || value !== element.value) {
            element.value = value;
            const cursor = this.engine.GetTextInputCursor();
            element.setSelectionRange(cursor, cursor);
        }
        this.textVersion = this.engine.GetTextInputContentVersion();
        this.textCursor = this.engine.GetTextInputCursor();
    }

    restoreTextElement() {
        const element = this.textElement;
        if (!element) return;
        element.value = this.engine.GetTextInputValue();
        const cursor = this.engine.GetTextInputCursor();
        element.setSelectionRange(cursor, cursor);
        this.textVersion = this.engine.GetTextInputContentVersion();
        this.textCursor = cursor;
    }

    positionTextElement() {
        const element = this.textElement;
        if (!element) return;
        this.positionProjectedElement(element, this.textPosition, this.engine.GetTextInputX(),
            this.engine.GetTextInputY(), this.engine.GetTextInputWidth(), this.engine.GetTextInputHeight());
        if (this.textPosition.hidden && document.activeElement === element) {
            this.textConflict = true;
            this.canvas.focus({ preventScroll: true });
        }
        element.tabIndex = this.textPosition.hidden ? -1 : 0;
        element.setAttribute('aria-hidden', this.textPosition.hidden ? 'true' : 'false');
    }

    positionProjectedElement(element, position, x, y, width, height) {
        const rect = this.canvas.getBoundingClientRect();
        const left = rect.left + x * rect.width;
        const top = rect.top + y * rect.height;
        const boxWidth = width * rect.width;
        const boxHeight = height * rect.height;
        const right = left + boxWidth;
        const bottom = top + boxHeight;
        if (width <= 0 || height <= 0 || !Number.isFinite(left) || !Number.isFinite(top) ||
            right <= rect.left || left >= rect.right || bottom <= rect.top || top >= rect.bottom) {
            if (!position.hidden) {
                element.style.left = '-10000px';
                element.style.top = '0';
                element.style.width = '1px';
                element.style.height = '1px';
                element.style.clipPath = 'inset(50%)';
                position.hidden = true;
            }
            return;
        }

        // Clip the projected box without moving its origin; moving it would shift
        // the native caret relative to the engine-rendered text.
        const clipTop = Math.max(0, rect.top - top);
        const clipRight = Math.max(0, right - rect.right);
        const clipBottom = Math.max(0, bottom - rect.bottom);
        const clipLeft = Math.max(0, rect.left - left);
        if (position.hidden || position.left !== left) element.style.left = `${left}px`;
        if (position.hidden || position.top !== top) element.style.top = `${top}px`;
        if (position.hidden || position.width !== boxWidth) element.style.width = `${boxWidth}px`;
        if (position.hidden || position.height !== boxHeight) element.style.height = `${boxHeight}px`;
        if (position.hidden || position.clipTop !== clipTop || position.clipRight !== clipRight ||
            position.clipBottom !== clipBottom || position.clipLeft !== clipLeft)
            element.style.clipPath = `inset(${clipTop}px ${clipRight}px ${clipBottom}px ${clipLeft}px)`;
        position.hidden = false;
        position.left = left;
        position.top = top;
        position.width = boxWidth;
        position.height = boxHeight;
        position.clipTop = clipTop;
        position.clipRight = clipRight;
        position.clipBottom = clipBottom;
        position.clipLeft = clipLeft;
    }

    syncAccessibleControl() {
        const generation = this.engine.RefreshAccessibleControl();
        if (!generation || !this.canvas.isConnected) {
            this.removeAccessibleControl();
            return;
        }
        if (generation !== this.controlGeneration)
            this.createAccessibleControl(generation);
        const element = this.controlElement;
        if (!element) return;
        const labelVersion = this.engine.GetAccessibleControlLabelVersion();
        if (labelVersion !== this.controlLabelVersion) {
            this.controlLabelVersion = labelVersion;
            element.setAttribute('aria-label', this.engine.GetAccessibleControlLabel() || 'Engine button');
        }
        this.positionProjectedElement(element, this.controlPosition,
            this.engine.GetAccessibleControlX(), this.engine.GetAccessibleControlY(),
            this.engine.GetAccessibleControlWidth(), this.engine.GetAccessibleControlHeight());
        if (this.controlPosition.hidden && document.activeElement === element)
            this.canvas.focus({ preventScroll: true });
        element.tabIndex = this.controlPosition.hidden ? -1 : 0;
        element.setAttribute('aria-hidden', this.controlPosition.hidden ? 'true' : 'false');
    }

    createAccessibleControl(generation) {
        const hadFocus = document.activeElement === this.controlElement;
        this.removeAccessibleControl();
        const element = document.createElement('button');
        element.type = 'button';
        element.setAttribute('aria-label', this.engine.GetAccessibleControlLabel() || 'Engine button');
        this.controlLabelVersion = this.engine.GetAccessibleControlLabelVersion();
        element.style.position = 'fixed';
        element.style.zIndex = '2147483647';
        element.style.boxSizing = 'border-box';
        element.style.background = 'transparent';
        element.style.color = 'transparent';
        element.style.border = '0';
        element.style.outline = '0';
        element.style.pointerEvents = 'none';
        const controller = new AbortController();
        const signal = controller.signal;
        this.controlEvents = controller;
        this.controlElement = element;
        this.controlGeneration = generation;
        element.addEventListener('focus', () => { element.style.outline = '2px solid #90c9ef'; }, { signal });
        element.addEventListener('blur', () => {
            element.style.outline = '0';
            this.engine.ResetInput();
        }, { signal });
        element.addEventListener('keydown', event => {
            if (!this.ownsFocus() || event.isComposing || event.altKey || event.metaKey ||
                event.code === 'Enter' || event.code === 'Space' || event.code === 'Tab') return;
            const key = keys.get(event.code);
            if (key === undefined) return;
            this.engine.InputKey(key, true);
            event.preventDefault();
        }, { signal });
        element.addEventListener('keyup', event => {
            if (event.code === 'Enter' || event.code === 'Space' || event.code === 'Tab') return;
            const key = keys.get(event.code);
            if (key === undefined) return;
            this.engine.InputKey(key, false);
            if (this.ownsFocus()) event.preventDefault();
        }, { signal });
        element.addEventListener('click', () => {
            this.engine.ActivateAccessibleControl(generation);
        }, { signal });
        this.canvas.insertAdjacentElement('afterend', element);
        this.positionProjectedElement(element, this.controlPosition,
            this.engine.GetAccessibleControlX(), this.engine.GetAccessibleControlY(),
            this.engine.GetAccessibleControlWidth(), this.engine.GetAccessibleControlHeight());
        element.tabIndex = this.controlPosition.hidden ? -1 : 0;
        element.setAttribute('aria-hidden', this.controlPosition.hidden ? 'true' : 'false');
        if (hadFocus && !this.controlPosition.hidden) element.focus({ preventScroll: true });
    }

    removeAccessibleControl() {
        const hadFocus = this.controlElement && document.activeElement === this.controlElement;
        this.controlEvents?.abort();
        this.controlEvents = null;
        this.controlElement?.remove();
        this.controlElement = null;
        this.controlGeneration = 0;
        this.controlLabelVersion = 0;
        this.controlPosition.hidden = false;
        this.controlPosition.left = this.controlPosition.top = NaN;
        this.controlPosition.width = this.controlPosition.height = NaN;
        this.controlPosition.clipTop = this.controlPosition.clipRight = NaN;
        this.controlPosition.clipBottom = this.controlPosition.clipLeft = NaN;
        if (hadFocus) {
            this.engine.ResetInput();
            if (this.canvas.isConnected && !document.hidden && document.hasFocus())
                this.canvas.focus({ preventScroll: true });
        }
    }

    removeTextElement() {
        const hadFocus = this.textElement && document.activeElement === this.textElement;
        ++this.textEpoch;
        this.textEvents?.abort();
        this.textEvents = null;
        this.textElement?.remove();
        this.textElement = null;
        this.textGeneration = 0;
        this.textVersion = 0;
        this.textLabelVersion = 0;
        this.textCursor = -1;
        this.textComposing = false;
        this.textCommitPending = false;
        this.textConflict = false;
        this.compositionBaseValue = '';
        this.textCommitSequence++;
        this.textPosition.hidden = false;
        this.textPosition.left = this.textPosition.top = NaN;
        this.textPosition.width = this.textPosition.height = NaN;
        this.textPosition.clipTop = this.textPosition.clipRight = NaN;
        this.textPosition.clipBottom = this.textPosition.clipLeft = NaN;
        if (hadFocus && this.canvas.isConnected && !document.hidden && document.hasFocus())
            this.canvas.focus({ preventScroll: true });
    }
}
