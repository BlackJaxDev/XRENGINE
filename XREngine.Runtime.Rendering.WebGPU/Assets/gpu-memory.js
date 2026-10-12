// This is a cold diagnostic walk of the renderer's existing owners, not an allocation
// registry. WebGPU exposes requested buffer size and texture descriptors, but not
// physical device memory, row padding, tiling, compression metadata, or aliasing.
const bytesPerTexel = new Map([
    ['r8unorm', 1], ['r8snorm', 1], ['r8uint', 1], ['r8sint', 1],
    ['r16uint', 2], ['r16sint', 2], ['r16float', 2], ['rg8unorm', 2], ['rg8snorm', 2],
    ['rg8uint', 2], ['rg8sint', 2], ['depth16unorm', 2],
    ['r32uint', 4], ['r32sint', 4], ['r32float', 4], ['rg16uint', 4],
    ['rg16sint', 4], ['rg16float', 4], ['rgba8unorm', 4], ['rgba8unorm-srgb', 4],
    ['rgba8snorm', 4], ['rgba8uint', 4], ['rgba8sint', 4], ['bgra8unorm', 4],
    ['bgra8unorm-srgb', 4], ['rgb10a2uint', 4], ['rgb10a2unorm', 4],
    ['rg11b10ufloat', 4], ['rgb9e5ufloat', 4], ['depth32float', 4],
    // depth24plus can use a different physical representation on each adapter.
    ['depth24plus', 4], ['depth24plus-stencil8', 4],
    ['rg32uint', 8], ['rg32sint', 8], ['rg32float', 8], ['rgba16uint', 8],
    ['rgba16sint', 8], ['rgba16float', 8], ['depth32float-stencil8', 8],
    ['rgba32uint', 16], ['rgba32sint', 16], ['rgba32float', 16],
]);
const astcBlocks = new Set(['4x4', '5x4', '5x5', '6x5', '6x6', '8x5', '8x6', '8x8',
    '10x5', '10x6', '10x8', '10x10', '12x10', '12x12']);

function formatBlock(format) {
    const texelBytes = bytesPerTexel.get(format);
    if (texelBytes) return { width: 1, height: 1, bytes: texelBytes };
    if (/^bc[14]-/.test(format))
        return { width: 4, height: 4, bytes: 8 };
    if (/^bc(?:2|3|5|6h|7)-/.test(format)) return { width: 4, height: 4, bytes: 16 };
    if (/^etc2-rgb8/.test(format) || /^eac-r11/.test(format))
        return { width: 4, height: 4, bytes: 8 };
    if (/^etc2-rgba8/.test(format) || /^eac-rg11/.test(format))
        return { width: 4, height: 4, bytes: 16 };
    const astc = /^astc-(\d+x\d+)-(?:unorm|unorm-srgb)$/.exec(format);
    if (astc && astcBlocks.has(astc[1])) {
        const [width, height] = astc[1].split('x').map(Number);
        return { width, height, bytes: 16 };
    }
    return null;
}

/** Descriptor-sized texture estimate, including every mip, layer, sample, and compressed block tail. */
export function estimateTextureBytes(descriptor) {
    const width = descriptor?.width, height = descriptor?.height;
    const layers = descriptor?.depthOrArrayLayers ?? descriptor?.arrayLayerCount ?? 1;
    const mips = descriptor?.mipLevelCount ?? 1;
    const samples = descriptor?.sampleCount ?? 1;
    const dimension = descriptor?.dimension ?? '2d';
    const block = formatBlock(descriptor?.format);
    if (!block || ![width, height, layers, mips, samples].every(value => Number.isSafeInteger(value) && value > 0))
        return null;
    if (!['1d', '2d', '3d'].includes(dimension) || dimension === '1d' && (height !== 1 || layers !== 1) ||
        samples > 1 && (dimension !== '2d' || layers !== 1 || mips !== 1)) return null;
    const largestExtent = Math.max(width, height, dimension === '3d' ? layers : 1);
    if (mips > Math.min(32, 1 + Math.floor(Math.log2(largestExtent)))) return null;
    let total = 0;
    for (let mip = 0; mip < mips; mip++) {
        const mipWidth = Math.max(1, Math.floor(width / 2 ** mip));
        const mipHeight = Math.max(1, Math.floor(height / 2 ** mip));
        const mipLayers = dimension === '3d' ? Math.max(1, Math.floor(layers / 2 ** mip)) : layers;
        total += Math.ceil(mipWidth / block.width) * Math.ceil(mipHeight / block.height) *
            mipLayers * samples * block.bytes;
    }
    return Number.isSafeInteger(total) ? total : null;
}

