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
        this.partialSubmissions = 0;
        this.frame = this.frame.bind(this);
    }

    async start(manifestUrl, assetManifestUrl, artifactName = 'engine-depth-probe') {
        if (!['engine-depth-probe', 'engine-texture-probe', 'engine-standard-lit-color',
            'engine-standard-lit-color-directional-shadow'].includes(artifactName))
            throw new Error('Select an admitted engine raster diagnostic artifact.');
        const epoch = ++this.epoch;
        await this.stop(false);
        if (epoch !== this.epoch) return;
        this.failure = null;
        this.partialSubmissions = 0;
        this.kind = artifactName === 'engine-standard-lit-color-directional-shadow' ? 'shadow' :
            artifactName === 'engine-standard-lit-color' ? 'lit' : artifactName === 'engine-texture-probe' ? 'texture' : 'depth';
        this.readyMessage = `Engine mesh ${this.kind} diagnostic rendered; RuntimeWorld play and physics are unverified`;
        this.stage = 'loading-shader-artifact';
        if (!assetManifestUrl) throw new Error('Engine mesh diagnostics require a cooked engine asset manifest.');
        const controller = new AbortController();
        this.controller = controller;
        try {
            const manifestResponse = await fetch(manifestUrl, { signal: controller.signal });
            if (!manifestResponse.ok) throw new Error(`Diagnostic shader manifest failed: ${manifestResponse.status}`);
            const manifest = await manifestResponse.json();
            const loadArtifact = async name => {
                const selected = manifest.artifacts?.find(artifact => artifact.name === name);
                if (manifest.schemaVersion !== 3 || !selected) throw new Error(`Select a schema 3 manifest containing ${name}.`);
                const descriptorUrl = new URL(selected.descriptor, manifestResponse.url);
                const descriptorResponse = await fetch(descriptorUrl, { signal: controller.signal });
                if (!descriptorResponse.ok) throw new Error(`Diagnostic descriptor failed: ${descriptorResponse.status}`);
                const descriptorJson = await descriptorResponse.text();
                const descriptor = JSON.parse(descriptorJson);
                const sourceResponse = await fetch(new URL(descriptor.source.url, descriptorUrl), { signal: controller.signal });
                if (!sourceResponse.ok) throw new Error(`Diagnostic WGSL failed: ${sourceResponse.status}`);
                return { descriptorJson, source: await sourceResponse.text() };
            };
            const artifact = await loadArtifact(artifactName);
            const tonemap = ['lit', 'shadow'].includes(this.kind) ? await loadArtifact('engine-tonemap') : null;
            const shadowDepth = this.kind === 'shadow' ? await loadArtifact('engine-shadow-depth') : null;
            if (controller.signal.aborted || epoch !== this.epoch) return;
            this.stage = 'creating-engine-session';
            const creation = shadowDepth
                ? this.exports.CreateShadowAsync(this.canvas.id, String(assetManifestUrl), artifact.descriptorJson, artifact.source,
                    tonemap.descriptorJson, tonemap.source, shadowDepth.descriptorJson, shadowDepth.source)
                : tonemap ? this.exports.CreateLitAsync(this.canvas.id, String(assetManifestUrl), artifact.descriptorJson, artifact.source,
                    tonemap.descriptorJson, tonemap.source)
                : this.exports.CreateAsync(this.canvas.id, String(assetManifestUrl), artifact.descriptorJson, artifact.source);
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
            const submissionsBefore = this.kind === 'shadow' ? this.statistics()?.frameSubmitCalls ?? 0 : 0;
            const ready = this.exports.Frame(this.session);
            if (this.kind === 'shadow' && !ready && (this.statistics()?.frameSubmitCalls ?? 0) > submissionsBefore)
                this.partialSubmissions++;
            this.stage = 'waiting-for-next-frame';
            if (ready) this.onState(this.readyMessage);
            else if (performance.now() - this.startedAt > 45000)
                throw new Error(`Engine mesh ${this.kind} diagnostic did not submit its expected engine draws within 45 seconds. ${this.exports.GetFrameStatus(this.session)}`);
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

    setLitCase(sampleCase) {
        if (!this.session || this.kind !== 'lit') throw new Error('An active engine lit diagnostic is required.');
        this.exports.SetLitCase(this.session, sampleCase);
        this.startedAt = performance.now();
        this.onState('Preparing changed engine surface and light values');
    }

    setShadowCase(sampleCase) {
        if (!this.session || this.kind !== 'shadow') throw new Error('An active engine shadow diagnostic is required.');
        this.exports.SetShadowCase(this.session, sampleCase);
        this.startedAt = performance.now();
        this.onState('Preparing changed engine shadow scene');
    }

    setShadowMapSize(size) {
        if (!this.session || this.kind !== 'shadow') throw new Error('An active engine shadow diagnostic is required.');
        this.exports.SetShadowMapSize(this.session, size);
        this.startedAt = performance.now();
        this.onState('Preparing resized engine shadow map');
    }

    shadowState() {
        if (!this.session || this.kind !== 'shadow') throw new Error('An active engine shadow diagnostic is required.');
        const shaders = [], pipelines = [], hdrTargets = [], depthTargets = [];
        this.renderer._resources.slots.forEach((entry, slot) => {
            if (entry?.owner !== this.session) return;
            const identity = { slot, generation: entry.generation, label: entry.value.label ?? '' };
            if (entry.kind === 'shader') shaders.push(identity);
            if (entry.kind === 'render-pipeline') pipelines.push(identity);
            if (entry.kind === 'texture' && entry.value.format === 'rgba16float')
                hdrTargets.push({ ...identity, width: entry.value.width, height: entry.value.height });
            if (entry.kind === 'texture' && entry.value.format === 'depth24plus')
                depthTargets.push({ ...identity, width: entry.value.width, height: entry.value.height });
        });
        return { ...JSON.parse(this.exports.GetShadowState(this.session)), shaders, pipelines, hdrTargets, depthTargets };
    }

    litState() {
        if (!this.session || this.kind !== 'lit') throw new Error('An active engine lit diagnostic is required.');
        const shaders = [], pipelines = [], hdrTargets = [];
        this.renderer._resources.slots.forEach((entry, slot) => {
            if (entry?.owner !== this.session) return;
            const identity = { slot, generation: entry.generation, label: entry.value.label ?? '' };
            if (entry.kind === 'shader') shaders.push(identity);
            if (entry.kind === 'render-pipeline') pipelines.push(identity);
            if (entry.kind === 'texture' && entry.value.format === 'rgba16float')
                hdrTargets.push({ ...identity, width: entry.value.width, height: entry.value.height });
        });
        return { ...JSON.parse(this.exports.GetLitState(this.session)), shaders, pipelines, hdrTargets };
    }

    resize(width, height) {
        if (!this.session || !this.renderer) throw new Error('An active engine diagnostic is required.');
        if (![width, height].every(value => Number.isInteger(value) && value >= 64 && value <= 1024))
            throw new Error('Diagnostic resize dimensions must be integers from 64 through 1024.');
        this.canvas.style.width = `${width}px`;
        this.canvas.style.height = `${height}px`;
        const generation = this.renderer.resize(width, height);
        this.exports.ResizeGraphics(this.session, width, height, generation);
        this.startedAt = performance.now();
        this.onState('Preparing resized engine render resources');
    }

    /** Explicit diagnostic-only readback of the actual engine-owned HDR target, including opaque material alpha. */
    async readHdrCenter() {
        if (!this.session || this.kind !== 'lit') throw new Error('An active engine lit diagnostic is required.');
        return this.readHdrAt(0.5, 0.5);
    }

    /** Reads a five-pixel square at normalized canvas coordinates from the engine-owned HDR texture. */
    async readHdrAt(u, v) {
        if (!this.session || !['lit', 'shadow'].includes(this.kind))
            throw new Error('An active engine HDR diagnostic is required.');
        if (![u, v].every(value => Number.isFinite(value) && value >= 0.02 && value <= 0.98))
            throw new Error('HDR sample coordinates must lie inside the render target.');
        const renderer = this.renderer;
        const matches = renderer._resources.slots.filter(entry => entry?.owner === this.session &&
            entry.kind === 'texture' && entry.value.format === 'rgba16float');
        if (matches.length !== 1) throw new Error(`Expected one engine HDR target; found ${matches.length}.`);
        const target = matches[0].value;
        if (!(target.usage & GPUTextureUsage.COPY_SRC)) throw new Error('The engine HDR texture does not permit diagnostic readback.');
        const buffer = renderer.device.createBuffer({ label: 'Engine HDR diagnostic readback', size: 256 * 5,
            usage: GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ });
        let deadline;
        try {
            const encoder = renderer.device.createCommandEncoder({ label: 'Engine HDR diagnostic copy' });
            encoder.copyTextureToBuffer({ texture: target.texture,
                origin: [Math.floor(target.width * u) - 2, Math.floor(target.height * v) - 2, 0] },
                { buffer, bytesPerRow: 256, rowsPerImage: 5 }, [5, 5, 1]);
            renderer.device.queue.submit([encoder.finish()]);
            await Promise.race([buffer.mapAsync(GPUMapMode.READ), new Promise((_, reject) => {
                deadline = setTimeout(() => reject(new Error('Engine HDR diagnostic readback exceeded 15 seconds.')), 15000);
            })]);
            const view = new DataView(buffer.getMappedRange());
            const decode = bits => {
                const sign = bits & 0x8000 ? -1 : 1, exponent = (bits >> 10) & 31, fraction = bits & 1023;
                return sign * (exponent === 0 ? fraction * 2 ** -24 : exponent === 31
                    ? (fraction ? NaN : Infinity) : (1 + fraction / 1024) * 2 ** (exponent - 15));
            };
            const min = [Infinity, Infinity, Infinity, Infinity], max = [-Infinity, -Infinity, -Infinity, -Infinity], sum = [0, 0, 0, 0];
            for (let y = 0; y < 5; y++) for (let x = 0; x < 5; x++) for (let channel = 0; channel < 4; channel++) {
                const value = decode(view.getUint16(y * 256 + x * 8 + channel * 2, true));
                if (!Number.isFinite(value)) throw new Error('The engine HDR target contains a non-finite sample.');
                min[channel] = Math.min(min[channel], value);
                max[channel] = Math.max(max[channel], value);
                sum[channel] += value;
            }
            return { label: target.label, format: target.format, width: target.width, height: target.height,
                min, max, average: sum.map(value => value / 25) };
        } finally {
            clearTimeout(deadline);
            if (buffer.mapState === 'mapped') buffer.unmap();
            buffer.destroy();
        }
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
