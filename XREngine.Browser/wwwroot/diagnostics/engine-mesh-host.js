import { WebGpuCanvasRenderer } from '../webgpu/webgpu-renderer.js';

/** Isolated renderer/component qualification; this host never starts a production world or physics backend. */
export class EngineMeshDiagnosticHost {
    constructor(exports, renderers, canvas, onState) {
        this.exports = exports;
        this.renderers = renderers;
        this.canvas = canvas;
        this.onState = onState;
        this.session = 0;
        this.epoch = 0;
        this.creation = null;
        this.pendingStop = Promise.resolve();
        this.request = 0;
        this.stage = 'idle';
        this.failure = null;
        this.frame = this.frame.bind(this);
    }

    async start(manifestUrl, assetManifestUrl, artifactName = 'engine-depth-probe') {
        if (!['engine-depth-probe', 'engine-texture-probe'].includes(artifactName))
            throw new Error('Select an admitted engine raster diagnostic artifact.');
        const epoch = ++this.epoch;
        await this.stop(false);
        if (epoch !== this.epoch) return;
        this.failure = null;
        this.kind = artifactName === 'engine-texture-probe' ? 'texture' : 'depth';
        this.readyMessage = `Engine mesh ${this.kind} diagnostic rendered; RuntimeWorld play and physics are unverified`;
        this.stage = 'loading-shader-artifact';
        if (!assetManifestUrl) throw new Error('Engine mesh diagnostics require a cooked engine asset manifest.');
        const controller = new AbortController();
        this.controller = controller;
        try {
            const manifestResponse = await fetch(manifestUrl, { signal: controller.signal });
            if (!manifestResponse.ok) throw new Error(`Diagnostic shader manifest failed: ${manifestResponse.status}`);
            const manifest = await manifestResponse.json();
            const selected = manifest.artifacts?.find(artifact => artifact.name === artifactName);
            if (manifest.schemaVersion !== 3 || !selected) throw new Error(`Select a schema 3 manifest containing ${artifactName}.`);
            const descriptorUrl = new URL(selected.descriptor, manifestResponse.url);
            const descriptorResponse = await fetch(descriptorUrl, { signal: controller.signal });
            if (!descriptorResponse.ok) throw new Error(`Diagnostic descriptor failed: ${descriptorResponse.status}`);
            const descriptorJson = await descriptorResponse.text();
            const descriptor = JSON.parse(descriptorJson);
            const sourceResponse = await fetch(new URL(descriptor.source.url, descriptorUrl), { signal: controller.signal });
            if (!sourceResponse.ok) throw new Error(`Diagnostic WGSL failed: ${sourceResponse.status}`);
            const source = await sourceResponse.text();
            if (controller.signal.aborted || epoch !== this.epoch) return;
            this.stage = 'creating-engine-session';
            const creation = this.exports.CreateAsync(this.canvas.id, String(assetManifestUrl), descriptorJson, source);
            this.creation = creation;
            let session;
            try { session = await creation; }
            finally { if (this.creation === creation) this.creation = null; }
            if (controller.signal.aborted || epoch !== this.epoch) { this.exports.Stop(session); return; }
            this.session = session;
            const renderer = new WebGpuCanvasRenderer(this.canvas,
                stage => { if (epoch === this.epoch) this.onState(stage); },
                error => { if (epoch === this.epoch) this.fail(error); },
                'browser-unlit', 'CpuDirect', 'Cpu');
            this.renderer = renderer;
            try {
                this.stage = 'initializing-device';
                await renderer.initializeEngine(controller.signal);
                if (controller.signal.aborted || epoch !== this.epoch) { renderer.dispose(); return; }
                renderer.setOwner(this.session);
                this.renderers.set(this.session, renderer);
                this.stage = 'resizing-canvas';
                const generation = renderer.resize(512, 512);
                this.stage = 'initializing-engine-graphics';
                this.exports.InitializeGraphics(this.session, renderer.colorFormat, 512, 512, generation);
                this.startedAt = performance.now();
                this.onState('Preparing real engine mesh resources');
                this.request = requestAnimationFrame(this.frame);
            } catch (error) {
                if (epoch === this.epoch && !controller.signal.aborted) this.fail(error);
                throw error;
            }
        } catch (error) {
            if (controller.signal.aborted || epoch !== this.epoch) return;
            this.fail(error);
            throw error;
        }
    }

