import { GpuEngineFrameScopes } from './gpu-engine-frame-scopes.js';

const headerBytes = 48;
const recordBytes = 112;
const maximumRecords = 4097;
const maximumDynamicOffsets = 16;
const uploadBytes = 24;
const maximumUploads = 8192;
const uniformCapacity = 4 * 1024 * 1024;
// Managed recording and an abandoned attempt's preamble each own an 8 MiB budget.
const storageCapacity = 16 * 1024 * 1024;

/** Imports one bounded engine frame, including uniform snapshots, without retaining managed views. */
export class GpuEngineFrame {
    constructor(commands) {
        this.commands = commands;
        this.bytes = new Uint8Array(headerBytes + maximumRecords * recordBytes + maximumUploads * uploadBytes);
        this.view = new DataView(this.bytes.buffer);
        this.operations = new Array(maximumRecords);
        this.uploads = Array.from({ length: maximumUploads }, () => ({ destination: null, destinationOffset: 0, payloadOffset: 0, byteLength: 0, beforeRecord: 0 }));
        this.uniformBytes = new Uint8Array(uniformCapacity);
        this.storageBytes = new Uint8Array(storageCapacity);
        this.staging = null;
        this.lastSequence = 0;
        this.scopes = new GpuEngineFrameScopes(commands.renderer);
        this.encoderDescriptor = { label: 'Engine frame encoder' };
        this.commandBufferDescriptor = { label: 'Engine frame commands' };
        this.canvasViewDescriptor = { label: 'Engine canvas output' };
        this.stats = { bridgeCalls: 0, submittedFrames: 0, records: 0, draws: 0, storageUploads: 0,
            commandBytes: 0, uniformBytes: 0, storageBytes: 0, stagingBufferCreates: 0,
            canvasTextureAcquisitions: 0, textureViewCreates: 0, commandEncoderCreates: 0,
            commandBufferCreates: 0, renderPassCreates: 0, computePassCreates: 0 };
    }

