import { pipelineHeaderBytes, pipelineDrawBytes, pipelineMaximumItems } from './pipeline-frame-packet.js';
import { BrowserGpuSceneBvh } from './gpu-scene-bvh.js';

const recordBytes = 160;
const argumentBytes = 20;

/** Backend lowering of the retained scene packet; scene identity and bounds remain engine-owned. */
export class BrowserGpuSceneCulling {
    constructor(renderer) {
        this.renderer = renderer;
        this.records = new Uint32Array(pipelineMaximumItems * recordBytes / 4);
        this.parameters = new Uint32Array(4);
        this.count = 0;
        this.ready = false;
        this.disposed = false;
        this.hizAvailable = false;
        this.passDescriptor = { label: 'Browser scene visibility and indirect arguments' };
        this.stats = { dispatches: 0, publishedDraws: 0, uploadedBytes: 0, readbackBytes: 0 };
    }

    async initialize(source, hierarchySource = null) {
        const device = this.renderer.device, limits = device.limits;
        const bytes = this.records.byteLength;
        if (limits.maxStorageBuffersPerShaderStage < 2 || limits.maxBindingsPerBindGroup < 4
            || limits.maxUniformBufferBindingSize < 16 || limits.maxSampledTexturesPerShaderStage < 1
            || limits.maxStorageBufferBindingSize < bytes || limits.maxBufferSize < bytes
            || limits.maxComputeWorkgroupSizeX < 64 || limits.maxComputeInvocationsPerWorkgroup < 64
            || limits.maxComputeWorkgroupsPerDimension < Math.ceil(pipelineMaximumItems / 64))
            throw new Error('Selected device cannot satisfy bounded GPU scene visibility limits.');
        const module = device.createShaderModule({ label: 'Canonical world bounds visibility', code: source });
        const entries = [
            { binding: 0, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'read-only-storage', minBindingSize: recordBytes } },
            { binding: 1, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'storage', minBindingSize: argumentBytes } },
            { binding: 2, visibility: GPUShaderStage.COMPUTE, buffer: { minBindingSize: 16 } },
            { binding: 3, visibility: GPUShaderStage.COMPUTE, texture: { sampleType: 'unfilterable-float' } }];
        this.recordBuffer = device.createBuffer({ label: 'Published world bounds and views', size: bytes, usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST });
        this.argumentBuffer = device.createBuffer({ label: 'Bounded indexed arguments', size: pipelineMaximumItems * argumentBytes, usage: GPUBufferUsage.STORAGE | GPUBufferUsage.INDIRECT });
        this.parameterBuffer = device.createBuffer({ size: 16, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
        this.emptyDepth = device.createTexture({ size: [1, 1], format: 'r32float', usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST });
        device.queue.writeTexture({ texture: this.emptyDepth }, new Float32Array([1]), {}, [1, 1]);
        this.emptyDepthView = this.emptyDepth.createView();
        if (this.renderer.submissionStrategy.hierarchy) {
            if (!hierarchySource) throw new Error('Required canonical hierarchy shader artifact is missing.');
            this.hierarchy = new BrowserGpuSceneBvh(this.renderer, this.records, this.recordBuffer);
            await this.hierarchy.initialize(hierarchySource);
            if (this.disposed) throw new DOMException('GPU scene visibility startup superseded.', 'AbortError');
            entries.push(
                { binding: 4, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'read-only-storage', minBindingSize: 64 } },
                { binding: 5, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'read-only-storage', minBindingSize: 8 } },
                { binding: 6, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'read-only-storage', minBindingSize: 16 } },
                { binding: 7, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'storage', minBindingSize: 16 } });
        }
        this.layout = device.createBindGroupLayout({ entries });
        const layout = device.createPipelineLayout({ bindGroupLayouts: [this.layout] });
        this.resize(null);
        const [info, frustum, occlusion] = await Promise.all([
            module.getCompilationInfo(),
            device.createComputePipelineAsync({ layout, compute: { module, entryPoint: 'frustumMain' } }),
            device.createComputePipelineAsync({ layout, compute: { module, entryPoint: 'occlusionMain' } })]);
        for (const message of info.messages)
            if (message.type === 'error') throw new Error(`GPU scene visibility ${message.lineNum}:${message.linePos}: ${message.message}`);
        if (this.disposed) throw new DOMException('GPU scene visibility startup was superseded.', 'AbortError');
        this.frustumPipeline = frustum; this.occlusionPipeline = occlusion;
        if (this.hierarchy) {
            const [initialize, traverse, refine, resolve] = await Promise.all([
                device.createComputePipelineAsync({ layout, compute: { module, entryPoint: 'hierarchyArgumentsMain' } }),
                device.createComputePipelineAsync({ layout, compute: { module, entryPoint: 'hierarchyMain' } }),
                device.createComputePipelineAsync({ layout, compute: { module, entryPoint: 'hierarchyOcclusionMain' } }),
                device.createComputePipelineAsync({ layout, compute: { module, entryPoint: 'hierarchyResolveMain' } })]);
            if (this.disposed) throw new DOMException('GPU scene visibility startup superseded.', 'AbortError');
            this.hierarchyInitialize = initialize; this.hierarchyTraverse = traverse; this.hierarchyRefine = refine;
            this.hierarchyResolve = resolve;
        }
        this.ready = true;
    }

    resize(hizView) {
        if (this.disposed) return;
        this.commitResize(this.prepareResize(hizView));
    }

    prepareResize(hizView) {
        if (this.disposed) throw new Error('GPU scene visibility is disposed.');
        const entries = [
            { binding: 0, resource: { buffer: this.recordBuffer } },
            { binding: 1, resource: { buffer: this.argumentBuffer } },
            { binding: 2, resource: { buffer: this.parameterBuffer } },
            { binding: 3, resource: hizView ?? this.emptyDepthView }];
        if (this.hierarchy) entries.push(
            { binding: 4, resource: { buffer: this.hierarchy.nodeBuffer } },
            { binding: 5, resource: { buffer: this.hierarchy.mortonBuffer } },
            { binding: 6, resource: { buffer: this.hierarchy.viewBuffer } },
            { binding: 7, resource: { buffer: this.hierarchy.statusBuffer } });
        const group = this.renderer.device.createBindGroup({ layout: this.layout, entries });
        return { group, hizAvailable: !!hizView };
    }

    commitResize(state) {
        this.group = state.group; this.hizAvailable = state.hizAvailable;
    }

    prepare(view, count, width = view.getUint32(32, true), height = view.getUint32(36, true), cullingEnabled = true) {
        if (!this.ready || this.disposed || !Number.isInteger(count) || count < 0 || count > pipelineMaximumItems)
            throw new Error('GPU scene visibility is unavailable or its draw capacity was exceeded.');
        if (width !== view.getUint32(32, true) || height !== view.getUint32(36, true) || typeof cullingEnabled !== 'boolean')
            throw new Error('GPU scene visibility must use the published frame extent and explicit culling policy.');
        // Copy the existing BoundsGpu ABI directly; no browser-owned bounds database or ID map.
        for (let i = 0, at = pipelineHeaderBytes; i < count; i++, at += pipelineDrawBytes) {
            const target = i * 40;
            for (let j = 0; j < 32; j++) this.records[target + j] = view.getUint32(at + 176 + j * 4, true);
            for (let j = 0; j < 4; j++) this.records[target + 32 + j] = view.getUint32(at + 8 + j * 4, true);
            this.records[target + 36] = view.getUint32(at + 28, true);
            this.records[target + 37] = view.getUint32(at + 24, true);
            this.records[target + 38] = view.getUint32(at + 160, true) | (cullingEnabled ? 0 : 4);
            this.records[target + 39] = 0;
            const mesh = this.renderer._resources.getHandle(view.getUint32(at, true), 'mesh', this.renderer._owner);
            if (mesh.computeSkinned) this.records[target + 38] |= 4;
        }
        this.count = count;
        this.parameters[0] = count;
        this.parameters[1] = width; this.parameters[2] = height;
        this.hierarchy?.prepare(count);
        this.parameters[3] = this.hierarchy?.viewCount ?? 0;
        const queue = this.renderer.device.queue;
        queue.writeBuffer(this.parameterBuffer, 0, this.parameters);
        if (count) queue.writeBuffer(this.recordBuffer, 0, this.records.buffer, 0, count * recordBytes);
        this.stats.publishedDraws += count; this.stats.uploadedBytes += count * recordBytes + 16;
    }

    encode(encoder, useHiZ = false) {
        if (!this.ready || this.disposed || (useHiZ && !this.hizAvailable))
            throw new Error('Required GPU scene visibility or current Hi-Z resource is unavailable.');
        if (!this.count) return;
        if (this.hierarchy) {
            if (!useHiZ) {
                this.hierarchy.encode(encoder);
                this._encodeHierarchyPass(encoder, this.hierarchyInitialize, Math.ceil(this.count / 64));
                this._encodeHierarchyPass(encoder, this.hierarchyTraverse, this.hierarchy.viewCount);
                this._encodeHierarchyPass(encoder, this.hierarchyResolve, Math.ceil(this.count / 64));
            } else this._encodeHierarchyPass(encoder, this.hierarchyRefine, Math.ceil(this.count / 64));
            return;
        }
        const pass = encoder.beginComputePass(this.passDescriptor);
        pass.setPipeline(useHiZ ? this.occlusionPipeline : this.frustumPipeline);
        pass.setBindGroup(0, this.group);
        pass.dispatchWorkgroups(Math.ceil(this.count / 64));
        pass.end(); this.stats.dispatches++;
    }

    _encodeHierarchyPass(encoder, pipeline, groups) {
        const pass = encoder.beginComputePass(this.passDescriptor);
        pass.setPipeline(pipeline); pass.setBindGroup(0, this.group); pass.dispatchWorkgroups(groups); pass.end();
        this.stats.dispatches++;
    }

    dispose() {
        if (this.disposed) return;
        this.disposed = true; this.ready = false; this.count = 0;
        this.hierarchy?.dispose(); this.hierarchy = null;
        this.renderer._retire(this.recordBuffer); this.renderer._retire(this.argumentBuffer);
        this.renderer._retire(this.parameterBuffer); this.renderer._retire(this.emptyDepth);
        this.recordBuffer = null; this.argumentBuffer = null; this.parameterBuffer = null; this.emptyDepth = null;
        this.frustumPipeline = null; this.occlusionPipeline = null; this.group = null; this.layout = null;
        this.emptyDepthView = null; this.records = null; this.parameters = null;
        this.hierarchyInitialize = null; this.hierarchyTraverse = null; this.hierarchyRefine = null;
        this.hierarchyResolve = null;
    }
}
