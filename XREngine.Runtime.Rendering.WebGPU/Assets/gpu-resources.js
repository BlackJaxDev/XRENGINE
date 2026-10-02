const maximumBufferBytes = 256 * 1024 * 1024;
const maximumTextureBytes = 256 * 1024 * 1024;
const maximumWriteBytes = 64 * 1024 * 1024;
const colorFormats = new Set(['rgba8unorm', 'rgba8unorm-srgb']);
const renderColorFormats = new Set([...colorFormats, 'rgba16float']);
const depthFormats = new Set(['depth16unorm', 'depth24plus', 'depth24plus-stencil8', 'depth32float']);

function integer(value, minimum, maximum, name) {
    if (!Number.isSafeInteger(value) || value < minimum || value > maximum)
        throw new RangeError(`Invalid ${name}.`);
}

function debugLabel(value) {
    if (typeof value !== 'string' || value.length > 256)
        throw new RangeError('Resource debug names must contain at most 256 characters.');
    return value;
}

/** Session-owned resources; mapped native pointers are replaced by bounded queue writes. */
export class GpuResources {
    constructor(renderer) {
        this.renderer = renderer;
        this.staging = new Uint8Array(0);
        this.textureDestination = { texture: null, mipLevel: 0, origin: { x: 0, y: 0, z: 0 } };
        this.textureLayout = { offset: 0, bytesPerRow: 0, rowsPerImage: 0 };
        this.textureExtent = { width: 0, height: 0, depthOrArrayLayers: 1 };
        this.submission = [null];
    }

    _ready() {
        this.renderer._requireOwner();
        this.renderer._stats.controlCalls++;
        return this.renderer;
    }

    _bytes(memory) {
        const length = memory?.byteLength;
        integer(length, 1, maximumWriteBytes, 'upload byte count');
        if (this.staging.length < length) {
            let capacity = Math.max(4096, this.staging.length);
            while (capacity < length) capacity *= 2;
            this.staging = new Uint8Array(capacity);
            this.renderer._stats.arenaGrowth++;
        }
        memory.copyTo(this.staging);
        this.renderer._stats.copiedBytes += length;
        return length;
    }

    createBuffer(size, usage, label) {
        const r = this._ready();
        integer(size, 4, Math.min(maximumBufferBytes, r.device.limits.maxBufferSize), 'buffer size');
        const supported = GPUBufferUsage.COPY_SRC | GPUBufferUsage.COPY_DST | GPUBufferUsage.VERTEX |
            GPUBufferUsage.INDEX | GPUBufferUsage.UNIFORM | GPUBufferUsage.STORAGE | GPUBufferUsage.INDIRECT;
        integer(usage, 1, supported, 'buffer usage');
        if (size % 4 || (usage & ~supported)) throw new RangeError('Buffer size must be four-byte aligned and usage supported.');
        debugLabel(label);
        // WebGPU guarantees zero initialization before any read, including indirect
        // argument consumption. Untouched argument records are therefore empty draws;
        // compute producers must rewrite every record they make active on each replay.
        r._setOperation('create-buffer', label);
        const buffer = r.device.createBuffer({ size, usage, label });
        try { return r._resources.add('buffer', { buffer, size, usage, label, state: 'ready', references: 0 }, r._owner); }
        catch (error) { r._retire(buffer); throw error; }
    }

    writeBuffer(handle, offset, memory) {
        const r = this._ready();
        const entry = r._resources.getHandle(handle, 'buffer', r._owner);
        const length = memory?.byteLength;
        integer(length, 4, maximumWriteBytes, 'buffer upload size');
        integer(offset, 0, entry.size - length, 'buffer upload offset');
        if (!(entry.usage & GPUBufferUsage.COPY_DST) || offset % 4 || length % 4)
            throw new RangeError('Buffer writes require COPY_DST and four-byte aligned offsets and lengths.');
        this._bytes(memory);
        r._setOperation('write-buffer', entry.label);
        r.device.queue.writeBuffer(entry.buffer, offset, this.staging, 0, length);
        r._stats.uploadedBytes += length;
    }

