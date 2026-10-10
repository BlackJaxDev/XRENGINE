const maximumRequests = 4096;
const receiptBytes = 272;

/** Retained request identities are separate from the renderer's packed physical handle table. */
export class GpuEngineResourceCreation {
    constructor(commands) {
        this.commands = commands;
        this.requests = new Map();
        this.bytes = new Uint8Array(maximumRequests * receiptBytes);
        this.view = new DataView(this.bytes.buffer);
        this.encoder = new TextEncoder();
        this.receiptViews = new Map();
        this.disposed = false;
    }

    accept(json, receipts) {
        const r = this.commands.renderer;
        r._requireOwner();
        const length = receipts?.byteLength;
        if (!Number.isInteger(length) || length < 0 || length > this.bytes.length || length % receiptBytes ||
            typeof json !== 'string' || json.length > 16 * 1024 * 1024)
            throw new RangeError('WebGPU.Resource.RequestCapacity: invalid retained creation packet.');
        if (length === 0 && json === "[]") return;
        const updates = JSON.parse(json);
        if (!Array.isArray(updates) || updates.length > maximumRequests * 2)
            throw new RangeError('WebGPU.Resource.RequestCount: creation packet exceeds its bounded capacity.');
        receipts.copyTo(this.bytes);
        for (const update of updates) {
            if (!update || !Number.isSafeInteger(update.id) || update.id < 1 || update.id > 0x7fffffff ||
                !Number.isInteger(update.kind) || update.kind < -1 || update.kind > 7 ||
                update.kind > 0 && !Array.isArray(update.args))
                throw new RangeError('WebGPU.Resource.RequestDescriptor: invalid physical creation request.');
        }
        for (let at = 0; at < length; at += receiptBytes) {
            const id = this.view.getInt32(at, true);
            if (id < 1 || !this.requests.has(id) && !updates.some(update => update.id === id && update.kind >= 0))
                throw new Error('WebGPU.Resource.UnknownRequest: receipt identity has no retained descriptor.');
        }
        // Acknowledgments release transport identities before a full replacement batch
        // consumes capacity. They never retire handles already owned by managed wrappers.
        for (const update of updates) if (update.kind === -1) {
            const previous = this.requests.get(update.id);
            if (previous?.state === 1) throw new Error('WebGPU.Resource.PendingAcknowledgement: pending creation cannot be forgotten.');
            for (let at = 0; at < length; at += receiptBytes)
                if (this.view.getInt32(at, true) === update.id)
                    throw new Error('WebGPU.Resource.AcknowledgementOverlap: acknowledged identities cannot request another receipt.');
            this.requests.delete(update.id);
        }
        for (const update of updates) {
            if (update.kind === -1) continue;
            const previous = this.requests.get(update.id);
            if (update.kind === -1) {
                if (previous && previous.state === 1) throw new Error('WebGPU.Resource.PendingAcknowledgement: pending creation cannot be forgotten.');
                this.requests.delete(update.id);
            } else if (update.kind === 0) {
                if (previous) {
                    previous.cancelled = true;
                    this.retire(previous);
                    if (previous.state !== 1) previous.state = 4;
                } else this.requests.set(update.id, { state: 4, handle: 0, cancelled: true, error: '' });
            } else if (previous) {
                if (previous.kind !== update.kind || previous.json !== JSON.stringify(update.args))
                    throw new Error('WebGPU.Resource.RequestChanged: one identity cannot change its physical descriptor.');
            } else {
                if (this.requests.size >= maximumRequests) throw new RangeError('WebGPU.Resource.RetainedCapacity: too many unacknowledged requests.');
                const request = { state: 1, handle: 0, cancelled: false, error: '', kind: update.kind, json: JSON.stringify(update.args) };
                this.requests.set(update.id, request);
                this.create(request, update.args);
            }
        }
        this.writeReceipts(receipts, length);
    }

