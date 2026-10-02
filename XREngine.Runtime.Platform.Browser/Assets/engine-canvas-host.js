import { WebGpuCanvasRenderer } from './webgpu/webgpu-renderer.js';

const canvasOwners = new WeakMap();
const maximumRecoveryAttempts = 3;
const deviceStartupTimeoutMs = 20000;
const recoveryCompletionTimeoutMs = 10000;

// Browser adapter/device promises need not settle promptly after cancellation.
// Their own generation checks still destroy any device returned after this gate.
async function boundedOperation(operation, signal, milliseconds, description) {
    let timer, cancel;
    const stopped = new Promise((_, reject) => {
        cancel = () => reject(new DOMException('Canvas operation was canceled.', 'AbortError'));
        if (signal.aborted) cancel();
        else signal.addEventListener('abort', cancel, { once: true });
        timer = setTimeout(() => reject(new Error(`${description} exceeded ${milliseconds} ms.`)), milliseconds);
    });
    try { return await Promise.race([operation, stopped]); }
    finally {
        clearTimeout(timer);
        signal.removeEventListener('abort', cancel);
    }
}

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
        this.recoveryAttempts = 0;
        this.recoveryDiagnostics = [];
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
            if (epoch !== this.epoch || controller.signal.aborted) return;
            this.detail = detail;
            this.quality = JSON.parse(this.engine.GetCanvasQualitySettingsJson());
            const session = this.engine.GetRendererSession();
            if (!Number.isInteger(session) || session <= 0)
                throw new Error('WebGPU.EngineCanvas.SessionUnavailable: the authored world did not return a renderer owner.');
            this.session = session;
            this.installEvents(controller.signal);
            const renderer = this.createRenderer(epoch);
            await this.initializeRenderer(renderer, controller.signal);
            if (epoch !== this.epoch || controller.signal.aborted || this.renderer !== renderer || this.recovering) return;
            renderer.setOwner(session);
            this.renderers.set(session, renderer);
            this.engine.InitializeCanvasGraphics(session, renderer.colorFormat);
            this.rendererReady = true;
            this.syncSurface();
            if (this.drawable) this.onState('loading', 'Preparing the authored world’s first rendered frame…');
        } catch (error) {
            if (epoch === this.epoch && !controller.signal.aborted && !this.recovering) this.fail(error);
        }
    }

    createRenderer(epoch) {
        const renderer = new WebGpuCanvasRenderer(this.canvas,
            state => {
                if (epoch === this.epoch && this.renderer === renderer)
                    this.onState(this.recovering ? 'recovering' : 'loading', `WebGPU ${state.replaceAll('-', ' ')}…`);
            },
            error => {
                if (epoch === this.epoch && this.renderer === renderer) this.fail(error, renderer);
            }, 'browser-unlit', 'CpuDirect', 'Cpu');
        this.renderer = renderer;
        return renderer;
    }

    async initializeRenderer(renderer, signal) {
        const controller = new AbortController();
        const cancel = () => controller.abort();
        this.graphicsController = controller;
        if (signal.aborted) cancel();
        else signal.addEventListener('abort', cancel, { once: true });
        try {
            await boundedOperation(renderer.initializeEngine(controller.signal), controller.signal,
                deviceStartupTimeoutMs, 'WebGPU device acquisition');
        } catch (error) {
            controller.abort();
            renderer.dispose();
            throw error;
        } finally {
            signal.removeEventListener('abort', cancel);
            if (this.graphicsController === controller) this.graphicsController = null;
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
        if (!renderer || !this.session || !this.rendererReady || this.failed) return;
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
        const drawable = visible && attached && width > 0 && height > 0;
        const outputChanged = generation !== this.surfaceGeneration;
        const clockDiscontinuity = drawable !== this.drawable || outputChanged;
        this.engine.UpdateCanvasSurface(Math.max(0, bounds.width), Math.max(0, bounds.height),
            width, height, boundedRatio, generation, visible, focused, attached);
        this.rawRatio = rawRatio;
        this.attached = attached;
        this.surfaceGeneration = generation;
        this.drawable = drawable;
        if (outputChanged) {
            this.presented = false;
            this.firstFrameSeconds = 0;
        }
        if (clockDiscontinuity) this.previousFrame = undefined;
        if (this.drawable) {
            if (!this.request && !this.validatingRecoveryFrame) this.request = requestAnimationFrame(this.frame);
            if (this.presented) this.onState('running', `Engine world ready: ${this.detail}`);
            else if (outputChanged) this.onState(this.recovering ? 'recovering' : 'loading', 'Preparing the current canvas output…');
        } else {
            if (this.request) cancelAnimationFrame(this.request);
            this.request = 0;
            if (clockDiscontinuity) this.input.reset();
            this.onState('suspended', 'Rendering paused while the canvas is hidden, detached, or has no size.');
        }
    }

    frame(now) {
        this.request = 0;
        if (!this.drawable || !this.session || !this.rendererReady || this.failed) return;
        try {
            if ((window.devicePixelRatio || 1) !== this.rawRatio || this.canvas.isConnected !== this.attached)
                this.syncSurface();
            if (!this.drawable) return;
            if (this.recovering) {
                if (!this.validatingRecoveryFrame) void this.prepareRecoveryFrame(now);
                return;
            }
            this.input.publish();
            const gap = this.previousFrame === undefined ? 0 : now - this.previousFrame;
            let elapsed = gap / 1000;
            if (!Number.isFinite(gap) || gap < 0) {
                this.engine.ResetFrameTiming();
                elapsed = 0;
            }
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
                    // Clock resets discard simulation debt, not an active output's
                    // preparation time. Suspension clears previousFrame separately.
                    if (Number.isFinite(gap) && gap > 0) this.firstFrameSeconds += gap / 1000;
                    if (this.firstFrameSeconds > 45)
                        throw new Error(`First-frame preparation exceeded 45 seconds. ${this.engine.GetCanvasRenderingStatus()}`);
                }
            }
            if (!this.request) this.request = requestAnimationFrame(this.frame);
        } catch (error) { this.fail(error); }
    }

    async prepareRecoveryFrame(now) {
        const epoch = this.epoch, session = this.session, renderer = this.renderer;
        const signal = this.controller.signal;
        const surfaceGeneration = this.surfaceGeneration;
        const current = () => epoch === this.epoch && session === this.session &&
            renderer === this.renderer && !signal.aborted && !this.failed;
        this.validatingRecoveryFrame = true;
        const gap = this.previousFrame === undefined ? 0 : now - this.previousFrame;
        this.previousFrame = now;
        if (Number.isFinite(gap) && gap > 0) this.firstFrameSeconds += gap / 1000;
        let ticket;
        try {
            const device = renderer.device;
            device.pushErrorScope('out-of-memory');
            device.pushErrorScope('validation');
            let failure, scopes;
            try {
                if (!this.engine.Step(0)) throw new Error('The engine caller-thread loop stopped during device recovery.');
            } catch (error) { failure = error; }
            finally { scopes = Promise.all([device.popErrorScope(), device.popErrorScope()]); }
            const errors = await boundedOperation(scopes, signal, recoveryCompletionTimeoutMs,
                'WebGPU replacement frame validation');
            if (!current()) return;
            if (failure) throw failure;
            for (const error of errors)
                if (error) throw new Error(`WebGPU replacement frame: ${error.message}`);
            const state = this.engine.GetCanvasPreparationState();
            if (state < 0) throw new Error(this.engine.GetCanvasRenderingStatus());
            if (state > 0) {
                ticket = renderer.readback.beginCompletion();
                await boundedOperation(renderer.readback.wait(ticket), signal,
                    recoveryCompletionTimeoutMs, 'WebGPU replacement frame completion');
                if (!current()) return;
                renderer._requireOwner();
                if (this.recoveryFailure) throw this.recoveryFailure;
                if (this.drawable && surfaceGeneration === this.surfaceGeneration) {
                    this.engine.CompleteCanvasRecovery(session);
                    this.resolveRecovery?.();
                    return;
                }
            }
            if (this.firstFrameSeconds > 45)
                throw new Error(`Replacement frame preparation exceeded 45 active seconds. ${this.engine.GetCanvasRenderingStatus()}`);
        } catch (error) {
            if (current()) this.fail(error, renderer);
        } finally {
            if (ticket !== undefined) renderer.readback.release(ticket);
            if (current()) {
                this.validatingRecoveryFrame = false;
                if (this.drawable && !this.request && !this.recoveryFailure)
                    this.request = requestAnimationFrame(this.frame);
            }
        }
    }

    fail(error, renderer = this.renderer) {
        if (renderer !== this.renderer || !this.controller || this.controller.signal.aborted) return;
        if (this.recovering) {
            this.recoveryFailure = error;
            this.rejectRecovery?.(error);
            if (this.request) cancelAnimationFrame(this.request);
            this.request = 0;
            return;
        }
        if (this.failed) return;
        if (renderer?.isDeviceLost && this.session) {
            void this.recover(error);
            return;
        }
        this.failed = true;
        console.error(error);
        this.engine.CanvasRendererFailed(this.session, renderer?.isDeviceLost ?? false);
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

    async recover(error) {
        const epoch = this.epoch, signal = this.controller.signal;
        this.recovering = true;
        const current = () => epoch === this.epoch && !signal.aborted;
        let failure = error;
        while (current() && this.recoveryAttempts < maximumRecoveryAttempts) {
            const previous = this.renderer, previousSession = this.session;
            this.recoveryDiagnostics.push({ attempt: this.recoveryAttempts, message: String(failure.message ?? failure),
                renderer: previous?.getFailureDiagnostics() });
            console.warn('WebGPU device replacement:', this.recoveryDiagnostics[this.recoveryDiagnostics.length - 1]);
            this.recoveryAttempts++;
            this.rendererReady = false;
            this.presented = false;
            this.drawable = false;
            this.validatingRecoveryFrame = false;
            this.previousFrame = undefined;
            this.surfaceGeneration = undefined;
            this.firstFrameSeconds = 0;
            this.recoveryFailure = null;
            if (this.request) cancelAnimationFrame(this.request);
            this.request = 0;
            this.input.reset();
            this.onState('recovering', `Restoring WebGPU (${this.recoveryAttempts}/${maximumRecoveryAttempts}); gameplay is paused…`);
            try {
                this.session = this.engine.BeginCanvasRecovery(previousSession, previous?.isDeviceLost ?? false);
                this.renderers.delete(previousSession);
                previous?.dispose();
                const renderer = this.createRenderer(epoch);
                await this.initializeRenderer(renderer, signal);
                if (!current()) return;
                if (this.recoveryFailure) throw this.recoveryFailure;
                renderer.setOwner(this.session);
                this.renderers.set(this.session, renderer);
                this.engine.InitializeCanvasGraphics(this.session, renderer.colorFormat);
                this.rendererReady = true;
                const firstFrame = new Promise((resolve, reject) => {
                    this.resolveRecovery = resolve;
                    this.rejectRecovery = reject;
                });
                const cancel = () => this.rejectRecovery?.(new DOMException('Recovery was canceled.', 'AbortError'));
                signal.addEventListener('abort', cancel, { once: true });
                try {
                    this.syncSurface();
                    await firstFrame;
                } finally {
                    signal.removeEventListener('abort', cancel);
                    this.resolveRecovery = null;
                    this.rejectRecovery = null;
                }
                if (!current()) return;
                if (this.recoveryFailure) throw this.recoveryFailure;
                renderer._requireOwner();
                this.recovering = false;
                this.presented = true;
                this.previousFrame = undefined;
                this.syncSurface();
                return;
            } catch (recoveryError) {
                if (!current()) return;
                failure = recoveryError;
            }
        }
        if (!current()) return;
        this.failed = true;
        this.rendererReady = false;
        this.drawable = false;
        if (this.request) cancelAnimationFrame(this.request);
        this.request = 0;
        const message = `WebGPU recovery failed after ${this.recoveryAttempts} attempts: ${failure.message ?? failure}. ` +
            'The world remains paused. Retry explicitly reloads it.';
        this.recoveryDiagnostics.push({ attempt: this.recoveryAttempts, message,
            renderer: this.renderer?.getFailureDiagnostics() });
        console.error('WebGPU recovery exhausted:', this.recoveryDiagnostics);
        try { this.engine.SuspendCanvasRecovery(this.session, message); }
        catch (cleanup) { console.error('WebGPU recovery retirement failed:', cleanup); }
        finally {
            this.renderers.delete(this.session);
            this.renderer?.dispose();
        }
        this.recovering = false;
        this.onState('failed', message);
    }

    async stop(supersede = true) {
        const stoppedEpoch = supersede ? ++this.epoch : this.epoch;
        this.controller?.abort();
        this.graphicsController?.abort();
        this.controller = null;
        if (this.request) cancelAnimationFrame(this.request);
        this.request = 0;
        this.drawable = false;
        this.surfaceGeneration = undefined;
        this.previousFrame = undefined;
        this.input.reset();
        this.resizeObserver?.disconnect();
        this.resizeObserver = null;
        this.attachmentObserver?.disconnect();
        this.attachmentObserver = null;
        const session = this.session;
        const renderer = this.renderer;
        this.session = 0;
        this.renderer = null;
        this.rendererReady = false;
        this.recovering = false;
        this.validatingRecoveryFrame = false;
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
            this.recoveryAttempts = 0;
            this.recoveryDiagnostics = [];
            this.recoveryFailure = null;
        }
    }
}
