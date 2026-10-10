const maximumSlot = 0xffff;
const maximumGeneration = 0x7fff;

/** Generation-stamped handles share one namespace across all GPU resource kinds. */
export class GpuResourceTable {
    constructor() {
        this.slots = [null];
        this.generations = [0];
        this.free = [];
    }

    add(kind, value, owner) {
        const slot = this.free.length ? this.free.pop() : this.slots.length;
        if (slot > maximumSlot) throw new Error('WebGPU resource slots are exhausted.');
        const generation = this.generations[slot] ?? 1;
        this.generations[slot] = generation;
        this.slots[slot] = { kind, value, owner, generation };
        return generation * 0x10000 + slot;
    }

    get(slot, generation, kind, owner) {
        const entry = this.slots[slot];
        if (!entry || entry.generation !== generation || entry.kind !== kind || entry.owner !== owner)
            throw new Error(`Invalid or obsolete ${kind} resource handle.`);
        // Existing dependency edges retain their objects; retirement closes new handle access.
        if (entry.value.retired)
            throw new Error(`A retired ${kind} resource cannot be submitted, rebound or modified.`);
        return entry.value;
    }

    getHandle(handle, kind, owner) {
        if (!Number.isInteger(handle) || handle <= 0 || handle > 0x7fffffff)
            throw new RangeError('Resource handle must be a positive packed integer.');
        return this.get(handle & 0xffff, Math.floor(handle / 0x10000), kind, owner);
    }

    remove(handle, owner) {
        if (!Number.isInteger(handle) || handle <= 0 || handle > 0x7fffffff)
            throw new RangeError('Resource handle must be a positive packed integer.');
        const slot = handle & 0xffff;
        const generation = Math.floor(handle / 0x10000);
        const entry = this.slots[slot];
        if (!entry || entry.generation !== generation || entry.owner !== owner)
            throw new Error('Invalid or obsolete resource handle.');
        this.slots[slot] = null;
        if (generation < maximumGeneration) {
            this.generations[slot] = generation + 1;
            this.free.push(slot);
        }
        return entry;
    }

    clear(destroy) {
        for (let slot = 1; slot < this.slots.length; slot++) {
            const entry = this.slots[slot];
            if (entry) destroy(entry);
            this.slots[slot] = null;
        }
        this.free.length = 0;
    }
}
