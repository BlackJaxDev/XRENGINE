export const uploadHeaderBytes = 64;
export const uploadRecordBytes = 48;
export const maximumUploadCommands = 4096;
export const maximumUploadPayloadBytes = 32 * 1024 * 1024;
const u32 = (view, offset) => view.getUint32(offset, true);

/** Allocates development context only when a packet is rejected. */
function reject(message, commandIndex = -1, byteOffset = 0, opcode = null, cause) {
    const error = new Error(message, cause === undefined ? undefined : { cause });
    error.name = 'UploadPacketError';
    error.commandIndex = commandIndex;
    error.byteOffset = byteOffset;
    error.opcode = opcode;
    throw error;
}

/** Validates one packed upload and its payload before any GPU write is queued. */
export function validateUploadPacket(view, commandBytes, payloadView, payloadBytes, session,
    previousCommandArenaGeneration, previousPayloadArenaGeneration, previousSequence, resources) {
    if (!Number.isSafeInteger(commandBytes) || !Number.isSafeInteger(payloadBytes)
        || commandBytes < uploadHeaderBytes || commandBytes > uploadHeaderBytes + maximumUploadCommands * uploadRecordBytes
        || commandBytes > view.byteLength || payloadBytes > payloadView.byteLength
        || payloadBytes > maximumUploadPayloadBytes || payloadBytes < 0)
        reject('Invalid upload packet length.', -1, 16);
    if (!Number.isSafeInteger(session) || session <= 0 || session > 0x7fffffff)
        reject('Invalid upload session.', -1, 24);
    const count = u32(view, 20);
    const commandGeneration = u32(view, 32);
    const payloadGeneration = u32(view, 36);
    const sequence = u32(view, 40);
    if (u32(view, 0) !== 0x55524558 || u32(view, 4) !== 1 || u32(view, 8) !== 0x55504757
        || u32(view, 12) !== uploadHeaderBytes || u32(view, 16) !== commandBytes
        || count > maximumUploadCommands || uploadHeaderBytes + count * uploadRecordBytes !== commandBytes
        || u32(view, 24) !== session || u32(view, 28) !== session
        || !commandGeneration || commandGeneration > 0x7fffffff || commandGeneration < previousCommandArenaGeneration
        || !payloadGeneration || payloadGeneration > 0x7fffffff || payloadGeneration < previousPayloadArenaGeneration
        || !sequence || sequence <= previousSequence || u32(view, 44) !== payloadBytes
        || u32(view, 48) !== 0 || u32(view, 52) !== 0 || u32(view, 56) !== 0 || u32(view, 60) !== 0)
        reject('Upload packet header does not match this renderer.');

    let packedOffset = 0;
    for (let i = 0, base = uploadHeaderBytes; i < count; i++, base += uploadRecordBytes) {
        try {
            const opcode = u32(view, base);
            const destination = u32(view, base + 16);
            const y = u32(view, base + 20);
            const width = u32(view, base + 24);
            const height = u32(view, base + 28);
            const offset = u32(view, base + 32);
            const length = u32(view, base + 36);
            if (u32(view, base + 4) !== uploadRecordBytes || u32(view, base + 40) !== 0
                || u32(view, base + 44) !== 0 || offset !== packedOffset
                || length > payloadBytes - packedOffset)
                throw new RangeError('Invalid upload record size, reserved field, or packed payload range.');
            const slot = u32(view, base + 8), generation = u32(view, base + 12);
            if (opcode === 1 || opcode === 2) {
                const mesh = resources.get(slot, generation, 'mesh', session);
                const stride = opcode === 1 ? 20 : 4;
                const capacity = opcode === 1 ? mesh.vertexBytes : mesh.indexBytes;
                if (y || width || height || !length || destination % stride || length % stride
                    || destination > capacity || length > capacity - destination)
                    throw new RangeError('Mesh upload range is invalid.');
                if (opcode === 1) {
                    for (let p = offset; p < offset + length; p += 4) {
                        if (!Number.isFinite(payloadView.getFloat32(p, true)))
                            throw new RangeError('Mesh vertex components must be finite.');
                    }
                } else {
                    for (let p = offset; p < offset + length; p += 4) {
                        if (u32(payloadView, p) >= mesh.vertexCount)
                            throw new RangeError('Mesh index exceeds the vertex count.');
                    }
                }
            } else if (opcode === 3) {
                const texture = resources.get(slot, generation, 'texture', session);
                if (!width || !height || destination >= texture.width || y >= texture.height
                    || width > texture.width - destination || height > texture.height - y
                    || length !== width * height * 4)
                    throw new RangeError('Texture upload rectangle or byte count is invalid.');
            } else if (opcode === 4) {
                resources.get(slot, generation, 'material', session);
                if (destination || y || width || height || length !== 16)
                    throw new RangeError('Material tint upload fields are invalid.');
                for (let p = offset; p < offset + 16; p += 4) {
                    const channel = payloadView.getFloat32(p, true);
                    if (!Number.isFinite(channel) || channel < 0 || channel > 1 || (p === offset + 12 && channel !== 1))
                        throw new RangeError('Opaque material tint must have linear channels in [0, 1] and alpha 1.');
                }
            } else {
                throw new Error('Unsupported upload command.');
            }
            packedOffset += length;
        } catch (error) {
            reject(`Upload command ${i} (opcode ${u32(view, base)}) is invalid: ${error.message}`,
                i, base, u32(view, base), error);
        }
    }
    if (packedOffset !== payloadBytes) reject('Upload payload has trailing bytes.', -1, 44);
    return count;
}
