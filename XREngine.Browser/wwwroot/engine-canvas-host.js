import { WebGpuCanvasRenderer } from './webgpu/webgpu-renderer.js';

const canvasOwners = new WeakMap();

/** Owns the authored engine world's one canvas, WebGPU session, and browser frame clock. */
export class EngineCanvasHost {
    constructor(engine, renderers, canvas, input, onState) {
        if (!(canvas instanceof HTMLCanvasElement) || !canvas.id)
            throw new Error('The engine canvas requires a stable element ID.');
        this.engine = engine;
        this.renderers = renderers;
        this.canvas = canvas;
        this.input = input;
        this.onState = onState;
        this.epoch = 0;
        this.firstFrameSeconds = 0;
        this.frame = this.frame.bind(this);
    }

    async start(manifestUrl, qualityPreset = '') {
        const epoch = ++this.epoch;
        await this.stop(false);
        if (epoch !== this.epoch) return;
        const prior = canvasOwners.get(this.canvas);
        if (prior && prior !== this) throw new Error('The engine canvas already has an owner.');
        canvasOwners.set(this.canvas, this);
        const controller = new AbortController();
        this.controller = controller;
        this.onState('loading', 'Loading the published engine world…');
        try {
            this.engine.SetCanvasQualityPreset(qualityPreset);
            const detail = await this.engine.StartCanvasAsync(manifestUrl, this.canvas.id);
            this.detail = detail;
            if (epoch !== this.epoch || controller.signal.aborted) return;
            this.quality = JSON.parse(this.engine.GetCanvasQualitySettingsJson());
            const session = this.engine.GetRendererSession();
            if (!Number.isInteger(session) || session <= 0)
                throw new Error('WebGPU.EngineCanvas.SessionUnavailable: the authored world did not return a renderer owner.');
            this.session = session;
            const renderer = new WebGpuCanvasRenderer(this.canvas,
                state => { if (epoch === this.epoch) this.onState('loading', `WebGPU ${state.replaceAll('-', ' ')}…`); },
                error => { if (epoch === this.epoch) this.fail(error); },
                'browser-unlit', 'CpuDirect', 'Cpu');
            this.renderer = renderer;
            await renderer.initializeEngine(controller.signal);
            if (epoch !== this.epoch || controller.signal.aborted) return;
            renderer.setOwner(session);
            this.renderers.set(session, renderer);
            this.engine.InitializeCanvasGraphics(renderer.colorFormat);
            this.installEvents(controller.signal);
            this.syncSurface();
            if (this.drawable) this.onState('loading', 'Preparing the authored world’s first rendered frame…');
        } catch (error) {
            if (epoch === this.epoch && !controller.signal.aborted) this.fail(error);
        }
    }

    installEvents(signal) {
        const refresh = () => {
            if (signal.aborted) return;
            try { this.syncSurface(); } catch (error) { this.fail(error); }
        };
        this.resizeObserver = new ResizeObserver(refresh);
        this.resizeObserver.observe(this.canvas);
        // Status text and unrelated DOM updates must not reset the engine clock
        // or create a mutation -> status -> mutation microtask loop.
        this.attachmentObserver = new MutationObserver(() => {
            if (this.canvas.isConnected !== this.attached) refresh();
        });
        this.attachmentObserver.observe(document.documentElement, { childList: true, subtree: true });
        window.addEventListener('resize', refresh, { signal });
        window.addEventListener('orientationchange', refresh, { signal });
        window.visualViewport?.addEventListener('resize', refresh, { signal });
        document.addEventListener('visibilitychange', refresh, { signal });
        document.addEventListener('freeze', () => { this.frozen = true; refresh(); }, { signal });
        document.addEventListener('resume', () => { this.frozen = false; refresh(); }, { signal });
        window.addEventListener('blur', refresh, { signal });
        window.addEventListener('focus', refresh, { signal });
        this.canvas.addEventListener('focus', refresh, { signal });
        this.canvas.addEventListener('blur', refresh, { signal });
    }

    setPageHidden(hidden) {
        this.pageHidden = Boolean(hidden);
        // A back/forward-cache transition may precede GPU initialization. Keep
        // the state so that late startup never starts a hidden frame clock.
        if (this.renderer && this.session) {
            try { this.syncSurface(); } catch (error) { this.fail(error); }
        }
    }

