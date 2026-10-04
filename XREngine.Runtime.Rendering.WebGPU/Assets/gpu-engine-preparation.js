import { textureFormatInfo } from './gpu-texture-formats.js';

const headerBytes = 24;
const recordBytes = 48;
const maximumRecords = 4096;
const payloadCapacity = 256 * 1024 * 1024;
const writeChunkBytes = 64 * 1024 * 1024;

/** Exact initialization and texture transfers owned by the engine frame's acceptance call. */
export class GpuEnginePreparation {
    constructor(commands) {
        this.commands = commands;
        this.bytes = new Uint8Array(headerBytes + maximumRecords * recordBytes);
        this.view = new DataView(this.bytes.buffer);
        this.payload = new Uint8Array(0);
        this.destinations = new Array(maximumRecords);
        this.sources = new Array(maximumRecords);
        this.textureOffsets = new Uint32Array(maximumRecords);
        this.textureRows = new Uint32Array(maximumRecords);
        this.transferBytes = new Uint8Array(0);
        this.staging = null;
        this.stagingSize = 0;
        this.bufferSource = { buffer: null, offset: 0, bytesPerRow: 0, rowsPerImage: 0 };
        this.imageSource = { texture: null, mipLevel: 0, origin: { x: 0, y: 0, z: 0 } };
        this.imageDestination = { texture: null, mipLevel: 0, origin: { x: 0, y: 0, z: 0 } };
        this.extent = { width: 0, height: 0, depthOrArrayLayers: 1 };
        this.count = this.length = this.sequence = this.transferLength = this.textureCount = 0;
    }

    validate(memory, payload, lastSequence) {
        const c = this.commands, r = c.renderer;
        r._requireOwner();
        const length = memory?.byteLength, payloadLength = payload?.byteLength;
        if (!Number.isInteger(length) || length < headerBytes || length > this.bytes.length ||
            !Number.isInteger(payloadLength) || payloadLength < 0 || payloadLength > payloadCapacity || payloadLength % 4)
            throw new RangeError('WebGPU.Preparation.Length: transfers exceed the bounded command or payload arena.');
        memory.copyTo(this.bytes);
        const data = this.view, count = data.getUint32(16, true), sequence = data.getUint32(12, true);
        if (data.getUint32(0, true) !== 0x50524758 || data.getUint32(4, true) !== 2 ||
            data.getUint32(8, true) !== r._owner || sequence <= lastSequence || count > maximumRecords ||
            length !== headerBytes + count * recordBytes || data.getUint32(20, true) !== payloadLength)
            throw new Error('WebGPU.Preparation.Obsolete: owner, sequence, schema or extent does not match.');
        this.count = count;
        this.length = payloadLength;
        this.sequence = sequence;
        this.transferLength = this.textureCount = 0;
        let expected = 0;
        for (let index = 0; index < count; index++) {
            const at = headerBytes + index * recordBytes;
            const offset = data.getUint32(at + 4, true), source = data.getUint32(at + 8, true);
            const bytes = data.getUint32(at + 12, true), kind = data.getUint32(at + 16, true);
            if (kind > 2 || (offset | source | bytes) % 4 || source !== expected || source > payloadLength - bytes)
                throw new RangeError('WebGPU.Preparation.Range: transfers require exact contiguous aligned payload extents.');
            expected += bytes;
            if (kind === 0) {
                const buffer = c.get(data.getInt32(at, true), 'buffer');
                if (!(buffer.usage & GPUBufferUsage.COPY_DST) || !bytes || offset > buffer.size - bytes)
                    throw new RangeError('WebGPU.Preparation.BufferRange: initialization requires aligned COPY_DST ranges.');
                for (let reserved = 20; reserved < recordBytes; reserved += 4)
                    if (data.getUint32(at + reserved, true)) throw new RangeError('WebGPU.Preparation.Reserved: unused fields must be zero.');
                this.destinations[index] = buffer.buffer;
                continue;
            }
            const texture = c.get(data.getInt32(at, true), 'texture');
            const mip = data.getUint32(at + 20, true), layer = data.getUint32(at + 24, true);
            const width = data.getUint32(at + 28, true), height = data.getUint32(at + 32, true);
            const info = textureFormatInfo(texture.format, r.device);
            const mipWidth = Math.max(1, Math.floor(texture.width / 2 ** mip));
            const mipHeight = Math.max(1, Math.floor(texture.height / 2 ** mip));
            if (offset || mip >= texture.mipLevelCount || layer >= (texture.arrayLayerCount ?? 1) ||
                texture.sampleCount !== 1 || !(texture.usage & GPUTextureUsage.COPY_DST) || !info.color || !width || !height)
                throw new RangeError('WebGPU.Preparation.TextureRange: a transfer requires a live single-sample color destination.');
            this.destinations[index] = texture.texture;
            this.textureCount++;
            if (kind === 1) {
                const x = data.getUint32(at + 36, true), y = data.getUint32(at + 40, true);
                const exact = data.getUint32(at + 44, true), rowBytes = width * info.bytes;
                if (x > mipWidth - width || y > mipHeight - height || exact !== rowBytes * height || bytes !== Math.ceil(exact / 4) * 4)
                    throw new RangeError('WebGPU.Preparation.TexturePayload: mip snapshots require exact tightly packed source bytes.');
                const rowPitch = Math.ceil(rowBytes / 256) * 256;
                const transferOffset = Math.ceil(this.transferLength / 256) * 256;
                this.textureOffsets[index] = transferOffset;
                this.textureRows[index] = rowPitch;
                this.transferLength = transferOffset + Math.ceil((rowPitch * (height - 1) + rowBytes) / 4) * 4;
                if (this.transferLength > Math.min(payloadCapacity, r.device.limits.maxBufferSize))
                    throw new RangeError('WebGPU.Preparation.TextureStagingCapacity: padded texture snapshots exceed the device transfer arena.');
            } else {
                const from = c.get(data.getInt32(at + 36, true), 'texture');
                const fromMip = data.getUint32(at + 40, true);
                if (bytes || data.getUint32(at + 44, true) || from === texture || fromMip >= from.mipLevelCount ||
                    (from.arrayLayerCount ?? 1) !== 1 || from.sampleCount !== 1 || from.format !== texture.format ||
                    !(from.usage & GPUTextureUsage.COPY_SRC) || width !== mipWidth || height !== mipHeight ||
                    width !== Math.max(1, Math.floor(from.width / 2 ** fromMip)) || height !== Math.max(1, Math.floor(from.height / 2 ** fromMip)))
                    throw new RangeError('WebGPU.Preparation.TextureCopy: layer copies require complete matching source and destination mips.');
                this.sources[index] = from.texture;
            }
        }
        if (expected !== payloadLength)
            throw new RangeError('WebGPU.Preparation.Payload: every byte must belong to a retained transfer.');
        if (this.payload.length < payloadLength) this.payload = new Uint8Array(this.capacity(payloadLength, this.payload.length));
        if (payloadLength) payload.copyTo(this.payload);
        if (this.transferBytes.length < this.transferLength)
            this.transferBytes = new Uint8Array(this.capacity(this.transferLength, this.transferBytes.length));
        for (let index = 0; index < count; index++) {
            const at = headerBytes + index * recordBytes;
            if (data.getUint32(at + 16, true) !== 1) continue;
            const source = data.getUint32(at + 8, true), height = data.getUint32(at + 32, true);
            const rowBytes = data.getUint32(at + 44, true) / height;
            for (let row = 0; row < height; row++) {
                const from = source + row * rowBytes, to = this.textureOffsets[index] + row * this.textureRows[index];
                for (let byte = 0; byte < rowBytes; byte++) this.transferBytes[to + byte] = this.payload[from + byte];
            }
        }
    }

