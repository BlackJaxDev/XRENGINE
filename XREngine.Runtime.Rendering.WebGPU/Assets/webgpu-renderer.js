import { GpuResourceTable } from './gpu-resource-table.js';
import { captureDeviceCapabilities } from './gpu-capabilities.js';
import { GpuResources } from './gpu-resources.js';
import { GpuPipelineCache } from './gpu-pipeline-cache.js';
import { GpuReadback } from './gpu-readback.js';
import { GpuLuminance } from './gpu-luminance.js';
import { GpuCommands } from './gpu-commands.js';
import { BrowserRenderPipeline } from './browser-render-pipeline.js';
import { browserPipelineRequirements } from './browser-pipeline-requirements.js';
import { selectBrowserSubmissionStrategy } from './browser-submission-strategy.js';
import { GpuSkinning } from './gpu-skinning.js';
import { createCookedTexture, isBrowserColorTexture } from './cooked-texture.js';
import { drawRecordBytes, maximumDraws, packetHeaderBytes, validateFramePacket } from '../frame-packet.js';
import { maximumUploadPayloadBytes, uploadHeaderBytes, uploadRecordBytes, maximumUploadCommands, validateUploadPacket } from '../upload-packet.js';
import { formatShaderDiagnostic, loadBrowserUnlitArtifact, shaderDeviceRequirements } from './shader-artifact.js';

const maximumUploadBytes = 64 * 1024 * 1024;
const maximumTextureBytes = 64 * 1024 * 1024;
const whitePixel = new Uint8Array([255, 255, 255, 255]);
const shaderCompilationBudgetMs = 45_000;
const pipelineCreationBudgetMs = 45_000;

function asError(value) {
    return value instanceof Error ? value : new Error(value?.message ?? String(value));
}

function byteLengthOf(memory) {
    const length = memory?.byteLength;
    if (!Number.isSafeInteger(length) || length < 0)
        throw new TypeError('A byte-oriented .NET memory view is required.');
    return length;
}

function copyMemory(memory, length) {
    const bytes = new Uint8Array(length);
    memory.copyTo(bytes);
    return bytes;
}

function startupAbortError() {
    return new DOMException('WebGPU initialization was canceled.', 'AbortError');
}

// The WebGPU promise is not cancellable; the deadline stops waiting and its late
// result remains private to the abandoned promise rather than reaching the renderer.
async function awaitStartupStage(operation, budgetMs, stage, signal, stopSignal, startedAt) {
    const deadline = startedAt + budgetMs;
    // Observe even an operation abandoned before Promise.race is installed.
    operation.catch(() => {});
    let timer;
    let rejectWait;
    const interrupted = new Promise((_, reject) => { rejectWait = reject; });
    const abort = () => rejectWait(startupAbortError());
    signal?.addEventListener('abort', abort, { once: true });
    stopSignal.addEventListener('abort', abort, { once: true });
    try {
        if (signal?.aborted || stopSignal.aborted) throw startupAbortError();
        const remaining = deadline - performance.now();
        if (remaining <= 0) throw new Error(`WebGPU ${stage} exceeded its ${budgetMs} ms startup budget.`);
        timer = setTimeout(() => rejectWait(new Error(`WebGPU ${stage} exceeded its ${budgetMs} ms startup budget.`)), remaining);
        const result = await Promise.race([operation, interrupted]);
        if (performance.now() > deadline)
            throw new Error(`WebGPU ${stage} exceeded its ${budgetMs} ms startup budget.`);
        return result;
    } finally {
        clearTimeout(timer);
        signal?.removeEventListener('abort', abort);
        stopSignal.removeEventListener('abort', abort);
    }
}

// Pop both scopes immediately after submitting the GPU operation. Their promises
// are observed even when the outer startup deadline expires before they settle.
async function scopedStartupOperation(device, stage, operation) {
    device.pushErrorScope('out-of-memory');
    device.pushErrorScope('validation');
    let pending;
    let validation;
    let outOfMemory;
    try {
        try { pending = Promise.resolve(operation()); }
        catch (error) { pending = Promise.reject(error); }
    } finally {
        try { validation = device.popErrorScope(); }
        finally { outOfMemory = device.popErrorScope(); }
    }
    const [result, validationResult, memoryResult] = await Promise.allSettled([pending, validation, outOfMemory]);
    for (const scoped of [validationResult, memoryResult]) {
        if (scoped.status === 'rejected') throw asError(scoped.reason);
        if (scoped.value) throw new Error(`WebGPU ${stage}: ${scoped.value.message}`);
    }
    if (result.status === 'rejected') throw asError(result.reason);
    return result.value;
}

