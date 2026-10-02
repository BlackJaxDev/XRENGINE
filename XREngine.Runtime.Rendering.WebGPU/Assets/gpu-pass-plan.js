const depthFormats = new Set(['depth16unorm', 'depth24plus', 'depth24plus-stencil8', 'depth32float', 'depth32float-stencil8']);
const stencilFormats = new Set(['stencil8', 'depth24plus-stencil8', 'depth32float-stencil8']);

function fields(value, allowed) {
    if (!value || typeof value !== 'object' || Array.isArray(value)) throw new TypeError('An attachment plan object is required.');
    for (const key of Object.keys(value))
        if (!allowed.includes(key)) throw new TypeError(`Unsupported attachment plan field: ${key}.`);
}

function operation(value, allowed, name) {
    if (!allowed.includes(value)) throw new TypeError(`Invalid attachment ${name}.`);
    return value;
}

function clearColor(value) {
    if (!Array.isArray(value) || value.length !== 4 || value.some(item => typeof item !== 'number' || !Number.isFinite(item)))
        throw new TypeError('Color clear must contain four finite numbers.');
    return { r: value[0], g: value[1], b: value[2], a: value[3] };
}

/** Lowers immutable attachment plans; only acquired canvas views change between executions. */
export class GpuPassPlan {
    constructor(resources, owner, plan, canvas = {}, debugName = 'WebGPU render pass') {
        fields(plan, ['colors', 'depthStencil']);
        if (!plan || !Array.isArray(plan.colors) || plan.colors.length > 8)
            throw new TypeError('A render pass requires at most eight color attachment slots.');
        this.resources = resources;
        this.owner = owner;
        this.resourceHandles = [];
        this.bindings = [];
        this.signature = { colorFormats: [], depthStencilFormat: null, sampleCount: 0, width: 0, height: 0, depthReadOnly: false, stencilReadOnly: false };
        this.descriptor = { label: debugName, colorAttachments: [] };
        this._subresources = new Set();
        for (let slot = 0; slot < plan.colors.length; slot++) {
            const color = plan.colors[slot];
            if (color === null) { this.descriptor.colorAttachments.push(null); this.signature.colorFormats.push(null); continue; }
            fields(color, ['viewHandle', 'resolveTargetHandle', 'loadOp', 'storeOp', 'clearValue']);
            const source = this._resolve(color.viewHandle, canvas);
            this._validateView(source, false);
            this._use(source);
            this.signature.colorFormats.push(source.format);
            const attachment = {
                view: source.view,
                loadOp: operation(color.loadOp, ['load', 'clear'], 'load operation'),
                storeOp: operation(color.storeOp, ['store', 'discard'], 'store operation'),
                clearValue: clearColor(color.clearValue ?? [0, 0, 0, 0]),
            };
            this.bindings.push({ handle: color.viewHandle, source, attachment, key: 'view' });
            if (color.resolveTargetHandle !== undefined && color.resolveTargetHandle !== null) {
                const target = this._resolve(color.resolveTargetHandle, canvas);
                this._validateView(target, false, true);
                if (source.sampleCount <= 1 || target.sampleCount !== 1 || target.format !== source.format
                    || target.width !== source.width || target.height !== source.height)
                    throw new Error('Color resolve target must match a multisampled source and have one sample.');
                this._use(target);
                attachment.resolveTarget = target.view;
                this.bindings.push({ handle: color.resolveTargetHandle, source: target, attachment, key: 'resolveTarget' });
            }
            this.descriptor.colorAttachments.push(attachment);
        }
        if (plan.depthStencil !== undefined && plan.depthStencil !== null) {
            const depth = plan.depthStencil;
            fields(depth, ['viewHandle', 'depthReadOnly', 'depthLoadOp', 'depthStoreOp', 'depthClearValue',
                'stencilReadOnly', 'stencilLoadOp', 'stencilStoreOp', 'stencilClearValue']);
            for (const flag of ['depthReadOnly', 'stencilReadOnly'])
                if (depth[flag] !== undefined && typeof depth[flag] !== 'boolean')
                    throw new TypeError('Attachment read-only policy must be boolean.');
            if (depth.depthReadOnly && (depth.depthLoadOp !== undefined || depth.depthStoreOp !== undefined)
                || depth.stencilReadOnly && (depth.stencilLoadOp !== undefined || depth.stencilStoreOp !== undefined))
                throw new TypeError('Read-only attachment aspects cannot specify load/store operations.');
            const source = this._resolve(depth.viewHandle, canvas);
            this._validateView(source, true);
            this._use(source);
            const attachment = { view: source.view };
            const hasDepth = depthFormats.has(source.format) && source.aspect !== 'stencil-only';
            const hasStencil = stencilFormats.has(source.format) && source.aspect !== 'depth-only';
            if (hasDepth) {
                attachment.depthReadOnly = depth.depthReadOnly === true;
                this.signature.depthReadOnly = attachment.depthReadOnly;
                if (attachment.depthReadOnly && (depth.depthLoadOp !== undefined || depth.depthStoreOp !== undefined))
                    throw new Error('Read-only depth attachments cannot declare load/store operations.');
                if (!attachment.depthReadOnly) {
                    attachment.depthLoadOp = operation(depth.depthLoadOp, ['load', 'clear'], 'depth load operation');
                    attachment.depthStoreOp = operation(depth.depthStoreOp, ['store', 'discard'], 'depth store operation');
                    if (!Number.isFinite(depth.depthClearValue) || depth.depthClearValue < 0 || depth.depthClearValue > 1)
                        throw new RangeError('Depth clear must be in [0, 1].');
                    attachment.depthClearValue = depth.depthClearValue;
                }
            } else if (depth.depthLoadOp !== undefined || depth.depthStoreOp !== undefined)
                throw new Error('Depth operations require a depth aspect.');
            if (hasStencil) {
                attachment.stencilReadOnly = depth.stencilReadOnly === true;
                this.signature.stencilReadOnly = attachment.stencilReadOnly;
                if (attachment.stencilReadOnly && (depth.stencilLoadOp !== undefined || depth.stencilStoreOp !== undefined))
                    throw new Error('Read-only stencil attachments cannot declare load/store operations.');
                if (!attachment.stencilReadOnly) {
                    attachment.stencilLoadOp = operation(depth.stencilLoadOp, ['load', 'clear'], 'stencil load operation');
                    attachment.stencilStoreOp = operation(depth.stencilStoreOp, ['store', 'discard'], 'stencil store operation');
                    if (!Number.isInteger(depth.stencilClearValue) || depth.stencilClearValue < 0 || depth.stencilClearValue > 0xffffffff)
                        throw new RangeError('Stencil clear must be an unsigned integer.');
                    attachment.stencilClearValue = depth.stencilClearValue;
                }
            } else if (depth.stencilLoadOp !== undefined || depth.stencilStoreOp !== undefined)
                throw new Error('Stencil operations require a stencil aspect.');
            this.signature.depthStencilFormat = source.format;
            this.descriptor.depthStencilAttachment = attachment;
            this.bindings.push({ handle: depth.viewHandle, source, attachment, key: 'view' });
        }
        if (!this.signature.sampleCount) throw new Error('Render pass requires at least one attachment.');
        Object.freeze(this.signature.colorFormats);
        Object.freeze(this.signature);
        Object.freeze(this.resourceHandles);
        this.release();
    }

