const headerBytes = 48;
const recordBytes = 80;
const maximumRecords = 4097;
const maximumDynamicOffsets = 16;

/** Imports one bounded engine frame, including uniform snapshots, without retaining managed views. */
export class GpuEngineFrame {
    constructor(commands) {
        this.commands = commands;
        this.bytes = new Uint8Array(headerBytes + maximumRecords * recordBytes);
        this.view = new DataView(this.bytes.buffer);
        this.operations = new Array(maximumRecords);
        this.lastSequence = 0;
    }

    submit(memory, uniforms) {
        const c = this.commands, r = c.renderer;
        r._requireOwner();
        const length = memory?.byteLength;
        if (!Number.isInteger(length) || length < headerBytes || length > this.bytes.length)
            throw new RangeError('WebGPU.EngineFrame.InvalidLength: command arena is outside its bounded range.');
        memory.copyTo(this.bytes);
        const data = this.view;
        const count = data.getUint32(12, true), sequence = data.getUint32(40, true);
        if (data.getUint32(0, true) !== 0x45475258 || data.getUint32(4, true) !== 1 ||
            data.getUint32(8, true) !== length || count < 1 || count > maximumRecords ||
            length !== headerBytes + count * recordBytes ||
            data.getUint32(16, true) !== r._owner || data.getUint32(20, true) !== r._generation ||
            data.getUint32(24, true) !== r._width || data.getUint32(28, true) !== r._height ||
            !r._configured || sequence <= this.lastSequence)
            throw new Error('WebGPU.EngineFrame.Obsolete: owner, surface, sequence or schema does not match the renderer.');

        const uniformHandle = data.getInt32(32, true), uniformLength = data.getUint32(36, true);
        if (uniformLength !== uniforms?.byteLength || uniformLength % 4)
            throw new RangeError('WebGPU.EngineFrame.UniformLength: invalid borrowed uniform arena.');
        const arena = uniformHandle ? c.get(uniformHandle, 'buffer') : undefined;
        if (uniformLength && (!arena || !(arena.usage & 64) || !(arena.usage & 8) || uniformLength > arena.size))
            throw new Error('WebGPU.EngineFrame.UniformBuffer: uploads require an owned UNIFORM/COPY_DST buffer.');

        let presentsCanvas = false, drawCount = 0;
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
            if (operation.type !== 'render' && operation.type !== 'clear')
                throw new Error('WebGPU.EngineFrame.CommandType: only retained raster and attachment operations are admitted.');
            const offsetCount = data.getUint32(base + 4, true);
            if (offsetCount > maximumDynamicOffsets)
                throw new RangeError('WebGPU.EngineFrame.DynamicOffsets: too many dynamic bindings.');
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

        this.lastSequence = sequence;
        if (uniformLength) r.resources.writeBuffer(uniformHandle, 0, uniforms);
        r._executing = true;
        try {
            c.canvasColor.view = r.context.getCurrentTexture().createView();
            c.canvasColor.width = c.canvasDepth.width = r._width;
            c.canvasColor.height = c.canvasDepth.height = r._height;
            c.canvasColor.format = r.format;
            c.canvasDepth.view = r.depthView;
            const encoder = r.device.createCommandEncoder();
            for (let record = 0; record < count; record++) {
                const base = headerBytes + record * recordBytes;
                const operation = this.operations[record];
                c.encodeOperation(encoder, operation, data, base + 8);
            }
            r._submission[0] = encoder.finish();
            r.device.queue.submit(r._submission);
            r._stats.draws += drawCount;
            r._stats.frameSubmitCalls++;
            r._stats.copiedBytes += length;
            return presentsCanvas;
        } catch (error) { r._fail(error); throw error; }
        finally {
            for (let record = 0; record < count; record++) {
                this.operations[record]?.plan?.release();
                this.operations[record] = undefined;
            }
            c.canvasColor.view = c.canvasDepth.view = undefined;
            r._submission[0] = null;
            r._executing = false;
        }
    }
}