    capacity(required, previous) {
        let capacity = Math.max(65536, previous);
        while (capacity < required) capacity = Math.min(payloadCapacity, capacity * 2);
        return capacity;
    }

    encode(encoder) {
        const r = this.commands.renderer, data = this.view;
        if (this.transferLength && this.stagingSize < this.transferLength) {
            const size = Math.min(r.device.limits.maxBufferSize, this.capacity(this.transferLength, this.stagingSize));
            const replacement = r.device.createBuffer({ size, usage: GPUBufferUsage.COPY_SRC | GPUBufferUsage.COPY_DST, label: 'Engine texture snapshots' });
            if (this.staging) r._retire(this.staging);
            this.staging = replacement;
            this.stagingSize = size;
            this.commands.engineFrame.stats.stagingBufferCreates++;
        }
        this.bufferSource.buffer = this.staging;
        for (let index = 0; index < this.count; index++) {
            const at = headerBytes + index * recordBytes, kind = data.getUint32(at + 16, true);
            if (kind === 0) continue;
            this.imageDestination.texture = this.destinations[index];
            this.imageDestination.mipLevel = data.getUint32(at + 20, true);
            this.imageDestination.origin.z = data.getUint32(at + 24, true);
            this.extent.width = data.getUint32(at + 28, true);
            this.extent.height = data.getUint32(at + 32, true);
            if (kind === 1) {
                this.imageDestination.origin.x = data.getUint32(at + 36, true);
                this.imageDestination.origin.y = data.getUint32(at + 40, true);
                this.bufferSource.offset = this.textureOffsets[index];
                this.bufferSource.bytesPerRow = this.textureRows[index];
                this.bufferSource.rowsPerImage = this.extent.height;
                encoder.copyBufferToTexture(this.bufferSource, this.imageDestination, this.extent);
            } else {
                this.imageDestination.origin.x = this.imageDestination.origin.y = 0;
                this.imageSource.texture = this.sources[index];
                this.imageSource.mipLevel = data.getUint32(at + 40, true);
                encoder.copyTextureToTexture(this.imageSource, this.imageDestination, this.extent);
            }
        }
        this.bufferSource.buffer = this.imageSource.texture = this.imageDestination.texture = null;
    }

    write() {
        const queue = this.commands.renderer.device.queue, data = this.view;
        for (let index = 0; index < this.count; index++) {
            const at = headerBytes + index * recordBytes;
            if (data.getUint32(at + 16, true) !== 0) continue;
            const offset = data.getUint32(at + 4, true), source = data.getUint32(at + 8, true);
            const bytes = data.getUint32(at + 12, true);
            for (let written = 0; written < bytes; written += writeChunkBytes)
                queue.writeBuffer(this.destinations[index], offset + written, this.payload,
                    source + written, Math.min(writeChunkBytes, bytes - written));
        }
        if (this.transferLength) queue.writeBuffer(this.staging, 0, this.transferBytes, 0, this.transferLength);
    }

    release() {
        for (let index = 0; index < this.count; index++) this.destinations[index] = this.sources[index] = undefined;
        this.bufferSource.buffer = this.imageSource.texture = this.imageDestination.texture = null;
        this.count = this.length = this.transferLength = this.textureCount = 0;
    }

    dispose() { this.release(); this.staging?.destroy(); this.staging = null; }
}