    copyBuffer(sourceHandle, sourceOffset, destinationHandle, destinationOffset, size) {
        const r = this._ready();
        const source = r._resources.getHandle(sourceHandle, 'buffer', r._owner);
        const destination = r._resources.getHandle(destinationHandle, 'buffer', r._owner);
        integer(size, 4, maximumBufferBytes, 'buffer copy size');
        integer(sourceOffset, 0, source.size - size, 'source offset');
        integer(destinationOffset, 0, destination.size - size, 'destination offset');
        if (sourceHandle === destinationHandle || !(source.usage & GPUBufferUsage.COPY_SRC) ||
            !(destination.usage & GPUBufferUsage.COPY_DST) || (size % 4) || sourceOffset % 4 || destinationOffset % 4)
            throw new RangeError('Buffer copies require distinct resources, copy usages and four-byte alignment.');
        const encoder = r.device.createCommandEncoder({ label: 'Buffer copy' });
        encoder.copyBufferToBuffer(source.buffer, sourceOffset, destination.buffer, destinationOffset, size);
        this.submission[0] = encoder.finish();
        try { r.device.queue.submit(this.submission); r._stats.gpuCopiedBytes += size; }
        finally { this.submission[0] = null; }
    }

    bufferBinding(handle, offset, size, storage) {
        const r = this.renderer;
        r._requireOwner();
        const entry = r._resources.getHandle(handle, 'buffer', r._owner);
        const alignment = storage ? r.device.limits.minStorageBufferOffsetAlignment : r.device.limits.minUniformBufferOffsetAlignment;
        const maximum = storage ? r.device.limits.maxStorageBufferBindingSize : r.device.limits.maxUniformBufferBindingSize;
        integer(size, 4, Math.min(entry.size, maximum), 'buffer binding size');
        integer(offset, 0, entry.size - size, 'buffer binding offset');
        if (offset % alignment || size % 4 || !(entry.usage & (storage ? GPUBufferUsage.STORAGE : GPUBufferUsage.UNIFORM)))
            throw new RangeError('Buffer binding usage, range or device alignment is incompatible.');
        return { buffer: entry.buffer, offset, size };
    }

    createTexture(width, height, mipLevelCount, sampleCount, format, usage, label) {
        const r = this._ready();
        integer(width, 1, r.device.limits.maxTextureDimension2D, 'texture width');
        integer(height, 1, r.device.limits.maxTextureDimension2D, 'texture height');
        integer(mipLevelCount, 1, 1 + Math.floor(Math.log2(Math.max(width, height))), 'mip count');
        if (sampleCount !== 1 && sampleCount !== 4) throw new RangeError('Texture sample count must be one or four.');
        const color = renderColorFormats.has(format);
        if (!color && !depthFormats.has(format)) throw new RangeError('Texture format is outside the baseline profile.');
        const supported = GPUTextureUsage.COPY_SRC | GPUTextureUsage.COPY_DST | GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.RENDER_ATTACHMENT;
        integer(usage, 1, supported, 'texture usage');
        if (usage & ~supported) throw new RangeError('Texture usage is outside the baseline profile.');
        if (!color && (usage & (GPUTextureUsage.COPY_SRC | GPUTextureUsage.COPY_DST)))
            throw new RangeError('Depth/stencil transfers are outside the baseline texture profile.');
        if (sampleCount > 1 && (mipLevelCount !== 1 || !(usage & GPUTextureUsage.RENDER_ATTACHMENT) ||
            (usage & (GPUTextureUsage.COPY_SRC | GPUTextureUsage.COPY_DST))))
            throw new RangeError('Multisampled textures require attachment usage, one mip and no transfer usage.');
        let estimatedBytes = 0;
        for (let mip = 0; mip < mipLevelCount; mip++)
            estimatedBytes += Math.max(1, Math.floor(width / 2 ** mip)) * Math.max(1, Math.floor(height / 2 ** mip)) * 8 * sampleCount;
        if (estimatedBytes > maximumTextureBytes) throw new RangeError('Texture exceeds the bounded allocation budget.');
        debugLabel(label);
        r._setOperation('create-texture', label);
        const texture = r.device.createTexture({ label, size: { width, height, depthOrArrayLayers: 1 },
            dimension: '2d', format, usage, mipLevelCount, sampleCount });
        try {
            return r._resources.add('texture', { texture, view: texture.createView(), width, height, format,
                mipLevelCount, sampleCount, usage, label, state: 'ready', references: 0 }, r._owner);
        } catch (error) { r._retire(texture); throw error; }
    }

