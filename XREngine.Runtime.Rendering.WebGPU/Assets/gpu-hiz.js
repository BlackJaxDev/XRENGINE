/** Backend lowering of GPURenderPassCollection.BuildHiZPyramid.
 * Owns only GPU pyramid resources; scene bounds and visibility belong to the caller.
 * Source depth must be current-frame, single-sample, standard zero-to-one depth.
 */
export class BrowserGpuHiZ {
    constructor(renderer) {
        this.renderer = renderer;
        this.width = 0;
        this.height = 0;
        this.mipCount = 0;
        this.view = null;
        this.texture = null;
        this.levels = [];
        this.ready = false;
        this.disposed = false;
        this.passDescriptor = { label: 'Conservative depth pyramid level' };
    }

    async initialize(initializeSource, reduceSource) {
        if (this.disposed || this.ready) throw new Error('Depth pyramid initialization requires a fresh owner.');
        const device = this.renderer.device;
        const limits = device.limits;
        if (limits.maxComputeWorkgroupSizeX < 8 || limits.maxComputeWorkgroupSizeY < 8
            || limits.maxComputeInvocationsPerWorkgroup < 64 || limits.maxStorageTexturesPerShaderStage < 1
            || limits.maxSampledTexturesPerShaderStage < 1 || limits.maxBindingsPerBindGroup < 2)
            throw new Error('The selected device does not support the conservative depth pyramid layout.');
        const initializeModule = device.createShaderModule({ label: 'Depth pyramid source', code: initializeSource });
        const reduceModule = device.createShaderModule({ label: 'Depth pyramid reduction', code: reduceSource });
        this.initializeLayout = device.createBindGroupLayout({ entries: [
            { binding: 0, visibility: GPUShaderStage.COMPUTE, texture: { sampleType: 'depth' } },
            { binding: 1, visibility: GPUShaderStage.COMPUTE, storageTexture: { access: 'write-only', format: 'r32float' } }] });
        this.reduceLayout = device.createBindGroupLayout({ entries: [
            { binding: 0, visibility: GPUShaderStage.COMPUTE, texture: { sampleType: 'unfilterable-float' } },
            { binding: 1, visibility: GPUShaderStage.COMPUTE, storageTexture: { access: 'write-only', format: 'r32float' } }] });
        const results = await Promise.all([
            device.createComputePipelineAsync({ label: 'Depth pyramid source',
                layout: device.createPipelineLayout({ bindGroupLayouts: [this.initializeLayout] }),
                compute: { module: initializeModule, entryPoint: 'initializeDepth' } }),
            device.createComputePipelineAsync({ label: 'Depth pyramid reduction',
                layout: device.createPipelineLayout({ bindGroupLayouts: [this.reduceLayout] }),
                compute: { module: reduceModule, entryPoint: 'reduceDepth' } }),
            initializeModule.getCompilationInfo(), reduceModule.getCompilationInfo()]);
        for (let i = 2; i < results.length; i++)
            for (const message of results[i].messages)
                if (message.type === 'error') throw new Error(`Depth pyramid shader ${message.lineNum}:${message.linePos}: ${message.message}`);
        if (this.disposed || this.renderer.device !== device)
            throw new DOMException('Depth pyramid startup was superseded.', 'AbortError');
        this.initializePipeline = results[0];
        this.reducePipeline = results[1];
        this.ready = true;
    }

    resize(width, height, depthView) {
        if (!this.ready || this.disposed) throw new Error('Depth pyramid is not initialized.');
        if (width === this.width && height === this.height && depthView === this.sourceView) return;
        this.commitResize(this.prepareResize(width, height, depthView));
    }

    prepareResize(width, height, depthView) {
        if (!this.ready || this.disposed) throw new Error('Depth pyramid is not initialized.');
        if (!Number.isSafeInteger(width) || !Number.isSafeInteger(height) || width < 0 || height < 0)
            throw new Error('Depth pyramid dimensions must be nonnegative integers.');
        if (width === 0 || height === 0)
            return { owner: this, consumed: false, texture: null, view: null, levels: [],
                sourceView: null, width: 0, height: 0, mipCount: 0 };
        const device = this.renderer.device;
        if (!depthView || width > device.limits.maxTextureDimension2D || height > device.limits.maxTextureDimension2D
            || Math.ceil(width / 8) > device.limits.maxComputeWorkgroupsPerDimension
            || Math.ceil(height / 8) > device.limits.maxComputeWorkgroupsPerDimension)
            throw new Error('Depth pyramid source or dispatch dimensions exceed the device limits.');
        const mipCount = Math.floor(Math.log2(Math.max(width, height))) + 1;
        const texture = device.createTexture({ label: 'Current-frame conservative depth pyramid',
            size: [width, height], mipLevelCount: mipCount, format: 'r32float',
            usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.STORAGE_BINDING });
        try {
            const view = texture.createView();
            const levels = new Array(mipCount);
            let source = depthView;
            for (let mip = 0; mip < mipCount; mip++) {
                const destination = texture.createView({ baseMipLevel: mip, mipLevelCount: 1 });
                const group = device.createBindGroup({ layout: mip === 0 ? this.initializeLayout : this.reduceLayout,
                    entries: [{ binding: 0, resource: source }, { binding: 1, resource: destination }] });
                levels[mip] = { group, groupsX: Math.ceil(Math.max(1, Math.floor(width / 2 ** mip)) / 8),
                    groupsY: Math.ceil(Math.max(1, Math.floor(height / 2 ** mip)) / 8) };
                source = destination;
            }
            return { owner: this, consumed: false, texture, view, levels, sourceView: depthView, width, height, mipCount };
        } catch (error) { this.renderer._retire(texture); throw error; }
    }

    commitResize(candidate) {
        if (!this.ready || this.disposed || candidate?.owner !== this || candidate.consumed)
            throw new Error('Depth pyramid resize candidate does not belong to this active owner.');
        // Candidate resources are complete before any installed resource retires.
        // Its levels array is distinct from the current generation's array.
        this.release();
        this.texture = candidate.texture;
        this.view = candidate.view;
        this.levels = candidate.levels;
        this.sourceView = candidate.sourceView;
        this.width = candidate.width;
        this.height = candidate.height;
        this.mipCount = candidate.mipCount;
        candidate.consumed = true;
    }

    discardResize(candidate) {
        if (candidate?.owner !== this || candidate.consumed) return;
        candidate.consumed = true;
        this.renderer._retire(candidate.texture);
        candidate.levels.length = 0;
    }

    encode(encoder) {
        if (!this.ready || this.disposed || !this.texture)
            throw new Error('Depth pyramid does not match a drawable surface.');
        for (let mip = 0; mip < this.mipCount; mip++) {
            const level = this.levels[mip];
            // Each pass owns disjoint source/destination mip views. Ending the
            // pass establishes the storage-write -> sampled-read dependency.
            const pass = encoder.beginComputePass(this.passDescriptor);
            pass.setPipeline(mip === 0 ? this.initializePipeline : this.reducePipeline);
            pass.setBindGroup(0, level.group);
            pass.dispatchWorkgroups(level.groupsX, level.groupsY);
            pass.end();
        }
    }

    release() {
        this.renderer._retire(this.texture);
        this.texture = null;
        this.view = null;
        this.sourceView = null;
        this.levels.length = 0;
        this.width = this.height = this.mipCount = 0;
    }

    dispose() {
        if (this.disposed) return;
        this.disposed = true;
        this.ready = false;
        this.release();
        this.initializePipeline = this.reducePipeline = null;
        this.initializeLayout = this.reduceLayout = null;
    }
}
