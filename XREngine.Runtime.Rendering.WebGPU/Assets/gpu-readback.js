const maximumTickets = 16;
const maximumReadbackBytes = 16 * 1024 * 1024;
const maximumResidentBytes = 32 * 1024 * 1024;
const maximumGeneration = 0x7fff;

function canceled() {
    return new DOMException('The GPU request was canceled or its renderer session ended.', 'AbortError');
}

function integer(value, name, minimum = 0) {
    if (!Number.isSafeInteger(value) || value < minimum)
        throw new RangeError(`${name} is outside the supported integer range.`);
}

/** Bounded request-owned staging and generation-stamped asynchronous completion tickets. */
export class GpuReadback {
    constructor(renderer) {
        this.renderer = renderer;
        this.slots = new Array(maximumTickets).fill(null);
        this.generations = new Uint16Array(maximumTickets).fill(1);
        this.residentBytes = 0;
        this.activeCount = 0;
        this.disposed = false;
    }

    beginBuffer(handle, offset, byteLength) {
        const renderer = this.renderer;
        renderer._requireOwner();
        integer(offset, 'Buffer offset');
        integer(byteLength, 'Readback length', 1);
        const source = renderer._resources.getHandle(handle, 'buffer', renderer._owner);
        if ((offset % 4) || (byteLength % 4) || byteLength > maximumReadbackBytes ||
            offset > source.size || byteLength > source.size - offset || !(source.usage & GPUBufferUsage.COPY_SRC))
            throw new RangeError('Buffer readback requires a bounded aligned range and copy-source usage.');
        const ticket = this._reserve(byteLength, byteLength, byteLength, 1);
        this._start(ticket, encoder => encoder.copyBufferToBuffer(source.buffer, offset, ticket.buffer, 0, byteLength));
        return ticket.handle;
    }

    beginTexture(handle, mipLevel, x, y, width, height) {
        const renderer = this.renderer;
        renderer._requireOwner();
        integer(mipLevel, 'Mip level');
        integer(x, 'Texture X');
        integer(y, 'Texture Y');
        integer(width, 'Texture width', 1);
        integer(height, 'Texture height', 1);
        const source = renderer._resources.getHandle(handle, 'texture', renderer._owner);
        if (!['rgba8unorm', 'rgba8unorm-srgb', 'bgra8unorm', 'bgra8unorm-srgb'].includes(source.format) ||
            source.sampleCount !== 1 || !(source.usage & GPUTextureUsage.COPY_SRC) || mipLevel >= source.mipLevelCount)
            throw new RangeError('Texture readback requires a single-sample RGBA8/BGRA8 color mip with copy-source usage.');
        const mipWidth = Math.max(1, Math.floor(source.width / 2 ** mipLevel));
        const mipHeight = Math.max(1, Math.floor(source.height / 2 ** mipLevel));
        const rowBytes = width * 4;
        const byteLength = rowBytes * height;
        const paddedRowBytes = Math.ceil(rowBytes / 256) * 256;
        const stagingBytes = paddedRowBytes * height;
        if (x > mipWidth || y > mipHeight || width > mipWidth - x || height > mipHeight - y ||
            !Number.isSafeInteger(byteLength) || byteLength > maximumReadbackBytes || !Number.isSafeInteger(stagingBytes))
            throw new RangeError('Texture readback rectangle exceeds the mip extent or readback byte budget.');
        const ticket = this._reserve(byteLength, stagingBytes, rowBytes, height);
        this._start(ticket, encoder => encoder.copyTextureToBuffer(
            { texture: source.texture, mipLevel, origin: [x, y, 0], aspect: 'all' },
            { buffer: ticket.buffer, bytesPerRow: paddedRowBytes, rowsPerImage: height },
            [width, height, 1]));
        return ticket.handle;
    }

    beginCompletion() {
        this.renderer._requireOwner();
        const ticket = this._reserve(0, 0, 0, 0);
        this._start(ticket, null);
        return ticket.handle;
    }

    _reserve(byteLength, stagingBytes, rowBytes, rows) {
        if (this.disposed) throw canceled();
        const cost = byteLength + stagingBytes;
        if (!Number.isSafeInteger(cost) || this.activeCount >= maximumTickets ||
            cost > maximumResidentBytes - this.residentBytes || stagingBytes > this.renderer.device.limits.maxBufferSize)
            throw new RangeError('Asynchronous GPU request capacity is exhausted; release completed tickets before retrying.');
        const slot = this.slots.findIndex((entry, index) => entry === null && this.generations[index] !== 0);
        if (slot < 0) throw new RangeError('GPU ticket generations are exhausted; restart the renderer.');
        let resolve, reject;
        const promise = new Promise((done, fail) => { resolve = done; reject = fail; });
        // Cancellation may precede installation of the managed await continuation.
        promise.catch(() => {});
        const ticket = { handle: this.generations[slot] * 0x10000 + slot + 1, slot,
            device: this.renderer.device, owner: this.renderer._owner, byteLength, stagingBytes, rowBytes, rows,
            cost, buffer: null, bytes: null, state: 'pending', released: false, finished: false,
            promise, resolve, reject };
        this.slots[slot] = ticket;
        this.residentBytes += cost;
        this.activeCount++;
        return ticket;
    }