    _resolve(handle, canvas) {
        if (handle === 0 || handle === -1) {
            const source = handle === 0 ? canvas.color : canvas.depth;
            if (!source) throw new Error('Canvas attachment metadata must be supplied when compiling its pass.');
            // Never capture an acquired canvas view in a persistent plan.
            return { canvasHandle: handle, width: source.width, height: source.height, format: source.format,
                sampleCount: source.sampleCount, usage: source.usage, aspect: source.aspect ?? 'all' };
        }
        const source = this.resources.getHandle(handle, 'texture-view', this.owner);
        if (!this.resourceHandles.includes(handle)) this.resourceHandles.push(handle);
        return source;
    }

    _validateView(source, depthStencil, resolve = false) {
        if (!source || source.state !== undefined && source.state !== 'ready' || !(source.usage & GPUTextureUsage.RENDER_ATTACHMENT))
            throw new Error('Render attachment requires a ready renderable texture view.');
        if (source.mipCount !== undefined && source.mipCount !== 1)
            throw new Error('Render attachment views must select exactly one mip level.');
        if (source.dimension !== undefined && (source.dimension !== '2d' || source.arrayLayerCount !== 1))
            throw new Error('Render attachment views must select one 2D layer or face.');
        if (depthStencil !== (depthFormats.has(source.format) || stencilFormats.has(source.format)))
            throw new Error('Render attachment format does not match its color or depth/stencil role.');
        if (!depthStencil && source.aspect !== undefined && source.aspect !== 'all') throw new Error('Color attachment requires the all aspect.');
        if (depthStencil && source.aspect !== undefined && source.aspect !== 'all')
            throw new Error('Depth/stencil render attachments must expose all texture aspects; use read-only aspect policies to preserve an aspect.');
        if (![1, 4].includes(source.sampleCount)) throw new Error('Unsupported render attachment sample count.');
        if (!Number.isInteger(source.width) || source.width < 1 || !Number.isInteger(source.height) || source.height < 1)
            throw new Error('Render attachment dimensions must be positive integers.');
        const signature = this.signature;
        if (signature.width && (source.width !== signature.width || source.height !== signature.height))
            throw new Error('Render attachments must have equal extents.');
        if (!resolve && signature.sampleCount && source.sampleCount !== signature.sampleCount)
            throw new Error('Render attachments must have equal sample counts.');
        if (!resolve) signature.sampleCount = source.sampleCount;
        signature.width = source.width;
        signature.height = source.height;
    }

