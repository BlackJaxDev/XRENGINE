import { textureFormatInfo, assertStorageTextureFormat } from './gpu-texture-formats.js';

const maximumBufferBytes = 256 * 1024 * 1024;
const maximumTextureBytes = 256 * 1024 * 1024;
const maximumCapturedAtlasBytes = 320 * 1024 * 1024;
const maximumWriteBytes = 64 * 1024 * 1024;
function uploadPixelBytes(format, device) {
    const info = textureFormatInfo(format, device);
    return info.color ? info.bytes : 0;
}

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

    createTexture(width, height, mipLevelCount, sampleCount, format, usage, label, arrayLayerCount = 1, allowSrgbView = false) {
        const r = this._ready();
        integer(width, 1, r.device.limits.maxTextureDimension2D, 'texture width');
        integer(height, 1, r.device.limits.maxTextureDimension2D, 'texture height');
        integer(arrayLayerCount, 1, r.device.limits.maxTextureArrayLayers, 'texture array layers');
        integer(mipLevelCount, 1, 1 + Math.floor(Math.log2(Math.max(width, height))), 'mip count');
        if (sampleCount !== 1 && sampleCount !== 4) throw new RangeError('Texture sample count must be one or four.');
        const info = textureFormatInfo(format, r.device);
        const color = info.color;
        const supported = GPUTextureUsage.COPY_SRC | GPUTextureUsage.COPY_DST | GPUTextureUsage.TEXTURE_BINDING |
            GPUTextureUsage.STORAGE_BINDING | GPUTextureUsage.RENDER_ATTACHMENT;
        integer(usage, 1, supported, 'texture usage');
        if (usage & ~supported) throw new RangeError('Texture usage is outside the admitted profile.');
        if (typeof allowSrgbView !== 'boolean' || allowSrgbView &&
            (format !== 'rgba8unorm' || sampleCount !== 1 || (usage & GPUTextureUsage.STORAGE_BINDING)))
            throw new RangeError('sRGB views require a non-storage single-sample RGBA8 texture.');
        if (usage & GPUTextureUsage.STORAGE_BINDING) assertStorageTextureFormat(format, 'write-only', r.device);
        if (sampleCount > 1 && !info.multisample) throw new RangeError(`Texture format '${format}' does not support multisampling.`);
        if (!color && (usage & (GPUTextureUsage.COPY_SRC | GPUTextureUsage.COPY_DST)) &&
            (!['depth16unorm', 'depth24plus', 'depth32float'].includes(format) || sampleCount !== 1))
            throw new RangeError('Depth transfers require single-sample depth-only copy resources; combined depth/stencil transfers are unsupported.');
        if (sampleCount > 1 && (arrayLayerCount !== 1 || mipLevelCount !== 1 || !(usage & GPUTextureUsage.RENDER_ATTACHMENT) ||
            (usage & (GPUTextureUsage.COPY_SRC | GPUTextureUsage.COPY_DST | GPUTextureUsage.STORAGE_BINDING))))
            throw new RangeError('Multisampled textures require one layer, attachment usage, one mip and no transfer usage.');
        const bytesPerPixel = info.bytes;
        let estimatedBytes = 0;
        for (let mip = 0; mip < mipLevelCount; mip++)
            estimatedBytes += Math.max(1, Math.floor(width / 2 ** mip)) * Math.max(1, Math.floor(height / 2 ** mip)) * bytesPerPixel * sampleCount * arrayLayerCount;
        // The persisted 26-direction HDR atlas includes its complete mip chain.
        // Keep this exception narrower than the general texture allocation cap.
        const capturedAtlas = format === 'rgba16float' && arrayLayerCount === 26 && sampleCount === 1 &&
            width <= 1024 && height <= 1024 && mipLevelCount === 1 + Math.floor(Math.log2(Math.max(width, height))) &&
            (usage & (GPUTextureUsage.COPY_SRC | GPUTextureUsage.COPY_DST | GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.RENDER_ATTACHMENT)) ===
                (GPUTextureUsage.COPY_SRC | GPUTextureUsage.COPY_DST | GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.RENDER_ATTACHMENT);
        if (estimatedBytes > (capturedAtlas ? maximumCapturedAtlasBytes : maximumTextureBytes))
            throw new RangeError('Texture exceeds the bounded allocation budget.');
        debugLabel(label);
        r._setOperation('create-texture', label);
        const texture = r.device.createTexture({ label, size: { width, height, depthOrArrayLayers: arrayLayerCount },
            dimension: '2d', format, usage, mipLevelCount, sampleCount,
            viewFormats: allowSrgbView ? ['rgba8unorm-srgb'] : [] });
        try {
            return r._resources.add('texture', { texture, view: texture.createView(), width, height, arrayLayerCount,
                format, mipLevelCount, sampleCount, usage, allowSrgbView, label, state: 'ready', references: 0 }, r._owner);
        } catch (error) { r._retire(texture); throw error; }
    }

    uploadTextureMip(handle, mip, x, y, width, height, memory, layer = 0) {
        const r = this._ready();
        const entry = r._resources.getHandle(handle, 'texture', r._owner);
        integer(mip, 0, entry.mipLevelCount - 1, 'texture mip');
        integer(layer, 0, (entry.arrayLayerCount ?? 1) - 1, 'texture layer');
        const mipWidth = Math.max(1, Math.floor(entry.width / 2 ** mip));
        const mipHeight = Math.max(1, Math.floor(entry.height / 2 ** mip));
        integer(width, 1, mipWidth, 'upload width'); integer(height, 1, mipHeight, 'upload height');
        integer(x, 0, mipWidth - width, 'upload x'); integer(y, 0, mipHeight - height, 'upload y');
        const bytesPerPixel = uploadPixelBytes(entry.format, r.device);
        const length = width * height * bytesPerPixel;
        if (!bytesPerPixel || entry.sampleCount !== 1 || !(entry.usage & GPUTextureUsage.COPY_DST) || memory?.byteLength !== length)
            throw new RangeError('Mip uploads require an admitted single-sample color format, COPY_DST and exact tightly packed bytes.');
        this._bytes(memory);
        this.textureDestination.texture = entry.texture;
        this.textureDestination.mipLevel = mip;
        this.textureDestination.origin.x = x; this.textureDestination.origin.y = y;
        this.textureDestination.origin.z = layer;
        this.textureLayout.bytesPerRow = width * bytesPerPixel; this.textureLayout.rowsPerImage = height;
        this.textureExtent.width = width; this.textureExtent.height = height;
        try {
            r.device.queue.writeTexture(this.textureDestination, this.staging, this.textureLayout, this.textureExtent);
            r._stats.uploadedBytes += length;
        } finally { this.textureDestination.texture = null; }
    }

    copyTextureSubresource(sourceHandle, destinationHandle, sourceMip, destinationMip, destinationLayer, width, height) {
        const r = this._ready();
        if (sourceHandle === destinationHandle) throw new RangeError('Texture subresource copies require distinct resources.');
        const source = r._resources.getHandle(sourceHandle, 'texture', r._owner);
        const destination = r._resources.getHandle(destinationHandle, 'texture', r._owner);
        integer(sourceMip, 0, source.mipLevelCount - 1, 'source mip');
        integer(destinationMip, 0, destination.mipLevelCount - 1, 'destination mip');
        integer(destinationLayer, 0, (destination.arrayLayerCount ?? 1) - 1, 'destination layer');
        const sourceWidth = Math.max(1, Math.floor(source.width / 2 ** sourceMip));
        const sourceHeight = Math.max(1, Math.floor(source.height / 2 ** sourceMip));
        const destinationWidth = Math.max(1, Math.floor(destination.width / 2 ** destinationMip));
        const destinationHeight = Math.max(1, Math.floor(destination.height / 2 ** destinationMip));
        integer(width, 1, Math.min(sourceWidth, destinationWidth), 'copy width');
        integer(height, 1, Math.min(sourceHeight, destinationHeight), 'copy height');
        if ((source.arrayLayerCount ?? 1) !== 1 || source.sampleCount !== 1 || destination.sampleCount !== 1 ||
            source.format !== destination.format || !textureFormatInfo(source.format, r.device).color ||
            !(source.usage & GPUTextureUsage.COPY_SRC) || !(destination.usage & GPUTextureUsage.COPY_DST) ||
            width !== sourceWidth || height !== sourceHeight || width !== destinationWidth || height !== destinationHeight)
            throw new RangeError('Texture subresource copies require complete matching single-sample color mips and copy usages.');
        const encoder = r.device.createCommandEncoder({ label: 'Engine texture layer copy' });
        encoder.copyTextureToTexture(
            { texture: source.texture, mipLevel: sourceMip, origin: [0, 0, 0] },
            { texture: destination.texture, mipLevel: destinationMip, origin: [0, 0, destinationLayer] },
            [width, height, 1]);
        this.submission[0] = encoder.finish();
        try { r.device.queue.submit(this.submission); r._stats.gpuCopiedBytes += width * height *
            textureFormatInfo(source.format, r.device).bytes; }
        finally { this.submission[0] = null; }
    }

    createTextureView(textureHandle, baseMip, mipCount, aspect, label, baseArrayLayer = 0, arrayLayerCount = 1, dimension = '2d', format = '') {
        const r = this._ready();
        const texture = r._resources.getHandle(textureHandle, 'texture', r._owner);
        integer(baseMip, 0, texture.mipLevelCount - 1, 'view base mip');
        integer(mipCount, 1, texture.mipLevelCount - baseMip, 'view mip count');
        integer(baseArrayLayer, 0, (texture.arrayLayerCount ?? 1) - 1, 'view base layer');
        integer(arrayLayerCount, 1, (texture.arrayLayerCount ?? 1) - baseArrayLayer, 'view array layers');
        if (!['2d', '2d-array', 'cube'].includes(dimension) ||
            dimension === '2d' && arrayLayerCount !== 1 ||
            dimension === 'cube' && (arrayLayerCount !== 6 || baseArrayLayer % 6 !== 0 || texture.width !== texture.height) ||
            texture.sampleCount > 1 && dimension !== '2d')
            throw new RangeError('Texture view dimension and layer range are incompatible.');
        if (aspect !== 'all' && aspect !== 'depth-only' && aspect !== 'stencil-only') throw new RangeError('Unsupported texture view aspect.');
        if ((aspect === 'depth-only' && textureFormatInfo(texture.format, r.device).sampleType !== 'depth') ||
            (aspect === 'stencil-only' && !['depth24plus-stencil8', 'depth32float-stencil8'].includes(texture.format)))
            throw new RangeError('Texture view aspect is incompatible with its format.');
        if (typeof format !== 'string') throw new RangeError('Texture view format must be a string.');
        const viewFormat = format || texture.format;
        if (viewFormat !== texture.format && !(texture.allowSrgbView && texture.format === 'rgba8unorm' &&
            viewFormat === 'rgba8unorm-srgb' && aspect === 'all'))
            throw new RangeError('Texture view format is outside the declared compatible sRGB pair.');
        debugLabel(label);
        r._setOperation('create-texture-view', label);
        const descriptor = { label, dimension, baseMipLevel: baseMip, mipLevelCount: mipCount,
            baseArrayLayer, arrayLayerCount, aspect };
        if (format) descriptor.format = viewFormat;
        const view = texture.texture.createView(descriptor);
        const handle = r._resources.add('texture-view', { view, texture, textureHandle, baseMip, mipCount, aspect,
            baseArrayLayer, arrayLayerCount, dimension,
            format: viewFormat, width: Math.max(1, Math.floor(texture.width / 2 ** baseMip)),
            height: Math.max(1, Math.floor(texture.height / 2 ** baseMip)), sampleCount: texture.sampleCount,
            usage: texture.usage, label, state: 'ready', references: 0 }, r._owner);
        texture.references++;
        return handle;
    }

    createSampler(addressU, addressV, minFilter, magFilter, mipmapFilter, label, lodMaxClamp = 32, maxAnisotropy = 1, lodMinClamp = 0, compare = '', addressW = 'clamp-to-edge') {
        const r = this._ready();
        if (![addressU, addressV, addressW].every(value => value === 'clamp-to-edge' || value === 'repeat' || value === 'mirror-repeat') ||
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
            addressModeW: addressW, minFilter, magFilter, mipmapFilter, lodMinClamp, lodMaxClamp, maxAnisotropy };
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
