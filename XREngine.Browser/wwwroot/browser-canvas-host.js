import { WebGpuCanvasRenderer } from './webgpu/webgpu-renderer.js';
import { BrowserContentLoader } from './content-loader.js';
import { BrowserAudioService } from './browser-audio.js';
import { BrowserServicePolicy } from './browser-services.js';
import { BrowserInput } from './browser-input.js';

// Ownership is keyed by the supplied element, never by a process-wide current canvas.
const canvasOwners = new WeakMap();

/** Owns one canvas session. The shared runtime only routes interop by session ID. */
export class BrowserCanvasHost {
    constructor(scene, renderers, canvas, onState, shaderName = 'browser-unlit') {
        if (typeof shaderName !== 'string' || !/^[a-z][a-z0-9-]{0,63}$/.test(shaderName))
            throw new Error('The shader name must identify a cooked manifest artifact.');
        this.shaderName = shaderName;
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
        this.cullingEnabled = true;
        this.recolorAlternate = false;
        this.snapshotJson = null;
        this.contentUrl = null;
        this.contentLoader = null;
        this.contentProgress = null;
        this.graphicsReady = false;
        this.audio = null;
        this.services = null;
        this.input = new BrowserInput(this);
        this.maxPixelRatio = 1.5;
        this.maxBackingDimension = 1280;
        this.resolutionScale = 1;
        this.qualityPreset = 'balanced';
        this.uiEnabled = true;
        this.frozen = false;
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
        const owner = canvasOwners.get(this.canvas);
        if (owner && owner !== this) throw new Error('This canvas already has an active host.');
        this.stop();
        canvasOwners.set(this.canvas, this);
        this.frozen = false;
        const epoch = this.epoch;
        const controller = new AbortController();
        this.controller = controller;
        const audio = new BrowserAudioService({ onState: () => {
            if (epoch !== this.epoch || controller.signal.aborted) return;
            this.onAudioState?.(this.audio?.state, this.audio?.reason);
            this.services?.changed();
        } });
        this.audio = audio;
        this.onAudioState?.(audio.state, audio.reason);
        audio.setVisible(!document.hidden);
        document.addEventListener('visibilitychange', () => audio.setVisible(!document.hidden && !this.frozen),
            { signal: controller.signal });
        this.services = new BrowserServicePolicy(audio, !this.contentUrl && !this.snapshotJson, () => {
            if (epoch === this.epoch && this.graphicsReady) this.syncSurface();
        });
        this.services.configure(!this.contentUrl && !this.snapshotJson
            ? { required: ['dom-ui', 'cpu-animation', 'character-collision'], optional: ['web-audio'] } : null);
        const renderer = new WebGpuCanvasRenderer(this.canvas,
            state => {
                if (epoch === this.epoch) this.setState(state, state.replaceAll('-', ' '));
            },
            error => { if (epoch === this.epoch) this.fail(error); }, this.shaderName);
        this.renderer = renderer;
        renderer.audioService = audio;
        try {
            this.session = this.contentUrl ? this.scene.CreateCooked(this.canvas.id) : this.snapshotJson
                ? this.scene.CreateFromSnapshot(this.canvas.id, this.snapshotJson)
                : this.scene.Create(this.canvas.id);
            await renderer.initialize(controller.signal);
            if (epoch !== this.epoch || controller.signal.aborted) {
                renderer.dispose();
                return;
            }
            renderer.setOwner(this.session);
            this.renderers.set(this.session, renderer);
            this.scene.SetQualityPreset(this.session, this.qualityPreset);
            this.scene.SetUiEnabled(this.session, this.uiEnabled);
            if (!this.snapshotJson && !this.contentUrl) this.scene.SetInstanceCount(this.session, this.instanceCount);
            this.scene.InitializeGraphics(this.session, renderer.colorFormat);
            this.scene.SetCullingEnabled(this.session, this.cullingEnabled);
            if (!this.snapshotJson && !this.contentUrl) this.scene.SetSplitView(this.session, this.splitView);
            if (this.contentUrl) {
                this.setState('loading-content', 'Loading the cooked world essentials…');
                const loader = await BrowserContentLoader.open(this.contentUrl,
                    { features: Array.from(renderer.device.features) }, controller.signal,
                    progress => {
                        if (epoch !== this.epoch || controller.signal.aborted) return;
                        this.contentProgress = progress;
                        this.onContentProgress?.(progress);
                    });
                if (epoch !== this.epoch || controller.signal.aborted) return;
                this.contentLoader = loader;
                this.services.configure(loader.manifest.services);
                if (!this.services.ready) {
                    this.setState('waiting-services', 'This world requires sound. Choose Enable sound to continue.');
                    await this.services.waitReady(controller.signal);
                    if (epoch !== this.epoch || controller.signal.aborted) return;
                }
                const session = this.session;
                const consume = (asset, variant, bytes) => {
                    if (epoch !== this.epoch || controller.signal.aborted || session !== this.session)
                        throw new DOMException('Content session was replaced.', 'AbortError');
                    const metadata = asset.kind === 'texture' ? JSON.stringify({
                        width: variant.width, height: variant.height, format: variant.format,
                        mipByteLengths: variant.mipByteLengths, normalConvention: variant.normalConvention,
                        alphaMode: variant.alphaMode
                    }) : '{}';
                    this.scene.UploadCookedAsset(session, asset.id, asset.kind, metadata, bytes);
                };
                await loader.consumeEssential(consume);
                if (epoch !== this.epoch || controller.signal.aborted) return;
                this.graphicsReady = true;
                this.installEvents(controller.signal);
                this.syncSurface();
                // Streaming has the same owner and cancellation lifetime as frame submission.
                void loader.consumeStreamed(consume).catch(error => {
                    if (epoch === this.epoch && !controller.signal.aborted) this.fail(error);
                });
                return;
            }
            this.graphicsReady = true;
            this.installEvents(controller.signal);
            this.syncSurface();
        } catch (error) {
            if (epoch === this.epoch && !controller.signal.aborted) this.fail(error);
        }
    }

