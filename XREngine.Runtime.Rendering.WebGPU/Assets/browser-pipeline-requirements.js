const limits = Object.freeze({ maxBindGroups: 3, maxBindingsPerBindGroup: 3,
    maxVertexBuffers: 2, maxVertexAttributes: 10, maxVertexBufferArrayStride: 128,
    maxUniformBufferBindingSize: 160, maxBufferSize: 1048576 });

/** Combine the selected engine pipeline and cooked bootstrap requirements before device acquisition. */
export function browserPipelineRequirements(adapter, requirements) {
    const requiredLimits = { ...requirements.requiredLimits };
    for (const [name, minimum] of Object.entries(limits)) {
        const requested = Math.max(requiredLimits[name] ?? 0, minimum);
        if (!Number.isSafeInteger(adapter.limits[name]) || adapter.limits[name] < requested)
            throw new Error(`Browser render pipeline requires ${name} >= ${requested}; adapter exposes ${adapter.limits[name]}.`);
        requiredLimits[name] = requested;
    }
    return { requiredFeatures: [...requirements.requiredFeatures], requiredLimits };
}
