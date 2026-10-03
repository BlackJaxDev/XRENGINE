/** Conservative preparation-time alias checks; each encoded pass owns a separate scope. */
export class GpuCommandUsageScope {
    constructor() {
        this.buffers = new Map();
        this.textures = [];
    }

    buffer(resource, writable, role) {
        const previous = this.buffers.get(resource);
        // Treat the complete buffer as one resource, including disjoint binding ranges.
        if (previous && (previous.writable || writable))
            throw new Error(`Buffer aliases incompatible ${previous.role} and ${role} usages in one pass.`);
        if (!previous) this.buffers.set(resource, { writable, role });
    }

    texture(view, writable, role) {
        if (!view.texture) return; // Canvas textures cannot be supplied in binding groups.
        const start = view.baseMip ?? 0, end = start + (view.mipCount ?? 1);
        for (const previous of this.textures) {
            if (previous.texture === view.texture && start < previous.end && previous.start < end
                && (previous.writable || writable))
                throw new Error(`Texture aliases incompatible ${previous.role} and ${role} usages in one pass.`);
        }
        this.textures.push({ texture: view.texture, start, end, writable, role });
    }

    bindings(bindings) {
        for (const binding of bindings) {
            for (const resource of binding.resources) {
                if (resource.kind === 'buffer') this.buffer(resource.value, resource.writable, resource.role);
                else this.texture(resource.value, false, 'sampled texture');
            }
        }
    }
}
