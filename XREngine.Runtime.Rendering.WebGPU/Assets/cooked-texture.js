const maximumBytes = 64 * 1024 * 1024;
const encodings = new Map([
    ['rgba8unorm', { block: 1, bytes: 4, feature: null }],
    ['rgba8unorm-srgb', { block: 1, bytes: 4, feature: null }],
    ['astc-4x4-unorm', { block: 4, bytes: 16, feature: 'texture-compression-astc' }],
    ['astc-4x4-unorm-srgb', { block: 4, bytes: 16, feature: 'texture-compression-astc' }],
    ['etc2-rgba8unorm', { block: 4, bytes: 16, feature: 'texture-compression-etc2' }],
    ['etc2-rgba8unorm-srgb', { block: 4, bytes: 16, feature: 'texture-compression-etc2' }],
]);

/** Legacy descriptors imply straight-alpha color; explicit normal data never becomes a base-color binding. */
export function isBrowserColorTexture(texture) {
    return (texture.usage & GPUTextureUsage.TEXTURE_BINDING) !== 0 && texture.sampleCount === 1
        && encodings.has(texture.format) && (texture.normalConvention ?? 'none') === 'none'
        && (texture.alphaMode ?? 'straight') === 'straight';
}

/** Cold resource creation only: validates and copies the entire borrowed payload before issuing any GPU writes. */
export function createCookedTexture(renderer, json, memory) {
    renderer._stats.controlCalls++;
    renderer._requireOwner();
    if (typeof json !== 'string' || json.length > 2048) throw new RangeError('Cooked texture metadata exceeds its budget.');
    const data = JSON.parse(json);
    if (!data || typeof data !== 'object' || Array.isArray(data)) throw new TypeError('Cooked texture metadata must be an object.');
    for (const name of Object.keys(data))
        if (!['width', 'height', 'format', 'mipCount', 'normalConvention', 'alphaMode'].includes(name))
            throw new Error(`Unsupported cooked texture metadata field: ${name}.`);
    const { width, height, format, mipCount, normalConvention, alphaMode } = data;
    const encoding = encodings.get(format);
    if (!encoding) throw new Error('Cooked texture encoding is unsupported; choose an explicitly available asset variant.');
    if (encoding.feature && !renderer.device.features.has(encoding.feature))
        throw new Error(`Cooked texture requires device feature ${encoding.feature}; select a compatible cooked variant.`);
    const maxDimension = Math.min(8192, renderer.maxDimension, renderer.focusedPipeline.settings.maxTextureDimension);
    if (!Number.isInteger(width) || !Number.isInteger(height) || width < 1 || height < 1
        || width > maxDimension || height > maxDimension || width % encoding.block || height % encoding.block)
        throw new RangeError('Cooked texture dimensions exceed the selected tier or violate encoding block alignment.');
    if (!Number.isInteger(mipCount) || mipCount !== 1 + Math.floor(Math.log2(Math.max(width, height))))
        throw new RangeError('Cooked textures require a complete mip chain down to 1×1.');
    if (alphaMode !== 'straight') throw new Error('Cooked browser textures require straight alpha.');
    if (!['none', 'tangent-y-positive'].includes(normalConvention)
        || (normalConvention !== 'none' && format.endsWith('-srgb')))
        throw new Error('Normal textures require linear encoding and tangent-y-positive convention.');
    let total = 0;
    const levels = [];
    for (let mip = 0; mip < mipCount; mip++) {
        const blocksX = Math.ceil(Math.max(1, width >> mip) / encoding.block);
        const blocksY = Math.ceil(Math.max(1, height >> mip) / encoding.block);
        const bytesPerRow = blocksX * encoding.bytes, length = bytesPerRow * blocksY;
        levels.push({ offset: total, bytesPerRow, rowsPerImage: blocksY,
            width: blocksX * encoding.block, height: blocksY * encoding.block });
        total += length;
    }
    if (total > maximumBytes || memory?.byteLength !== total || typeof memory.copyTo !== 'function')
        throw new RangeError('Cooked texture mip bytes must exactly match the encoding and fit within 64 MiB.');
    const bytes = new Uint8Array(total);
    memory.copyTo(bytes);
    renderer._stats.copiedBytes += total;
    let texture;
    try {
        const usage = GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST;
        texture = renderer.device.createTexture({ label: 'Cooked browser texture', size: [width, height],
            format, mipLevelCount: mipCount, usage });
        for (let mip = 0; mip < levels.length; mip++) {
            const level = levels[mip];
            // WebGPU copies compressed mip tails using their padded physical extent, including 1×1 logical mips.
            renderer.device.queue.writeTexture({ texture, mipLevel: mip }, bytes,
                { offset: level.offset, bytesPerRow: level.bytesPerRow, rowsPerImage: level.rowsPerImage },
                [level.width, level.height]);
        }
        const handle = renderer._resources.add('texture', { texture, view: texture.createView(), width, height,
            format, usage, mipLevelCount: mipCount, sampleCount: 1, references: 0, state: 'ready',
            normalConvention, alphaMode, byteLength: total, label: 'Cooked browser texture' }, renderer._owner);
        try {
            if (normalConvention === 'none') renderer.focusedPipeline.registerTexture(handle);
        } catch (error) { renderer._resources.remove(handle, renderer._owner); throw error; }
        renderer._stats.uploadedBytes += total;
        renderer._stats.cookedTextureUploadedBytes = (renderer._stats.cookedTextureUploadedBytes ?? 0) + total;
        renderer._stats.cookedTextureCreates = (renderer._stats.cookedTextureCreates ?? 0) + 1;
        return handle;
    } catch (error) {
        renderer._retire(texture);
        throw error;
    }
}
