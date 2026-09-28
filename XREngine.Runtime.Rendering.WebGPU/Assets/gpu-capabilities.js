// Device limits are captured once after startup checks, never inferred from a backend ID.
const limitNames = [
    'maxTextureDimension1D', 'maxTextureDimension2D', 'maxTextureDimension3D', 'maxTextureArrayLayers',
    'maxBindGroups', 'maxBindGroupsPlusVertexBuffers', 'maxBindingsPerBindGroup',
    'maxDynamicUniformBuffersPerPipelineLayout', 'maxDynamicStorageBuffersPerPipelineLayout',
    'maxSampledTexturesPerShaderStage', 'maxSamplersPerShaderStage', 'maxStorageBuffersPerShaderStage',
    'maxStorageTexturesPerShaderStage', 'maxUniformBuffersPerShaderStage', 'maxUniformBufferBindingSize',
    'maxStorageBufferBindingSize', 'minUniformBufferOffsetAlignment', 'minStorageBufferOffsetAlignment',
    'maxVertexBuffers', 'maxBufferSize', 'maxVertexAttributes', 'maxVertexBufferArrayStride',
    'maxInterStageShaderVariables', 'maxColorAttachments', 'maxColorAttachmentBytesPerSample',
    'maxComputeWorkgroupStorageSize', 'maxComputeInvocationsPerWorkgroup', 'maxComputeWorkgroupSizeX',
    'maxComputeWorkgroupSizeY', 'maxComputeWorkgroupSizeZ', 'maxComputeWorkgroupsPerDimension',
];

export function captureDeviceCapabilities(device, requirements) {
    for (const feature of requirements.requiredFeatures) {
        if (!device.features.has(feature)) throw new Error(`Selected device lacks required feature ${feature}.`);
    }
    for (const [name, required] of Object.entries(requirements.requiredLimits)) {
        const actual = device.limits[name];
        const alignment = name.startsWith('min');
        if (!Number.isSafeInteger(actual) || (alignment ? actual > required : actual < required))
            throw new Error(`Selected device limit ${name} does not satisfy ${required}.`);
    }
    const limits = {};
    for (const name of limitNames) {
        const value = device.limits[name];
        if (Number.isSafeInteger(value) && value >= 0) limits[name] = value;
    }
    return Object.freeze({ profile: 'BrowserWebGPUCore',
        features: Object.freeze([...device.features].sort()), limits: Object.freeze(limits) });
}