/** One device, one canvas surface, and one generation-stamped resource namespace. */
export class WebGpuCanvasRenderer {
    constructor(canvas, onState, onFailure, shaderName = 'browser-unlit', submissionStrategy = 'Auto', skinningMode = 'Cpu') {
        if (typeof shaderName !== 'string' || !/^[a-z][a-z0-9-]{0,63}$/.test(shaderName))
            throw new TypeError('Shader artifact name must be a lowercase manifest name.');
        this.canvas = canvas;
        this._shaderName = shaderName;
        this.submissionStrategy = selectBrowserSubmissionStrategy(submissionStrategy);
        if (!['Cpu', 'Compute'].includes(skinningMode)) throw new Error('Unsupported browser skinning mode.');
        this.skinningMode = skinningMode;
        this.skinning = new GpuSkinning(this);
        this.onState = onState;
        this.onFailure = onFailure;
        this._generation = 0;
        this._disposed = false;
        this._failed = false;
        this._deviceLost = false;
        this._configured = false;
        this._width = 0;
        this._height = 0;
        this._owner = 0;
        this._arenaGeneration = 0;
        this._frameSequence = 0;
        this._uploadCommandGeneration = 0;
        this._uploadPayloadGeneration = 0;
        this._uploadSequence = 0;
        this._executing = false;
        this._resources = new GpuResourceTable();
        this.resources = new GpuResources(this);
        this.readback = new GpuReadback(this);
        this.luminance = new GpuLuminance(this);
        this.commands = new GpuCommands(this);
        this.focusedPipeline = null;
        this._retired = new Set();
        this._packetBytes = new Uint8Array(packetHeaderBytes);
        this._packetView = new DataView(this._packetBytes.buffer);
        this._uploadCommands = new Uint8Array(uploadHeaderBytes);
        this._uploadCommandView = new DataView(this._uploadCommands.buffer);
        this._uploadPayload = new Uint8Array(0);
        this._uploadPayloadView = new DataView(this._uploadPayload.buffer);
        this._uploadOrigin = { x: 0, y: 0, z: 0 };
        this._uploadTextureDestination = { texture: undefined, origin: this._uploadOrigin };
        this._uploadTextureLayout = { offset: 0, bytesPerRow: 0, rowsPerImage: 0 };
        this._uploadExtent = { width: 0, height: 0, depthOrArrayLayers: 1 };
        this._uniformCapacity = 16;
        this._dynamicOffsets = [0];
        this._submission = [null];
        this._colorAttachment = { view: undefined, loadOp: 'clear', storeOp: 'store', clearValue: { r: 0, g: 0, b: 0, a: 1 } };
        this._depthAttachment = { view: undefined, depthClearValue: 1, depthLoadOp: 'clear', depthStoreOp: 'discard' };
        this._renderPassDescriptor = { colorAttachments: [this._colorAttachment], depthStencilAttachment: this._depthAttachment };
        this._stats = { packets: 0, draws: 0, copiedBytes: 0, gpuCopiedBytes: 0, uploadedBytes: 0, arenaGrowth: 0, rejectedPackets: 0, controlCalls: 0,
            uploadPackets: 0, uploadCommands: 0, uploadBytes: 0, uploadCopiedBytes: 0, uploadArenaGrowth: 0, rejectedUploads: 0,
            frameSubmitCalls: 0, uploadSubmitCalls: 0 };
        this._lastPacketFailure = null;
        this._firstError = null;
        this._deviceLoss = null;
        this._deviceDestroy = null;
        // Reuse one record on the frame path; snapshot it only when reporting a failure.
        this._operation = { stage: 'idle', label: '', commandIndex: -1, drawIndex: -1 };
        this._shaderArtifact = undefined;
        this._capabilities = undefined;
        this._engineOnly = false;
        this._ready = false;
        this._startupAbort = undefined;
        this._startupToken = undefined;
        this._startup = { stage: 'idle', budgetsMs: { shaderCompilation: shaderCompilationBudgetMs, pipelineCreation: pipelineCreationBudgetMs },
            timingsMs: { fetchHash: null, shaderCompilation: null, pipelineCreation: null, total: null } };
        this._onUncapturedError = event => this._fail(event.error);
    }

    get maxDimension() {
        return this.device?.limits.maxTextureDimension2D ?? 0;
    }

    get isDeviceLost() {
        return this._deviceLost;
    }

    get generation() {
        return this._generation;
    }

    get colorFormat() {
        return this.format;
    }

    _assertActive(signal, lateDevice) {
        if (signal?.aborted || this._disposed || this._failed || this._deviceLost) {
            if (lateDevice) this._destroyDevice(lateDevice, 'abandoned-initialization');
            if (signal?.aborted || (this._disposed && !this._failed))
                throw new DOMException('WebGPU initialization was canceled.', 'AbortError');
            throw new Error('WebGPU device is unavailable.');
        }
    }

    _assertStartup(signal, token, device) {
        this._assertActive(signal);
        if (this._startupToken !== token || this.device !== device)
            throw startupAbortError();
    }

    _setOperation(stage, label = '', commandIndex = -1, drawIndex = -1) {
        const operation = this._operation;
        operation.stage = stage;
        operation.label = label;
        operation.commandIndex = commandIndex;
        operation.drawIndex = drawIndex;
    }

    _recordError(reason, stage = this._operation.stage, label = this._operation.label) {
        if (this._firstError) return;
        const error = asError(reason);
        this._firstError = { name: String(reason?.name ?? error.name).slice(0, 128), message: String(error.message).slice(0, 2048),
            stack: error.stack?.slice(0, 4096) ?? '', startupStage: this._startup.stage,
            operation: { ...this._operation, stage, label }, owner: this._owner,
            generation: this._generation, frameSubmits: this._stats.frameSubmitCalls,
            explicitDestroyRequested: this._deviceDestroy !== null };
    }

    _destroyDevice(device, reason) {
        this._deviceDestroy ??= { reason, stack: new Error('WebGPU device destruction requested.').stack?.slice(0, 4096) ?? '',
            operation: { ...this._operation }, owner: this._owner,
            disposed: this._disposed, failed: this._failed, deviceLost: this._deviceLost };
        device.destroy();
    }

    _observeDevice(device) {
        device.addEventListener('uncapturederror', this._onUncapturedError);
        device.lost.then(info => {
            if (this._disposed || this.device !== device) return;
            this._deviceLoss = { reason: info.reason, message: String(info.message).slice(0, 2048),
                operation: { ...this._operation }, explicitDestroyRequested: this._deviceDestroy !== null };
            this._deviceLost = true;
            this._fail(new Error(`WebGPU device lost (${info.reason}): ${info.message}`));
        }, error => { if (!this._disposed && this.device === device) this._fail(error); });
    }

    getFailureDiagnostics() {
        return { firstError: this._firstError, deviceLoss: this._deviceLoss, deviceDestroy: this._deviceDestroy,
            lastOperation: { ...this._operation }, startupStage: this._startup.stage,
            owner: this._owner, generation: this._generation, frameSubmits: this._stats.frameSubmitCalls,
            draws: this._stats.draws, disposed: this._disposed, failed: this._failed };
    }

    _fail(reason) {
        if (this._disposed || this._failed) return;
        this._recordError(reason);
        this._failed = true;
        const error = asError(reason);
        try { this.dispose(); }
        catch (cleanupError) { console.error('WebGPU cleanup after renderer failure:', cleanupError); }
        // A synchronous import may still own managed spans. Notify the host only
        // after that stack unwinds; its captured epoch rejects a stopped/replaced host.
        queueMicrotask(() => {
            try { this.onFailure(error); } catch (callbackError) { console.error(callbackError); }
        });
    }

    _requireOwner() {
        if (!this.device || !this._ready || this._disposed || this._failed || !this._owner)
            throw new Error('WebGPU renderer has no active session owner.');
        if (this._executing) throw new Error('Resources cannot change during packet execution.');
    }

    setOwner(session) {
        this._stats.controlCalls++;
        if (!this._ready || this._disposed || this._owner || !Number.isInteger(session)
            || session <= 0 || session > 0x7fffffff)
            throw new Error('A ready renderer can be assigned one positive session owner.');
        this._owner = session;
    }

    _newUniformBuffer(capacity) {
        return this.device.createBuffer({
            size: this._uniformStride * capacity,
            usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
        });
    }

    _newViewBindGroup(buffer) {
        return this.device.createBindGroup({ layout: this._viewLayout, entries: [{
            binding: 0, resource: { buffer, size: 64 },
        }] });
    }

