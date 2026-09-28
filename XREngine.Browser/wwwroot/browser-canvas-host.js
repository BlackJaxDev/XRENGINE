import { WebGpuCanvasRenderer } from './webgpu-renderer.js';

/** Owns one canvas session. The shared runtime only routes interop by session ID. */
export class BrowserCanvasHost {
    constructor(scene, renderers, canvas, onState) {
        if (!(canvas instanceof HTMLCanvasElement) || !canvas.id)
            throw new Error('A canvas with a stable ID must be supplied.');
        this.scene = scene;
        this.renderers = renderers;
        this.canvas = canvas;
        this.onState = onState;
        this.session = 0;
        this.epoch = 0;
        this.frameId = 0;
        this.disposed = false;
        this.splitView = true;
        this.instanceCount = 16;
        this.pointerId = null;
        this.pointerX = 0.5;
        this.pointerY = 0.5;
        this.keys = new Set();
        this.maxPixelRatio = 2;
        this.frame = this.frame.bind(this);
    }

    setState(state, message) {
        if (this.state === state && this.message === message) return;
        this.state = state;
        this.message = message;
        this.onState(state, message);
    }

    async start() {
        if (this.disposed) throw new Error('The canvas host is disposed.');
        this.stop();
        const epoch = this.epoch;
        const controller = new AbortController();
        this.controller = controller;
        const renderer = new WebGpuCanvasRenderer(this.canvas,
            state => {
                if (epoch === this.epoch) this.setState(state, state.replaceAll('-', ' '));
            },
            error => { if (epoch === this.epoch) this.fail(error); });
        this.renderer = renderer;
        try {
            await renderer.initialize(controller.signal);
            if (epoch !== this.epoch || controller.signal.aborted) {
                renderer.dispose();
                return;
            }
            this.session = this.scene.Create(this.canvas.id);
            renderer.setOwner(this.session);
            this.renderers.set(this.session, renderer);
            this.scene.SetInstanceCount(this.session, this.instanceCount);
            this.scene.InitializeGraphics(this.session);
            this.scene.SetSplitView(this.session, this.splitView);
            this.installEvents(controller.signal);
            this.syncSurface();
        } catch (error) {
            if (epoch === this.epoch && !controller.signal.aborted) this.fail(error);
        }
    }

    installEvents(signal) {
        const refresh = () => {
            try { this.syncSurface(); } catch (error) { this.fail(error); }
        };
        this.resizeObserver = new ResizeObserver(refresh);
        this.resizeObserver.observe(this.canvas);
        this.attachmentObserver = new MutationObserver(() => {
            if (this.canvas.isConnected !== this.attached) refresh();
        });
        this.attachmentObserver.observe(document.documentElement, { childList: true, subtree: true });
        window.addEventListener('resize', refresh, { signal });
        window.addEventListener('orientationchange', refresh, { signal });
        document.addEventListener('visibilitychange', () => {
            this.clearInput();
            refresh();
        }, { signal });
        window.addEventListener('blur', () => {
            this.clearInput();
            refresh();
        }, { signal });
        window.addEventListener('focus', refresh, { signal });
        this.canvas.addEventListener('focus', refresh, { signal });
        this.canvas.addEventListener('blur', () => {
            this.clearInput();
            refresh();
        }, { signal });
        this.canvas.addEventListener('pointerdown', event => {
            if (!event.isPrimary || event.button !== 0 || this.pointerId !== null) return;
            this.canvas.focus({ preventScroll: true });
            if (!this.session) return;
            this.pointerId = event.pointerId;
            this.canvas.setPointerCapture(event.pointerId);
            this.updatePointer(event);
            event.preventDefault();
        }, { signal });
        this.canvas.addEventListener('pointermove', event => {
            if (event.pointerId === this.pointerId) this.updatePointer(event);
        }, { signal });
        const release = event => {
            if (event.pointerId === this.pointerId) this.clearPointer();
        };
        this.canvas.addEventListener('pointerup', release, { signal });
        this.canvas.addEventListener('pointercancel', release, { signal });
        this.canvas.addEventListener('lostpointercapture', release, { signal });
        this.canvas.addEventListener('keydown', event => {
            if (!this.isMovementKey(event.code)) return;
            this.keys.add(event.code);
            this.publishInput();
            event.preventDefault();
        }, { signal });
        this.canvas.addEventListener('keyup', event => {
            if (!this.isMovementKey(event.code)) return;
            this.keys.delete(event.code);
            this.publishInput();
            event.preventDefault();
        }, { signal });
    }

    isMovementKey(code) {
        return code === 'ArrowLeft' || code === 'ArrowRight' || code === 'ArrowUp' || code === 'ArrowDown';
    }

    updatePointer(event) {
        const bounds = this.canvas.getBoundingClientRect();
        if (bounds.width <= 0 || bounds.height <= 0) return;
        this.pointerX = (event.clientX - bounds.left) / bounds.width;
        this.pointerY = (event.clientY - bounds.top) / bounds.height;
        this.publishInput();
    }

