import { pipelineMaximumItems } from './pipeline-frame-packet.js';

const maximumViews = 16;
const recordWords = 40;
const nodeBytes = 48;
const headerBytes = 16;

/** WebGPU lowering of GpuBvhTree's Morton/LBVH layout over the published draw slots. */
export class BrowserGpuSceneBvh {
    constructor(renderer, records, recordBuffer) {
        this.renderer = renderer; this.records = records; this.recordBuffer = recordBuffer;
        this.recordFloats = new Float32Array(records.buffer);
        this.previousBounds = new Uint32Array(pipelineMaximumItems * 6);
        this.frameWords = new Uint32Array(16); this.frameFloats = new Float32Array(this.frameWords.buffer);
        this.normalizationDomain = new Float64Array(6);
        this.viewWords = new Uint32Array(maximumViews * 4);
        this.count = 0; this.viewCount = 0; this.paddedCount = 1; this.consecutiveRefits = 0;
        this.pendingBuild = false; this.pendingRefit = false; this.disposed = false;
        this.publicationFaulted = false;
        this.passDescriptor = { label: 'Canonical scene LBVH publication' };
        this.sortGroups = []; this.sortWidths = [];
        this.stats = { builds: 0, refits: 0, cleanFrames: 0, buildDispatches: 0,
            nodeStrideBytes: nodeBytes, headerBytes, maximumViews, synchronousReadbackBytes: 0,
            topology: 'canonical-karras-lbvh', publication: 'retained-focused-draw-slots',
            overflowPolicy: 'GPU-resident fault flag and conservative visible candidates' };
    }

    async initialize(source) {
        const device = this.renderer.device, limits = device.limits;
        if (limits.maxStorageBuffersPerShaderStage < 6 || limits.maxBindingsPerBindGroup < 8
            || limits.maxUniformBuffersPerShaderStage < 2 || limits.maxUniformBufferBindingSize < 64)
            throw new Error('Required canonical BVH path needs six storage bindings, eight group bindings and two uniform bindings.');
        this.nodeBuffer = device.createBuffer({ label: 'Canonical compact scene BVH', size: headerBytes + (pipelineMaximumItems * 2 - 1) * nodeBytes, usage: GPUBufferUsage.STORAGE });
        this.mortonBuffer = device.createBuffer({ label: 'Canonical Morton object pairs', size: pipelineMaximumItems * 8, usage: GPUBufferUsage.STORAGE });
        this.statusBuffer = device.createBuffer({ label: 'Resident BVH topology and traversal faults', size: 16, usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_SRC });
        this.viewBuffer = device.createBuffer({ label: 'Published BVH view ranges', size: maximumViews * 16, usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST });
        this.frameBuffer = device.createBuffer({ size: 64, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
        const sortStride = Math.max(256, limits.minUniformBufferOffsetAlignment);
        const sortWords = sortStride / 4;
        const sortParameters = new Uint32Array(79 * sortWords);
        let sortIndex = 1;
        for (let width = 2; width <= pipelineMaximumItems; width *= 2)
            for (let distance = width / 2; distance; distance /= 2) {
                sortParameters[sortIndex * sortWords] = width; sortParameters[sortIndex * sortWords + 1] = distance;
                this.sortWidths.push(width); sortIndex++;
            }
        this.sortBuffer = device.createBuffer({ size: sortParameters.byteLength, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
        device.queue.writeBuffer(this.sortBuffer, 0, sortParameters);
        this.layout = device.createBindGroupLayout({ entries: [
            { binding: 0, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'read-only-storage', minBindingSize: 160 } },
            { binding: 1, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'storage', minBindingSize: 8 } },
            { binding: 2, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'storage', minBindingSize: 64 } },
            { binding: 3, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'storage', minBindingSize: 16 } },
            { binding: 4, visibility: GPUShaderStage.COMPUTE, buffer: { minBindingSize: 64 } },
            { binding: 5, visibility: GPUShaderStage.COMPUTE, buffer: { minBindingSize: 16 } }] });
        for (let i = 0; i < sortIndex; i++) {
            const group = device.createBindGroup({ layout: this.layout, entries: [
                { binding: 0, resource: { buffer: this.recordBuffer } },
                { binding: 1, resource: { buffer: this.mortonBuffer } },
                { binding: 2, resource: { buffer: this.nodeBuffer } },
                { binding: 3, resource: { buffer: this.statusBuffer } },
                { binding: 4, resource: { buffer: this.frameBuffer } },
                { binding: 5, resource: { buffer: this.sortBuffer, offset: i * sortStride, size: 16 } }] });
            if (!i) this.group = group; else this.sortGroups.push(group);
        }
        const module = device.createShaderModule({ label: 'Canonical Morton and LBVH backend port', code: source });
        const layout = device.createPipelineLayout({ bindGroupLayouts: [this.layout] });
        const names = ['mortonMain', 'sortMain', 'initializeMain', 'connectMain', 'parentsMain', 'rootMain', 'refitMain'];
        const pending = names.map(entryPoint => device.createComputePipelineAsync({ layout, compute: { module, entryPoint } }));
        const [info, pipelines] = await Promise.all([module.getCompilationInfo(), Promise.all(pending)]);
        for (const message of info.messages)
            if (message.type === 'error') throw new Error(`Scene LBVH ${message.lineNum}:${message.linePos}: ${message.message}`);
        if (this.disposed) throw new DOMException('Scene LBVH startup superseded.', 'AbortError');
        this.pipelines = pipelines;
    }

