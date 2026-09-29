const limits = Object.freeze({ maxBindGroups: 3, maxBindingsPerBindGroup: 3,
    maxVertexBuffers: 2, maxVertexAttributes: 10, maxVertexBufferArrayStride: 128,
    maxUniformBufferBindingSize: 160, maxBufferSize: 1048576 });

/** Combine the selected engine pipeline and cooked bootstrap requirements before device acquisition. */
export function browserPipelineRequirements(adapter, requirements, strategy, skinningMode) {
    const requiredLimits = { ...requirements.requiredLimits };
    const selected = { ...limits };
    if (strategy?.gpu || skinningMode === 'Compute') {
        Object.assign(selected, { maxBindingsPerBindGroup: skinningMode === 'Compute' ? 6 : 4,
            maxStorageBuffersPerShaderStage: skinningMode === 'Compute' ? 5 : 2,
            maxStorageBufferBindingSize: 1048576, maxComputeWorkgroupSizeX: 64,
            maxComputeWorkgroupSizeY: strategy?.hiZ ? 8 : 1, maxComputeWorkgroupSizeZ: 1,
            maxComputeInvocationsPerWorkgroup: 64, maxComputeWorkgroupsPerDimension: 256 });
        if (strategy?.hiZ) selected.maxStorageTexturesPerShaderStage = 1;
        if (skinningMode === 'Compute') {
            selected.maxBufferSize = 4 * 1024 * 1024;
            selected.maxStorageBufferBindingSize = 4 * 1024 * 1024;
        }
    }
    for (const [name, minimum] of Object.entries(selected)) {
        const requested = Math.max(requiredLimits[name] ?? 0, minimum);
        if (!Number.isSafeInteger(adapter.limits[name]) || adapter.limits[name] < requested)
            throw new Error(`Browser render pipeline requires ${name} >= ${requested}; adapter exposes ${adapter.limits[name]}.`);
        requiredLimits[name] = requested;
    }
    return { requiredFeatures: [...requirements.requiredFeatures], requiredLimits };
}
