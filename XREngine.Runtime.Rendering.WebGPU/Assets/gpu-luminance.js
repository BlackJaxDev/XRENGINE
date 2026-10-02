const maximumPixels = 4 * 1024 * 1024;
const maximumRequests = 4;
const maximumResidentBytes = 32 * 1024 * 1024;
const canvasTimeoutMs = 5000;

function integer(value, name, minimum = 0) {
    if (!Number.isSafeInteger(value) || value < minimum)
        throw new RangeError(`WebGPU luminance ${name} is outside its supported range.`);
}

function weights(red, green, blue) {
    if (![red, green, blue].every(Number.isFinite))
        throw new RangeError('WebGPU luminance weights must be finite.');
    return [red, green, blue];
}

/** Backend-private execution of an exact, hash-owned engine reduction module. */
export class GpuLuminance {
    constructor(renderer) {
        this.renderer = renderer;
        this.ready = false;
        this.disposed = false;
        this.pendingCanvas = new Set();
        this.activeCount = 0;
        this.residentBytes = 0;
    }

    async prepare(source) {
        const r = this.renderer;
        r._requireOwner();
        if (this.ready) return;
        if (this.disposed || typeof source !== 'string' || !source.length || source.length > 1024 * 1024)
            throw new Error('WebGPU luminance requires a bounded cooked engine module.');
        const device = r.device, owner = r._owner;
        const limits = device.limits;
        if (limits.maxComputeInvocationsPerWorkgroup < 256 || limits.maxComputeWorkgroupSizeX < 256 ||
            limits.maxComputeWorkgroupStorageSize < 2048 || limits.maxStorageBuffersPerShaderStage < 2 ||
            limits.maxSampledTexturesPerShaderStage < 1 || limits.maxBindingsPerBindGroup < 4 ||
            limits.maxStorageBufferBindingSize < 8)
            throw new Error('WebGPU luminance requires 256-thread workgroups, 2 KiB workgroup storage, two storage buffers and one sampled texture.');
        const layout = r.pipelineCache.getBindGroupLayout({ entries: [
            { binding: 0, visibility: GPUShaderStage.COMPUTE, texture: { sampleType: 'float', viewDimension: '2d-array' } },
            { binding: 1, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'storage', minBindingSize: 8 } },
            { binding: 2, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'storage', minBindingSize: 4 } },
            { binding: 3, visibility: GPUShaderStage.COMPUTE, buffer: { type: 'uniform', minBindingSize: 64 } },
        ] });
        const module = device.createShaderModule({ code: source, label: 'Engine luminance reduction' });
        const compilation = module.getCompilationInfo();
        const pipeline = r.pipelineCache.getComputePipelineAsync({ label: 'Engine luminance reduction',
            layout: r.pipelineCache.getPipelineLayout({ bindGroupLayouts: [layout] }),
            compute: { module, entryPoint: 'reduce' } });
        const [info, compiled] = await Promise.all([compilation, pipeline]);
        if (this.disposed || r.device !== device || r._owner !== owner) throw new Error('WebGPU luminance preparation was superseded.');
        const errors = info.messages.filter(message => message.type === 'error');
        if (errors.length) throw new Error(errors.map(message => message.message).join('\n'));
        this.layout = layout;
        this.pipeline = compiled;
        this.ready = true;
    }

    _validateExtent(width, height, layers) {
        integer(width, 'width', 1);
        integer(height, 'height', 1);
        integer(layers, 'layer count', 1);
        if (width * height * layers > maximumPixels)
            throw new RangeError('WebGPU luminance exceeds the four-million-texel work budget.');
        const tilesX = Math.ceil(width / 16), tilesY = Math.ceil(height / 16);
        const tileCount = tilesX * tilesY * layers;
        const tileBytes = tileCount * 8;
        if (tileCount > this.renderer.device.limits.maxComputeWorkgroupsPerDimension ||
            tileBytes > this.renderer.device.limits.maxStorageBufferBindingSize ||
            tileBytes > this.renderer.device.limits.maxBufferSize)
            throw new RangeError('WebGPU luminance reduction exceeds selected-device workgroup or storage limits.');
        return { tilesX, tilesY, tileCount, tileBytes };
    }

    _reserve(request, extraBytes) {
        if (!this.ready || this.disposed) throw new Error('WebGPU luminance was not prepared for this renderer.');
        const cost = request.tileBytes + extraBytes + 4 + 128;
        if (this.activeCount >= maximumRequests || cost > maximumResidentBytes - this.residentBytes)
            throw new RangeError('WebGPU luminance resident request capacity is exhausted.');
        let ticket;
        const owned = [];
        const cleanup = () => {
            for (const resource of owned) resource.destroy();
            owned.length = 0;
            this.pendingCanvas.delete(ticket);
            this.activeCount--;
            this.residentBytes -= cost;
        };
        ticket = this.renderer.readback.reserveComputed(cleanup);
        this.activeCount++;
        this.residentBytes += cost;
        ticket.luminance = { request, owned };
        return ticket;
    }

    beginTexture(handle, mip, width, height, layers, red, green, blue) {
        const r = this.renderer;
        r._requireOwner();
        integer(mip, 'mip');
        const source = r._resources.getHandle(handle, 'texture', r._owner);
        if (!['rgba8unorm', 'rgba8unorm-srgb', 'rgba16float'].includes(source.format) ||
            source.sampleCount !== 1 || !(source.usage & GPUTextureUsage.TEXTURE_BINDING) ||
            mip >= source.mipLevelCount || layers !== source.arrayLayerCount ||
            width > Math.max(1, Math.floor(source.width / 2 ** mip)) ||
            height > Math.max(1, Math.floor(source.height / 2 ** mip)))
            throw new RangeError('WebGPU luminance requires a compatible single-sample engine texture subresource.');
        const request = { ...this._validateExtent(width, height, layers), width, height, layers, mip, x: 0, y: 0,
            weights: weights(red, green, blue), source, canvas: false };
        const ticket = this._reserve(request, 0);
        this.renderer.readback.startComputed(ticket, encoder => this._encode(ticket, encoder, source.texture), undefined);
        return ticket.handle;
    }

    beginCanvas(generation, x, y, width, height, red, green, blue) {
        const r = this.renderer;
        r._requireOwner();
        for (const [name, value, minimum] of [['generation', generation, 1], ['x', x, 0], ['y', y, 0]])
            integer(value, name, minimum);
        if (!r._configured || generation !== r._generation || !['rgba8unorm', 'bgra8unorm'].includes(r.format) ||
            x > r._width || y > r._height || width > r._width - x || height > r._height - y ||
            globalThis.document?.visibilityState === 'hidden')
            throw new RangeError('WebGPU luminance requires a visible current canvas and a contained rectangle.');
        const request = { ...this._validateExtent(width, height, 1), width, height, layers: 1, mip: 0, x: 0, y: 0,
            sourceX: x, sourceY: y, weights: weights(red, green, blue), canvas: true, format: r.format };
        const ticket = this._reserve(request, width * height * 4);
        ticket.canvas = { generation, x, y, width, height };
        this.pendingCanvas.add(ticket);
        ticket.timeout = setTimeout(() => {
            if (!ticket.started && !ticket.finished) this.renderer.readback._cancelCanvasTicket(ticket);
        }, canvasTimeoutMs);
        return ticket.handle;
    }

    hasPendingCanvas(generation) {
        if (this.pendingCanvas.size === 0) return false;
        for (const ticket of this.pendingCanvas)
            if (ticket.canvas?.generation === generation && !ticket.started && !ticket.finished && !ticket.released)
                return true;
        return false;
    }

    captureCanvas(texture, generation, presentsCanvas, producerGate) {
        if (!presentsCanvas || this.pendingCanvas.size === 0) return;
        for (const ticket of this.pendingCanvas) {
            if (ticket.finished || ticket.released || ticket.started) continue;
            if (ticket.canvas.generation !== generation) {
                this.renderer.readback._cancelCanvasTicket(ticket);
                continue;
            }
            clearTimeout(ticket.timeout);
            ticket.timeout = null;
            this.renderer.readback.startComputed(ticket, encoder => this._encode(ticket, encoder, texture), producerGate);
        }
    }

    _encode(ticket, encoder, sourceTexture) {
        const r = this.renderer, device = r.device, request = ticket.luminance.request;
        const owned = ticket.luminance.owned;
        let source = sourceTexture;
        if (request.canvas) {
            source = device.createTexture({ label: 'Engine luminance canvas snapshot', size: [request.width, request.height],
                format: request.format, usage: GPUTextureUsage.COPY_DST | GPUTextureUsage.TEXTURE_BINDING });
            owned.push(source);
            encoder.copyTextureToTexture({ texture: sourceTexture, origin: [request.sourceX, request.sourceY, 0] },
                { texture: source }, [request.width, request.height, 1]);
        }
        const partials = device.createBuffer({ label: 'Engine luminance partials', size: request.tileBytes,
            usage: GPUBufferUsage.STORAGE });
        owned.push(partials);
        const result = device.createBuffer({ label: 'Engine luminance result', size: 4,
            usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_SRC });
        owned.push(result);
        const tileParameters = device.createBuffer({ label: 'Engine luminance tile parameters', size: 64,
            usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
        owned.push(tileParameters);
        const finalParameters = device.createBuffer({ label: 'Engine luminance final parameters', size: 64,
            usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
        owned.push(finalParameters);
        const parameters = new ArrayBuffer(64), data = new DataView(parameters);
        data.setUint32(0, request.x, true);
        data.setUint32(4, request.y, true);
        data.setUint32(8, request.width, true);
        data.setUint32(12, request.height, true);
        data.setUint32(16, request.mip, true);
        data.setUint32(20, request.layers, true);
        data.setUint32(24, request.tilesX, true);
        data.setUint32(28, request.tilesY, true);
        data.setUint32(32, request.tileCount, true);
        data.setFloat32(48, request.weights[0], true);
        data.setFloat32(52, request.weights[1], true);
        data.setFloat32(56, request.weights[2], true);
        device.queue.writeBuffer(tileParameters, 0, parameters);
        data.setUint32(36, 1, true);
        device.queue.writeBuffer(finalParameters, 0, parameters);
        const view = source.createView({ dimension: '2d-array' });
        const bind = parametersBuffer => device.createBindGroup({ layout: this.layout, entries: [
            { binding: 0, resource: view }, { binding: 1, resource: { buffer: partials } },
            { binding: 2, resource: { buffer: result } }, { binding: 3, resource: { buffer: parametersBuffer } },
        ] });
        const tiles = bind(tileParameters), final = bind(finalParameters);
        const pass = encoder.beginComputePass({ label: 'Engine luminance reduction' });
        pass.setPipeline(this.pipeline);
        pass.setBindGroup(0, tiles);
        pass.dispatchWorkgroups(request.tileCount);
        pass.setBindGroup(0, final);
        pass.dispatchWorkgroups(1);
        pass.end();
        encoder.copyBufferToBuffer(result, 0, ticket.buffer, 0, 4);
    }

    dispose() {
        if (this.disposed) return;
        this.disposed = true;
        for (const ticket of this.pendingCanvas)
            if (!ticket.finished) this.renderer.readback._cancelCanvasTicket(ticket);
        this.pendingCanvas.clear();
        this.pipeline = undefined;
        this.layout = undefined;
        this.ready = false;
    }
}