    _use(source) {
        // View identity alone cannot detect two views of the same writable subresource.
        const texture = source.canvasHandle ?? source.texture?.texture ?? source.texture ?? source;
        for (const used of this._subresources) {
            if (used.texture === texture && used.mip === (source.baseMip ?? 0) &&
                used.layer === (source.baseArrayLayer ?? 0))
                throw new Error('A render pass cannot alias attachment subresources.');
        }
        this._subresources.add({ texture, mip: source.baseMip ?? 0, layer: source.baseArrayLayer ?? 0 });
    }

    assertPipeline(descriptor) {
        const targets = descriptor.fragment?.targets ?? [];
        if (targets.length !== this.signature.colorFormats.length) throw new Error('Pipeline color attachment count does not match its pass.');
        for (let i = 0; i < targets.length; i++)
            if ((targets[i]?.format ?? null) !== this.signature.colorFormats[i]) throw new Error('Pipeline color format does not match its pass.');
        if ((descriptor.multisample?.count ?? 1) !== this.signature.sampleCount
            || (descriptor.depthStencil?.format ?? null) !== this.signature.depthStencilFormat)
            throw new Error('Pipeline depth format or sample count does not match its pass.');
        if (this.signature.depthReadOnly && descriptor.depthStencil?.depthWriteEnabled)
            throw new Error('Pipeline writes to a read-only depth attachment.');
        if (this.signature.stencilReadOnly && descriptor.depthStencil && (descriptor.depthStencil.stencilWriteMask ?? 0xffffffff) !== 0) {
            for (const face of [descriptor.depthStencil.stencilFront, descriptor.depthStencil.stencilBack])
                if (face && ['failOp', 'depthFailOp', 'passOp'].some(key => (face[key] ?? 'keep') !== 'keep'))
                    throw new Error('Pipeline writes to a read-only stencil attachment.');
        }
    }

    prepare(canvasOutput, canvasDepth) {
        for (let i = 0; i < this.bindings.length; i++) {
            const binding = this.bindings[i];
            const current = binding.handle === 0 ? canvasOutput : binding.handle === -1 ? canvasDepth : binding.source;
            if (!current || !current.view || current.state !== undefined && current.state !== 'ready'
                || current.format !== binding.source.format || current.width !== binding.source.width
                || current.height !== binding.source.height || current.sampleCount !== binding.source.sampleCount)
                throw new Error('Attachment is obsolete or incompatible; rebuild the render pass plan.');
            binding.attachment[binding.key] = current.view;
        }
        return this.descriptor;
    }

    release() {
        for (let i = 0; i < this.bindings.length; i++)
            if (this.bindings[i].handle <= 0) this.bindings[i].attachment[this.bindings[i].key] = undefined;
    }
}