    publishInput() {
        if (!this.session) return;
        try {
            this.scene.Input(this.session, this.pointerX, this.pointerY, this.pointerId !== null,
                Number(this.keys.has('ArrowRight')) - Number(this.keys.has('ArrowLeft')),
                Number(this.keys.has('ArrowUp')) - Number(this.keys.has('ArrowDown')));
        } catch (error) { this.fail(error); }
    }

    clearPointer() {
        const pointer = this.pointerId;
        this.pointerId = null;
        if (pointer !== null && this.canvas.hasPointerCapture(pointer)) this.canvas.releasePointerCapture(pointer);
        this.publishInput();
    }

    clearInput() {
        this.keys.clear();
        this.clearPointer();
    }

    syncSurface() {
        if (!this.session || !this.renderer) return;
        const bounds = this.canvas.getBoundingClientRect();
        this.attached = this.canvas.isConnected;
        this.rawPixelRatio = window.devicePixelRatio || 1;
        let ratio = Math.min(this.rawPixelRatio, this.maxPixelRatio);
        const logicalWidth = Math.max(0, bounds.width);
        const logicalHeight = Math.max(0, bounds.height);
        const largest = Math.max(logicalWidth, logicalHeight);
        if (largest > 0) ratio = Math.min(ratio, this.renderer.maxDimension / largest);
        const width = this.attached && logicalWidth > 0 && logicalHeight > 0
            ? Math.max(1, Math.min(this.renderer.maxDimension, Math.round(logicalWidth * ratio))) : 0;
        const height = width > 0
            ? Math.max(1, Math.min(this.renderer.maxDimension, Math.round(logicalHeight * ratio))) : 0;
        this.renderer.resize(width, height);
        const visible = !document.hidden;
        const focused = document.hasFocus() && document.activeElement === this.canvas;
        this.scene.Resize(this.session, logicalWidth, logicalHeight, width, height,
            ratio, this.renderer.generation, visible, focused, this.attached);
        this.drawable = visible && this.attached && width > 0 && height > 0;
        if (this.drawable) {
            this.setState('running', 'WebGPU ready. Drag the scene or focus the canvas and use arrow keys.');
            if (!this.frameId) this.frameId = requestAnimationFrame(this.frame);
        } else {
            cancelAnimationFrame(this.frameId);
            this.frameId = 0;
            this.scene.ResetClock(this.session);
            this.clearInput();
            this.setState('suspended', 'Rendering paused while the canvas is hidden, detached, or has no size.');
        }
    }

    frame(timestamp) {
        this.frameId = 0;
        if (!this.session || !this.drawable) return;
        try {
            // DPR can change when moving between screens without a CSS resize.
            if ((window.devicePixelRatio || 1) !== this.rawPixelRatio || this.canvas.isConnected !== this.attached)
                this.syncSurface();
            if (!this.drawable) return;
            this.scene.Frame(this.session, timestamp);
            if (!this.frameId) this.frameId = requestAnimationFrame(this.frame);
        } catch (error) { this.fail(error); }
    }

    setSplitView(enabled) {
        this.splitView = enabled;
        try {
            if (this.session) this.scene.SetSplitView(this.session, enabled);
        } catch (error) { this.fail(error); }
    }

    setInstanceCount(count) {
        if (!Number.isInteger(count) || count < 1 || count > 256)
            throw new Error('Instance count must be between 1 and 256.');
        this.instanceCount = count;
        try {
            if (this.session) this.scene.SetInstanceCount(this.session, count);
        } catch (error) { this.fail(error); }
    }

    getStatistics() {
        // Diagnostic snapshots are explicit UI actions, never animation-frame work.
        return this.renderer?.getStatistics() ?? null;
    }

    fail(error) {
        this.stop();
        console.error(error);
        this.setState('failed', `WebGPU startup/rendering failed: ${error.message ?? error}. Restart to retry; no fallback renderer is packaged.`);
    }

    stop() {
        this.epoch++;
        cancelAnimationFrame(this.frameId);
        this.frameId = 0;
        this.controller?.abort();
        this.controller = null;
        this.resizeObserver?.disconnect();
        this.attachmentObserver?.disconnect();
        this.resizeObserver = null;
        this.attachmentObserver = null;
        const session = this.session;
        this.session = 0;
        this.clearInput();
        try {
            if (session) this.scene.Destroy(session);
        } catch (error) {
            console.error('Scene shutdown failed.', error);
        } finally {
            this.renderers.delete(session);
            this.renderer?.dispose();
            this.renderer = null;
            this.drawable = false;
        }
        this.setState('stopped', 'Stopped. Restart to create a new scene and WebGPU device.');
    }

    dispose() {
        if (this.disposed) return;
        this.stop();
        this.disposed = true;
    }
}
