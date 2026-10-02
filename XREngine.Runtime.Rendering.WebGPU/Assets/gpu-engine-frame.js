const headerBytes = 48;
const recordBytes = 80;
const maximumRecords = 4097;
const maximumDynamicOffsets = 16;
const uploadBytes = 24;
const maximumUploads = 4096;
const uniformCapacity = 4 * 1024 * 1024;
const storageCapacity = 8 * 1024 * 1024;

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
    }

    submit(memory, uniforms, storage) {
        const c = this.commands, r = c.renderer;
        r._setOperation('validate-engine-frame');
        r._requireOwner();
        const length = memory?.byteLength;
        if (!Number.isInteger(length) || length < headerBytes || length > this.bytes.length)
            throw new RangeError('WebGPU.EngineFrame.InvalidLength: command arena is outside its bounded range.');
        memory.copyTo(this.bytes);
        const data = this.view;
        const count = data.getUint32(12, true), sequence = data.getUint32(40, true);
        const uploadCount = data.getUint32(44, true);
        if (data.getUint32(0, true) !== 0x45475258 || data.getUint32(4, true) !== 2 ||
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
            if (operation.type !== 'render' && operation.type !== 'clear' && operation.type !== 'compute')
                throw new Error('WebGPU.EngineFrame.CommandType: only retained raster, compute and attachment operations are admitted.');
            const offsetCount = data.getUint32(base + 4, true);
            if (offsetCount > maximumDynamicOffsets)
                throw new RangeError('WebGPU.EngineFrame.DynamicOffsets: too many dynamic bindings.');
            for (let offset = offsetCount; offset < maximumDynamicOffsets; offset++)
                if (data.getUint32(base + 8 + offset * 4, true) !== 0)
                    throw new RangeError('WebGPU.EngineFrame.ReservedOffset: unused dynamic-offset fields must be zero.');
            const instanceCount = data.getUint32(base + 72, true), flags = data.getUint32(base + 76, true);
            if (flags & ~1 || (flags === 0 && instanceCount !== 0) ||
                (flags === 1 && (operation.type !== 'render' || !operation.engineInstanceCountLimit ||
                    instanceCount > operation.engineInstanceCountLimit)))
                throw new RangeError('WebGPU.EngineFrame.InstanceOverride: an instance count requires a bounded direct-draw opt-in.');
            let dynamicIndex = 0;
            if (operation.bindings) {
                for (const group of operation.bindings) {
                    for (const binding of group.dynamic) {
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
            drawCount += prepared.draws;
            if (drawCount > 4096)
                throw new RangeError('WebGPU.EngineFrame.DrawCapacity: too many draws.');
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
            if (data.getUint32(base + 20, true) !== 0 || beforeRecord >= count || beforeRecord < previousRecord ||
                !byteLength || (destinationOffset | payloadOffset | byteLength) % 4 ||
                payloadOffset !== expectedPayloadOffset || payloadOffset > storageLength - byteLength ||
                destinationOffset > destination.size - byteLength ||
                !(destination.usage & GPUBufferUsage.STORAGE) || !(destination.usage & GPUBufferUsage.COPY_DST))
                throw new RangeError('WebGPU.EngineFrame.StorageUpload: invalid owner, range, usage, order or alignment.');
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
        let producerScopesOpen = false;
        r._executing = true;
        try {
            if (guardCanvasCapture) {
                r.readback.beginCanvasProducerScopes();
                producerScopesOpen = true;
            }
            if (!this.staging) {
                if (uniformCapacity + storageCapacity > r.device.limits.maxBufferSize)
                    throw new RangeError('WebGPU.EngineFrame.StagingCapacity: device buffer limit is too small.');
                this.staging = r.device.createBuffer({ size: uniformCapacity + storageCapacity,
                    usage: GPUBufferUsage.COPY_SRC | GPUBufferUsage.COPY_DST, label: 'Engine frame staging' });
            }
            r._setOperation('acquire-canvas');
            const canvasTexture = r.context.getCurrentTexture();
            c.canvasColor.view = canvasTexture.createView();
            c.canvasColor.width = c.canvasDepth.width = r._width;
            c.canvasColor.height = c.canvasDepth.height = r._height;
            c.canvasColor.format = r.format;
            c.canvasDepth.view = r.depthView;
            r._setOperation('create-command-encoder');
            const encoder = r.device.createCommandEncoder();
            if (uniformLength) encoder.copyBufferToBuffer(this.staging, 0, arena.buffer, 0, uniformLength);
            let uploadIndex = 0;
            for (let record = 0; record < count; record++) {
                while (uploadIndex < uploadCount && this.uploads[uploadIndex].beforeRecord === record) {
                    const entry = this.uploads[uploadIndex++];
                    encoder.copyBufferToBuffer(this.staging, uniformCapacity + entry.payloadOffset,
                        entry.destination, entry.destinationOffset, entry.byteLength);
                }
                const base = headerBytes + record * recordBytes;
                const operation = this.operations[record];
                c.encodeOperation(encoder, operation, data, base + 8, record);
            }
            r._setOperation('finish-command-encoder');
            r._submission[0] = encoder.finish();
            if (uniformLength) r.device.queue.writeBuffer(this.staging, 0, this.uniformBytes, 0, uniformLength);
            if (storageLength) r.device.queue.writeBuffer(this.staging, uniformCapacity, this.storageBytes, 0, storageLength);
            r._setOperation('submit-engine-frame');
            r.device.queue.submit(r._submission);
            this.lastSequence = sequence;
            const producerGate = producerScopesOpen ? r.readback.endCanvasProducerScopes() : undefined;
            producerScopesOpen = false;
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
            if (producerScopesOpen) void r.readback.endCanvasProducerScopes();
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

    dispose() { this.staging?.destroy(); this.staging = null; }
}
