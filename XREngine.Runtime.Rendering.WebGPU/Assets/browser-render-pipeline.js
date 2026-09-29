import { pipelineHeaderBytes, pipelineDrawBytes, pipelineUiBytes, pipelineMaximumItems,
    pipelineMaximumBytes, validatePipelinePacket } from './pipeline-frame-packet.js';
import { isBrowserColorTexture } from './cooked-texture.js';

const alphaModes = ['opaque', 'masked', 'transparent'];
const cullModes = ['none', 'back', 'front'];
const sceneFormats = ['rgba8unorm', 'rgba16float'];
const startupBudgetMs = 45000;

async function loadShader(name, signal) {
    const response = await fetch(new URL(name, import.meta.url), { signal, cache: 'no-cache' });
    if (!response.ok) throw new Error(`Browser raster shader ${name}: HTTP ${response.status}.`);
    const declared = Number(response.headers.get('content-length'));
    if (declared > 65536) throw new Error('Browser raster shader exceeds its source budget.');
    const reader = response.body.getReader();
    const data = new Uint8Array(65536);
    let size = 0;
    try {
        for (;;) {
            const result = await reader.read();
            if (result.done) break;
            if (result.value.length > data.length - size) throw new Error('Browser raster shader exceeds its source budget.');
            data.set(result.value, size); size += result.value.length;
        }
    } finally { await reader.cancel(); reader.releaseLock(); }
    return new TextDecoder('utf-8', { fatal: true }).decode(data.subarray(0, size));
}

/** Bounded CPU-direct raster passes, distinct from the desktop advanced pipeline. */
export class BrowserRenderPipeline {
    constructor(renderer) {
        this.renderer = renderer;
        this.materials = new Map();
        this.sequence = 0;
        this.settings = { resolutionScale: 1, maxDevicePixelRatio: 2, shadowResolution: 1024,
            shadowUpdateInterval: 1, directionalLightCount: 1, textureTier: 'rgba8', maxMaterials: 256,
            maxMaterialComplexity: 'lambert', maxTextureDimension: 2048, hdr: false, toneMap: 'none', uiEnabled: true };
        this.bytes = new Uint8Array(pipelineMaximumBytes);
        this.view = new DataView(this.bytes.buffer);
        this.instances = new Float32Array(pipelineMaximumItems * 32);
        this.uniform = new Float32Array(40);
        this.shadowMatrix = new Float32Array(16);
        this.shadowMatrix[0] = this.shadowMatrix[5] = this.shadowMatrix[10] = this.shadowMatrix[15] = 1;
        this.pipelines = new Array(18);
        this.shadowPipelines = new Array(3);
        this.skyPipelines = new Array(2);
        this.submission = [null];
        this.encoderDescriptor = { label: 'Browser CPU-direct raster frame' };
        this.shadowAttachment = { view: undefined, depthClearValue: 1, depthLoadOp: 'clear', depthStoreOp: 'store' };
        this.shadowPass = { colorAttachments: [], depthStencilAttachment: this.shadowAttachment };
        this.sceneAttachment = { view: undefined, loadOp: 'clear', storeOp: 'store', clearValue: { r: 0, g: 0, b: 0, a: 1 } };
        this.sceneDepth = { view: undefined, depthClearValue: 1, depthLoadOp: 'clear', depthStoreOp: 'discard' };
        this.scenePass = { colorAttachments: [this.sceneAttachment], depthStencilAttachment: this.sceneDepth };
        this.composeAttachment = { view: undefined, loadOp: 'clear', storeOp: 'store', clearValue: { r: 0, g: 0, b: 0, a: 1 } };
        this.composePass = { colorAttachments: [this.composeAttachment] };
        this.outputAttachment = { view: undefined, loadOp: 'clear', storeOp: 'store', clearValue: { r: 0, g: 0, b: 0, a: 1 } };
        this.outputPass = { colorAttachments: [this.outputAttachment] };
        this.stats = { frames: 0, sceneDrawCalls: 0, shadowDrawCalls: 0, instances: 0, uiDrawCalls: 0,
            shadowUpdates: 0, rejectedPackets: 0, copiedBytes: 0, targetGenerations: 0 };
        this._stop = new AbortController();
        this._disposed = false;
        this.ready = false;
    }