    uploadTextureMip(handle, mip, x, y, width, height, memory) {
        const r = this._ready();
        const entry = r._resources.getHandle(handle, 'texture', r._owner);
        integer(mip, 0, entry.mipLevelCount - 1, 'texture mip');
        const mipWidth = Math.max(1, Math.floor(entry.width / 2 ** mip));
        const mipHeight = Math.max(1, Math.floor(entry.height / 2 ** mip));
        integer(width, 1, mipWidth, 'upload width'); integer(height, 1, mipHeight, 'upload height');
        integer(x, 0, mipWidth - width, 'upload x'); integer(y, 0, mipHeight - height, 'upload y');
        const length = width * height * 4;
        if (!colorFormats.has(entry.format) || entry.sampleCount !== 1 || !(entry.usage & GPUTextureUsage.COPY_DST) || memory?.byteLength !== length)
            throw new RangeError('Mip uploads require single-sample RGBA8, COPY_DST and tightly packed bytes.');
        this._bytes(memory);
        this.textureDestination.texture = entry.texture;
        this.textureDestination.mipLevel = mip;
        this.textureDestination.origin.x = x; this.textureDestination.origin.y = y;
        this.textureLayout.bytesPerRow = width * 4; this.textureLayout.rowsPerImage = height;
        this.textureExtent.width = width; this.textureExtent.height = height;
        try {
            r.device.queue.writeTexture(this.textureDestination, this.staging, this.textureLayout, this.textureExtent);
            r._stats.uploadedBytes += length;
        } finally { this.textureDestination.texture = null; }
    }

    createTextureView(textureHandle, baseMip, mipCount, aspect, label) {
        const r = this._ready();
        const texture = r._resources.getHandle(textureHandle, 'texture', r._owner);
        integer(baseMip, 0, texture.mipLevelCount - 1, 'view base mip');
        integer(mipCount, 1, texture.mipLevelCount - baseMip, 'view mip count');
        if (aspect !== 'all' && aspect !== 'depth-only' && aspect !== 'stencil-only') throw new RangeError('Unsupported texture view aspect.');
        if ((aspect === 'depth-only' && !depthFormats.has(texture.format)) ||
            (aspect === 'stencil-only' && texture.format !== 'depth24plus-stencil8'))
            throw new RangeError('Texture view aspect is incompatible with its format.');
        debugLabel(label);
        r._setOperation('create-texture-view', label);
        const view = texture.texture.createView({ label, dimension: '2d', baseMipLevel: baseMip, mipLevelCount: mipCount,
            baseArrayLayer: 0, arrayLayerCount: 1, aspect });
        const handle = r._resources.add('texture-view', { view, texture, textureHandle, baseMip, mipCount, aspect,
            format: texture.format, width: Math.max(1, Math.floor(texture.width / 2 ** baseMip)),
            height: Math.max(1, Math.floor(texture.height / 2 ** baseMip)), sampleCount: texture.sampleCount,
            usage: texture.usage, label, state: 'ready', references: 0 }, r._owner);
        texture.references++;
        return handle;
    }

    createSampler(addressU, addressV, minFilter, magFilter, mipmapFilter, label, lodMaxClamp = 32, maxAnisotropy = 1, lodMinClamp = 0, compare = '') {
        const r = this._ready();
        if (![addressU, addressV].every(value => value === 'clamp-to-edge' || value === 'repeat' || value === 'mirror-repeat') ||
            ![minFilter, magFilter, mipmapFilter].every(value => value === 'nearest' || value === 'linear') ||
            !Number.isFinite(lodMinClamp) || !Number.isFinite(lodMaxClamp) ||
            lodMinClamp < 0 || lodMinClamp > lodMaxClamp || lodMaxClamp > 32 ||
            !Number.isInteger(maxAnisotropy) || maxAnisotropy < 1 || maxAnisotropy > 16 ||
            (compare !== '' && compare !== 'less-equal') ||
            (maxAnisotropy > 1 && [minFilter, magFilter, mipmapFilter].some(value => value !== 'linear')))
            throw new RangeError('Sampler address, filtering, LOD or anisotropy mode is unsupported.');
        debugLabel(label);
        r._setOperation('create-sampler', label);
        const descriptor = { label, addressModeU: addressU, addressModeV: addressV,
            addressModeW: 'clamp-to-edge', minFilter, magFilter, mipmapFilter, lodMinClamp, lodMaxClamp, maxAnisotropy };
        if (compare) descriptor.compare = compare;
        const sampler = r.device.createSampler(descriptor);
        return r._resources.add('sampler', { sampler, label, state: 'ready', references: 0,
            filtering: minFilter === 'linear' || magFilter === 'linear' || mipmapFilter === 'linear', compare }, r._owner);
    }

    destroy(entry, immediate) {
        if (entry.kind === 'buffer') {
            if (immediate) entry.value.buffer.destroy(); else this.renderer._retire(entry.value.buffer);
        } else if (entry.kind === 'texture-view') {
            entry.value.texture.references--;
            if (!entry.value.texture.references) entry.value.texture.tryRetire?.();
            entry.value.view = null;
        } else if (entry.kind === 'sampler') entry.value.sampler = null;
        else return false;
        entry.value.state = 'released';
        return true;
    }

    dispose() {
        this.staging = new Uint8Array(0);
        this.textureDestination.texture = null;
        this.submission[0] = null;
    }
}