    async initialize(signal) {
        if (this._disposed || this._failed || this.device || this._initializing)
            throw new Error('WebGPU renderer cannot be initialized again.');
        this._initializing = true;
        this.focusedPipeline = new BrowserRenderPipeline(this);
        const startupToken = {};
        const startupStart = performance.now();
        this._startupToken = startupToken;
        this._startupAbort = new AbortController();
        try {
            this._assertActive(signal);
            if (!globalThis.isSecureContext || !navigator.gpu)
                throw new Error('WebGPU requires a secure context and navigator.gpu.');
            this.onState('requesting-adapter');
            const adapter = await navigator.gpu.requestAdapter();
            this._assertActive(signal);
            if (!adapter) throw new Error('No WebGPU adapter is available.');

            this.onState('loading-shader');
            this._startup.stage = 'fetch-hash';
            const fetchStart = performance.now();
            let artifact;
            try { artifact = await loadBrowserUnlitArtifact(signal, this._shaderName); }
            finally { this._startup.timingsMs.fetchHash = performance.now() - fetchStart; }
            this._assertActive(signal);
            const deviceRequirements = browserPipelineRequirements(adapter,
                shaderDeviceRequirements(adapter, artifact.descriptor, artifact.artifactIdentity), this.submissionStrategy, this.skinningMode);
            // Payload selection happens against enabled device features, never a user-agent guess.
            for (const feature of ['texture-compression-astc', 'texture-compression-etc2'])
                if (adapter.features.has(feature) && !deviceRequirements.requiredFeatures.includes(feature))
                    deviceRequirements.requiredFeatures.push(feature);
            this._shaderArtifact = {
                identity: artifact.artifactIdentity,
                requiredFeatures: [...deviceRequirements.requiredFeatures],
                requiredLimits: { ...deviceRequirements.requiredLimits },
            };
            this.onState('requesting-device');
            const device = await adapter.requestDevice(deviceRequirements);
            this._assertActive(signal, device);
            this.device = device;
            this.pipelineCache = new GpuPipelineCache(device);
            const capabilities = captureDeviceCapabilities(device, deviceRequirements);
            this._observeDevice(device);

            this.context = this.canvas.getContext('webgpu');
            if (!this.context) throw new Error('WebGPU canvas context is unavailable.');
            this.format = navigator.gpu.getPreferredCanvasFormat();
            const alignment = device.limits.minUniformBufferOffsetAlignment;
            this._uniformStride = Math.ceil(64 / alignment) * alignment;
            this._uniformBytes = new Uint8Array(this._uniformCapacity * this._uniformStride);
            this._uniformView = new DataView(this._uniformBytes.buffer);
            this.uniformBuffer = this._newUniformBuffer(this._uniformCapacity);
            this._viewLayout = this.pipelineCache.getBindGroupLayout({ entries: [{
                binding: 0, visibility: GPUShaderStage.VERTEX,
                buffer: { type: 'uniform', hasDynamicOffset: true, minBindingSize: 64 },
            }] });
            this.bindGroup = this._newViewBindGroup(this.uniformBuffer);
            this._materialLayout = this.pipelineCache.getBindGroupLayout({ entries: [
                { binding: 0, visibility: GPUShaderStage.FRAGMENT, buffer: { type: 'uniform', minBindingSize: 16 } },
                { binding: 1, visibility: GPUShaderStage.FRAGMENT, texture: { sampleType: 'float', viewDimension: '2d' } },
                { binding: 2, visibility: GPUShaderStage.FRAGMENT, sampler: { type: 'filtering' } },
            ] });
            this._sampler = device.createSampler({ minFilter: 'linear', magFilter: 'linear', mipmapFilter: 'linear', addressModeU: 'clamp-to-edge', addressModeV: 'clamp-to-edge' });
            this._whiteTexture = device.createTexture({ size: [1, 1], format: 'rgba8unorm-srgb', usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST });
            this._whiteView = this._whiteTexture.createView();
            device.queue.writeTexture({ texture: this._whiteTexture }, whitePixel, { bytesPerRow: 4 }, [1, 1]);

            this.onState('warming-pipeline');
            this._startup.stage = 'shader-compilation';
            const compilationStart = performance.now();
            const compilation = scopedStartupOperation(device, 'shader compilation', async () => {
                const shader = device.createShaderModule({ code: artifact.source });
                return { shader, diagnostics: await shader.getCompilationInfo() };
            });
            let compiled;
            try {
                compiled = await awaitStartupStage(compilation, shaderCompilationBudgetMs,
                    'shader compilation', signal, this._startupAbort.signal, compilationStart);
            } catch (error) {
                if (error?.name === 'AbortError') throw error;
                throw new Error(`Shader ${artifact.artifactIdentity} ${artifact.descriptor.source.path}: ${error.message ?? error}`, { cause: error });
            } finally { this._startup.timingsMs.shaderCompilation = performance.now() - compilationStart; }
            const { shader, diagnostics } = compiled;
            this._assertStartup(signal, startupToken, device);
            const errors = diagnostics.messages.filter(message => message.type === 'error');
            for (const message of diagnostics.messages) {
                if (message.type === 'warning')
                    console.warn(formatShaderDiagnostic(artifact.descriptor, artifact.artifactIdentity, message));
            }
            if (errors.length)
                throw new Error(`WGSL compilation failed: ${errors.map(message => formatShaderDiagnostic(artifact.descriptor, artifact.artifactIdentity, message)).join('; ')}`);
            this.onState('creating-pipeline');
            this._startup.stage = 'pipeline-creation';
            const pipelineStart = performance.now();
            const pipelineOperation = scopedStartupOperation(device, 'pipeline creation', () => this.pipelineCache.getRenderPipelineAsync({
                layout: this.pipelineCache.getPipelineLayout({ bindGroupLayouts: [this._viewLayout, this._materialLayout] }),
                vertex: { module: shader, entryPoint: artifact.descriptor.entryPoints.vertex, buffers: [{ arrayStride: 20, attributes: [
                    { shaderLocation: 0, offset: 0, format: 'float32x3' },
                    { shaderLocation: 1, offset: 12, format: 'float32x2' },
                ] }] },
                fragment: { module: shader, entryPoint: artifact.descriptor.entryPoints.fragment, targets: [{ format: this.format }] },
                primitive: { topology: 'triangle-list', frontFace: 'ccw', cullMode: 'none' },
                depthStencil: { format: 'depth24plus', depthWriteEnabled: true, depthCompare: 'less' },
            }));
            let pipeline;
            try {
                pipeline = await awaitStartupStage(pipelineOperation, pipelineCreationBudgetMs,
                    'pipeline creation', signal, this._startupAbort.signal, pipelineStart);
            } catch (error) {
                if (error?.name === 'AbortError') throw error;
                throw new Error(`Shader ${artifact.artifactIdentity} opaque vertexMain/fragmentMain pipeline: ${error.message ?? error}`, { cause: error });
            } finally { this._startup.timingsMs.pipelineCreation = performance.now() - pipelineStart; }
            this._assertStartup(signal, startupToken, device);
            this.pipeline = pipeline;
            this._startup.stage = 'focused-pipeline';
            await this.focusedPipeline.initialize(signal);
            this._assertStartup(signal, startupToken, device);
            this._capabilities = capabilities;
            this._startup.stage = 'ready';
            this._ready = true;
            this.onState('ready');
        } catch (error) {
            if (error?.name !== 'AbortError') this._recordError(error);
            if (this._startup.stage !== 'ready') this._startup.stage = error?.name === 'AbortError' ? 'canceled' : 'failed';
            if (error?.name === 'AbortError' && (signal?.aborted || this._disposed)) this.dispose();
            else this._fail(error);
            throw error;
        } finally {
            this._startup.timingsMs.total = performance.now() - startupStart;
            this._initializing = false;
            if (this._startupToken === startupToken) this._startupToken = undefined;
        }
    }

