export const packetHeaderBytes = 64;
export const drawRecordBytes = 112;
export const maximumDraws = 4096;
const magic = 0x50524558;
const backend = 0x55504757;
const u32 = (view, offset) => view.getUint32(offset, true);

/** Checks the entire immutable packet before the renderer makes any GPU calls. */
export function validateFramePacket(view, byteLength, session, surfaceGeneration,
    previousArenaGeneration, previousFrameSequence, width, height, resources) {
    if (byteLength < packetHeaderBytes || byteLength > packetHeaderBytes + maximumDraws * drawRecordBytes)
        throw new RangeError('Invalid frame packet length.');
    if (u32(view, 0) !== magic || u32(view, 4) !== 1 || u32(view, 8) !== backend || u32(view, 12) !== packetHeaderBytes
        || u32(view, 16) !== byteLength || u32(view, 24) !== session || u32(view, 28) !== session
        || u32(view, 32) !== surfaceGeneration || u32(view, 44) !== 0 || u32(view, 48) !== 0
        || u32(view, 52) !== 0 || u32(view, 56) !== 0 || u32(view, 60) !== 0)
        throw new Error('Frame packet header does not match this renderer.');
    const count = u32(view, 20);
    const arenaGeneration = u32(view, 36);
    const frameSequence = u32(view, 40);
    if (count > maximumDraws || packetHeaderBytes + count * drawRecordBytes !== byteLength
        || arenaGeneration === 0 || arenaGeneration > 0x7fffffff || arenaGeneration < previousArenaGeneration
        || frameSequence === 0 || frameSequence <= previousFrameSequence)
        throw new Error('Frame packet count or generation is invalid.');
    for (let i = 0, base = packetHeaderBytes; i < count; i++, base += drawRecordBytes) {
        if (u32(view, base) !== 1 || u32(view, base + 4) !== drawRecordBytes)
            throw new Error('Unsupported frame command.');
        const mesh = resources.get(u32(view, base + 8), u32(view, base + 12), 'mesh', session);
        resources.get(u32(view, base + 16), u32(view, base + 20), 'material', session);
        const x = u32(view, base + 24), y = u32(view, base + 28);
        const rectWidth = u32(view, base + 32), rectHeight = u32(view, base + 36);
        const firstIndex = u32(view, base + 40), indexCount = u32(view, base + 44);
        if (!rectWidth || !rectHeight || x >= width || y >= height
            || rectWidth > width - x || rectHeight > height - y
            || !indexCount || indexCount % 3 !== 0 || firstIndex > mesh.indexCount
            || indexCount > mesh.indexCount - firstIndex)
            throw new RangeError('Draw rectangle or mesh index range is invalid.');
        for (let component = 0; component < 16; component++) {
            if (!Number.isFinite(view.getFloat32(base + 48 + component * 4, true)))
                throw new RangeError('Draw transform must contain finite numbers.');
        }
    }
    return count;
}
