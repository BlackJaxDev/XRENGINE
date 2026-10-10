const schemas = {
    bindGroupLayout: ['label', 'entries'], entry: ['binding', 'visibility', 'buffer', 'sampler', 'texture', 'storageTexture'],
    buffer: ['type', 'hasDynamicOffset', 'minBindingSize'], sampler: ['type'],
    texture: ['sampleType', 'viewDimension', 'multisampled'], storageTexture: ['access', 'format', 'viewDimension'],
    pipelineLayout: ['label', 'bindGroupLayouts'],
    render: ['label', 'layout', 'vertex', 'fragment', 'primitive', 'depthStencil', 'multisample'],
    compute: ['label', 'layout', 'compute'],
    vertex: ['module', 'entryPoint', 'constants', 'buffers'], fragment: ['module', 'entryPoint', 'constants', 'targets'],
    stage: ['module', 'entryPoint', 'constants'], vertexBuffer: ['arrayStride', 'stepMode', 'attributes'],
    attribute: ['format', 'offset', 'shaderLocation'], target: ['format', 'blend', 'writeMask'], blend: ['color', 'alpha'],
    blendComponent: ['operation', 'srcFactor', 'dstFactor'],
    primitive: ['topology', 'stripIndexFormat', 'frontFace', 'cullMode', 'unclippedDepth'],
    depthStencil: ['format', 'depthWriteEnabled', 'depthCompare', 'stencilFront', 'stencilBack', 'stencilReadMask', 'stencilWriteMask', 'depthBias', 'depthBiasSlopeScale', 'depthBiasClamp'],
    stencil: ['compare', 'failOp', 'depthFailOp', 'passOp'], multisample: ['count', 'mask', 'alphaToCoverageEnabled'],
};
const children = {
    bindGroupLayout: { entries: 'entry[]' }, entry: { buffer: 'buffer', sampler: 'sampler', texture: 'texture', storageTexture: 'storageTexture' },
    pipelineLayout: { bindGroupLayouts: 'opaque[]' }, render: { layout: 'opaque', vertex: 'vertex', fragment: 'fragment', primitive: 'primitive', depthStencil: 'depthStencil', multisample: 'multisample' },
    compute: { layout: 'opaque', compute: 'stage' }, vertex: { module: 'opaque', constants: 'constants', buffers: 'vertexBuffer[]' },
    fragment: { module: 'opaque', constants: 'constants', targets: 'target[]' }, stage: { module: 'opaque', constants: 'constants' },
    vertexBuffer: { attributes: 'attribute[]' }, target: { blend: 'blend' }, blend: { color: 'blendComponent', alpha: 'blendComponent' },
    depthStencil: { stencilFront: 'stencil', stencilBack: 'stencil' },
};

/** Device-local immutable descriptor cache. Lookup and compilation are cold control operations. */
export class GpuPipelineCache {
    constructor(device, capacity = 128) {
        if (!Number.isInteger(capacity) || capacity < 1 || capacity > 1024) throw new RangeError('Pipeline cache capacity must be in [1, 1024].');
        this.device = device;
        this.capacity = capacity;
        this.entries = new Map();
        this.shaderModules = new Map();
        this.shaderModuleKeyBytes = 0;
        this.shaderModuleKeyLimit = 8 * 1024 * 1024;
        this.shaderModuleHits = 0;
        this.shaderModuleMisses = 0;
        this.identities = new WeakMap();
        this.nextIdentity = 1;
        this.generation = 1;
        this.disposed = false;
    }

    _snapshot(value, schema, depth = 0) {
        if (depth > 16) throw new RangeError('Pipeline descriptor nesting exceeds its bound.');
        if (schema === 'opaque') {
            if (value === 'auto') throw new TypeError('Cached pipelines require explicit binding layouts.');
            if (!value || typeof value !== 'object') throw new TypeError('A GPU object is required in the pipeline descriptor.');
            let id = this.identities.get(value);
            if (!id) { id = this.nextIdentity++; this.identities.set(value, id); }
            return { value, key: ['gpu', id] };
        }
        if (schema?.endsWith('[]')) {
            if (!Array.isArray(value) || value.length > 256) throw new TypeError('Pipeline descriptor array is missing or exceeds its bound.');
            const nested = schema.slice(0, -2);
            const snapshots = value.map(item => item === null ? { value: null, key: null } : this._snapshot(item, nested, depth + 1));
            return { value: Object.freeze(snapshots.map(item => item.value)), key: snapshots.map(item => item.key) };
        }
        if (schema) {
            if (!value || Object.getPrototypeOf(value) !== Object.prototype) throw new TypeError(`Expected a plain ${schema} descriptor.`);
            const keys = Object.keys(value).sort();
            if (keys.length > 256) throw new RangeError('Pipeline dictionary exceeds its bound.');
            const snapshot = {}, key = [];
            for (const name of keys) {
                if (schema !== 'constants' && !schemas[schema]?.includes(name)) throw new TypeError(`Unsupported ${schema} descriptor field: ${name}.`);
                if (value[name] === undefined) continue;
                const item = this._snapshot(value[name], children[schema]?.[name], depth + 1);
                snapshot[name] = item.value;
                key.push([name, item.key]);
            }
            return { value: Object.freeze(snapshot), key };
        }
        if (typeof value === 'number' && Number.isFinite(value) || typeof value === 'boolean' || typeof value === 'string' && value.length <= 4096)
            return { value, key: value };
        throw new TypeError('Unsupported value in immutable pipeline descriptor.');
    }

