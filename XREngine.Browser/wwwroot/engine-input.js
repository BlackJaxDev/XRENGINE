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
        this.textProxy = null;
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
        this.controlRoot = null;
        this.controlNodes = new Map();
        this.controlOrder = [];
        this.controlRevision = 0;
        this.controlScan = 0;
        this.focusedTextControl = null;
        this.controlCanvasRect = { left: NaN, top: NaN, width: NaN, height: NaN };
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
        this.removeAccessibleControls();
        this.engine.ResetInput();
    }

    dispose() {
        this.events.abort();
        this.reset();
    }

    ownsFocus() {
        return !document.hidden && document.hasFocus() &&
            (document.activeElement === this.canvas || document.activeElement === this.textElement ||
                this.controlRoot?.contains(document.activeElement));
    }

    /** Called after the engine frame so focused UI and projected bounds are current. */
    syncTextFocus() {
        this.syncAccessibleControls();
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
            document.activeElement === this.textElement ||
            document.activeElement === this.focusedTextControl?.element;
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
        const proxy = this.focusedTextControl?.element;
        this.textProxy = this.focusedTextControl;
        if (proxy) {
            proxy.appendChild(element);
            proxy.setAttribute('role', 'presentation');
            proxy.removeAttribute('aria-label');
            proxy.removeAttribute('aria-readonly');
            proxy.removeAttribute('aria-multiline');
            proxy.tabIndex = -1;
            this.textProxy.nativeText = true;
        } else {
            document.body.appendChild(element);
        }
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

    positionProjectedElement(element, position, x, y, width, height, canvasRect = null) {
        const rect = canvasRect ?? this.canvas.getBoundingClientRect();
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

    syncAccessibleControls() {
        const revision = this.engine.RefreshAccessibleControls();
        if (!this.canvas.isConnected) {
            this.removeAccessibleControls();
            return;
        }
        const rect = this.canvas.getBoundingClientRect();
        const previous = this.controlCanvasRect;
        const moved = rect.left !== previous.left || rect.top !== previous.top ||
            rect.width !== previous.width || rect.height !== previous.height;
        if (revision === this.controlRevision && !moved) return;
        previous.left = rect.left;
        previous.top = rect.top;
        previous.width = rect.width;
        previous.height = rect.height;
        this.controlRevision = revision;
        this.controlScan++;
        this.focusedTextControl = null;
        const count = Math.min(256, this.engine.GetAccessibleControlCount());
        this.controlOrder.length = count;
        if (count && !this.controlRoot) {
            this.controlRoot = document.createElement('div');
            this.canvas.insertAdjacentElement('afterend', this.controlRoot);
        }
        for (let index = 0; index < count; index++) {
            const generation = this.engine.GetAccessibleControlGeneration(index);
            const role = this.engine.GetAccessibleControlRole(index);
            let node = this.controlNodes.get(generation);
            if (!node || node.role !== role) {
                if (node) this.removeAccessibleNode(node);
                node = this.createAccessibleNode(generation, role);
                this.controlNodes.set(generation, node);
            }
            node.seen = this.controlScan;
            this.controlOrder[index] = node;
            const version = this.engine.GetAccessibleControlVersion(index);
            if (node.version !== version) {
                node.version = version;
                node.name = this.engine.GetAccessibleControlName(index) || 'Engine control';
                if (!node.nativeText) node.element.setAttribute('aria-label', node.name);
                if (role === 3) {
                    const checked = this.engine.GetAccessibleControlChecked(index);
                    node.element.setAttribute('aria-checked', checked === 2 ? 'mixed' : checked === 1 ? 'true' : 'false');
                }
                if (role === 2) {
                    node.readOnly = this.engine.GetAccessibleControlReadOnly(index);
                    node.multiline = this.engine.GetAccessibleControlMultiline(index);
                    if (!node.nativeText) {
                        node.element.setAttribute('aria-readonly', String(node.readOnly));
                        node.element.setAttribute('aria-multiline', String(node.multiline));
                    }
                }
                node.x = this.engine.GetAccessibleControlX(index);
                node.y = this.engine.GetAccessibleControlY(index);
                node.width = this.engine.GetAccessibleControlWidth(index);
                node.height = this.engine.GetAccessibleControlHeight(index);
            }
            if (role === 2 && this.engine.GetAccessibleControlFocused(index))
                this.focusedTextControl = node;
            if (moved || node.position.left !== rect.left + node.x * rect.width ||
                node.position.top !== rect.top + node.y * rect.height ||
                node.position.width !== node.width * rect.width ||
                node.position.height !== node.height * rect.height)
                this.positionProjectedElement(node.element, node.position, node.x, node.y, node.width, node.height, rect);
            if (node.position.hidden && document.activeElement === node.element)
                this.canvas.focus({ preventScroll: true });
            const ownsNativeText = role === 2 && this.textElement?.parentElement === node.element;
            if (node.hidden !== node.position.hidden || node.nativeText !== ownsNativeText) {
                node.hidden = node.position.hidden;
                node.nativeText = ownsNativeText;
                node.element.tabIndex = node.hidden || ownsNativeText ? -1 : 0;
                if (node.hidden) node.element.setAttribute('aria-hidden', 'true');
                else node.element.removeAttribute('aria-hidden');
                if (role === 2) {
                    node.element.setAttribute('role', ownsNativeText ? 'presentation' : 'textbox');
                    if (ownsNativeText) {
                        node.element.removeAttribute('aria-label');
                        node.element.removeAttribute('aria-readonly');
                        node.element.removeAttribute('aria-multiline');
                    } else {
                        node.element.setAttribute('aria-label', node.name);
                        node.element.setAttribute('aria-readonly', String(node.readOnly));
                        node.element.setAttribute('aria-multiline', String(node.multiline));
                    }
                }
            }
        }
        for (const node of this.controlNodes.values()) {
            if (node.seen !== this.controlScan) {
                this.removeAccessibleNode(node);
                this.controlNodes.delete(node.generation);
            }
        }
        this.orderAccessibleNodes();
        if (!count) {
            this.controlRoot?.remove();
            this.controlRoot = null;
        }
    }

    orderAccessibleNodes() {
        const root = this.controlRoot;
        if (!root) return;
        const order = this.controlOrder;
        let needsOrder = false;
        let activeIndex = -1;
        for (let index = 0; index < order.length; index++) {
            if (root.children[index] !== order[index].element) needsOrder = true;
            if (order[index].element.contains(document.activeElement)) activeIndex = index;
        }
        if (!needsOrder) return;
        if (activeIndex < 0) {
            for (let index = 0; index < order.length; index++)
                if (root.children[index] !== order[index].element)
                    root.insertBefore(order[index].element, root.children[index] ?? null);
            return;
        }
        const active = order[activeIndex].element;
        for (let index = 0; index < activeIndex; index++)
            root.insertBefore(order[index].element, active);
        let anchor = null;
        for (let index = order.length - 1; index > activeIndex; index--) {
            root.insertBefore(order[index].element, anchor);
            anchor = order[index].element;
        }
    }

    createAccessibleNode(generation, role) {
        const element = document.createElement(role === 2 ? 'div' : 'button');
        if (role !== 2) element.type = 'button';
        if (role === 3) element.setAttribute('role', 'checkbox');
        if (role === 2) element.setAttribute('role', 'textbox');
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
        const node = { element, controller, generation, role, name: '', version: 0, seen: 0,
            readOnly: false, multiline: false,
            hidden: null, nativeText: null,
            x: -1, y: -1, width: 0, height: 0,
            position: { hidden: false, left: NaN, top: NaN, width: NaN, height: NaN,
                clipTop: NaN, clipRight: NaN, clipBottom: NaN, clipLeft: NaN } };
        element.addEventListener('focus', () => {
            if (!this.engine.FocusAccessibleControl(generation)) {
                this.canvas.focus({ preventScroll: true });
                return;
            }
            element.style.outline = '2px solid #90c9ef';
            if (role === 2) queueMicrotask(() => {
                if (this.controlNodes.get(generation) === node) this.syncTextFocus();
            });
        }, { signal });
        element.addEventListener('blur', () => {
            element.style.outline = '0';
            this.engine.ResetInput();
        }, { signal });
        element.addEventListener('keydown', event => {
            if (event.target !== element || !this.ownsFocus() || event.isComposing || event.altKey || event.metaKey ||
                event.code === 'Tab' || (role !== 2 && (event.code === 'Enter' || event.code === 'Space')))
                return;
            const key = keys.get(event.code);
            if (key === undefined) return;
            this.engine.InputKey(key, true);
            if (role === 2 && event.key.length === 1 && !event.ctrlKey)
                this.engine.InputText(event.key);
            event.preventDefault();
        }, { signal });
        element.addEventListener('keyup', event => {
            if (event.target !== element || event.code === 'Tab' ||
                (role !== 2 && (event.code === 'Enter' || event.code === 'Space')))
                return;
            const key = keys.get(event.code);
            if (key === undefined) return;
            this.engine.InputKey(key, false);
            if (this.ownsFocus()) event.preventDefault();
        }, { signal });
        if (role !== 2) element.addEventListener('click', () => {
            this.engine.ActivateAccessibleControl(generation);
        }, { signal });
        return node;
    }

    removeAccessibleNode(node) {
        if (this.textProxy === node) this.removeTextElement();
        const hadFocus = node.element.contains(document.activeElement);
        node.controller.abort();
        node.element.remove();
        if (hadFocus) {
            this.engine.ResetInput();
            if (this.canvas.isConnected && !document.hidden && document.hasFocus())
                this.canvas.focus({ preventScroll: true });
        }
    }

    removeAccessibleControls() {
        for (const node of this.controlNodes.values()) this.removeAccessibleNode(node);
        this.controlNodes.clear();
        this.controlOrder.length = 0;
        this.controlRoot?.remove();
        this.controlRoot = null;
        this.controlRevision = 0;
        this.focusedTextControl = null;
        this.controlCanvasRect.left = this.controlCanvasRect.top = NaN;
        this.controlCanvasRect.width = this.controlCanvasRect.height = NaN;
    }

    removeTextElement() {
        const hadFocus = this.textElement && document.activeElement === this.textElement;
        const proxy = this.textElement?.parentElement;
        ++this.textEpoch;
        this.textEvents?.abort();
        this.textEvents = null;
        this.textElement?.remove();
        if (proxy && proxy === this.textProxy?.element && proxy.isConnected) {
            proxy.setAttribute('role', 'textbox');
            proxy.setAttribute('aria-label', this.textProxy.name);
            proxy.setAttribute('aria-readonly', String(this.textProxy.readOnly));
            proxy.setAttribute('aria-multiline', String(this.textProxy.multiline));
            proxy.tabIndex = this.textProxy.position.hidden ? -1 : 0;
            this.textProxy.nativeText = false;
        }
        this.textProxy = null;
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
