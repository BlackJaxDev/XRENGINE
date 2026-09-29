const maximumPointers = 8;
const left = 1, right = 2, forward = 4, backward = 8, jump = 16;
const clamp = (value, minimum, maximum) => Math.max(minimum, Math.min(maximum, value));

/** Retained per-canvas actions and captured pointer identities. UI events never bubble into movement. */
export class BrowserInput {
    constructor(host) {
        this.host = host;
        this.canvas = host.canvas;
        this.pointers = Array.from({ length: maximumPointers }, () => ({
            id: -1, kind: 0, role: 0, startX: 0, startY: 0, x: 0, y: 0,
            normalizedX: 0, normalizedY: 0
        }));
        this.keys = 0;
        this.composing = false;
        this.gamepadEnabled = false;
        this.jumpPointer = -1;
        this.lookX = 0;
        this.lookY = 0;
        this.idle = true;
    }

    install(signal) {
        const canvas = this.canvas;
        this.composing = false;
        canvas.addEventListener('pointerdown', event => this.pointerDown(event), { signal });
        canvas.addEventListener('pointermove', event => this.pointerMove(event), { signal });
        canvas.addEventListener('pointerup', event => this.release(event.pointerId, false), { signal });
        canvas.addEventListener('pointercancel', event => this.release(event.pointerId, true), { signal });
        canvas.addEventListener('lostpointercapture', event => this.release(event.pointerId, true), { signal });
        canvas.addEventListener('keydown', event => this.key(event, true), { signal });
        canvas.addEventListener('keyup', event => this.key(event, false), { signal });
        document.addEventListener('compositionstart', () => {
            this.composing = true;
            this.clear();
        }, { signal });
        document.addEventListener('compositionend', () => { this.composing = false; }, { signal });
        canvas.addEventListener('wheel', event => {
            if (!this.ownsInput() || this.isEditing(event.target)) return;
            const unit = event.deltaMode === WheelEvent.DOM_DELTA_LINE ? 16
                : event.deltaMode === WheelEvent.DOM_DELTA_PAGE ? canvas.clientHeight : 1;
            const delta = clamp(event.deltaY * unit, -240, 240);
            if (!Number.isFinite(delta)) return;
            this.host.scene.InputWheel(this.host.session, delta);
            this.idle = false;
            event.preventDefault();
        }, { signal, passive: false });

        // Controls opt into one canvas owner. Another canvas never steals these bindings.
        const controls = document.querySelectorAll('[data-input-for]');
        for (const control of controls) {
            if (control.dataset.inputFor !== canvas.id) continue;
            if (control.dataset.action === 'gamepad') {
                this.gamepadControl = control;
                this.gamepadStatus = control.parentElement?.querySelector('[role="status"]');
                this.gamepadEnabled = control.checked;
                control.addEventListener('change', () => {
                    this.gamepadEnabled = control.checked;
                    if (this.gamepadStatus) this.gamepadStatus.textContent = '';
                }, { signal });
            } else if (control.dataset.action === 'jump') {
                this.jumpButton = control;
                control.addEventListener('pointerdown', event => {
                    if (!this.host.session || !this.host.drawable || event.button !== 0 || this.jumpPointer !== -1) return;
                    canvas.focus({ preventScroll: true });
                    this.jumpPointer = event.pointerId;
                    try { control.setPointerCapture(event.pointerId); }
                    catch { this.jumpPointer = -1; return; }
                    event.preventDefault();
                }, { signal });
                const release = event => {
                    if (event.pointerId !== this.jumpPointer) return;
                    this.jumpPointer = -1;
                    if (control.hasPointerCapture(event.pointerId)) control.releasePointerCapture(event.pointerId);
                };
                control.addEventListener('pointerup', release, { signal });
                control.addEventListener('pointercancel', release, { signal });
                control.addEventListener('lostpointercapture', release, { signal });
                control.addEventListener('keydown', event => {
                    if (event.code !== 'Space' && event.code !== 'Enter') return;
                    if (!event.repeat && this.host.session) {
                        canvas.focus({ preventScroll: true });
                        this.jumpPulse = true;
                    }
                    event.preventDefault();
                }, { signal });
                control.addEventListener('click', event => {
                    if (event.detail !== 0 || !this.host.session || !this.host.drawable) return;
                    canvas.focus({ preventScroll: true });
                    this.jumpPulse = true;
                }, { signal });
            }
        }
    }

