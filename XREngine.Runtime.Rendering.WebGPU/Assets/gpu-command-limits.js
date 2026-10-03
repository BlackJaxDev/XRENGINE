/** Checks explicit layout/workgroup metadata against the limits granted to this device. */
export function assertPipelineBindingLimits(layouts, limits) {
    let dynamicUniforms = 0, dynamicStorage = 0;
    const stages = [1, 2, 4];
    for (const stage of stages) {
        let uniforms = 0, storage = 0, textures = 0, samplers = 0;
        for (const layout of layouts) {
            for (const entry of layout.descriptor.entries) {
                if (!(entry.visibility & stage)) continue;
                if (entry.buffer?.type === 'uniform') uniforms++;
                else if (entry.buffer) storage++;
                else if (entry.texture) textures++;
                else if (entry.sampler) samplers++;
            }
        }
        if (uniforms > limits.maxUniformBuffersPerShaderStage || storage > limits.maxStorageBuffersPerShaderStage
            || textures > limits.maxSampledTexturesPerShaderStage || samplers > limits.maxSamplersPerShaderStage)
            throw new RangeError('Pipeline bindings exceed a selected-device per-stage resource limit.');
    }
    for (const layout of layouts) {
        for (const entry of layout.descriptor.entries) {
            if (!entry.buffer?.hasDynamicOffset) continue;
            if (entry.buffer.type === 'uniform') dynamicUniforms++;
            else dynamicStorage++;
        }
    }
    if (dynamicUniforms > limits.maxDynamicUniformBuffersPerPipelineLayout
        || dynamicStorage > limits.maxDynamicStorageBuffersPerPipelineLayout)
        throw new RangeError('Pipeline bindings exceed a selected-device dynamic buffer limit.');
}

export function computeWorkgroupMetadata(input, limits) {
    const size = input.workgroupSize;
    const maximum = [limits.maxComputeWorkgroupSizeX, limits.maxComputeWorkgroupSizeY, limits.maxComputeWorkgroupSizeZ];
    if (!Array.isArray(size) || size.length !== 3)
        throw new TypeError('Compute pipelines require explicit workgroupSize metadata with three dimensions.');
    for (let axis = 0; axis < 3; axis++) {
        if (!Number.isSafeInteger(size[axis]) || size[axis] < 1 || size[axis] > maximum[axis])
            throw new RangeError('Compute workgroup size exceeds the selected-device axis limit.');
    }
    if (size[0] * size[1] * size[2] > limits.maxComputeInvocationsPerWorkgroup)
        throw new RangeError('Compute workgroup size exceeds the selected-device invocation limit.');
    const storage = input.workgroupStorageSize ?? 0;
    if (!Number.isSafeInteger(storage) || storage < 0 || storage > limits.maxComputeWorkgroupStorageSize)
        throw new RangeError('Compute workgroup storage exceeds the selected-device limit.');
    // The producer must match these declarations to WGSL. Native pipeline creation
    // validates the actual shader; these fields are not shader reflection.
    return { size, storage };
}
