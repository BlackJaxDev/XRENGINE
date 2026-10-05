/** Records existing dependency edges only on an explicitly instrumented diagnostic renderer. */
export function retainEngineResourceOwnership(renderer, owner) {
    renderer._requireOwner();
    if (renderer._owner !== owner) throw new Error('The resource diagnostic must own its renderer.');
    const commands = renderer.commands, publish = commands.publish;
    const descriptor = Object.getOwnPropertyDescriptor(commands, 'publish');
    const empty = [];
    let dependencies = new WeakMap(), disposed = false, captureError = null;
    const wrappedPublish = function(kind, value, retained = empty) {
        const handle = publish.call(commands, kind, value, retained);
        // This is the executor's existing dependency array. Its normal release
        // clears it; the weak key adds no resource owner or independent lease.
        try { dependencies.set(value, retained); }
        catch (error) { captureError = error; }
        return handle;
    };
    commands.publish = wrappedPublish;
    return {
        capture(nativeId) {
            renderer._requireOwner();
            if (disposed || renderer._owner !== owner || renderer.commands !== commands || commands.publish !== wrappedPublish)
                throw new Error('Current diagnostic resource ownership is required.');
            if (captureError) throw captureError;
            const entries = [], handles = new Map(), nativeBuffers = new Map();
            for (let slot = 1; slot < renderer._resources.slots.length; slot++) {
                const entry = renderer._resources.slots[slot];
                if (!entry || entry.owner !== owner) continue;
                if (entries.length === 4096) throw new Error('The diagnostic resource inventory exceeds 4096 handles.');
                const handle = entry.generation * 0x10000 + slot;
                entries.push({ handle, entry });
                handles.set(entry.value, handle);
                if (entry.kind === 'buffer') nativeBuffers.set(entry.value.buffer, handle);
            }
            const requireHandle = value => {
                const handle = handles.get(value);
                if (!handle) throw new Error('A retained resource dependency has no current owner handle.');
                return handle;
            };
            const resources = entries.map(({ handle, entry }) => {
                const value = entry.value;
                let retained;
                if (entry.kind === 'texture-view') retained = [value.texture];
                else if (['buffer', 'texture', 'sampler'].includes(entry.kind)) retained = empty;
                else {
                    retained = dependencies.get(value);
                    if (!retained) throw new Error('A published diagnostic resource has no captured dependency owner.');
                }
                const operations = (value.operations ?? []).map(operation => {
                    const pipeline = operation.pipeline ? retained.filter(item => item.native === operation.pipeline) : [];
                    if (operation.pipeline && pipeline.length !== 1)
                        throw new Error('A retained operation must identify its exact logical pipeline owner.');
                    return { type: operation.type, pipeline: pipeline.length ? requireHandle(pipeline[0]) : 0,
                        indirectBuffers: (operation.draws ?? []).filter(draw =>
                            draw.type === 'drawIndexedIndirect' || draw.type === 'drawIndirect').map(draw => {
                            const buffer = nativeBuffers.get(draw.native);
                            if (!buffer) throw new Error('An indirect draw has no retained argument buffer owner.');
                            return buffer;
                        }),
                        attachments: (operation.plan?.bindings ?? []).map(binding => ({ key: binding.key,
                            view: binding.handle, texture: binding.handle > 0 ? requireHandle(binding.source.texture) : 0,
                            width: binding.source.width, height: binding.source.height })),
                    };
                });
                return { handle, kind: entry.kind, label: value.label ?? '', retired: value.retired === true,
                    dependencies: retained.map(requireHandle),
                    nativeId: ['shader', 'render-pipeline', 'compute-pipeline'].includes(entry.kind) ? nativeId(value.native) : null,
                    size: entry.kind === 'buffer' ? value.size : null,
                    usage: entry.kind === 'buffer' ? value.usage : null,
                    operations };
            });
            return { owner, generation: renderer._generation, resources };
        },
        dispose() {
            disposed = true;
            dependencies = new WeakMap();
            captureError = null;
            if (commands.publish !== wrappedPublish) return;
            if (descriptor) Object.defineProperty(commands, 'publish', descriptor);
            else delete commands.publish;
        },
    };
}