    async initialize(signal) {
        const device = this.renderer.device;
        const abort = () => this._stop.abort();
        signal?.addEventListener('abort', abort, { once: true });
        if (signal?.aborted) abort();
        const timer = setTimeout(abort, startupBudgetMs);
        let rejectAbort;
        const interrupted = new Promise((_, reject) => { rejectAbort = reject; });
        const failAbort = () => rejectAbort(new DOMException('Browser raster startup canceled or exceeded 45 seconds.', 'AbortError'));
        this._stop.signal.addEventListener('abort', failAbort, { once: true });
        if (this._stop.signal.aborted) failAbort();
        const operation = this._initializeDevice(device, this._stop.signal);
        try {
            await Promise.race([operation, interrupted]);
            if (this._disposed || this._stop.signal.aborted || device !== this.renderer.device)
                throw new DOMException('Browser raster startup was superseded.', 'AbortError');
            this.ready = true;
        } finally {
            clearTimeout(timer);
            signal?.removeEventListener('abort', abort);
            this._stop.signal.removeEventListener('abort', failAbort);
        }
    }

    async _initializeDevice(device, signal) {
        const [rasterSource, composeSource] = await Promise.all([
            loadShader('browser-raster.wgsl', signal), loadShader('browser-compose.wgsl', signal)]);
        if (signal.aborted || this._disposed) throw new DOMException('Raster startup canceled.', 'AbortError');
        device.pushErrorScope('out-of-memory'); device.pushErrorScope('validation');
        let operation;
        try { operation = this._compile(device, rasterSource, composeSource); }
        catch (error) { operation = Promise.reject(error); }
        const validation = device.popErrorScope(), memory = device.popErrorScope();
        const results = await Promise.allSettled([operation, validation, memory]);
        for (let i = 0; i < results.length; i++) {
            if (results[i].status === 'rejected') throw results[i].reason;
            if (i && results[i].value) throw new Error(results[i].value.message);
        }
    }