    syncSurface() {
        const renderer = this.renderer;
        if (!renderer || !this.session) return;
        const bounds = this.canvas.getBoundingClientRect();
        const attached = this.canvas.isConnected;
        const rawRatio = window.devicePixelRatio || 1;
        const maximum = Math.min(this.quality.maxBackingDimension, renderer.maxDimension);
        const ratio = Math.min(rawRatio, this.quality.maxDevicePixelRatio) * this.quality.resolutionScale;
        const boundedRatio = Math.min(ratio, maximum / Math.max(1, bounds.width, bounds.height));
        const width = attached && bounds.width > 0 && bounds.height > 0
            ? Math.max(1, Math.min(maximum, Math.round(bounds.width * boundedRatio))) : 0;
        const height = width > 0 ? Math.max(1, Math.min(maximum, Math.round(bounds.height * boundedRatio))) : 0;
        const generation = renderer.resize(width, height);
        const visible = !document.hidden && !this.frozen && !this.pageHidden;
        const focused = this.input.ownsFocus();
        this.engine.UpdateCanvasSurface(Math.max(0, bounds.width), Math.max(0, bounds.height),
            width, height, boundedRatio, generation, visible, focused, attached);
        this.rawRatio = rawRatio;
        this.attached = attached;
        this.drawable = visible && attached && width > 0 && height > 0;
        if (this.drawable) {
            this.engine.ResetFrameTiming();
            this.previousFrame = undefined;
            if (!this.request) this.request = requestAnimationFrame(this.frame);
            if (this.presented) this.onState('running', `Engine world ready: ${this.detail}`);
        } else {
            if (this.request) cancelAnimationFrame(this.request);
            this.request = 0;
            this.previousFrame = undefined;
            this.input.reset();
            this.engine.ResetFrameTiming();
            this.onState('suspended', 'Rendering paused while the canvas is hidden, detached, or has no size.');
        }
    }

    frame(now) {
        this.request = 0;
        if (!this.drawable || !this.session) return;
        try {
            if ((window.devicePixelRatio || 1) !== this.rawRatio || this.canvas.isConnected !== this.attached)
                this.syncSurface();
            if (!this.drawable) return;
            this.input.publish();
            const elapsed = this.previousFrame === undefined ? 0 : Math.max(0, (now - this.previousFrame) / 1000);
            this.previousFrame = now;
            if (!this.engine.Step(elapsed)) throw new Error('The engine caller-thread loop stopped.');
            this.input.syncTextFocus();
            if (!this.presented) {
                const state = this.engine.GetCanvasPreparationState();
                if (state < 0) throw new Error(this.engine.GetCanvasRenderingStatus());
                if (state > 0) {
                    this.presented = true;
                    this.onState('running', `Engine world ready: ${this.detail}`);
                } else {
                    this.firstFrameSeconds += elapsed;
                    if (this.firstFrameSeconds > 45)
                        throw new Error(`First-frame preparation exceeded 45 seconds. ${this.engine.GetCanvasRenderingStatus()}`);
                }
            }
            if (!this.request) this.request = requestAnimationFrame(this.frame);
        } catch (error) { this.fail(error); }
    }

    fail(error) {
        if (this.failed) return;
        this.failed = true;
        console.error(error);
        this.engine.CanvasRendererFailed(this.renderer?.isDeviceLost ?? false);
        const message = `Engine canvas failed: ${error.message ?? error}`;
        const stoppedEpoch = this.epoch + 1;
        void this.stop().then(() => {
            if (this.epoch === stoppedEpoch) this.onState('failed', message);
        }, cleanup => {
            console.error(cleanup);
            if (this.epoch === stoppedEpoch)
                this.onState('failed', `${message}; cleanup failed: ${cleanup.message ?? cleanup}`);
        });
    }

    async stop(supersede = true) {
        const stoppedEpoch = supersede ? ++this.epoch : this.epoch;
        this.controller?.abort();
        this.controller = null;
        if (this.request) cancelAnimationFrame(this.request);
        this.request = 0;
        this.drawable = false;
        this.input.reset();
        this.resizeObserver?.disconnect();
        this.resizeObserver = null;
        this.attachmentObserver?.disconnect();
        this.attachmentObserver = null;
        const session = this.session;
        const renderer = this.renderer;
        this.session = 0;
        this.renderer = null;
        try {
            while (true) {
                try { await this.engine.StopAsync(); break; }
                catch (error) {
                    if (!String(error.message ?? error).includes('jobs remain pending')) throw error;
                    await new Promise(resolve => setTimeout(resolve, 100));
                }
            }
        } finally {
            this.renderers.delete(session);
            renderer?.dispose();
            if (this.epoch === stoppedEpoch && canvasOwners.get(this.canvas) === this)
                canvasOwners.delete(this.canvas);
        }
        if (this.epoch === stoppedEpoch) {
            this.detail = null;
            this.quality = null;
            this.presented = false;
            this.firstFrameSeconds = 0;
            this.failed = false;
        }
    }
}