    ownsInput() {
        return this.host.session && this.host.drawable && !document.hidden && document.hasFocus()
            && document.activeElement === this.canvas && !this.composing;
    }

    isEditing(target) {
        return target instanceof Element && (target.isContentEditable
            || target.closest('input, textarea, select, [contenteditable="true"], [role="textbox"]') !== null);
    }

    key(event, down) {
        if (!this.ownsInput() || event.isComposing || this.isEditing(event.target)) return;
        const bit = event.code === 'ArrowLeft' ? left : event.code === 'ArrowRight' ? right
            : event.code === 'ArrowUp' ? forward : event.code === 'ArrowDown' ? backward
            : event.code === 'Space' ? jump : 0;
        // Each physical key has its own bit so releasing W does not release a held Up arrow.
        const physical = event.code === 'KeyA' ? 32 : event.code === 'KeyD' ? 64
            : event.code === 'KeyW' ? 128 : event.code === 'KeyS' ? 256 : bit;
        if (!physical || (down && (event.altKey || event.ctrlKey || event.metaKey))) return;
        this.keys = down ? this.keys | physical : this.keys & ~physical;
        this.idle = false;
        event.preventDefault();
    }

    findPointer(id) {
        for (let i = 0; i < maximumPointers; i++) if (this.pointers[i].id === id) return this.pointers[i];
        return null;
    }

    pointerDown(event) {
        if (!this.host.session || !this.host.drawable || event.button !== 0 || this.composing) return;
        if (this.findPointer(event.pointerId)) return;
        let slot = null;
        for (let i = 0; i < maximumPointers; i++) if (this.pointers[i].id === -1) { slot = this.pointers[i]; break; }
        if (!slot) return;
        const bounds = this.canvas.getBoundingClientRect();
        if (bounds.width <= 0 || bounds.height <= 0) return;
        this.canvas.focus({ preventScroll: true });
        slot.id = event.pointerId;
        this.idle = false;
        slot.kind = event.pointerType === 'touch' ? 1 : event.pointerType === 'pen' ? 2 : 0;
        slot.startX = slot.x = event.clientX;
        slot.startY = slot.y = event.clientY;
        slot.normalizedX = clamp((event.clientX - bounds.left) / bounds.width, 0, 1);
        slot.normalizedY = clamp((event.clientY - bounds.top) / bounds.height, 0, 1);
        const role = slot.kind === 1 && slot.normalizedX < 0.5 ? 1 : 2;
        slot.role = role;
        for (let i = 0; i < maximumPointers; i++) {
            const other = this.pointers[i];
            if (other !== slot && other.id !== -1 && other.role === role) { slot.role = 0; break; }
        }
        try { this.canvas.setPointerCapture(event.pointerId); }
        catch { slot.id = -1; slot.role = 0; return; }
        this.publishPointer(slot, 0);
        event.preventDefault();
    }

    pointerMove(event) {
        const slot = this.findPointer(event.pointerId);
        if (!slot) return;
        const bounds = this.canvas.getBoundingClientRect();
        if (bounds.width <= 0 || bounds.height <= 0) { this.release(slot.id, true); return; }
        if (slot.role === 2) {
            this.lookX += (event.clientX - slot.x) / bounds.width;
            this.lookY -= (event.clientY - slot.y) / bounds.height;
        }
        slot.x = event.clientX;
        slot.y = event.clientY;
        slot.normalizedX = clamp((slot.x - bounds.left) / bounds.width, 0, 1);
        slot.normalizedY = clamp((slot.y - bounds.top) / bounds.height, 0, 1);
        this.publishPointer(slot, 1);
        event.preventDefault();
    }