    async _compile(device, rasterSource, composeSource) {
        const raster = device.createShaderModule({ label: 'Browser flat raster', code: rasterSource });
        const compose = device.createShaderModule({ label: 'Browser linear SDR composition', code: composeSource });
        const compilation = Promise.all([raster.getCompilationInfo(), compose.getCompilationInfo()]);
        if (this._disposed || this._stop.signal.aborted) throw new DOMException('Raster startup canceled.', 'AbortError');
        this.frameLayout = device.createBindGroupLayout({ entries: [
            { binding: 0, visibility: GPUShaderStage.VERTEX | GPUShaderStage.FRAGMENT, buffer: { minBindingSize: 160 } },
            { binding: 1, visibility: GPUShaderStage.FRAGMENT, texture: { sampleType: 'depth' } },
            { binding: 2, visibility: GPUShaderStage.FRAGMENT, sampler: { type: 'comparison' } }] });
        this.optionsLayout = device.createBindGroupLayout({ entries: [
            { binding: 0, visibility: GPUShaderStage.FRAGMENT, buffer: { minBindingSize: 16 } }] });
        this.composeFrameLayout = device.createBindGroupLayout({ entries: [
            { binding: 0, visibility: GPUShaderStage.VERTEX | GPUShaderStage.FRAGMENT, buffer: { minBindingSize: 160 } }] });
        this.imageLayout = device.createBindGroupLayout({ entries: [
            { binding: 0, visibility: GPUShaderStage.FRAGMENT, texture: {} },
            { binding: 1, visibility: GPUShaderStage.FRAGMENT, sampler: {} }] });
        const sceneLayout = device.createPipelineLayout({ bindGroupLayouts: [this.frameLayout, this.renderer._materialLayout, this.optionsLayout] });
        const shadowLayout = device.createPipelineLayout({ bindGroupLayouts: [this.composeFrameLayout, this.renderer._materialLayout, this.optionsLayout] });
        const skyLayout = device.createPipelineLayout({ bindGroupLayouts: [this.composeFrameLayout] });
        const screenLayout = device.createPipelineLayout({ bindGroupLayouts: [this.composeFrameLayout, this.imageLayout] });
        const instanceAttributes = [];
        for (let i = 0; i < 8; i++) instanceAttributes.push({ shaderLocation: i + 2, offset: i * 16, format: 'float32x4' });
        const buffers = [{ arrayStride: 20, attributes: [
            { shaderLocation: 0, offset: 0, format: 'float32x3' }, { shaderLocation: 1, offset: 12, format: 'float32x2' }] },
            { arrayStride: 128, stepMode: 'instance', attributes: instanceAttributes }];
        const blend = { color: { srcFactor: 'src-alpha', dstFactor: 'one-minus-src-alpha' }, alpha: { srcFactor: 'one', dstFactor: 'one-minus-src-alpha' } };
        const pending = [];
        for (let format = 0; format < 2; format++) {
            for (let mode = 0; mode < 3; mode++) {
                for (let cull = 0; cull < 3; cull++) {
                    const index = format * 9 + mode * 3 + cull;
                    pending.push(device.createRenderPipelineAsync({ layout: sceneLayout,
                        vertex: { module: raster, entryPoint: 'sceneVertex', buffers },
                        fragment: { module: raster, entryPoint: 'sceneFragment', targets: [{ format: sceneFormats[format], blend: mode === 2 ? blend : undefined }] },
                        primitive: { topology: 'triangle-list', frontFace: 'ccw', cullMode: cullModes[cull] },
                        depthStencil: { format: 'depth24plus', depthWriteEnabled: mode !== 2, depthCompare: 'less' } })
                        .then(pipeline => { if (!this._disposed) this.pipelines[index] = pipeline; }));
                }
            }
            pending.push(device.createRenderPipelineAsync({ layout: skyLayout,
                vertex: { module: raster, entryPoint: 'screenVertex' },
                fragment: { module: raster, entryPoint: 'skyFragment', targets: [{ format: sceneFormats[format] }] },
                depthStencil: { format: 'depth24plus', depthWriteEnabled: false, depthCompare: 'always' } })
                .then(pipeline => { if (!this._disposed) this.skyPipelines[format] = pipeline; }));
        }
        for (let cull = 0; cull < 3; cull++)
            pending.push(device.createRenderPipelineAsync({ layout: shadowLayout,
                vertex: { module: raster, entryPoint: 'shadowVertex', buffers },
                fragment: { module: raster, entryPoint: 'shadowFragment', targets: [] },
                primitive: { topology: 'triangle-list', frontFace: 'ccw', cullMode: cullModes[cull] },
                depthStencil: { format: 'depth32float', depthWriteEnabled: true, depthCompare: 'less', depthBias: 2, depthBiasSlopeScale: 2 } })
                .then(pipeline => { if (!this._disposed) this.shadowPipelines[cull] = pipeline; }));
        pending.push(device.createRenderPipelineAsync({ layout: screenLayout,
            vertex: { module: compose, entryPoint: 'screenVertex' },
            fragment: { module: compose, entryPoint: 'presentFragment', targets: [{ format: 'rgba8unorm' }] } })
            .then(pipeline => { if (!this._disposed) this.presentPipeline = pipeline; }));
        pending.push(device.createRenderPipelineAsync({ layout: screenLayout,
            vertex: { module: compose, entryPoint: 'screenVertex' },
            fragment: { module: compose, entryPoint: 'encodeFragment', targets: [{ format: this.renderer.format }] } })
            .then(pipeline => { if (!this._disposed) this.encodePipeline = pipeline; }));
        pending.push(device.createRenderPipelineAsync({ layout: screenLayout,
            vertex: { module: compose, entryPoint: 'uiVertex', buffers: [{ arrayStride: 80, stepMode: 'instance', attributes: [
                { shaderLocation: 0, offset: 4, format: 'float32x4' }, { shaderLocation: 1, offset: 20, format: 'float32x4' },
                { shaderLocation: 2, offset: 36, format: 'float32x4' }] }] },
            fragment: { module: compose, entryPoint: 'uiFragment', targets: [{ format: 'rgba8unorm', blend }] } })
            .then(pipeline => { if (!this._disposed) this.uiPipeline = pipeline; }));
        this.uniformBuffer = device.createBuffer({ size: 160, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
        this.instanceBuffer = device.createBuffer({ size: this.instances.byteLength, usage: GPUBufferUsage.VERTEX | GPUBufferUsage.COPY_DST });
        this.uiBuffer = device.createBuffer({ size: pipelineMaximumItems * pipelineUiBytes, usage: GPUBufferUsage.VERTEX | GPUBufferUsage.COPY_DST });
        this.comparisonSampler = device.createSampler({ compare: 'less-equal', magFilter: 'linear', minFilter: 'linear' });
        this.composeFrame = device.createBindGroup({ layout: this.composeFrameLayout, entries: [{ binding: 0, resource: { buffer: this.uniformBuffer } }] });
        this.whiteImage = this._imageGroup(this.renderer._whiteView);
        const [information] = await Promise.all([compilation, Promise.all(pending)]);
        for (const info of information)
            for (const message of info.messages)
                if (message.type === 'error') throw new Error(`Browser raster shader ${message.lineNum}:${message.linePos}: ${message.message}`);
        if (this._disposed || this._stop.signal.aborted) throw new DOMException('Raster startup canceled.', 'AbortError');
    }

    _imageGroup(view) {
        return this.renderer.device.createBindGroup({ layout: this.imageLayout, entries: [
            { binding: 0, resource: view }, { binding: 1, resource: this.renderer._sampler }] });
    }

    registerTexture(handle) {
        const texture = this.renderer._resources.getHandle(handle, 'texture', this.renderer._owner);
        if (texture.width > this.settings.maxTextureDimension || texture.height > this.settings.maxTextureDimension)
            throw new Error(`UI texture exceeds selected maxTextureDimension ${this.settings.maxTextureDimension}.`);
        if (!(texture.usage & GPUTextureUsage.TEXTURE_BINDING) || texture.sampleCount !== 1
            || !isBrowserColorTexture(texture))
            throw new Error('UI atlases require a sampleable single-sample supported color texture with straight alpha.');
        texture.uiBindGroup = this._imageGroup(texture.view);
    }

    releaseTexture(handle) {
        this.renderer._resources.getHandle(handle, 'texture', this.renderer._owner).uiBindGroup = null;
    }

    configureMaterial(handle, json) {
        this.renderer._requireOwner();
        if (typeof json !== 'string' || json.length > 4096) throw new Error('Material selection payload exceeds its budget.');
        const selected = JSON.parse(json);
        if (!selected || Array.isArray(selected) || typeof selected !== 'object') throw new Error('Material selection must be an object.');
        for (const name of Object.keys(selected))
            if (!['alphaMode','cullMode','shading','alphaCutoff','castShadow','receiveShadow'].includes(name))
                throw new Error(`Unsupported required browser material feature: ${name}.`);
        if ((selected.castShadow !== undefined && typeof selected.castShadow !== 'boolean')
            || (selected.receiveShadow !== undefined && typeof selected.receiveShadow !== 'boolean'))
            throw new Error('Shadow material selections must be booleans.');
        const material = this.renderer._resources.getHandle(handle, 'material', this.renderer._owner);
        const mode = alphaModes.indexOf(selected.alphaMode ?? 'opaque'), cull = cullModes.indexOf(selected.cullMode ?? 'back');
        const shading = selected.shading ?? 'unlit', cutoff = selected.alphaCutoff ?? 0.5;
        if (mode < 0 || cull < 0 || !['unlit','lambert'].includes(shading) || !Number.isFinite(cutoff) || cutoff < 0 || cutoff > 1)
            throw new Error('Unsupported required browser material alpha, shading, culling or cutoff profile.');
        if (mode === 2 && selected.castShadow !== false)
            throw new Error('Transparent shadow casting is unsupported; explicitly disable castShadow.');
        if (material.texture && (material.texture.width > this.settings.maxTextureDimension
            || material.texture.height > this.settings.maxTextureDimension))
            throw new Error(`Material texture exceeds selected maxTextureDimension ${this.settings.maxTextureDimension}.`);
        if (shading === 'lambert' && this.settings.maxMaterialComplexity === 'unlit')
            throw new Error('Lambert material exceeds the selected unlit material complexity.');
        if (!this.materials.has(handle) && this.materials.size >= this.settings.maxMaterials)
            throw new Error('Browser material residency limit reached.');
        const buffer = this.renderer.device.createBuffer({ size: 16, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
        try {
            this.renderer.device.queue.writeBuffer(buffer, 0, new Float32Array([cutoff, mode, shading === 'lambert' ? 1 : 0, selected.receiveShadow === false ? 0 : 1]));
            const options = this.renderer.device.createBindGroup({ layout: this.optionsLayout, entries: [{ binding: 0, resource: { buffer } }] });
            this.releaseMaterial(handle);
            this.materials.set(handle, { mode, cull, shading, castShadow: selected.castShadow !== false && mode !== 2, buffer, options, material });
        } catch (error) { this.renderer._retire(buffer); throw error; }
    }

    releaseMaterial(handle) {
        const previous = this.materials.get(handle);
        if (previous) { this.materials.delete(handle); this.renderer._retire(previous.buffer); }
    }

    configure(json) {
        this.renderer._requireOwner();
        if (typeof json !== 'string' || json.length > 4096) throw new Error('Quality selection payload exceeds its budget.');
        const selection = JSON.parse(json);
        if (!selection || Array.isArray(selection) || typeof selection !== 'object') throw new Error('Quality selection must be an object.');
        for (const name of Object.keys(selection))
            if (!Object.hasOwn(this.settings, name)) throw new Error(`Unsupported required browser quality feature: ${name}.`);
        const next = { ...this.settings, ...selection };
        if (!Number.isFinite(next.resolutionScale) || next.resolutionScale < 0.25 || next.resolutionScale > 1
            || !Number.isFinite(next.maxDevicePixelRatio) || next.maxDevicePixelRatio < 0.5 || next.maxDevicePixelRatio > 4
            || ![128,256,512,1024,2048,4096].includes(next.shadowResolution)
            || !Number.isInteger(next.shadowUpdateInterval) || next.shadowUpdateInterval < 1 || next.shadowUpdateInterval > 120
            || ![0,1].includes(next.directionalLightCount) || next.textureTier !== 'rgba8'
            || !Number.isInteger(next.maxMaterials) || next.maxMaterials < this.materials.size || next.maxMaterials > 4096 || next.maxMaterials < 1
            || !['unlit','lambert'].includes(next.maxMaterialComplexity) || !['none','reinhard'].includes(next.toneMap)
            || typeof next.hdr !== 'boolean' || typeof next.uiEnabled !== 'boolean')
            throw new Error('Unsupported required browser quality setting.');
        if (!Number.isInteger(next.maxTextureDimension) || next.maxTextureDimension < 128
            || next.maxTextureDimension > 8192 || next.maxTextureDimension > this.renderer.maxDimension)
            throw new Error('Required maxTextureDimension exceeds the supported quality or device limit.');
        if (next.shadowResolution > this.renderer.maxDimension)
            throw new Error('Required shadowResolution exceeds the selected device limit.');
        if (!next.hdr && next.toneMap !== 'none')
            throw new Error('Reinhard tonemapping requires the HDR intermediate.');
        for (const value of this.materials.values())
            if (value.material.texture && (value.material.texture.width > next.maxTextureDimension
                || value.material.texture.height > next.maxTextureDimension))
                throw new Error('A live material texture exceeds the requested maxTextureDimension.');
        for (const entry of this.renderer._resources.slots)
            if (entry?.kind === 'texture' && entry.value.uiBindGroup
                && (entry.value.width > next.maxTextureDimension || entry.value.height > next.maxTextureDimension))
                throw new Error('A live UI texture exceeds the requested maxTextureDimension.');
        for (const value of this.materials.values())
            if (next.maxMaterialComplexity === 'unlit' && value.shading === 'lambert')
                throw new Error('Active Lambert materials prevent selecting unlit-only quality.');
        const old = this.settings;
        this.settings = next;
        try { this.resize(this.renderer._width, this.renderer._height, true); }
        catch (error) { this.settings = old; throw error; }
    }

    resize(width, height, force = false) {
        if (!this.ready || this._disposed) return;
        if (!force && this.width === width && this.height === height) return;
        if (!width || !height) { this._releaseTargets(); this.width = width; this.height = height; return; }
        const device = this.renderer.device;
        const targets = [];
        try {
            const scene = device.createTexture({ size: [width,height], format: this.settings.hdr ? 'rgba16float' : 'rgba8unorm', usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.TEXTURE_BINDING }); targets.push(scene);
            const sdr = device.createTexture({ size: [width,height], format: 'rgba8unorm', usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.TEXTURE_BINDING }); targets.push(sdr);
            const depth = device.createTexture({ size: [width,height], format: 'depth24plus', usage: GPUTextureUsage.RENDER_ATTACHMENT }); targets.push(depth);
            const size = this.settings.shadowResolution && this.settings.directionalLightCount ? this.settings.shadowResolution : 1;
            if (size > device.limits.maxTextureDimension2D) throw new Error('Shadow size exceeds the device texture extent limit.');
            const shadow = device.createTexture({ size: [size,size], format: 'depth32float', usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.TEXTURE_BINDING }); targets.push(shadow);
            const sceneView = scene.createView(), sdrView = sdr.createView(), depthView = depth.createView(), shadowView = shadow.createView();
            const frame = device.createBindGroup({ layout: this.frameLayout, entries: [
                { binding: 0, resource: { buffer: this.uniformBuffer } }, { binding: 1, resource: shadowView }, { binding: 2, resource: this.comparisonSampler }] });
            const sceneImage = this._imageGroup(sceneView), sdrImage = this._imageGroup(sdrView);
            this._releaseTargets();
            this.targets = targets; this.width = width; this.height = height; this.shadowSize = size;
            this.sceneAttachment.view = sceneView; this.sceneDepth.view = depthView; this.composeAttachment.view = sdrView;
            this.shadowAttachment.view = shadowView; this.frameGroup = frame;
            this.sceneImage = sceneImage; this.sdrImage = sdrImage; this.shadowValid = false;
            this.stats.targetGenerations++;
        } catch (error) { for (const target of targets) this.renderer._retire(target); throw error; }
    }

    _releaseTargets() {
        if (this.targets) for (const target of this.targets) this.renderer._retire(target);
        this.targets = null; this.sceneAttachment.view = undefined; this.sceneDepth.view = undefined;
        this.composeAttachment.view = undefined; this.shadowAttachment.view = undefined;
        this.frameGroup = null; this.shadowFrameGroup = null; this.sceneImage = null; this.sdrImage = null;
    }

    submit(memory) {
        const renderer = this.renderer;
        renderer._requireOwner();
        if (!this.ready || !this.targets || this.width !== renderer._width || this.height !== renderer._height)
            throw new Error('Browser raster targets do not match the current surface.');
        const length = memory?.byteLength;
        if (!Number.isSafeInteger(length) || length < pipelineHeaderBytes || length > this.bytes.length)
            throw new Error('Browser raster packet size is invalid.');
        // The reusable destination is JS-owned; no managed-memory view escapes.
        memory.copyTo(this.bytes);
        const view = this.view;
        let count;
        try { count = validatePipelinePacket(view, length, this); }
        catch (error) { this.stats.rejectedPackets++; throw error; }
        const uiCount = view.getUint32(28, true), uiOffset = pipelineHeaderBytes + count * pipelineDrawBytes;
        for (let i = 0, at = pipelineHeaderBytes; i < count; i++, at += pipelineDrawBytes)
            for (let j = 0; j < 32; j++) this.instances[i * 32 + j] = view.getFloat32(at + 32 + j * 4, true);
        for (let i = 0; i < 16; i++) this.uniform[16 + i] = view.getFloat32(192 + i * 4, true);
        this.uniform[32] = this.width; this.uniform[33] = this.height;
        const shadows = this.settings.shadowResolution > 0 && this.settings.directionalLightCount === 1;
        const updateShadows = shadows && (!this.shadowValid || (view.getUint32(40, true) & 1));
        if (updateShadows)
            for (let i = 0; i < 16; i++) this.shadowMatrix[i] = view.getFloat32(128 + i * 4, true);
        this.uniform.set(this.shadowMatrix, 0);
        this.uniform[36] = shadows ? 1 : 0; this.uniform[37] = this.settings.toneMap === 'reinhard' ? 1 : 0;
        if (!this.settings.directionalLightCount) this.uniform[19] = 0;
        renderer._executing = true;
        try {
            const device = renderer.device;
            device.queue.writeBuffer(this.uniformBuffer, 0, this.uniform);
            if (count) device.queue.writeBuffer(this.instanceBuffer, 0, this.instances.buffer, 0, count * 128);
            if (uiCount) device.queue.writeBuffer(this.uiBuffer, 0, this.bytes.buffer, uiOffset, uiCount * pipelineUiBytes);
            const encoder = device.createCommandEncoder(this.encoderDescriptor);
            if (updateShadows) {
                const shadow = encoder.beginRenderPass(this.shadowPass);
                shadow.setBindGroup(0, this.composeFrame);
                shadow.setVertexBuffer(1, this.instanceBuffer);
                this._drawMeshes(shadow, count, true);
                shadow.end(); this.shadowValid = true; this.stats.shadowUpdates++;
            }
            const pass = encoder.beginRenderPass(this.scenePass);
            pass.setPipeline(this.skyPipelines[this.settings.hdr ? 1 : 0]); pass.setBindGroup(0, this.composeFrame); pass.draw(3);
            pass.setBindGroup(0, this.frameGroup);
            pass.setVertexBuffer(1, this.instanceBuffer);
            this._drawMeshes(pass, count, false); pass.end();
            const composition = encoder.beginRenderPass(this.composePass);
            composition.setPipeline(this.presentPipeline); composition.setBindGroup(0, this.composeFrame); composition.setBindGroup(1, this.sceneImage); composition.draw(3);
            if (uiCount) {
                composition.setPipeline(this.uiPipeline); composition.setVertexBuffer(0, this.uiBuffer);
                for (let i = 0, at = uiOffset; i < uiCount; i++, at += pipelineUiBytes) {
                    const handle = view.getUint32(at, true);
                    const width = view.getFloat32(at + 60, true), height = view.getFloat32(at + 64, true);
                    if (!width || !height) continue;
                    composition.setBindGroup(1, handle ? renderer._resources.getHandle(handle, 'texture', renderer._owner).uiBindGroup : this.whiteImage);
                    composition.setScissorRect(view.getFloat32(at + 52, true), view.getFloat32(at + 56, true), width, height);
                    composition.draw(6, 1, 0, i); this.stats.uiDrawCalls++;
                }
            }
            composition.end();
            this.outputAttachment.view = renderer.context.getCurrentTexture().createView();
            const output = encoder.beginRenderPass(this.outputPass);
            output.setPipeline(this.encodePipeline); output.setBindGroup(0, this.composeFrame); output.setBindGroup(1, this.sdrImage); output.draw(3); output.end();
            this.submission[0] = encoder.finish(); device.queue.submit(this.submission);
            this.sequence = view.getUint32(24, true); this.stats.frames++; this.stats.instances += count; this.stats.copiedBytes += length;
        } catch (error) { renderer._fail(error); throw error; }
        finally { this.outputAttachment.view = undefined; this.submission[0] = null; renderer._executing = false; }
    }

    _drawMeshes(pass, count, shadow) {
        const view = this.view, renderer = this.renderer;
        for (let i = 0; i < count;) {
            const at = pipelineHeaderBytes + i * pipelineDrawBytes;
            const meshHandle = view.getUint32(at, true), materialHandle = view.getUint32(at + 4, true);
            const material = this.materials.get(materialHandle), flags = view.getUint32(at + 160, true);
            if (shadow ? !(flags & 1) || !material.castShadow : (flags & 2)) { i++; continue; }
            let instances = 1;
            while (i + instances < count) {
                const next = at + instances * pipelineDrawBytes;
                if (view.getUint32(next, true) !== meshHandle || view.getUint32(next + 4, true) !== materialHandle
                    || view.getUint32(next + 160, true) !== flags || view.getUint32(next + 24, true) !== view.getUint32(at + 24, true)
                    || view.getUint32(next + 28, true) !== view.getUint32(at + 28, true)) break;
                if (!shadow && (view.getUint32(next + 8, true) !== view.getUint32(at + 8, true)
                    || view.getUint32(next + 12, true) !== view.getUint32(at + 12, true)
                    || view.getUint32(next + 16, true) !== view.getUint32(at + 16, true)
                    || view.getUint32(next + 20, true) !== view.getUint32(at + 20, true))) break;
                instances++;
            }
            const mesh = renderer._resources.getHandle(meshHandle, 'mesh', renderer._owner);
            pass.setPipeline(shadow ? this.shadowPipelines[material.cull] : this.pipelines[(this.settings.hdr ? 9 : 0) + material.mode * 3 + material.cull]);
            pass.setBindGroup(1, material.material.bindGroup); pass.setBindGroup(2, material.options);
            pass.setVertexBuffer(0, mesh.vertexBuffer); pass.setIndexBuffer(mesh.indexBuffer, 'uint32');
            if (!shadow) {
                const x=view.getUint32(at+8,true),y=view.getUint32(at+12,true),width=view.getUint32(at+16,true),height=view.getUint32(at+20,true);
                pass.setViewport(x,y,width,height,0,1); pass.setScissorRect(x,y,width,height);
            }
            pass.drawIndexed(view.getUint32(at + 28, true), instances, view.getUint32(at + 24, true), 0, i);
            if (shadow) this.stats.shadowDrawCalls++; else this.stats.sceneDrawCalls++;
            i += instances;
        }
    }

    getStatistics() {
        return { ...this.stats, residentMaterials: this.materials.size, pipelineVariants: 26,
            submissionMode: 'CpuDirect', shading: 'unlit-or-flat-lambert', settings: { ...this.settings },
            exclusions: ['bindless', 'normal maps', 'skinning', 'reversed Z', 'GPU indirect', 'desktop post effects'] };
    }

    dispose() {
        if (this._disposed) return;
        this._disposed = true; this._stop.abort(); this.ready = false;
        this._releaseTargets();
        for (const material of this.materials.values()) this.renderer._retire(material.buffer);
        this.materials.clear();
        this.renderer._retire(this.uniformBuffer); this.renderer._retire(this.instanceBuffer); this.renderer._retire(this.uiBuffer);
        this.uniformBuffer = null; this.instanceBuffer = null; this.uiBuffer = null;
        this.pipelines.fill(null); this.shadowPipelines.fill(null); this.skyPipelines.fill(null);
        this.frameGroup = null; this.shadowFrameGroup = null; this.composeFrame = null; this.whiteImage = null;
        this.presentPipeline = null; this.encodePipeline = null; this.uiPipeline = null;
        this.bytes = null; this.view = null; this.instances = null; this.uniform = null; this.shadowMatrix = null;
        this.frameLayout = null; this.optionsLayout = null; this.composeFrameLayout = null; this.imageLayout = null;
        this.comparisonSampler = null;
    }
}