/** Snapshot requested GPU allocations reachable through the renderer's existing owners. */
export function estimateRendererGpuMemory(renderer) {
    const seen = new Map();
    const result = { logicalBufferBytes: 0, estimatedTextureBytes: 0, totalEstimatedBytes: 0,
        liveEstimatedBytes: 0, retiringEstimatedBytes: 0, depth24PlusEstimatedBytes: 0,
        bufferCount: 0, textureCount: 0, unmeasuredCount: 0,
        basis: 'WebGPU logical buffer sizes and texture descriptor estimates (depth24plus uses 4 bytes/texel); not driver resident memory',
        exclusions: ['driver allocation padding and tiling', 'canvas swapchain textures',
            'shader, pipeline, sampler and bind-group allocations', 'native Jolt memory'] };
    if (renderer._disposed) return result;

    function add(bufferOrTexture, kind, descriptor, retiring = false) {
        if (!bufferOrTexture) return;
        const prior = seen.get(bufferOrTexture);
        if (prior) {
            if (retiring && !prior.retiring) {
                result.liveEstimatedBytes -= prior.bytes;
                result.retiringEstimatedBytes += prior.bytes;
                prior.retiring = true;
            }
            return;
        }
        let bytes;
        if (kind === 'buffer') {
            bytes = descriptor?.size ?? bufferOrTexture.size;
            if (!Number.isSafeInteger(bytes) || bytes < 0) { result.unmeasuredCount++; seen.set(bufferOrTexture, { bytes: 0, retiring }); return; }
            result.logicalBufferBytes += bytes;
            result.bufferCount++;
        } else {
            bytes = estimateTextureBytes(bufferOrTexture.width ? bufferOrTexture : descriptor);
            if (bytes === null) { result.unmeasuredCount++; seen.set(bufferOrTexture, { bytes: 0, retiring }); return; }
            result.estimatedTextureBytes += bytes;
            result.textureCount++;
            const format = bufferOrTexture.format ?? descriptor?.format;
            if (format === 'depth24plus' || format === 'depth24plus-stencil8')
                result.depth24PlusEstimatedBytes += bytes;
        }
        seen.set(bufferOrTexture, { bytes, retiring });
        if (retiring) result.retiringEstimatedBytes += bytes;
        else result.liveEstimatedBytes += bytes;
    }
    const buffer = (resource, descriptor, retiring) => add(resource, 'buffer', descriptor, retiring);
    const texture = (resource, descriptor, retiring) => add(resource, 'texture', descriptor, retiring);

    for (const entry of renderer._resources.slots) {
        if (!entry) continue;
        const value = entry.value;
        if (entry.kind === 'buffer') buffer(value.buffer, value, value.retired);
        else if (entry.kind === 'texture') texture(value.texture, value, value.retired);
        else if (entry.kind === 'mesh') {
            buffer(value.vertexBuffer, { size: value.vertexBytes }, value.retired);
            buffer(value.indexBuffer, { size: value.indexBytes }, value.retired);
        } else if (entry.kind === 'material') buffer(value.colorBuffer, { size: 16 }, value.retired);
    }

    buffer(renderer.uniformBuffer);
    texture(renderer._whiteTexture);
    texture(renderer.depthTexture);
    buffer(renderer.commands?.engineFrame?.staging);

    const pipeline = renderer.focusedPipeline;
    if (pipeline) {
        buffer(pipeline.uniformBuffer);
        buffer(pipeline.instanceBuffer);
        buffer(pipeline.uiBuffer);
        for (const target of pipeline.targets ?? []) texture(target);
        for (const material of pipeline.materials.values()) buffer(material.buffer);
        const culling = pipeline.gpuScene;
        if (culling) {
            buffer(culling.recordBuffer); buffer(culling.argumentBuffer); buffer(culling.parameterBuffer);
            texture(culling.emptyDepth);
            const hierarchy = culling.hierarchy;
            if (hierarchy) {
                buffer(hierarchy.recordBuffer); buffer(hierarchy.nodeBuffer); buffer(hierarchy.mortonBuffer);
                buffer(hierarchy.statusBuffer); buffer(hierarchy.viewBuffer);
                buffer(hierarchy.frameBuffer); buffer(hierarchy.sortBuffer);
            }
        }
        texture(pipeline.hiZ?.texture);
    }
    for (const job of renderer.skinning.jobs.values())
        for (const resource of job.buffers) buffer(resource);
    for (const ticket of renderer.readback.slots) {
        if (!ticket) continue;
        buffer(ticket.buffer, { size: ticket.stagingBytes });
        for (const resource of ticket.luminance?.owned ?? []) {
            if (resource?.width) texture(resource);
            else buffer(resource);
        }
    }
    for (const resource of renderer._retired) {
        if (resource?.width) add(resource, 'texture', null, true);
        else add(resource, 'buffer', null, true);
    }
    result.totalEstimatedBytes = result.liveEstimatedBytes + result.retiringEstimatedBytes;
    return result;
}