    /** Initializes only the canvas/device executor used by authored engine command plans. */
    async initializeEngine(signal) {
        if (this._disposed || this._failed || this.device || this._initializing)
            throw new Error('WebGPU renderer cannot be initialized again.');
        this._initializing = true;
        this._engineOnly = true;
        const startedAt = performance.now();
        const token = {};
        this._startupToken = token;
        this._startupAbort = new AbortController();
        try {
            this._assertActive(signal);
            if (!globalThis.isSecureContext || !navigator.gpu)
                throw new Error('WebGPU requires a secure context and navigator.gpu.');
            this.onState('requesting-adapter');
            this._startup.stage = 'requesting-adapter';
            const adapter = await navigator.gpu.requestAdapter();
            this._assertActive(signal);
            if (!adapter) throw new Error('No WebGPU adapter is available.');
            const requirements = { requiredFeatures: [], requiredLimits: {} };
            this.onState('requesting-device');
            this._startup.stage = 'requesting-device';
            const device = await adapter.requestDevice(requirements);
            this._assertActive(signal, device);
            this.device = device;
            this.pipelineCache = new GpuPipelineCache(device);
            this._observeDevice(device);
            this._startup.stage = 'configuring-context';
            this.context = this.canvas.getContext('webgpu');
            if (!this.context) throw new Error('WebGPU canvas context is unavailable.');
            this.format = navigator.gpu.getPreferredCanvasFormat();
            if (this.format !== 'rgba8unorm' && this.format !== 'bgra8unorm')
                throw new Error(`WebGPU.EngineCanvas.FormatUnsupported: ${this.format}`);
            this._capabilities = captureDeviceCapabilities(device, requirements);
            this._ready = true;
            this._startup.stage = 'ready';
            this.onState('ready');
        } catch (error) {
            if (error?.name !== 'AbortError') this._recordError(error);
            this._startup.stage = error?.name === 'AbortError' ? 'canceled' : 'failed';
            if (error?.name === 'AbortError' && (signal?.aborted || this._disposed)) this.dispose();
            else this._fail(error);
            throw error;
        } finally {
            this._startup.timingsMs.total = performance.now() - startedAt;
            this._initializing = false;
            if (this._startupToken === token) this._startupToken = undefined;
        }
    }

    _retire(resource) {
        if (!resource) return;
        const device = this.device;
        const owner = this._owner;
        this._retired.add(resource);
        device.queue.onSubmittedWorkDone().then(() => {
            if (this.device !== device || this._owner !== owner) return;
            this._retired.delete(resource);
            resource.destroy();
        }, () => {
            if (this.device !== device || this._owner !== owner) return;
            this._retired.delete(resource);
            resource.destroy();
        });
    }

    resize(width, height) {
        this._stats.controlCalls++;
        if (!this._ready || this._disposed) throw new Error('WebGPU renderer is not ready.');
        if (this._executing) throw new Error('The surface cannot resize during packet execution.');
        const limit = this.maxDimension;
        if (!Number.isInteger(width) || !Number.isInteger(height) || width < 0 || height < 0 || width > limit || height > limit)
            throw new RangeError(`Canvas dimensions must be integers from 0 to ${limit}.`);
        if (width === this._width && height === this._height) return this._generation;
        if (this._generation === 0x7fffffff) throw new Error('Canvas generations are exhausted; restart the session.');
        this.readback.invalidateCanvas();
        this._width = width;
        this._height = height;
        try {
            this.canvas.width = width;
            this.canvas.height = height;
            if (this._configured) this.context.unconfigure();
            this._configured = false;
            const oldDepth = this.depthTexture;
            this.depthTexture = undefined;
            this.depthView = undefined;
            this._retire(oldDepth);
            if (width > 0 && height > 0) {
                this._setOperation('configure-canvas');
                this.context.configure({ device: this.device, format: this.format, alphaMode: 'opaque',
                    usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.COPY_SRC });
                this._setOperation('create-canvas-depth');
                this.depthTexture = this.device.createTexture({ size: [width, height], format: 'depth24plus', usage: GPUTextureUsage.RENDER_ATTACHMENT });
                this.depthView = this.depthTexture.createView();
                this._configured = true;
            }
            if (!this._engineOnly) this.focusedPipeline.resize(width, height);
            this._generation++;
            return this._generation;
        } catch (error) {
            this._fail(error);
            throw error;
        }
    }

