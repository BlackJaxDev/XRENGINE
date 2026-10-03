export const packetHeaderBytes = 96;
export const drawRecordBytes = 112;
export const maximumDraws = 4096;
const magic = 0x50524558;
const backend = 0x55504757;
const u32 = (view, offset) => view.getUint32(offset, true);

/** Context for a rejected packet, populated only on the failure path. */
export class FramePacketError extends Error {
    constructor(message, commandIndex = -1, byteOffset = 0, opcode = null, cause) {
        super(message, cause === undefined ? undefined : { cause });
        this.name = 'FramePacketError';
        this.commandIndex = commandIndex;
        this.byteOffset = byteOffset;
        this.opcode = opcode;
    }
}

const reject = (message, commandIndex = -1, byteOffset = 0, opcode = null) => {
    throw new FramePacketError(message, commandIndex, byteOffset, opcode);
};

/** Checks the entire immutable packet before the renderer makes any GPU calls. */
export function validateFramePacket(view, byteLength, session, surfaceGeneration,
    previousArenaGeneration, previousFrameSequence, width, height, resources) {
    if (!Number.isSafeInteger(byteLength) || byteLength < packetHeaderBytes
        || byteLength > packetHeaderBytes + maximumDraws * drawRecordBytes
        || byteLength > view.byteLength)
        reject('Invalid frame packet length.', -1, 16);
    if (!Number.isSafeInteger(session) || session <= 0
        || !Number.isSafeInteger(surfaceGeneration) || surfaceGeneration <= 0)
        reject('The renderer session or surface generation is invalid.', -1, 24);
    if (u32(view, 0) !== magic || u32(view, 4) !== 2 || u32(view, 8) !== backend || u32(view, 12) !== packetHeaderBytes
        || u32(view, 16) !== byteLength || u32(view, 24) !== session || u32(view, 28) !== session
        || u32(view, 32) !== surfaceGeneration || u32(view, 44) !== 0 || u32(view, 48) !== 0
        || u32(view, 52) !== 0 || u32(view, 56) !== 0 || u32(view, 60) !== 0 || u32(view, 92) !== 0)
        reject('Frame packet header does not match this renderer.');
    const red = view.getFloat32(64, true), green = view.getFloat32(68, true);
    const blue = view.getFloat32(72, true), alpha = view.getFloat32(76, true);
    const depth = view.getFloat32(80, true);
    if (!Number.isFinite(red) || red < 0 || red > 1
        || !Number.isFinite(green) || green < 0 || green > 1
        || !Number.isFinite(blue) || blue < 0 || blue > 1
        || alpha !== 1 || depth !== 1)
        reject('The canvas pass requires finite RGB, opaque alpha, and a depth clear of 1.', -1, 64);
    if (!Number.isSafeInteger(width) || !Number.isSafeInteger(height) || width <= 0 || height <= 0
        || u32(view, 84) !== width || u32(view, 88) !== height)
        reject('Frame packet output extent does not match the configured canvas.', -1, 84);
    const count = u32(view, 20);
    const arenaGeneration = u32(view, 36);
    const frameSequence = u32(view, 40);
    if (count > maximumDraws || packetHeaderBytes + count * drawRecordBytes !== byteLength)
        reject('Frame packet command count is invalid.', -1, 20);
    if (arenaGeneration === 0 || arenaGeneration > 0x7fffffff || arenaGeneration < previousArenaGeneration)
        reject('Frame packet arena generation is invalid.', -1, 36);
    if (frameSequence === 0 || frameSequence <= previousFrameSequence)
        reject('Frame packet sequence is invalid.', -1, 40);
    for (let i = 0, base = packetHeaderBytes; i < count; i++, base += drawRecordBytes) {
        const opcode = u32(view, base);
        if (opcode !== 1 || u32(view, base + 4) !== drawRecordBytes)
            reject('Unsupported frame command.', i, base, opcode);
        let mesh;
        try {
            mesh = resources.get(u32(view, base + 8), u32(view, base + 12), 'mesh', session);
            resources.get(u32(view, base + 16), u32(view, base + 20), 'material', session);
        } catch (error) {
            throw new FramePacketError(`Invalid draw resource: ${error?.message ?? String(error)}`, i, base, opcode, error);
        }
        const x = u32(view, base + 24), y = u32(view, base + 28);
        const rectWidth = u32(view, base + 32), rectHeight = u32(view, base + 36);
        const firstIndex = u32(view, base + 40), indexCount = u32(view, base + 44);
        if (!rectWidth || !rectHeight || x >= width || y >= height
            || rectWidth > width - x || rectHeight > height - y
            || !indexCount || firstIndex % 3 !== 0 || indexCount % 3 !== 0 || firstIndex > mesh.indexCount
            || indexCount > mesh.indexCount - firstIndex)
            reject('Draw rectangle or mesh index range is invalid.', i, base, opcode);
        for (let component = 0; component < 16; component++) {
            if (!Number.isFinite(view.getFloat32(base + 48 + component * 4, true)))
                reject('Draw transform must contain finite numbers.', i, base + 48 + component * 4, opcode);
        }
    }
    return count;
}
