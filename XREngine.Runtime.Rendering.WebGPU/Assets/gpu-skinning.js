const maximumPacketBytes = 8 * 1024 * 1024;
const maximumJobs = 256;
const maximumResidentBytes = 64 * 1024 * 1024;
const maximumResidentVertices = 262144;

function integer(value, minimum, maximum, name) {
    if (!Number.isSafeInteger(value) || value < minimum || value > maximum)
        throw new RangeError(`Invalid compute skinning ${name}.`);
    return value;
}

function copy(memory, destination) {
    if (typeof memory?.copyTo === 'function') memory.copyTo(destination);
    else if (ArrayBuffer.isView(memory)) destination.set(new Uint8Array(memory.buffer, memory.byteOffset, memory.byteLength));
    else throw new TypeError('Compute skinning requires a borrowed byte view.');
}

/** WebGPU lowering of SkinningPrepass.comp's canonical Core4/spill, affine palette and sparse morph contracts. */
export class GpuSkinning {
    constructor(renderer) {
        this.renderer = renderer;
        this.jobs = new Map();
        this.orderedJobs = [];
        this.ready = false;
        this.disposed = false;
        this.passDescriptor = { label: 'Packed skinning and sparse morph deformation' };
        this.stats = { dispatches: 0, reusedOutputs: 0, uploadedBytes: 0, vertices: 0,
            residentBytes: 0, residentVertices: 0, residentJobs: 0 };
    }