    createMesh(vertexMemory, indexMemory) {
        this._stats.controlCalls++;
        this._requireOwner();
        const vertexBytes = byteLengthOf(vertexMemory);
        const indexBytes = byteLengthOf(indexMemory);
        if (!vertexBytes || vertexBytes > maximumUploadBytes || vertexBytes % 20
            || indexBytes < 12 || indexBytes > maximumUploadBytes || indexBytes % 12
            || vertexBytes > this.device.limits.maxBufferSize || indexBytes > this.device.limits.maxBufferSize)
            throw new RangeError('Mesh vertex or triangle index bytes are invalid.');
        const vertices = copyMemory(vertexMemory, vertexBytes);
        const indices = copyMemory(indexMemory, indexBytes);
        this._stats.copiedBytes += vertexBytes + indexBytes;
        const vertexData = new DataView(vertices.buffer);
        const indexData = new DataView(indices.buffer);
        const vertexCount = vertexBytes / 20;
        if (vertexCount < 3) throw new RangeError('A mesh requires at least three vertices.');
        for (let offset = 0; offset < vertexBytes; offset += 20) {
            for (let component = 0; component < 5; component++) {
                if (!Number.isFinite(vertexData.getFloat32(offset + component * 4, true)))
                    throw new RangeError('Mesh vertices must be finite.');
            }
        }
        for (let offset = 0; offset < indexBytes; offset += 4) {
            if (indexData.getUint32(offset, true) >= vertexCount)
                throw new RangeError('Mesh index exceeds the vertex count.');
        }
        let vertexBuffer, indexBuffer;
        try {
            vertexBuffer = this.device.createBuffer({ size: vertexBytes, usage: GPUBufferUsage.VERTEX | GPUBufferUsage.COPY_DST | GPUBufferUsage.STORAGE });
            indexBuffer = this.device.createBuffer({ size: indexBytes, usage: GPUBufferUsage.INDEX | GPUBufferUsage.COPY_DST });
            this.device.queue.writeBuffer(vertexBuffer, 0, vertices);
            this.device.queue.writeBuffer(indexBuffer, 0, indices);
            const handle = this._resources.add('mesh', { vertexBuffer, indexBuffer, vertexBytes, indexBytes, vertexCount,
                indexCount: indexBytes / 4, references: 0, state: 'ready', label: 'Engine indexed mesh' }, this._owner);
            this._stats.uploadedBytes += vertexBytes + indexBytes;
            return handle;
        } catch (error) {
            this._retire(vertexBuffer);
            this._retire(indexBuffer);
            throw error;
        }
    }

    createCookedTexture(description, bytes) {
        return createCookedTexture(this, description, bytes);
    }

    createTexture(width, height, rgbaMemory) {
        this._stats.controlCalls++;
        this._requireOwner();
        if (width > this.focusedPipeline.settings.maxTextureDimension || height > this.focusedPipeline.settings.maxTextureDimension)
            throw new RangeError('Texture dimensions exceed the selected browser pipeline texture tier.');
        const bytes = width * height * 4;
        if (!Number.isInteger(width) || !Number.isInteger(height) || width <= 0 || height <= 0
            || width > this.maxDimension || height > this.maxDimension
            || !Number.isSafeInteger(bytes) || bytes > maximumTextureBytes || byteLengthOf(rgbaMemory) !== bytes)
            throw new RangeError('RGBA8 texture dimensions or byte count are invalid.');
        const pixels = copyMemory(rgbaMemory, bytes);
        this._stats.copiedBytes += bytes;
        let texture;
        try {
            texture = this.device.createTexture({ size: [width, height], format: 'rgba8unorm-srgb', usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST | GPUTextureUsage.COPY_SRC });
            this.device.queue.writeTexture({ texture }, pixels, { bytesPerRow: width * 4, rowsPerImage: height }, [width, height]);
            const handle = this._resources.add('texture', { texture, view: texture.createView(), width, height, references: 0,
                format: 'rgba8unorm-srgb', usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST | GPUTextureUsage.COPY_SRC,
                mipLevelCount: 1, sampleCount: 1, state: 'ready', label: 'Engine sampled color' }, this._owner);
            try { this.focusedPipeline.registerTexture(handle); }
            catch (error) { this._resources.remove(handle, this._owner); throw error; }
            this._stats.uploadedBytes += bytes;
            return handle;
        } catch (error) {
            this._retire(texture);
            throw error;
        }
    }