    prepare(count) {
        const rebuildAfterFailure = this.publicationFaulted;
        this.publicationFaulted = true;
        let dirty = count !== this.count;
        let minX = Infinity, minY = Infinity, minZ = Infinity, maxX = -Infinity, maxY = -Infinity, maxZ = -Infinity;
        let views = 0, first = 0, viewsChanged = rebuildAfterFailure;
        for (let i = 0; i < count; i++) {
            const at = i * recordWords, previous = i * 6;
            for (let j = 0; j < 6; j++) {
                const value = this.records[at + (j < 3 ? 4 + j : 5 + j)];
                if (this.previousBounds[previous + j] !== value) dirty = true;
                this.previousBounds[previous + j] = value;
            }
            minX = Math.min(minX, this.recordFloats[at + 4]); minY = Math.min(minY, this.recordFloats[at + 5]); minZ = Math.min(minZ, this.recordFloats[at + 6]);
            maxX = Math.max(maxX, this.recordFloats[at + 8]); maxY = Math.max(maxY, this.recordFloats[at + 9]); maxZ = Math.max(maxZ, this.recordFloats[at + 10]);
            let sameView = i > 0;
            for (let j = 16; sameView && j < 36; j++)
                sameView = this.records[at + j] === this.records[(i - 1) * recordWords + j];
            if (!sameView) {
                if (views === maximumViews) throw new Error('Required BVH publication exceeds sixteen contiguous view groups.');
                if (views) {
                    const at = (views - 1) * 4 + 1;
                    viewsChanged ||= this.viewWords[at] !== i - first;
                    this.viewWords[at] = i - first;
                }
                first = i; viewsChanged ||= this.viewWords[views * 4] !== first;
                this.viewWords[views * 4] = first; views++;
            }
        }
        if (views) {
            const at = (views - 1) * 4 + 1;
            viewsChanged ||= this.viewWords[at] !== count - first;
            this.viewWords[at] = count - first;
        }
        viewsChanged ||= views !== this.viewCount;
        const domain = this.normalizationDomain;
        const escaped = count > 0 && (minX < domain[0] || minY < domain[1] || minZ < domain[2]
            || maxX > domain[3] || maxY > domain[4] || maxZ > domain[5]);
        this.pendingBuild = rebuildAfterFailure || count !== this.count || (dirty && (escaped || this.consecutiveRefits >= 120));
        this.pendingRefit = dirty && !this.pendingBuild;
        this.count = count; this.viewCount = views; this.paddedCount = 1;
        while (this.paddedCount < count) this.paddedCount *= 2;
        this.frameWords[0] = count; this.frameWords[1] = this.paddedCount; this.frameWords[2] = views;
        if (count && this.pendingBuild) {
            // Match GPUScene's live-bounds margin; escaping the retained domain requests a new Morton order.
            const marginX = Math.max((maxX - minX) * 0.1, 0.5), marginY = Math.max((maxY - minY) * 0.1, 0.5), marginZ = Math.max((maxZ - minZ) * 0.1, 0.5);
            domain[0] = minX - marginX; domain[1] = minY - marginY; domain[2] = minZ - marginZ;
            domain[3] = maxX + marginX; domain[4] = maxY + marginY; domain[5] = maxZ + marginZ;
            this.frameFloats[4] = domain[0]; this.frameFloats[5] = domain[1]; this.frameFloats[6] = domain[2];
            this.frameFloats[8] = domain[3] - domain[0];
            this.frameFloats[9] = domain[4] - domain[1];
            this.frameFloats[10] = domain[5] - domain[2];
            for (let i = 4; i <= 10; i++)
                if (!Number.isFinite(this.frameFloats[i])) throw new Error('World bounds exceed the finite BVH normalization domain.');
        }
        const queue = this.renderer.device.queue;
        if (this.pendingBuild) queue.writeBuffer(this.frameBuffer, 0, this.frameWords);
        if (views && viewsChanged) queue.writeBuffer(this.viewBuffer, 0, this.viewWords.buffer, 0, views * 16);
        this.publicationFaulted = false;
    }