    _get(kind, descriptor, create, asynchronous) {
        if (this.disposed) throw new Error('Pipeline cache is disposed.');
        const snapshot = this._snapshot(descriptor, kind);
        const key = `${kind}:${JSON.stringify(snapshot.key)}`;
        const cached = this.entries.get(key);
        if (cached) {
            this.entries.delete(key);
            this.entries.set(key, cached);
            return cached.value;
        }
        if (this.entries.size >= this.capacity) {
            let removed = false;
            for (const [oldKey, entry] of this.entries) {
                if (!entry.pending) { this.entries.delete(oldKey); removed = true; break; }
            }
            if (!removed) throw new Error('Pipeline cache is full of pending compilations.');
        }
        const generation = this.generation;
        const entry = { pending: asynchronous, value: null };
        const result = create(snapshot.value);
        if (asynchronous) {
            entry.value = Promise.resolve(result).then(value => {
                if (this.disposed || this.generation !== generation) throw new Error('Pipeline compilation completed for an obsolete cache.');
                entry.pending = false;
                return value;
            }, error => {
                if (this.entries.get(key) === entry) this.entries.delete(key);
                throw error;
            });
        } else entry.value = result;
        this.entries.set(key, entry);
        return entry.value;
    }

    getBindGroupLayout(descriptor) { return this._get('bindGroupLayout', descriptor, value => this.device.createBindGroupLayout(value), false); }
    getPipelineLayout(descriptor) { return this._get('pipelineLayout', descriptor, value => this.device.createPipelineLayout(value), false); }
    getRenderPipelineAsync(descriptor) { return this._get('render', descriptor, value => this.device.createRenderPipelineAsync(value), true); }
    getComputePipelineAsync(descriptor) { return this._get('compute', descriptor, value => this.device.createComputePipelineAsync(value), true); }

    _createUncachedShaderModule(create) {
        try { return { value: Promise.resolve(create()), reused: false }; }
        catch (error) { return { value: Promise.reject(error), reused: false }; }
    }

    /** The creator supplies the complete scoped preparation, including compilation diagnostics. */
    getShaderModuleAsync(descriptor, create) {
        if (this.disposed) throw new Error('Pipeline cache is disposed.');
        if (!descriptor || Object.getPrototypeOf(descriptor) !== Object.prototype ||
            Object.keys(descriptor).some(key => key !== 'code' && key !== 'label') ||
            Object.getOwnPropertySymbols(descriptor).length !== 0 ||
            typeof descriptor.code !== 'string' || typeof descriptor.label !== 'string')
            throw new TypeError('Unsupported shader module descriptor.');
        const key = JSON.stringify([descriptor.code, descriptor.label]);
        const bytes = key.length * 2;
        const cached = this.shaderModules.get(key);
        if (cached) {
            this.shaderModuleHits++;
            this.shaderModules.delete(key);
            this.shaderModules.set(key, cached);
            return { value: cached.value, reused: true };
        }
        this.shaderModuleMisses++;
        // Oversized sources and a cache occupied entirely by pending work still compile.
        // Neither case may reduce the renderer's existing preparation concurrency.
        if (bytes > this.shaderModuleKeyLimit) return this._createUncachedShaderModule(create);
        while (this.shaderModules.size >= this.capacity || this.shaderModuleKeyBytes + bytes > this.shaderModuleKeyLimit) {
            let removed = false;
            for (const [oldKey, entry] of this.shaderModules) {
                if (entry.pending) continue;
                this.shaderModules.delete(oldKey);
                this.shaderModuleKeyBytes -= entry.bytes;
                removed = true;
                break;
            }
            if (!removed) return this._createUncachedShaderModule(create);
        }
        const generation = this.generation;
        const entry = { value: null, bytes, pending: true };
        // Start the creator synchronously so its native call is enclosed by its own error scopes.
        try { entry.value = Promise.resolve(create()).then(value => {
            if (this.disposed || this.generation !== generation)
                throw new Error('Shader module completed for an obsolete cache.');
            entry.pending = false;
            return value;
        }, error => {
            if (this.shaderModules.get(key) === entry) {
                this.shaderModules.delete(key);
                this.shaderModuleKeyBytes -= bytes;
            }
            throw error;
        }); }
        catch (error) { return { value: Promise.reject(error), reused: false }; }
        this.shaderModules.set(key, entry);
        this.shaderModuleKeyBytes += bytes;
        return { value: entry.value, reused: false };
    }

    clear() { this.generation++; this.entries.clear(); this.shaderModules.clear(); this.shaderModuleKeyBytes = 0; }
    dispose() { this.clear(); this.disposed = true; this.device = undefined; }
}
