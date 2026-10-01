import { WebGpuCanvasRenderer } from '../webgpu/webgpu-renderer.js';

/** Isolated renderer/component qualification; this host never starts a production world or physics backend. */
export class EngineMeshDiagnosticHost {
    constructor(exports, renderers, canvas, onState) {
        this.exports = exports;
        this.renderers = renderers;
        this.canvas = canvas;
        this.onState = onState;
        this.session = 0;
        this.request = 0;
        this.frame = this.frame.bind(this);
    }

    async start(manifestUrl) {
        this.stop();
        const controller = new AbortController();
        this.controller = controller;
        const manifestResponse = await fetch(manifestUrl, { signal: controller.signal });
        if (!manifestResponse.ok) throw new Error(`Diagnostic shader manifest failed: ${manifestResponse.status}`);
        const manifest = await manifestResponse.json();
        const selected = manifest.artifacts?.find(artifact => artifact.name === 'engine-depth-probe');
        if (manifest.schemaVersion !== 3 || !selected) throw new Error('Select a schema 3 manifest containing engine-depth-probe.');
        const descriptorUrl = new URL(selected.descriptor, manifestResponse.url);
        const descriptorResponse = await fetch(descriptorUrl, { signal: controller.signal });
        if (!descriptorResponse.ok) throw new Error(`Diagnostic descriptor failed: ${descriptorResponse.status}`);
        const descriptorJson = await descriptorResponse.text();
        const descriptor = JSON.parse(descriptorJson);
        const sourceResponse = await fetch(new URL(descriptor.source.url, descriptorUrl), { signal: controller.signal });
        if (!sourceResponse.ok) throw new Error(`Diagnostic WGSL failed: ${sourceResponse.status}`);
        const source = await sourceResponse.text();
        if (controller.signal.aborted) return;
        this.session = this.exports.Create(this.canvas.id, descriptorJson, source);
        const renderer = new WebGpuCanvasRenderer(this.canvas,
            stage => this.onState(stage), error => this.fail(error), 'browser-unlit', 'CpuDirect', 'Cpu');
        this.renderer = renderer;
        try {
            await renderer.initialize(controller.signal);
            if (controller.signal.aborted) { renderer.dispose(); return; }
            renderer.setOwner(this.session);
            this.renderers.set(this.session, renderer);
            const generation = renderer.resize(512, 512);
            this.exports.InitializeGraphics(this.session, renderer.colorFormat, 512, 512, generation);
            this.startedAt = performance.now();
            this.onState('Preparing real engine mesh resources');
            this.request = requestAnimationFrame(this.frame);
        } catch (error) { this.fail(error); throw error; }
    }

    frame() {
        this.request = 0;
        if (!this.session) return;
        try {
            const ready = this.exports.Frame(this.session);
            if (ready) this.onState('Engine mesh depth diagnostic rendered; RuntimeWorld play and physics are unverified');
            else if (performance.now() - this.startedAt > 45000)
                throw new Error('Engine mesh depth diagnostic did not submit all three expected mesh draws within 45 seconds.');
            this.request = requestAnimationFrame(this.frame);
        } catch (error) { this.fail(error); }
    }

    statistics() { return this.renderer?.getStatistics() ?? null; }

    fail(error) {
        this.onState(`Failed: ${error.message ?? error}`);
        this.stop();
    }

    stop() {
        this.controller?.abort();
        if (this.request) cancelAnimationFrame(this.request);
        this.request = 0;
        const session = this.session;
        this.session = 0;
        try { if (session) this.exports.Stop(session); }
        finally {
            this.renderer?.dispose();
            this.renderers.delete(session);
            this.renderer = undefined;
        }
    }
}