    encode(encoder) {
        if (!this.count) return;
        const groups = Math.ceil(this.count / 64);
        if (this.pendingBuild) {
            this._dispatch(encoder, 0, Math.ceil(this.paddedCount / 64), this.group);
            for (let i = 0; i < this.sortGroups.length && this.sortWidths[i] <= this.paddedCount; i++)
                this._dispatch(encoder, 1, Math.ceil(this.paddedCount / 64), this.sortGroups[i]);
            this._dispatch(encoder, 2, Math.ceil((this.count * 2 - 1) / 64), this.group);
            if (this.count > 1) {
                this._dispatch(encoder, 3, groups, this.group);
                this._dispatch(encoder, 4, groups, this.group);
            }
            this._dispatch(encoder, 5, 1, this.group);
            this._dispatch(encoder, 6, Math.ceil((this.count * 2 - 1) / 64), this.group);
            this.consecutiveRefits = 0; this.stats.builds++;
        } else if (this.pendingRefit) {
            this._dispatch(encoder, 6, Math.ceil((this.count * 2 - 1) / 64), this.group);
            this.consecutiveRefits++; this.stats.refits++;
        } else this.stats.cleanFrames++;
        this.pendingBuild = false; this.pendingRefit = false;
    }

    _dispatch(encoder, index, count, group) {
        const pass = encoder.beginComputePass(this.passDescriptor);
        pass.setPipeline(this.pipelines[index]); pass.setBindGroup(0, group); pass.dispatchWorkgroups(count); pass.end();
        this.stats.buildDispatches++;
    }

    dispose() {
        if (this.disposed) return;
        this.disposed = true;
        this.renderer._retire(this.nodeBuffer); this.renderer._retire(this.mortonBuffer); this.renderer._retire(this.statusBuffer);
        this.renderer._retire(this.viewBuffer); this.renderer._retire(this.frameBuffer); this.renderer._retire(this.sortBuffer);
        this.nodeBuffer = null; this.mortonBuffer = null; this.statusBuffer = null; this.viewBuffer = null;
        this.frameBuffer = null; this.sortBuffer = null; this.group = null; this.layout = null; this.pipelines = null;
        this.sortGroups.length = 0; this.records = null; this.recordFloats = null; this.previousBounds = null;
        this.frameWords = null; this.frameFloats = null; this.viewWords = null;
        this.normalizationDomain = null;
    }
}