    frame() {
        this.request = 0;
        if (!this.session) return;
        try {
            this.stage = 'engine-frame';
            const ready = this.exports.Frame(this.session);
            this.stage = 'waiting-for-next-frame';
            if (ready) this.onState(this.readyMessage);
            else if (performance.now() - this.startedAt > 45000)
                throw new Error(`Engine mesh ${this.kind} diagnostic did not submit all three expected mesh draws within 45 seconds. ${this.exports.GetFrameStatus(this.session)}`);
            this.request = requestAnimationFrame(this.frame);
        } catch (error) { this.fail(error); }
    }

    statistics() { return this.renderer?.getStatistics() ?? null; }

    setTextureCase(sampleCase) {
        if (!this.session || this.kind !== 'texture') throw new Error('An active engine texture diagnostic is required.');
        this.exports.SetTextureCase(this.session, sampleCase);
        this.startedAt = performance.now();
        this.onState('Preparing replacement engine texture resources');
    }

    fail(error) {
        // Capture before Stop removes the renderer and managed fixture; cleanup and
        // delayed device-loss callbacks must not replace the original failure.
        if (this.failure) return;
        let frameStatus = null;
        if (this.session) {
            try { frameStatus = String(this.exports.GetFrameStatus(this.session)).slice(0, 2048); }
            catch (statusError) { frameStatus = `Unavailable: ${statusError?.message ?? statusError}`.slice(0, 2048); }
        }
        const renderer = this.renderer?.getFailureDiagnostics() ?? null;
        const message = String(error?.message ?? error).slice(0, 2048);
        this.failure = { message, stack: String(error?.stack ?? '').slice(0, 4096),
            stage: this.stage, session: this.session, frameStatus, renderer };
        console.error('Engine mesh diagnostic failed:', JSON.stringify({ ...this.failure, renderer: undefined }));
        if (renderer) {
            // Separate bounded records remain readable in browser console artifacts.
            console.error('WebGPU first error:', JSON.stringify(renderer.firstError));
            console.error('WebGPU device loss:', JSON.stringify(renderer.deviceLoss));
            console.error('WebGPU device destruction:', JSON.stringify(renderer.deviceDestroy));
            console.error('WebGPU failure state:', JSON.stringify({ ...renderer, firstError: undefined, deviceLoss: undefined, deviceDestroy: undefined }));
        }
        this.onState(`Failed: ${message}`);
        try {
            void this.stop().catch(cleanup => this.onState(`Failed: ${message}; cleanup failed: ${String(cleanup?.message ?? cleanup).slice(0, 2048)}`));
        } catch (cleanup) {
            this.onState(`Failed: ${message}; cleanup failed: ${String(cleanup?.message ?? cleanup).slice(0, 2048)}`);
        }
    }

    stop(supersede = true) {
        if (supersede) this.epoch++;
        this.controller?.abort();
        this.exports.CancelPendingCreate();
        if (this.request) cancelAnimationFrame(this.request);
        this.request = 0;
        const session = this.session;
        this.session = 0;
        const creation = this.creation;
        this.creation = null;
        try { if (session) this.exports.Stop(session); }
        finally {
            this.renderer?.dispose();
            this.renderers.delete(session);
            this.renderer = undefined;
        }
        if (creation) {
            const cleanup = creation.then(created => this.exports.Stop(created), () => {});
            this.pendingStop = Promise.allSettled([this.pendingStop, cleanup]).then(results => {
                const failure = results.find(result => result.status === 'rejected');
                if (failure) throw failure.reason;
            });
        }
        return this.pendingStop;
    }
}