    poll(receipts) {
        this.commands.renderer._requireOwner();
        const length = receipts?.byteLength;
        if (this.disposed || !Number.isInteger(length) || length <= 0 ||
            length > this.bytes.length || length % receiptBytes)
            throw new RangeError('WebGPU.Resource.ReceiptCapacity: invalid retained receipt packet.');
        receipts.copyTo(this.bytes);
        for (let at = 0; at < length; at += receiptBytes) {
            const id = this.view.getInt32(at, true);
            const request = this.requests.get(id);
            if (id < 1 || !request || !Number.isInteger(request.state) || request.state < 1 || request.state > 4 ||
                request.state === 2 && (!Number.isSafeInteger(request.handle) || request.handle < 0x10000 ||
                    request.handle > 0x7fffffff || !(request.handle & 0xffff)) ||
                typeof request.error !== 'string' || request.state <= 2 && request.error !== '')
                throw new Error('WebGPU.Resource.InvalidPoll: receipt identity or retained state is invalid.');
        }
        this.writeReceipts(receipts, length);
    }

    writeReceipts(receipts, length) {
        for (let at = 0; at < length; at += receiptBytes) {
            const request = this.requests.get(this.view.getInt32(at, true));
            this.view.setInt32(at + 4, request.state, true);
            this.view.setInt32(at + 8, request.state === 2 ? request.handle : 0, true);
            this.bytes.fill(0, at + 16, at + receiptBytes);
            const errorLength = request.error
                ? this.encoder.encodeInto(request.error, this.bytes.subarray(at + 16, at + receiptBytes)).written : 0;
            this.view.setInt32(at + 12, errorLength, true);
        }
        let view = this.receiptViews.get(length);
        if (!view) {
            view = this.bytes.subarray(0, length);
            this.receiptViews.set(length, view);
        }
        receipts.set(view);
    }

    create(request, args) {
        const c = this.commands, r = c.renderer, device = r.device;
        let failure = null, scopeCount = 0;
        const completions = [];
        try {
            device.pushErrorScope('out-of-memory'); scopeCount++;
            device.pushErrorScope('validation'); scopeCount++;
            switch (request.kind) {
                case 1: request.handle = r.resources.createBuffer(...args); break;
                case 2: request.handle = r.resources.createTexture(...args); break;
                case 3: request.handle = r.resources.createTextureView(...args); break;
                case 4: request.handle = r.resources.createSampler(...args); break;
                case 5: request.handle = c.createBindingLayout(...args); break;
                case 6: request.handle = c.createBindingGroup(...args); break;
                case 7: request.handle = c.prepareCommands(...args); break;
            }
        } catch (error) { failure = error; }
        while (scopeCount > 0) {
            scopeCount--;
            try { completions.push(device.popErrorScope()); }
            catch (error) { failure ??= error; }
        }
        if (failure) {
            request.error = String(failure?.message ?? failure);
            this.retire(request);
            request.state = 3;
            Promise.all(completions).catch(() => {});
            return;
        }
        Promise.all(completions).then(errors => {
            const error = failure ?? errors.find(value => value !== null);
            if (error || request.cancelled || this.disposed) {
                request.error = String(error?.message ?? error ?? 'Physical descriptor owner retired.');
                this.retire(request);
                request.state = request.cancelled || this.disposed ? 4 : 3;
            } else request.state = 2;
        }, error => {
            request.error = String(error?.message ?? error);
            this.retire(request);
            request.state = request.cancelled || this.disposed ? 4 : 3;
        });
    }

    retire(request) {
        const handle = request.handle;
        request.handle = 0;
        const r = this.commands.renderer;
        const entry = handle && r._resources.slots[handle & 0xffff];
        if (handle && !this.disposed && !r._disposed && entry?.generation === Math.floor(handle / 0x10000) && entry.owner === r._owner)
            r.retireResource(handle);
    }

    dispose() {
        // The renderer's existing table teardown owns every remaining physical handle.
        this.disposed = true;
        for (const request of this.requests.values()) { request.cancelled = true; request.handle = 0; }
        this.requests.clear();
        this.receiptViews.clear();
    }
}
