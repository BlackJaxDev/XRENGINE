import { isBrowserColorTexture } from './cooked-texture.js';

export const pipelineHeaderBytes = 256;
export const pipelineDrawBytes = 304;
export const pipelineUiBytes = 80;
export const pipelineMaximumItems = 4096;
export const pipelineMaximumBytes = pipelineHeaderBytes + pipelineMaximumItems * (pipelineDrawBytes + pipelineUiBytes);

/** Reject the complete frame before a GPU write or command is issued. */
export function validatePipelinePacket(view, byteLength, pipeline) {
    const renderer = pipeline.renderer;
    if (byteLength < pipelineHeaderBytes || byteLength > pipelineMaximumBytes
        || view.getUint32(0, true) !== 0x46524558 || view.getUint32(4, true) !== 2
        || view.getUint32(8, true) !== byteLength)
        throw new Error('Invalid focused raster packet envelope.');
    const draws = view.getUint32(12, true), ui = view.getUint32(28, true);
    if (draws > pipelineMaximumItems || ui > pipelineMaximumItems
        || byteLength !== pipelineHeaderBytes + draws * pipelineDrawBytes + ui * pipelineUiBytes
        || view.getUint32(16, true) !== renderer._owner
        || view.getUint32(20, true) !== renderer._generation
        || view.getUint32(24, true) <= pipeline.sequence
        || view.getUint32(32, true) !== renderer._width || view.getUint32(36, true) !== renderer._height
        || !renderer._width || !renderer._height || (view.getUint32(40, true) & ~1))
        throw new Error('Stale, oversized or incompatible focused raster packet.');
    for (let i = 44; i < 64; i += 4)
        if (view.getUint32(i, true)) throw new Error('Reserved raster header fields must be zero.');
    for (let i = 64; i < 256; i += 4)
        if (!Number.isFinite(view.getFloat32(i, true))) throw new Error('Frame lighting and transforms must be finite.');
    if (view.getFloat32(204, true) < 0 || view.getFloat32(220, true) <= 0)
        throw new Error('Light intensity and exposure are invalid.');
    for (let i = 208; i < 256; i += 4)
        if (view.getFloat32(i, true) < 0) throw new Error('Lighting channels must be nonnegative.');
    if (view.getFloat32(192, true) ** 2 + view.getFloat32(196, true) ** 2 + view.getFloat32(200, true) ** 2 < 1e-12)
        throw new Error('Directional light direction must be nonzero.');
    let previousMode = 0, lastX = -1, lastY = -1, lastWidth = -1, lastHeight = -1;
    for (let i = 0, at = pipelineHeaderBytes; i < draws; i++, at += pipelineDrawBytes) {
        const mesh = renderer._resources.getHandle(view.getUint32(at, true), 'mesh', renderer._owner);
        const handle = view.getUint32(at + 4, true);
        renderer._resources.getHandle(handle, 'material', renderer._owner);
        const material = pipeline.materials.get(handle);
        if (!material) throw new Error('Material has no selected browser raster profile.');
        const x = view.getUint32(at + 8, true), y = view.getUint32(at + 12, true);
        const width = view.getUint32(at + 16, true), height = view.getUint32(at + 20, true);
        if (x !== lastX || y !== lastY || width !== lastWidth || height !== lastHeight) previousMode = 0;
        if (material.mode < previousMode) throw new Error('Each viewport must order opaque, masked, then transparent draws.');
        previousMode = material.mode; lastX = x; lastY = y; lastWidth = width; lastHeight = height;
        const first = view.getUint32(at + 24, true), count = view.getUint32(at + 28, true);
        if (!width || !height || width > renderer._width - x || height > renderer._height - y
            || x > renderer._width || y > renderer._height || !count || first % 3 || count % 3
            || count > mesh.indexCount - first || first > mesh.indexCount)
            throw new Error('Raster draw viewport or index range is invalid.');
        for (let j = 32; j < 160; j += 4)
            if (!Number.isFinite(view.getFloat32(at + j, true))) throw new Error('Instance matrices must be finite.');
        if (view.getFloat32(at + 44, true) !== 0 || view.getFloat32(at + 60, true) !== 0
            || view.getFloat32(at + 76, true) !== 0 || view.getFloat32(at + 92, true) !== 1)
            throw new Error('World bounds require an affine model transform.');
        if ((view.getUint32(at + 160, true) & ~15) || view.getUint32(at + 164, true)
            || view.getUint32(at + 168, true) || view.getUint32(at + 172, true))
            throw new Error('Unsupported raster instance flags.');
        if ((view.getUint32(at + 160, true) & 2) && !(view.getUint32(at + 160, true) & 1))
            throw new Error('Shadow-only instances must cast shadows.');
        const flags = view.getUint32(at + 160, true);
        if ((flags & 8) && (material.mode !== 0 || (flags & 4) || mesh.computeSkinned))
            throw new Error('Hi-Z occluders require opaque geometry with trusted bounds.');
        for (let j = 176; j < 224; j += 4)
            if (!Number.isFinite(view.getFloat32(at + j, true))) throw new Error('World bounds must be finite.');
        for (let j = 240; j < 304; j += 4)
            if (!Number.isFinite(view.getFloat32(at + j, true))) throw new Error('View projection must be finite.');
        if (view.getFloat32(at + 188, true) < 0 || view.getUint32(at + 204, true)
            || view.getUint32(at + 220, true) || view.getUint32(at + 228, true)
            || view.getUint32(at + 232, true) || view.getUint32(at + 236, true))
            throw new Error('Canonical world bounds radius or padding is invalid.');
        for (let j = 0; j < 12; j += 4)
            if (view.getFloat32(at + 192 + j, true) > view.getFloat32(at + 208 + j, true))
                throw new Error('World AABB minimum exceeds its maximum.');
    }
    if (ui && !pipeline.settings.uiEnabled) throw new Error('UI composition is disabled by the selected quality profile.');
    for (let i = 0, at = pipelineHeaderBytes + draws * pipelineDrawBytes; i < ui; i++, at += pipelineUiBytes) {
        const handle = view.getUint32(at, true);
        if (handle) {
            const texture = renderer._resources.getHandle(handle, 'texture', renderer._owner);
            if (!texture.uiBindGroup || texture.sampleCount !== 1 || !isBrowserColorTexture(texture))
                throw new Error('UI texture must be a registered, single-sample color atlas.');
        }
        for (let j = 4; j <= 64; j += 4)
            if (!Number.isFinite(view.getFloat32(at + j, true))) throw new Error('UI coordinates and tint must be finite.');
        if (view.getFloat32(at + 12, true) < 0 || view.getFloat32(at + 16, true) < 0)
            throw new Error('UI rectangle extents must be nonnegative.');
        for (let j = 20; j <= 48; j += 4)
            if (view.getFloat32(at + j, true) < 0 || view.getFloat32(at + j, true) > 1)
                throw new Error('UI atlas coordinates and tint must be normalized.');
        if (view.getFloat32(at + 20, true) + view.getFloat32(at + 28, true) > 1
            || view.getFloat32(at + 24, true) + view.getFloat32(at + 32, true) > 1)
            throw new Error('UI atlas rectangle exceeds normalized texture bounds.');
        const x = view.getFloat32(at + 52, true), y = view.getFloat32(at + 56, true);
        const width = view.getFloat32(at + 60, true), height = view.getFloat32(at + 64, true);
        if (!Number.isInteger(x) || !Number.isInteger(y) || !Number.isInteger(width) || !Number.isInteger(height) || x < 0 || y < 0 || width < 0 || height < 0
            || x + width > renderer._width || y + height > renderer._height
            || view.getUint32(at + 68, true) || view.getUint32(at + 72, true) || view.getUint32(at + 76, true))
            throw new Error('UI clip rectangle or reserved fields are invalid.');
    }
    return draws;
}