    createMaterial(textureHandle, r, g, b, a) {
        this._stats.controlCalls++;
        this._requireOwner();
        if (![r, g, b, a].every(value => Number.isFinite(value) && value >= 0 && value <= 1))
            throw new RangeError('Material tint must have finite linear channels and alpha in [0, 1].');
        const texture = textureHandle === 0 ? null : this._resources.getHandle(textureHandle, 'texture', this._owner);
        if (texture && !isBrowserColorTexture(texture))
            throw new Error('Browser materials require a supported sampleable color texture with straight alpha.');
        let colorBuffer;
        try {
            colorBuffer = this.device.createBuffer({ size: 16, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
            this.device.queue.writeBuffer(colorBuffer, 0, new Float32Array([r, g, b, a]));
            const bindGroup = this.device.createBindGroup({ layout: this._materialLayout, entries: [
                { binding: 0, resource: { buffer: colorBuffer, size: 16 } },
                { binding: 1, resource: texture?.view ?? this._whiteView },
                { binding: 2, resource: this._sampler },
            ] });
            const handle = this._resources.add('material', { colorBuffer, bindGroup, texture,
                references: 0, state: 'ready', label: 'Engine bound-texture material' }, this._owner);
            if (texture) texture.references++;
            this._stats.uploadedBytes += 16;
            return handle;
        } catch (error) {
            this._retire(colorBuffer);
            throw error;
        }
    }

    destroyResource(handle) {
        this._stats.controlCalls++;
        this._requireOwner();
        // Verify a referenced texture before removing its handle from the table.
        const slot = Number.isInteger(handle) ? handle & 0xffff : 0;
        const generation = Number.isInteger(handle) ? Math.floor(handle / 0x10000) : 0;
        const entry = this._resources.slots[slot];
        if (!entry || entry.generation !== generation || entry.owner !== this._owner)
            throw new Error('Invalid or obsolete resource handle.');
        if (entry.value.references)
            throw new Error('A resource referenced by a live view, binding, pipeline or command plan cannot be destroyed.');
        if (entry.kind === 'material') this.focusedPipeline.releaseMaterial(handle);
        if (entry.kind === 'texture') this.focusedPipeline.releaseTexture(handle);
        if (entry.kind === 'mesh') this.skinning.releaseMesh(handle);
        this._resources.remove(handle, this._owner);
        this._destroyEntry(entry, false);
    }

    retireResource(handle) {
        this._stats.controlCalls++;
        this._requireOwner();
        const slot = Number.isInteger(handle) ? handle & 0xffff : 0;
        const generation = Number.isInteger(handle) ? Math.floor(handle / 0x10000) : 0;
        const entry = this._resources.slots[slot];
        if (!entry || entry.generation !== generation || entry.owner !== this._owner)
            throw new Error('Invalid or obsolete resource handle.');
        if (!['buffer', 'texture', 'texture-view', 'sampler', 'shader', 'binding-layout', 'binding-group', 'render-pipeline', 'compute-pipeline', 'commands'].includes(entry.kind))
            throw new Error('Deferred engine retirement requires an engine command, buffer, texture, view or sampler resource.');
        if (entry.value.retired) return;
        entry.value.retired = true;
        entry.value.tryRetire = () => {
            if (this._disposed || entry.value.references) return;
            entry.value.tryRetire = undefined;
            this._resources.remove(handle, this._owner);
            this._destroyEntry(entry, false);
        };
        entry.value.tryRetire();
    }

    copyTexture(sourceHandle, destinationHandle, sourceX, sourceY, destinationX, destinationY, width, height) {
        this._stats.controlCalls++;
        this._requireOwner();
        if (sourceHandle === destinationHandle)
            throw new RangeError('Texture copies require distinct source and destination resources.');
        const source = this._resources.getHandle(sourceHandle, 'texture', this._owner);
        const destination = this._resources.getHandle(destinationHandle, 'texture', this._owner);
        if (!(source.usage & GPUTextureUsage.COPY_SRC) || !(destination.usage & GPUTextureUsage.COPY_DST)
            || source.sampleCount !== 1 || destination.sampleCount !== 1
            || !['rgba8unorm', 'rgba8unorm-srgb'].includes(source.format) || source.format !== destination.format)
            throw new Error('Texture copies require matching single-sample RGBA8 formats and copy usages.');
        if (![sourceX, sourceY, destinationX, destinationY, width, height].every(Number.isSafeInteger)
            || sourceX < 0 || sourceY < 0 || destinationX < 0 || destinationY < 0
            || width <= 0 || height <= 0 || width > source.width - sourceX || height > source.height - sourceY
            || width > destination.width - destinationX || height > destination.height - destinationY)
            throw new RangeError('Texture copy rectangle exceeds a texture extent.');
        // The legacy copy route addresses mip zero; explicit mip uploads use the resource API.
        const encoder = this.device.createCommandEncoder();
        encoder.copyTextureToTexture(
            { texture: source.texture, origin: [sourceX, sourceY, 0] },
            { texture: destination.texture, origin: [destinationX, destinationY, 0] },
            [width, height, 1]);
        this._submission[0] = encoder.finish();
        try {
            this.device.queue.submit(this._submission);
            this._stats.gpuCopiedBytes += width * height * 4;
        } finally {
            this._submission[0] = null;
        }
    }

    _destroyEntry(entry, immediate) {
        if (entry.value.release) { entry.value.release(); return; }
        if (this.resources.destroy(entry, immediate)) return;
        if (entry.kind === 'material') {
            if (entry.value.texture) {
                entry.value.texture.references--;
                if (!entry.value.texture.references) entry.value.texture.tryRetire?.();
            }
            if (immediate) entry.value.colorBuffer.destroy();
            else this._retire(entry.value.colorBuffer);
        } else if (entry.kind === 'texture') {
            if (immediate) entry.value.texture.destroy();
            else this._retire(entry.value.texture);
        } else if (entry.kind === 'mesh') {
            if (immediate) {
                entry.value.vertexBuffer.destroy();
                entry.value.indexBuffer.destroy();
            } else {
                this._retire(entry.value.vertexBuffer);
                this._retire(entry.value.indexBuffer);
            }
        }
    }

    _growPacketStorage(required) {
        if (required <= this._packetBytes.length) return;
        let capacity = this._packetBytes.length;
        while (capacity < required) capacity *= 2;
        this._packetBytes = new Uint8Array(capacity);
        this._packetView = new DataView(this._packetBytes.buffer);
        this._stats.arenaGrowth++;
    }

    _growUploadStorage(commandLength, payloadLength) {
        if (commandLength > this._uploadCommands.length) {
            let capacity = this._uploadCommands.length;
            const limit = uploadHeaderBytes + maximumUploadCommands * uploadRecordBytes;
            while (capacity < commandLength) capacity = Math.min(capacity * 2, limit);
            this._uploadCommands = new Uint8Array(capacity);
            this._uploadCommandView = new DataView(this._uploadCommands.buffer);
            this._stats.uploadArenaGrowth++;
        }
        if (payloadLength > this._uploadPayload.length) {
            let capacity = Math.max(this._uploadPayload.length, 256);
            while (capacity < payloadLength) capacity = Math.min(capacity * 2, maximumUploadPayloadBytes);
            this._uploadPayload = new Uint8Array(capacity);
            this._uploadPayloadView = new DataView(this._uploadPayload.buffer);
            this._stats.uploadArenaGrowth++;
        }
    }

    submitUploads(commandMemory, payloadMemory) {
        this._stats.controlCalls++;
        this._stats.uploadSubmitCalls++;
        this._requireOwner();
        let count, commandLength, payloadLength;
        try {
            commandLength = byteLengthOf(commandMemory);
            payloadLength = byteLengthOf(payloadMemory);
            if (commandLength < uploadHeaderBytes || commandLength > uploadHeaderBytes + maximumUploadCommands * uploadRecordBytes
                || payloadLength > maximumUploadPayloadBytes)
                throw new RangeError('Invalid upload packet length.');
            this._growUploadStorage(commandLength, payloadLength);
            commandMemory.copyTo(this._uploadCommands);
            payloadMemory.copyTo(this._uploadPayload);
            this._stats.uploadCopiedBytes += commandLength + payloadLength;
            count = validateUploadPacket(this._uploadCommandView, commandLength, this._uploadPayloadView, payloadLength,
                this._owner, this._uploadCommandGeneration, this._uploadPayloadGeneration, this._uploadSequence, this._resources);
        } catch (error) {
            this._stats.rejectedUploads++;
            this._recordPacketFailure('upload', error);
            throw error;
        }

        const data = this._uploadCommandView;
        this._executing = true;
        let commandIndex = -1, commandOffset = 0, commandOpcode = null;
        try {
            for (let i = 0, base = uploadHeaderBytes; i < count; i++, base += uploadRecordBytes) {
                const opcode = data.getUint32(base, true);
                commandIndex = i;
                commandOffset = base;
                commandOpcode = opcode;
                const slot = data.getUint32(base + 8, true);
                const generation = data.getUint32(base + 12, true);
                const destination = data.getUint32(base + 16, true);
                const y = data.getUint32(base + 20, true);
                const width = data.getUint32(base + 24, true);
                const height = data.getUint32(base + 28, true);
                const offset = data.getUint32(base + 32, true);
                const length = data.getUint32(base + 36, true);
                if (opcode === 1 || opcode === 2) {
                    const mesh = this._resources.get(slot, generation, 'mesh', this._owner);
                    this.device.queue.writeBuffer(opcode === 1 ? mesh.vertexBuffer : mesh.indexBuffer,
                        destination, this._uploadPayload, offset, length);
                } else if (opcode === 3) {
                    const texture = this._resources.get(slot, generation, 'texture', this._owner);
                    this._uploadTextureDestination.texture = texture.texture;
                    this._uploadOrigin.x = destination;
                    this._uploadOrigin.y = y;
                    this._uploadTextureLayout.offset = offset;
                    this._uploadTextureLayout.bytesPerRow = width * 4;
                    this._uploadTextureLayout.rowsPerImage = height;
                    this._uploadExtent.width = width;
                    this._uploadExtent.height = height;
                    this.device.queue.writeTexture(this._uploadTextureDestination, this._uploadPayload,
                        this._uploadTextureLayout, this._uploadExtent);
                } else {
                    const material = this._resources.get(slot, generation, 'material', this._owner);
                    this.device.queue.writeBuffer(material.colorBuffer, 0, this._uploadPayload, offset, length);
                }
            }
            this._uploadCommandGeneration = data.getUint32(32, true);
            this._uploadPayloadGeneration = data.getUint32(36, true);
            this._uploadSequence = data.getUint32(40, true);
            this._stats.uploadPackets++;
            this._stats.uploadCommands += count;
            this._stats.uploadBytes += payloadLength;
        } catch (error) {
            this._stats.rejectedUploads++;
            this._recordPacketFailure('upload', error, commandIndex, commandOffset, commandOpcode);
            // Queue writes already issued cannot be rolled back; end this session.
            this._fail(error);
            throw error;
        } finally {
            this._executing = false;
            this._uploadTextureDestination.texture = undefined;
        }
    }

    getCapabilities() {
        if (!this._capabilities || this._disposed || this._failed)
            throw new Error('Device capabilities are unavailable until validated startup completes.');
        const limits = this._capabilities.limits;
        const cookedTextureFormats = ['rgba8unorm', 'rgba8unorm-srgb'];
        if (this.device.features.has('texture-compression-astc'))
            cookedTextureFormats.push('astc-4x4-unorm', 'astc-4x4-unorm-srgb');
        if (this.device.features.has('texture-compression-etc2'))
            cookedTextureFormats.push('etc2-rgba8unorm', 'etc2-rgba8unorm-srgb');
        return {
            ...this._capabilities,
            submissionStrategy: this.submissionStrategy.selected, strategySelection: this.submissionStrategy,
            baselineQualified: false,
            commandCapabilities: { storageBuffers: true, compute: true,
                indirectDraws: ['drawIndirect', 'drawIndexedIndirect'],
                indirectFirstInstance: this.device.features.has('indirect-first-instance'),
                portableIndirectFirstInstance: 0, indirectCount: false, multiDraw: false,
                shaderDrawId: false, descriptorIndexing: false,
                computeWorkgroupSize: 'explicit metadata checked against selected-device limits; native shader validation remains authoritative',
                usageScopes: 'one dispatch or one render pass per command', qualification: 'pending' },
            focusedPipeline: { packetVersion: 2, maximumDraws: 4096, maximumUiQuads: 4096,
                skinning: this.skinningMode, visibility: this.submissionStrategy.selected,
                visibilityAlgorithm: this.submissionStrategy.visibilityAlgorithm,
                hierarchy: { requested: this.submissionStrategy.hierarchy, nodeLayout: 'GpuBvhNode:48-byte;header:16-byte',
                    publication: 'focused-draw-slot snapshot', maximumViewGroups: 16,
                    desktopGpuSceneHost: false, build: 'canonical Morton/Karras LBVH port',
                    refit: 'independent canonical primitive-range reduction', qualified: false },
                alphaModes: ['opaque', 'masked', 'transparent'], shading: ['unlit', 'flat-lambert'],
                directionalLights: 1, hdrIntermediate: 'rgba16float', presentation: 'sRGB',
                exclusions: ['transparent shadows', 'PBR', 'normal maps', 'reversed Z', 'desktop GPUScene host', 'meshlets'],
                experimentalComputeQualified: false },
            textureDimensions: ['2d'], textureFormats: ['rgba8unorm', 'rgba8unorm-srgb', 'rgba16float',
                'depth16unorm', 'depth24plus', 'depth24plus-stencil8', 'depth32float'],
            textureSampleCounts: [1, 4], optionalTextureFormats: [],
            cookedContent: { profile: 'browser-forward-v1', schema: 3, schemas: [1, 2, 3],
                animation: 'baked-native-transforms-packed-deformation', collision: 'static-aabb-character-v1',
                textureFormats: cookedTextureFormats, fullMipChains: true,
                alphaModes: ['straight'], normalStorageConventions: ['none', 'tangent-y-positive'],
                sampler: 'linear-min-mag-mip-clamp', maximumPayloadBytes: 4 * 1024 * 1024 },
            maximumResourceBufferBytes: 256 * 1024 * 1024,
            maximumResourceTextureBytes: 256 * 1024 * 1024,
            maximumReadbackTickets: 16, maximumReadbackBytes: 16 * 1024 * 1024,
            maximumReadbackResidentBytes: 32 * 1024 * 1024,
            framePacketVersion: 2, uploadPacketVersion: 1,
            maximumDraws, maximumUploadCommands, maximumUploadPayloadBytes,
            maximumMeshBytes: maximumUploadBytes, maximumTextureBytes,
            maxTextureDimension2D: limits?.maxTextureDimension2D ?? 0,
            maxBufferSize: limits?.maxBufferSize ?? 0,
        };
    }

    _growUniformStorage(required) {
        if (required <= this._uniformCapacity) return;
        let capacity = this._uniformCapacity;
        while (capacity < required) capacity *= 2;
        capacity = Math.min(capacity, maximumDraws);
        const bytes = new Uint8Array(capacity * this._uniformStride);
        const buffer = this._newUniformBuffer(capacity);
        let bindGroup;
        try { bindGroup = this._newViewBindGroup(buffer); }
        catch (error) { buffer.destroy(); throw error; }
        this._retire(this.uniformBuffer);
        this._uniformBytes = bytes;
        this._uniformView = new DataView(bytes.buffer);
        this.uniformBuffer = buffer;
        this.bindGroup = bindGroup;
        this._uniformCapacity = capacity;
        this._stats.arenaGrowth++;
    }

    submitPacket(memoryView) {
        this._stats.controlCalls++;
        this._stats.frameSubmitCalls++;
        this._requireOwner();
        if (this.submissionStrategy.gpu)
            throw new Error('The legacy mesh packet cannot fulfill a required GPU scene strategy; submit the focused bounds packet.');
        try {
            const length = byteLengthOf(memoryView);
            if (length < packetHeaderBytes || length > packetHeaderBytes + maximumDraws * drawRecordBytes)
                throw new RangeError('Invalid frame packet length.');
            this._growPacketStorage(length);
            memoryView.copyTo(this._packetBytes);
            this._stats.copiedBytes += length;
            const data = this._packetView;
            const count = validateFramePacket(data, length, this._owner, this._generation,
                this._arenaGeneration, this._frameSequence, this._width, this._height, this._resources);
            if (!this._configured) throw new Error('The canvas surface is suspended.');
            this._executing = true;
            const clear = this._colorAttachment.clearValue;
            clear.r = data.getFloat32(64, true);
            clear.g = data.getFloat32(68, true);
            clear.b = data.getFloat32(72, true);
            clear.a = data.getFloat32(76, true);
            this._growUniformStorage(count);
            for (let i = 0, base = packetHeaderBytes; i < count; i++, base += drawRecordBytes) {
                const offset = i * this._uniformStride;
                for (let component = 0; component < 16; component++)
                    this._uniformView.setUint32(offset + component * 4, data.getUint32(base + 48 + component * 4, true), true);
            }
            if (count) {
                const uploadBytes = count * this._uniformStride;
                this.device.queue.writeBuffer(this.uniformBuffer, 0, this._uniformBytes, 0, uploadBytes);
                this._stats.uploadedBytes += uploadBytes;
            }
            // Current swapchain view, encoder, pass, and command buffer necessarily belong to this frame.
            this._colorAttachment.view = this.context.getCurrentTexture().createView();
            this._depthAttachment.view = this.depthView;
            const encoder = this.device.createCommandEncoder();
            if (this.skinningMode === 'Compute') this.skinning.encode(encoder);
            const pass = encoder.beginRenderPass(this._renderPassDescriptor);
            pass.setPipeline(this.pipeline);
            for (let i = 0, base = packetHeaderBytes; i < count; i++, base += drawRecordBytes) {
                const mesh = this._resources.get(data.getUint32(base + 8, true), data.getUint32(base + 12, true), 'mesh', this._owner);
                const material = this._resources.get(data.getUint32(base + 16, true), data.getUint32(base + 20, true), 'material', this._owner);
                this._dynamicOffsets[0] = i * this._uniformStride;
                pass.setBindGroup(0, this.bindGroup, this._dynamicOffsets);
                pass.setBindGroup(1, material.bindGroup);
                const x = data.getUint32(base + 24, true), y = data.getUint32(base + 28, true);
                const width = data.getUint32(base + 32, true), height = data.getUint32(base + 36, true);
                pass.setViewport(x, y, width, height, 0, 1);
                pass.setScissorRect(x, y, width, height);
                pass.setVertexBuffer(0, mesh.vertexBuffer);
                pass.setIndexBuffer(mesh.indexBuffer, 'uint32');
                pass.drawIndexed(data.getUint32(base + 44, true), 1, data.getUint32(base + 40, true));
            }
            pass.end();
            this._submission[0] = encoder.finish();
            this.device.queue.submit(this._submission);
            this._arenaGeneration = data.getUint32(36, true);
            this._frameSequence = data.getUint32(40, true);
            this._stats.packets++;
            this._stats.draws += count;
        } catch (error) {
            this._stats.rejectedPackets++;
            this._recordPacketFailure('frame', error);
            throw error;
        } finally {
            this._executing = false;
            this._submission[0] = null;
            this._colorAttachment.view = undefined;
            this._depthAttachment.view = undefined;
        }
    }

    _recordPacketFailure(lane, error, commandIndex = -1, byteOffset = 0, opcode = null) {
        this._lastPacketFailure = { lane, message: error?.message ?? String(error),
            commandIndex: error?.commandIndex ?? commandIndex,
            byteOffset: error?.byteOffset ?? byteOffset, opcode: error?.opcode ?? opcode };
    }

    getStatistics() {
        return { ...this._stats, lastPacketFailure: this._lastPacketFailure && { ...this._lastPacketFailure },
            focusedPipeline: this.focusedPipeline?.getStatistics() ?? null,
            deformation: { mode: this.skinningMode, ...this.skinning.stats },
            resources: { live: this._resources.slots.reduce((count, entry) => count + (entry ? 1 : 0), 0),
                retiring: this._retired.size, pipelineCacheEntries: this.pipelineCache?.entries.size ?? 0,
                readbackTickets: this.readback.activeCount, readbackResidentBytes: this.readback.residentBytes },
            startup: { stage: this._startup.stage, budgetsMs: { ...this._startup.budgetsMs },
                timingsMs: { ...this._startup.timingsMs } },
            shaderArtifact: this._shaderArtifact && {
            identity: this._shaderArtifact.identity,
            requiredFeatures: [...this._shaderArtifact.requiredFeatures],
            requiredLimits: { ...this._shaderArtifact.requiredLimits },
        } };
    }

    dispose() {
        if (this._disposed) return;
        this._disposed = true;
        this._capabilities = undefined;
        this._startupToken = undefined;
        this._startupAbort?.abort();
        if (this._configured) {
            try { this.context.unconfigure(); } catch (error) { console.error(error); }
        }
        this._configured = false;
        this.commands.dispose();
        this.focusedPipeline?.dispose();
        this.skinning.dispose();
        this.luminance.dispose();
        this.readback.dispose();
        this._resources.clear(entry => this._destroyEntry(entry, true));
        this.resources.dispose();
        this.pipelineCache?.dispose();
        for (const resource of this._retired) resource.destroy();
        this._retired.clear();
        try { this.depthTexture?.destroy(); } catch (error) { console.error(error); }
        try { this._whiteTexture?.destroy(); } catch (error) { console.error(error); }
        try { this.uniformBuffer?.destroy(); } catch (error) { console.error(error); }
        try { this.device?.removeEventListener('uncapturederror', this._onUncapturedError); } catch (error) { console.error(error); }
        try { if (this.device) this._destroyDevice(this.device, 'renderer-dispose'); } catch (error) { console.error(error); }
        this.bindGroup = undefined;
        this._viewLayout = undefined;
        this._materialLayout = undefined;
        this._sampler = undefined;
        this._packetBytes = undefined;
        this._packetView = undefined;
        this._uniformBytes = undefined;
        this._uniformView = undefined;
        this._uploadCommands = undefined;
        this._uploadCommandView = undefined;
        this._uploadPayload = undefined;
        this._uploadPayloadView = undefined;
        this._uploadTextureDestination.texture = undefined;
        this._colorAttachment.view = undefined;
        this._depthAttachment.view = undefined;
        this._submission[0] = null;
        this.pipeline = undefined;
        this.uniformBuffer = undefined;
        this.depthTexture = undefined;
        this.depthView = undefined;
        this._whiteTexture = undefined;
        this._whiteView = undefined;
        this.device = undefined;
        this.context = undefined;
    }
}