    submit(memory, uniforms, storage) {
        const c = this.commands, r = c.renderer, stats = this.stats;
        stats.bridgeCalls++;
        r._setOperation('validate-engine-frame', this.commandBufferDescriptor.label);
        r._requireOwner();
        const length = memory?.byteLength;
        if (!Number.isInteger(length) || length < headerBytes || length > this.bytes.length)
            throw new RangeError('WebGPU.EngineFrame.InvalidLength: command arena is outside its bounded range.');
        memory.copyTo(this.bytes);
        const data = this.view;
        const count = data.getUint32(12, true), sequence = data.getUint32(40, true);
        const uploadCount = data.getUint32(44, true);
        if (data.getUint32(0, true) !== 0x45475258 || data.getUint32(4, true) !== 4 ||
            data.getUint32(8, true) !== length || count < 1 || count > maximumRecords || uploadCount > maximumUploads ||
            length !== headerBytes + count * recordBytes + uploadCount * uploadBytes ||
            data.getUint32(16, true) !== r._owner || data.getUint32(20, true) !== r._generation ||
            data.getUint32(24, true) !== r._width || data.getUint32(28, true) !== r._height ||
            !r._configured || sequence <= this.lastSequence)
            throw new Error('WebGPU.EngineFrame.Obsolete: owner, surface, sequence or schema does not match the renderer.');

        const uniformHandle = data.getInt32(32, true), uniformLength = data.getUint32(36, true);
        const storageLength = storage?.byteLength;
        if (uniformLength !== uniforms?.byteLength || uniformLength % 4 || uniformLength > uniformCapacity ||
            !Number.isInteger(storageLength) || storageLength < 0 || storageLength > storageCapacity || storageLength % 4)
            throw new RangeError('WebGPU.EngineFrame.UniformLength: invalid borrowed uniform arena.');
        const arena = uniformHandle ? c.get(uniformHandle, 'buffer') : undefined;
        if (uniformLength && (!arena || !(arena.usage & GPUBufferUsage.UNIFORM) || !(arena.usage & GPUBufferUsage.COPY_DST) || uniformLength > arena.size))
            throw new Error('WebGPU.EngineFrame.UniformBuffer: uploads require an owned UNIFORM/COPY_DST buffer.');

        let presentsCanvas = false, drawCount = 0;
        try {
        // Validate the entire packet before uploads or GPU command encoding. Retained
        // plans retain their own dependencies; packet records cannot forge native state.
        for (let record = 0; record < count; record++) {
            const base = headerBytes + record * recordBytes;
            const prepared = c.get(data.getInt32(base, true), 'commands');
            if (prepared.operations.length !== 1)
                throw new Error('WebGPU.EngineFrame.CommandShape: each frame record must reference one retained operation.');
            if (prepared.hasCanvas && (prepared.generation !== r._generation || prepared.width !== r._width || prepared.height !== r._height))
                throw new Error('WebGPU.EngineFrame.ObsoletePlan: rebuild commands after output replacement.');
            const operation = prepared.operations[0];
            this.operations[record] = operation;
            if (operation.type !== 'render' && operation.type !== 'clear' && operation.type !== 'compute' && operation.type !== 'copyBuffer')
                throw new Error('WebGPU.EngineFrame.CommandType: only retained raster, compute, buffer-copy and attachment operations are admitted.');
            const offsetCount = data.getUint32(base + 4, true);
            if (offsetCount > maximumDynamicOffsets)
                throw new RangeError('WebGPU.EngineFrame.DynamicOffsets: too many dynamic bindings.');
            for (let offset = offsetCount; offset < maximumDynamicOffsets; offset++)
                if (data.getUint32(base + 8 + offset * 4, true) !== 0)
                    throw new RangeError('WebGPU.EngineFrame.ReservedOffset: unused dynamic-offset fields must be zero.');
            const instanceCount = data.getUint32(base + 72, true), flags = data.getUint32(base + 76, true);
            if (flags & ~7 || (!(flags & 1) && instanceCount !== 0) ||
                ((flags & 1) && (operation.type !== 'render' || !operation.engineInstanceCountLimit ||
                    instanceCount > operation.engineInstanceCountLimit)))
                throw new RangeError('WebGPU.EngineFrame.InstanceOverride: an instance count requires a bounded direct-draw opt-in.');
            if (operation.type !== 'render' && (flags & 6))
                throw new RangeError('WebGPU.EngineFrame.DrawArea: only raster draws accept viewport or scissor overrides.');
            const width = operation.plan?.signature.width ?? 0, height = operation.plan?.signature.height ?? 0;
            let suppressDraw = operation.scissor !== undefined &&
                (operation.scissor.width === 0 || operation.scissor.height === 0);
            for (let kind = 0; kind < 2; kind++) {
                const enabled = (flags & (kind === 0 ? 2 : 4)) !== 0;
                const at = base + (kind === 0 ? 80 : 96);
                const x = data.getUint32(at, true), y = data.getUint32(at + 4, true);
                const rectWidth = data.getUint32(at + 8, true), rectHeight = data.getUint32(at + 12, true);
                if ((!enabled && (x || y || rectWidth || rectHeight)) ||
                    (enabled && (x > width || y > height || rectWidth > width - x || rectHeight > height - y ||
                        kind === 0 && (!rectWidth || !rectHeight))))
                    throw new RangeError('WebGPU.EngineFrame.DrawArea: viewport or scissor is outside the bound attachment.');
                if (kind === 1 && enabled)
                    suppressDraw = rectWidth === 0 || rectHeight === 0;
            }
            let dynamicIndex = 0;
            if (operation.bindings) {
                for (let groupIndex = 0; groupIndex < operation.bindings.length; groupIndex++) {
                    const group = operation.bindings[groupIndex];
                    for (let bindingIndex = 0; bindingIndex < group.dynamic.length; bindingIndex++) {
                        const binding = group.dynamic[bindingIndex];
                        if (dynamicIndex >= offsetCount)
                            throw new Error('WebGPU.EngineFrame.DynamicOffsets: missing dynamic binding offset.');
                        const offset = data.getUint32(base + 8 + dynamicIndex++ * 4, true);
                        if (binding.buffer !== arena || offset % binding.alignment ||
                            offset + binding.offset + binding.size > uniformLength)
                            throw new RangeError('WebGPU.EngineFrame.DynamicRange: uniform snapshot is unaligned, missing or owned by another buffer.');
                    }
                }
            }
            if (dynamicIndex !== offsetCount)
                throw new Error('WebGPU.EngineFrame.DynamicOffsets: extra dynamic binding offsets.');
            if (!suppressDraw) {
                if (!Number.isSafeInteger(prepared.draws) || prepared.draws < 0 || drawCount > 262144 - prepared.draws)
                    throw new RangeError('WebGPU.EngineFrame.DrawCapacity: the frame exceeds 262144 replay draws.');
                drawCount += prepared.draws;
            }
            presentsCanvas ||= prepared.presentsCanvas;
        }

        let previousRecord = 0, expectedPayloadOffset = 0;
        for (let index = 0; index < uploadCount; index++) {
            const base = headerBytes + count * recordBytes + index * uploadBytes;
            const destination = c.get(data.getInt32(base, true), 'buffer');
            const destinationOffset = data.getUint32(base + 4, true);
            const payloadOffset = data.getUint32(base + 8, true);
            const byteLength = data.getUint32(base + 12, true);
            const beforeRecord = data.getUint32(base + 16, true);
            if (data.getUint32(base + 20, true) > 1 || beforeRecord > count || beforeRecord < previousRecord ||
                !byteLength || (destinationOffset | payloadOffset | byteLength) % 4 ||
                payloadOffset !== expectedPayloadOffset || payloadOffset > storageLength - byteLength ||
                destinationOffset > destination.size - byteLength ||
                !(destination.usage & GPUBufferUsage.COPY_DST))
                throw new RangeError('WebGPU.EngineFrame.BufferUpload: invalid owner, range, usage, order or alignment.');
            const entry = this.uploads[index];
            entry.destination = destination.buffer;
            entry.destinationOffset = destinationOffset;
            entry.payloadOffset = payloadOffset;
            entry.byteLength = byteLength;
            entry.beforeRecord = beforeRecord;
            previousRecord = beforeRecord;
            expectedPayloadOffset += byteLength;
        }
        if (expectedPayloadOffset !== storageLength)
            throw new RangeError('WebGPU.EngineFrame.StoragePayload: payload must be exactly covered by ordered uploads.');
        if (uniformLength) uniforms.copyTo(this.uniformBytes);
        if (storageLength) storage.copyTo(this.storageBytes);
        } catch (error) {
            for (let record = 0; record < Math.min(count, maximumRecords); record++) this.operations[record] = undefined;
            for (let index = 0; index < Math.min(uploadCount, maximumUploads); index++) this.uploads[index].destination = null;
            throw error;
        }

        const guardCanvasCapture = presentsCanvas &&
            (r.readback.hasPendingCanvas(r._generation) || r.luminance.hasPendingCanvas(r._generation));
        let receipt = null;
        r._executing = true;
        try {
            receipt = this.scopes.begin(sequence, guardCanvasCapture);
            if (!this.staging) {
                if (uniformCapacity + storageCapacity > r.device.limits.maxBufferSize)
                    throw new RangeError('WebGPU.EngineFrame.StagingCapacity: device buffer limit is too small.');
                this.staging = r.device.createBuffer({ size: uniformCapacity + storageCapacity,
                    usage: GPUBufferUsage.COPY_SRC | GPUBufferUsage.COPY_DST, label: 'Engine frame staging' });
                stats.stagingBufferCreates++;
            }
            r._setOperation('acquire-canvas', this.canvasViewDescriptor.label);
            const canvasTexture = r.context.getCurrentTexture();
            stats.canvasTextureAcquisitions++;
            c.canvasColor.view = canvasTexture.createView(this.canvasViewDescriptor);
            stats.textureViewCreates++;
            c.canvasColor.width = c.canvasDepth.width = r._width;
            c.canvasColor.height = c.canvasDepth.height = r._height;
            c.canvasColor.format = r.format;
            c.canvasDepth.view = r.depthView;
            r._setOperation('create-command-encoder', this.encoderDescriptor.label);
            const encoder = r.device.createCommandEncoder(this.encoderDescriptor);
            stats.commandEncoderCreates++;
            if (uniformLength) encoder.copyBufferToBuffer(this.staging, 0, arena.buffer, 0, uniformLength);
            let uploadIndex = 0;
            for (let record = 0; record <= count; record++) {
                while (uploadIndex < uploadCount && this.uploads[uploadIndex].beforeRecord === record) {
                    const entry = this.uploads[uploadIndex++];
                    encoder.copyBufferToBuffer(this.staging, uniformCapacity + entry.payloadOffset,
                        entry.destination, entry.destinationOffset, entry.byteLength);
                }
                if (record === count) break;
                const base = headerBytes + record * recordBytes;
                const operation = this.operations[record];
                c.encodeOperation(encoder, operation, data, base + 8, record, stats);
            }
            r._setOperation('finish-command-encoder', this.commandBufferDescriptor.label);
            r._submission[0] = encoder.finish(this.commandBufferDescriptor);
            stats.commandBufferCreates++;
            if (uniformLength) r.device.queue.writeBuffer(this.staging, 0, this.uniformBytes, 0, uniformLength);
            if (storageLength) r.device.queue.writeBuffer(this.staging, uniformCapacity, this.storageBytes, 0, storageLength);
            r._setOperation('submit-engine-frame', this.commandBufferDescriptor.label);
            r.device.queue.submit(r._submission);
            this.lastSequence = sequence;
            stats.submittedFrames++;
            stats.records += count;
            stats.draws += drawCount;
            stats.storageUploads += uploadCount;
            stats.commandBytes += length;
            stats.uniformBytes += uniformLength;
            stats.storageBytes += storageLength;
            const producerGate = receipt.close(true);
            // The accepted frame's commands precede these readback copies in queue order.
            // The acquired canvas texture is still current until this browser task returns.
            r.readback.captureCanvas(canvasTexture, r._generation, presentsCanvas, producerGate);
            r.luminance.captureCanvas(canvasTexture, r._generation, presentsCanvas, producerGate);
            r._stats.draws += drawCount;
            r._stats.frameSubmitCalls++;
            r._stats.copiedBytes += length;
            r._stats.uploadedBytes += uniformLength + storageLength;
            return presentsCanvas;
        } catch (error) {
            receipt?.close();
            r._fail(error);
            throw error;
        }
        finally {
            for (let record = 0; record < count; record++) {
                this.operations[record]?.plan?.release();
                this.operations[record] = undefined;
            }
            for (let index = 0; index < uploadCount; index++) this.uploads[index].destination = null;
            c.canvasColor.view = c.canvasDepth.view = undefined;
            r._submission[0] = null;
            r._executing = false;
        }
    }

    getStatistics() {
        // Object counts are API creation calls, not implementation-defined heap bytes.
        return { ...this.stats, errorScopes: { ...this.scopes.stats } };
    }

    dispose() { this.scopes.dispose(); this.staging?.destroy(); this.staging = null; }
}