    installEvents(signal) {
        const epoch = this.epoch;
        const refresh = () => {
            if (signal.aborted || epoch !== this.epoch) return;
            try {
                this.canvas.style.setProperty('--browser-viewport-height', `${window.visualViewport?.height ?? window.innerHeight}px`);
                this.syncSurface();
            } catch (error) { this.fail(error); }
        };
        this.canvas.style.setProperty('--browser-viewport-height', `${window.visualViewport?.height ?? window.innerHeight}px`);
        this.resizeObserver = new ResizeObserver(refresh);
        this.resizeObserver.observe(this.canvas);
        this.attachmentObserver = new MutationObserver(() => {
            if (this.canvas.isConnected !== this.attached) refresh();
        });
        this.attachmentObserver.observe(document.documentElement, { childList: true, subtree: true });
        window.addEventListener('resize', refresh, { signal });
        window.addEventListener('orientationchange', refresh, { signal });
        window.visualViewport?.addEventListener('resize', refresh, { signal });
        window.visualViewport?.addEventListener('scroll', refresh, { signal });
        document.addEventListener('visibilitychange', () => {
            this.clearInput();
            refresh();
        }, { signal });
        document.addEventListener('freeze', () => {
            this.frozen = true;
            this.clearInput();
            refresh();
        }, { signal });
        document.addEventListener('resume', () => {
            this.frozen = false;
            if (this.session) this.scene.ResetClock(this.session);
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
        this.input.install(signal);
    }

    clearInput() {
        this.input.clear();
    }

    syncSurface() {
        if (!this.session || !this.renderer || !this.graphicsReady) return;
        const bounds = this.canvas.getBoundingClientRect();
        this.attached = this.canvas.isConnected;
        this.rawPixelRatio = window.devicePixelRatio || 1;
        let ratio = Math.min(this.rawPixelRatio, this.maxPixelRatio) * this.resolutionScale;
        const logicalWidth = Math.max(0, bounds.width);
        const logicalHeight = Math.max(0, bounds.height);
        const largest = Math.max(logicalWidth, logicalHeight);
        const maximum = Math.min(this.maxBackingDimension, this.renderer.maxDimension);
        if (largest > 0) ratio = Math.min(ratio, maximum / largest);
        const width = this.attached && logicalWidth > 0 && logicalHeight > 0
            ? Math.max(1, Math.min(maximum, Math.round(logicalWidth * ratio))) : 0;
        const height = width > 0
            ? Math.max(1, Math.min(maximum, Math.round(logicalHeight * ratio))) : 0;
        this.renderer.resize(width, height);
        const visible = !document.hidden && !this.frozen;
        const focused = document.hasFocus() && document.activeElement === this.canvas;
        this.scene.Resize(this.session, logicalWidth, logicalHeight, width, height,
            ratio, this.renderer.generation, visible, focused, this.attached);
        this.audio?.setVisible(visible);
        this.drawable = visible && this.attached && width > 0 && height > 0 && (this.services?.ready ?? true);
        if (this.drawable) {
            this.setState('running', this.contentUrl ? 'Cooked world ready. Additional content streams within frame budgets.' : this.snapshotJson
                ? 'Imported static scene ready. The captured camera projection fills the canvas.'
                : 'WebGPU ready. Left touch moves; right touch looks. Keyboard: WASD/arrows and Space.');
            if (!this.frameId) this.frameId = requestAnimationFrame(this.frame);
        } else {
            cancelAnimationFrame(this.frameId);
            this.frameId = 0;
            this.scene.ResetClock(this.session);
            this.clearInput();
            this.setState('suspended', this.services && !this.services.ready
                ? 'A required service is suspended. Enable sound to continue.'
                : 'Rendering paused while the canvas is hidden, detached, or has no size.');
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
            this.input.poll(timestamp);
            this.scene.Frame(this.session, timestamp);
            if (!this.frameId) this.frameId = requestAnimationFrame(this.frame);
        } catch (error) { this.fail(error); }
    }

    async loadSnapshot(file) {
        if (this.disposed) throw new Error('The canvas host is disposed.');
        if (file.size > 16 * 1024 * 1024) throw new Error('Scene snapshots are limited to 16 MiB.');
        const epoch = this.epoch;
        const json = await file.text();
        if (epoch !== this.epoch || this.disposed) return;
        // Parse and reject malformed packages before stopping the current scene.
        this.scene.ValidateSnapshot(json);
        this.contentUrl = null;
        this.snapshotJson = json;
        await this.start();
    }

    async loadCookedWorld(url) {
        if (this.disposed) throw new Error('The canvas host is disposed.');
        // The loader enforces origin, protocol, credentials and payload path policy.
        this.contentUrl = url;
        this.snapshotJson = null;
        await this.start();
    }

    enableAudio() {
        const audio = this.audio;
        if (!audio) return Promise.resolve(false);
        const epoch = this.epoch;
        // Invoke resume immediately while the trusted button gesture is still active.
        return audio.unlock().then(ready => {
            if (epoch !== this.epoch || this.audio !== audio) return false;
            if (ready && !this.contentUrl && !this.snapshotJson)
                audio.play('engine-demo-step', { gain: 0.2, loop: false, position: [0, 0, -2] });
            this.services?.changed();
            return ready;
        });
    }

    setSceneLabel(label) {
        if (!this.session || !this.graphicsReady) throw new Error('Load a scene before naming it.');
        this.scene.SetSceneLabel(this.session, label);
    }

    async playAudioFile(file, loop) {
        const audio = this.audio, epoch = this.epoch;
        if (!audio || audio.state !== 'ready') throw new Error('Enable sound before loading an audio clip.');
        if (file.size <= 0 || file.size > 8 * 1024 * 1024) throw new Error('Audio files must fit within 8 MiB.');
        const signal = this.controller.signal;
        const bytes = new Uint8Array(await file.arrayBuffer());
        signal.throwIfAborted();
        if (epoch !== this.epoch) return;
        audio.unload('user-clip');
        await audio.load('user-clip', bytes, signal);
        signal.throwIfAborted();
        if (epoch !== this.epoch) return;
        audio.play('user-clip', { loop: Boolean(loop), gain: 1, position: [0, 0, -2] });
    }

    setSplitView(enabled) {
        this.splitView = enabled;
        try {
            if (this.session && !this.snapshotJson && !this.contentUrl) this.scene.SetSplitView(this.session, enabled);
        } catch (error) { this.fail(error); }
    }

    setQualityPreset(preset) {
        if (!['low', 'balanced', 'high'].includes(preset)) throw new Error('Unknown browser quality preset.');
        if (this.session) this.scene.SetQualityPreset(this.session, preset);
        this.qualityPreset = preset;
        this.maxPixelRatio = preset === 'low' ? 1 : preset === 'high' ? 2 : 1.5;
        this.resolutionScale = preset === 'low' ? 0.75 : 1;
        this.maxBackingDimension = preset === 'low' ? 1024 : preset === 'high' ? 1920 : 1280;
        if (this.session && this.renderer) this.syncSurface();
    }

    setUiEnabled(enabled) {
        this.uiEnabled = Boolean(enabled);
        if (this.session) this.scene.SetUiEnabled(this.session, this.uiEnabled);
    }

    setInstanceCount(count) {
        if (!Number.isInteger(count) || count < 1 || count > 256)
            throw new Error('Instance count must be between 1 and 256.');
        this.instanceCount = count;
        try {
            if (this.session && !this.snapshotJson && !this.contentUrl) this.scene.SetInstanceCount(this.session, count);
        } catch (error) { this.fail(error); }
    }

    getStatistics() {
        // Diagnostic snapshots are explicit UI actions, never animation-frame work.
        if (!this.renderer || !this.session) return null;
        return { ...this.renderer.getStatistics(), capabilities: this.renderer.getCapabilities(),
            scene: JSON.parse(this.scene.GetStatistics(this.session)),
            content: this.contentLoader?.getStatistics() ?? null,
            contentResources: this.contentUrl ? JSON.parse(this.scene.GetCookedContentStatistics(this.session)) : null,
            services: this.services?.getStatistics() ?? null, audio: this.audio?.getStatistics() ?? null };
    }

    streamDemoTexture() {
        if (this.session && !this.snapshotJson && !this.contentUrl) this.scene.StreamDemoTexture(this.session);
    }

    setCullingEnabled(enabled) {
        this.cullingEnabled = enabled;
        if (this.session) this.scene.SetCullingEnabled(this.session, enabled);
    }

    recolorFirstMesh() {
        if (!this.session) return;
        this.scene.SetRenderableTint(this.session, 0,
            this.recolorAlternate ? 0.65 : 1, this.recolorAlternate ? 0.83 : 0.25,
            this.recolorAlternate ? 1 : 0.15);
        this.recolorAlternate = !this.recolorAlternate;
    }

    fail(error) {
        if (this.session) {
            try { this.scene.RendererFailed(this.session, this.renderer?.isDeviceLost ?? false); }
            catch (stateError) { console.error(stateError); }
        }
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
        this.graphicsReady = false;
        this.services?.dispose();
        this.services = null;
        this.audio?.dispose();
        this.audio = null;
        this.onAudioState?.('closed', 'Sound is stopped. Restart the scene, then enable sound.');
        this.contentLoader?.dispose();
        this.contentLoader = null;
        this.contentProgress = null;
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
            if (canvasOwners.get(this.canvas) === this) canvasOwners.delete(this.canvas);
        }
        this.setState('stopped', 'Stopped. Restart to create a new scene and WebGPU device.');
    }

    dispose() {
        if (this.disposed) return;
        this.stop();
        this.disposed = true;
    }
}