    publishPointer(slot, phase) {
        if (this.host.session) this.host.scene.InputPointer(this.host.session,
            slot.id, phase, slot.normalizedX, slot.normalizedY, slot.kind);
    }

    release(id, cancelled) {
        const slot = this.findPointer(id);
        if (!slot) return;
        this.publishPointer(slot, cancelled ? 3 : 2);
        slot.id = -1;
        slot.role = 0;
        if (this.canvas.hasPointerCapture(id)) this.canvas.releasePointerCapture(id);
    }

    poll(timestamp) {
        if (!this.ownsInput()) { this.clear(); return; }
        this.idle = false;
        let moveX = Number((this.keys & (right | 64)) !== 0) - Number((this.keys & (left | 32)) !== 0);
        let moveY = Number((this.keys & (forward | 128)) !== 0) - Number((this.keys & (backward | 256)) !== 0);
        let lookX = 0;
        let lookY = 0;
        let jumping = (this.keys & jump) !== 0 || this.jumpPointer !== -1 || this.jumpPulse === true;
        for (let i = 0; i < maximumPointers; i++) {
            const slot = this.pointers[i];
            if (slot.id !== -1 && slot.role === 1) {
                const radius = Math.max(24, Math.min(64, this.canvas.clientWidth * 0.2));
                moveX += (slot.x - slot.startX) / radius;
                moveY -= (slot.y - slot.startY) / radius;
            }
        }
        // getGamepads produces a browser-owned snapshot; the application creates no poll arrays or objects.
        if (this.gamepadEnabled) {
            let pads;
            try {
                if (typeof navigator.getGamepads !== 'function') throw new Error('The browser does not expose gamepads.');
                pads = navigator.getGamepads();
            } catch (error) {
                this.gamepadEnabled = false;
                if (this.gamepadControl) this.gamepadControl.checked = false;
                if (this.gamepadStatus) this.gamepadStatus.textContent = `Gamepad unavailable: ${error.message ?? error}`;
            }
            if (pads) for (let i = 0; i < pads.length; i++) {
                const pad = pads[i];
                if (!pad?.connected || pad.mapping !== 'standard') continue;
                moveX += this.deadzone(pad.axes[0]);
                moveY -= this.deadzone(pad.axes[1]);
                lookX += this.deadzone(pad.axes[2]);
                lookY -= this.deadzone(pad.axes[3]);
                jumping ||= pad.buttons[0]?.pressed === true;
                break;
            }
        }
        const magnitude = Math.max(1, Math.hypot(moveX, moveY));
        // Pointer displacement survives render frames with no fixed simulation tick.
        if (this.lookX || this.lookY)
            this.host.scene.InputLookDelta(this.host.session, clamp(this.lookX, -1, 1), clamp(this.lookY, -1, 1));
        this.host.scene.InputActions(this.host.session, moveX / magnitude, moveY / magnitude,
            clamp(lookX, -1, 1), clamp(lookY, -1, 1), jumping);
        this.lookX = this.lookY = 0;
        this.jumpPulse = false;
    }

    deadzone(value) {
        if (!Number.isFinite(value)) return 0;
        const magnitude = Math.abs(value);
        return magnitude <= 0.15 ? 0 : Math.sign(value) * Math.min(1, (magnitude - 0.15) / 0.85);
    }

    clear() {
        const notify = !this.idle;
        this.idle = true;
        this.keys = 0;
        this.lookX = this.lookY = 0;
        this.jumpPulse = false;
        const id = this.jumpPointer;
        this.jumpPointer = -1;
        if (id !== -1 && this.jumpButton?.hasPointerCapture(id)) this.jumpButton.releasePointerCapture(id);
        for (let i = 0; i < maximumPointers; i++) {
            if (this.pointers[i].id !== -1) this.release(this.pointers[i].id, true);
        }
        if (notify && this.host.session) this.host.scene.ResetInput(this.host.session);
    }
}