    _start(ticket, encode) {
        const device = ticket.device;
        // Scopes are popped before returning across the import boundary, so another
        // request cannot accidentally capture this request's validation errors.
        let pending;
        let completion;
        let validation;
        let memory;
        let scopeCount = 0;
        try {
            device.pushErrorScope('out-of-memory');
            scopeCount++;
            device.pushErrorScope('validation');
            scopeCount++;
            if (encode) {
                ticket.buffer = device.createBuffer({ label: 'Browser readback staging', size: ticket.stagingBytes,
                    usage: GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ });
                const encoder = device.createCommandEncoder({ label: 'Browser readback copy' });
                encode(encoder);
                device.queue.submit([encoder.finish()]);
                // A canceled map may reject before the submitted copy finishes.
                // Keep the staging reservation until both have settled.
                completion = device.queue.onSubmittedWorkDone();
                pending = ticket.buffer.mapAsync(GPUMapMode.READ, 0, ticket.stagingBytes);
            } else {
                pending = device.queue.onSubmittedWorkDone();
            }
        } catch (error) {
            pending = Promise.reject(error);
        } finally {
            try {
                if (scopeCount === 2) { scopeCount--; validation = device.popErrorScope(); }
            } catch (error) { validation = Promise.reject(error); }
            try {
                if (scopeCount === 1) memory = device.popErrorScope();
            } catch (error) { memory = Promise.reject(error); }
        }
        // This observes map/queue and scope rejections even after cancellation/disposal.
        void this._finish(ticket, Promise.allSettled([pending, validation, memory, completion]));
    }

    async _finish(ticket, operations) {
        try {
            const results = await operations;
            if (ticket.released || this.disposed || this.renderer.device !== ticket.device || this.renderer._owner !== ticket.owner)
                throw canceled();
            for (let index = 0; index < results.length; index++) {
                const result = results[index];
                if (result.status === 'rejected') throw result.reason;
                if (index > 0 && result.value) throw new Error(`WebGPU readback: ${result.value.message}`);
            }
            if (ticket.buffer) {
                const mapped = new Uint8Array(ticket.buffer.getMappedRange(0, ticket.stagingBytes));
                const bytes = new Uint8Array(ticket.byteLength);
                const stride = ticket.rows > 1 ? ticket.stagingBytes / ticket.rows : ticket.stagingBytes;
                for (let row = 0; row < ticket.rows; row++)
                    bytes.set(mapped.subarray(row * stride, row * stride + ticket.rowBytes), row * ticket.rowBytes);
                ticket.bytes = bytes;
            }
            ticket.state = 'ready';
            ticket.resolve();
        } catch (error) {
            ticket.state = 'failed';
            ticket.reject(error);
        } finally {
            if (ticket.buffer) {
                ticket.buffer.unmap();
                ticket.buffer.destroy();
                ticket.buffer = null;
            }
            ticket.finished = true;
            if (ticket.released) this._free(ticket);
        }
    }

    _get(handle) {
        integer(handle, 'GPU ticket', 1);
        const slot = (handle & 0xffff) - 1;
        const ticket = this.slots[slot];
        if (!ticket || ticket.handle !== handle || ticket.released)
            throw new Error('Invalid or obsolete GPU request ticket.');
        return ticket;
    }

    wait(handle) {
        return this._get(handle).promise;
    }

    copy(handle, destination) {
        const ticket = this._get(handle);
        if (ticket.state !== 'ready' || !ticket.bytes || destination?.byteLength !== ticket.byteLength)
            throw new Error('GPU readback is not ready or its destination length does not match.');
        // MemoryView is borrowed only within this synchronous call.
        destination.set(ticket.bytes);
    }

    release(handle) {
        if (this.disposed) return;
        const ticket = this._get(handle);
        ticket.released = true;
        ticket.bytes = null;
        if (!ticket.finished) {
            ticket.reject(canceled());
            ticket.buffer?.unmap();
            // Keep the reservation until the GPU promises settle. Repeated canceled
            // requests cannot bypass the ticket or staging memory limit.
        } else this._free(ticket);
    }

    _free(ticket) {
        if (this.slots[ticket.slot] !== ticket) return;
        this.slots[ticket.slot] = null;
        const generation = this.generations[ticket.slot];
        this.generations[ticket.slot] = generation < maximumGeneration ? generation + 1 : 0;
        this.residentBytes -= ticket.cost;
        this.activeCount--;
    }

    dispose() {
        if (this.disposed) return;
        this.disposed = true;
        for (const ticket of this.slots) {
            if (!ticket) continue;
            ticket.released = true;
            ticket.bytes = null;
            ticket.reject(canceled());
            if (ticket.buffer) {
                ticket.buffer.unmap();
                ticket.buffer.destroy();
                ticket.buffer = null;
            }
            this._free(ticket);
        }
    }
}