    async initialize(source) {
        const device = this.renderer.device;
        const limits = device.limits;
        if (limits.maxStorageBuffersPerShaderStage < 5 || limits.maxBindingsPerBindGroup < 6 ||
            limits.maxComputeInvocationsPerWorkgroup < 64 || limits.maxComputeWorkgroupSizeX < 64)
            throw new Error('Compute skinning requires five storage bindings and 64-thread workgroups.');
        const module = device.createShaderModule({ code: source, label: 'Canonical packed skinning' });
        const compilation = module.getCompilationInfo();
        const layout = device.createBindGroupLayout({ entries: [
            { binding: 0, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'read-only-storage' } },
            { binding: 1, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'read-only-storage' } },
            { binding: 2, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'read-only-storage' } },
            { binding: 3, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'storage' } },
            { binding: 4, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'storage' } },
            { binding: 5, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'uniform', minBindingSize: 16 } }
        ] });
        const pendingPipeline = device.createComputePipelineAsync({
            label: 'Canonical packed skinning', layout: device.createPipelineLayout({ bindGroupLayouts: [layout] }),
            compute: { module, entryPoint: 'skin' }
        });
        // Allocate under the caller's error scopes before yielding; cancellation
        // cannot resume into late device allocations after disposal.
        const [info, pipeline] = await Promise.all([compilation, pendingPipeline]);
        if (this.disposed || device !== this.renderer.device) throw new Error('Compute skinning startup was superseded.');
        const errors = info.messages.filter(message => message.type === 'error');
        if (errors.length) throw new Error(errors.map(message => message.message).join('\n'));
        this.layout = layout;
        this.pipeline = pipeline;
        this.ready = true;
    }

    configure(handle, memory) {
        const r = this.renderer;
        r._requireOwner();
        if (!this.ready || this.disposed) throw new Error('Compute skinning was not explicitly enabled at renderer startup.');
        if (this.jobs.has(handle)) throw new Error('Release the existing skinning binding before replacing its immutable inputs.');
        if (this.jobs.size >= maximumJobs) throw new RangeError('Compute skinning resident job capacity is exhausted.');
        const mesh = r._resources.getHandle(handle, 'mesh', r._owner);
        const byteLength = integer(memory?.byteLength, 256, maximumPacketBytes, 'packet bytes');
        if (byteLength % 4) throw new RangeError('Compute skinning data must be word-aligned.');
        const bytes = new Uint8Array(byteLength);
        copy(memory, bytes);
        const words = new Uint32Array(bytes.buffer), floats = new Float32Array(bytes.buffer);
        if (words[0] !== 0x534b5258 || words[1] !== 1 || words[23] * 4 !== byteLength)
            throw new Error('Unknown packed skinning adapter layout.');
        const vertices = integer(words[2], 3, 16384, 'vertex count');
        const bones = integer(words[3], 0, 1024, 'palette count');
        const format = integer(words[4], 1, 2, 'core index format');
        const flags = integer(words[5], 0, 31, 'flags');
        const shapes = integer(words[6], 0, 256, 'shape count');
        if (vertices !== mesh.vertexCount || words[7] !== shapes || Boolean(flags & 4) !== (bones > 0))
            throw new Error('Skinning packet does not match its mesh or palette.');
        integer(words[19], 1, 259, 'influence cap');
        const records = integer(words[20], 0, 65536, 'sparse record count');
        const deltas = integer(words[21], 0, 65536, 'delta count');
        const spillCount = integer(words[22], 0, 65536, 'spill entry count');
        if (!Number.isFinite(floats[24]) || floats[24] < 0) throw new RangeError('Invalid morph weight threshold.');
        for (let i = 25; i < 64; i++) if (words[i] !== 0) throw new Error('Reserved skinning header words must be zero.');
        const lengths = [vertices * 5, flags & 1 ? vertices * 3 : 0, flags & 2 ? vertices * 4 : 0,
            vertices * format, vertices, flags & 16 ? vertices : 0, spillCount,
            shapes * 4, records * 4, deltas * 2, shapes * 16];
        let end = 64;
        for (let i = 0; i < lengths.length; i++) {
            if (words[8 + i] !== end) throw new Error('Skinning arena sections must be contiguous and non-overlapping.');
            end += lengths[i];
        }
        if (end !== words.length || (!(flags & 16) && spillCount)) throw new Error('Skinning arena size or spill layout is invalid.');
        for (let i = words[8]; i < words[11]; i++) if (!Number.isFinite(floats[i])) throw new Error('Bind vertices must be finite.');
        for (let i = words[18]; i < end; i++) if (!Number.isFinite(floats[i])) throw new Error('Morph quantization metadata must be finite.');
        for (let vertex = 0; vertex < vertices; vertex++) {
            const weight = words[words[12] + vertex];
            for (let lane = 0; lane < 4; lane++) {
                const packed = words[words[11] + vertex * format + (format === 1 ? 0 : lane >> 1)];
                const bone = format === 1 ? (packed >>> (lane * 8)) & 255 : (packed >>> ((lane & 1) * 16)) & 65535;
                if (bones && ((weight >>> (lane * 8)) & 255) && bone >= bones)
                    throw new RangeError('A core influence addresses an absent palette bone.');
            }
            if (flags & 16) {
                const header = words[words[13] + vertex], start = header & 0xffffff, count = header >>> 24;
                if (start + count > spillCount) throw new RangeError('Spill influence range exceeds the canonical entries.');
            }
        }
        for (let i = 0; i < spillCount; i++) {
            const entry = words[words[14] + i];
            if (entry >>> 24 || (bones && ((entry >>> 16) & 255) && (entry & 65535) >= bones))
                throw new RangeError('Invalid packed spill influence.');
        }
        if (deltas && (words[words[17]] || words[words[17] + 1])) throw new Error('Morph delta zero must be the null sentinel.');
        for (let shape = 0; shape < shapes; shape++) {
            const base = words[15] + shape * 4, start = words[base], count = words[base + 1];
            if (start + count > records) throw new RangeError('Sparse morph range exceeds its records.');
            let previous = -1;
            for (let i = start; i < start + count; i++) {
                const record = words[16] + i * 4, vertex = words[record];
                if (vertex <= previous || vertex >= vertices) throw new Error('Sparse morph vertices must be unique and sorted per shape.');
                previous = vertex;
                for (let lane = 1; lane < 4; lane++)
                    if (words[record + lane] !== 0 && words[record + lane] >= deltas)
                        throw new RangeError('Sparse morph delta exceeds its packed array.');
            }
        }
        const sizes = [byteLength, Math.max(48, bones * 48), Math.max(8, shapes * 8), mesh.vertexBytes, vertices * 32];
        const residentBytes = sizes[0] + sizes[1] + sizes[2] + sizes[3] + sizes[4] + 16;
        if (this.stats.residentBytes + residentBytes > maximumResidentBytes ||
            this.stats.residentVertices + vertices > maximumResidentVertices)
            throw new RangeError('Compute skinning exceeds its 64 MiB or 262144-vertex resident budget.');
        for (const size of sizes)
            if (size > r.device.limits.maxBufferSize || size > r.device.limits.maxStorageBufferBindingSize)
                throw new RangeError('Skinning payload exceeds the selected device buffer limits.');
        const groups = Math.ceil(vertices / 64);
        if (groups > r.device.limits.maxComputeWorkgroupsPerDimension) throw new RangeError('Skinning dispatch exceeds the selected device limit.');
        const buffers = [];
        try {
            for (let i = 0; i < 5; i++) {
                if (i === 3) { buffers.push(mesh.vertexBuffer); continue; }
                const usage = GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST | (i === 4 ? GPUBufferUsage.VERTEX | GPUBufferUsage.COPY_SRC : 0);
                buffers.push(r.device.createBuffer({ size: sizes[i], usage, label: 'Packed skinning buffer' }));
            }
            buffers.push(r.device.createBuffer({ size: 16, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST }));
            const palette = new Float32Array(Math.max(12, bones * 12));
            for (let i = 0; i < Math.max(1, bones); i++) palette[i * 12] = palette[i * 12 + 5] = palette[i * 12 + 10] = 1;
            r.device.queue.writeBuffer(buffers[0], 0, bytes);
            r.device.queue.writeBuffer(buffers[1], 0, palette);
            const group = r.device.createBindGroup({ layout: this.layout,
                entries: buffers.map((buffer, binding) => ({ binding, resource: { buffer } })) });
            const job = { handle, mesh, buffers, group, groups, vertices, bones, shapes, residentBytes, dirty: true, activeCount: 0,
                paletteBytes: new Uint8Array(palette.buffer), paletteStaging: new Uint8Array(bones * 48),
                morphBytes: new Uint8Array(Math.max(8, shapes * 8)), morphStaging: new Uint8Array(shapes * 8),
                morphSeen: new Uint8Array(shapes), uniform: new Uint32Array(4) };
            job.paletteView = new Float32Array(job.paletteStaging.buffer);
            job.morphView = new Float32Array(job.morphStaging.buffer);
            mesh.computeSkinned = true;
            mesh.skinAttributes = buffers[4];
            this.jobs.set(handle, job);
            this.orderedJobs.push(job);
            this.stats.residentBytes += residentBytes;
            this.stats.residentVertices += vertices;
            this.stats.residentJobs++;
        } catch (error) {
            for (let i = 0; i < buffers.length; i++) if (i !== 3) r._retire(buffers[i]);
            throw error;
        }
    }

    update(handle, paletteMemory, activeMorphMemory) {
        const r = this.renderer;
        r._requireOwner();
        const job = this.jobs.get(handle);
        if (!job || job.mesh !== r._resources.getHandle(handle, 'mesh', r._owner)) throw new Error('Obsolete compute skinning binding.');
        const morphLength = integer(activeMorphMemory?.byteLength, 0, job.shapes * 8, 'active morph bytes');
        if (paletteMemory?.byteLength !== job.bones * 48 || morphLength % 8)
            throw new RangeError('Deformation update must match its affine palette and active morph record layout.');
        if (job.bones) copy(paletteMemory, job.paletteStaging);
        if (morphLength) copy(activeMorphMemory, job.morphStaging);
        for (let i = 0; i < job.paletteView.length; i++)
            if (!Number.isFinite(job.paletteView[i])) throw new Error('Skinning palette contains a non-finite component.');
        job.morphSeen.fill(0);
        for (let i = 0; i < morphLength / 4; i += 2) {
            const shape = job.morphView[i], weight = job.morphView[i + 1];
            integer(shape, 0, job.shapes - 1, 'active morph index');
            if (job.morphSeen[shape] || !Number.isFinite(weight) || Math.abs(weight) > 100)
                throw new Error('Active morph records require unique shape indices and bounded finite weights.');
            job.morphSeen[shape] = 1;
        }
        let paletteChanged = false, morphChanged = job.activeCount * 8 !== morphLength;
        for (let i = 0; i < job.paletteStaging.length; i++) if (job.paletteBytes[i] !== job.paletteStaging[i]) paletteChanged = true;
        for (let i = 0; i < morphLength; i++) if (job.morphBytes[i] !== job.morphStaging[i]) morphChanged = true;
        if (paletteChanged) {
            job.paletteBytes.set(job.paletteStaging);
            r.device.queue.writeBuffer(job.buffers[1], 0, job.paletteBytes);
            this.stats.uploadedBytes += job.paletteBytes.length;
        }
        if (morphChanged) {
            job.morphBytes.set(job.morphStaging);
            if (morphLength) r.device.queue.writeBuffer(job.buffers[2], 0, job.morphBytes, 0, morphLength);
            job.activeCount = morphLength / 8;
            job.uniform[0] = job.activeCount;
            r.device.queue.writeBuffer(job.buffers[5], 0, job.uniform);
            this.stats.uploadedBytes += morphLength + 16;
        }
        job.dirty ||= paletteChanged || morphChanged;
    }

    encode(encoder) {
        let pass;
        for (let i = 0; i < this.orderedJobs.length; i++) {
            const job = this.orderedJobs[i];
            if (!job.dirty) { this.stats.reusedOutputs++; continue; }
            if (!pass) { pass = encoder.beginComputePass(this.passDescriptor); pass.setPipeline(this.pipeline); }
            pass.setBindGroup(0, job.group);
            pass.dispatchWorkgroups(job.groups);
            job.dirty = false;
            this.stats.dispatches++;
            this.stats.vertices += job.vertices;
        }
        pass?.end();
    }

    releaseMesh(handle) {
        const job = this.jobs.get(handle);
        if (!job) return;
        this.jobs.delete(handle);
        this.orderedJobs.splice(this.orderedJobs.indexOf(job), 1);
        this.stats.residentBytes -= job.residentBytes;
        this.stats.residentVertices -= job.vertices;
        this.stats.residentJobs--;
        job.mesh.computeSkinned = false;
        job.mesh.skinAttributes = undefined;
        for (let i = 0; i < job.buffers.length; i++) if (i !== 3) this.renderer._retire(job.buffers[i]);
    }

    dispose() {
        this.disposed = true;
        this.ready = false;
        while (this.orderedJobs.length) this.releaseMesh(this.orderedJobs[this.orderedJobs.length - 1].handle);
        this.pipeline = this.layout = undefined;
    }
}
