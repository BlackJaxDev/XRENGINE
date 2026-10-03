// Core WebGPU exact encodings, including attachment pixel cost/alignment (not just copy footprint).
const formats = new Map();
function color(name, bytes, alignment, sampleType, storage = false, multisample = true, resolve = true, cost = bytes) {
    formats.set(name, Object.freeze({ bytes, alignment, sampleType, storage, multisample, resolve, cost, color: true }));
}
color('r8unorm', 1, 1, 'float');
color('rgba8unorm', 4, 1, 'float', true, true, true, 8);
color('rgba8unorm-srgb', 4, 1, 'float', false, true, true, 8);
color('bgra8unorm', 4, 1, 'float', false, true, true, 8);
color('bgra8unorm-srgb', 4, 1, 'float', false, true, true, 8);
color('r16float', 2, 2, 'float');
color('rg16float', 4, 2, 'float');
color('rgba16float', 8, 2, 'float', true);
for (const [prefix, bytes] of [['r', 4], ['rg', 8], ['rgba', 16]]) {
    color(`${prefix}32uint`, bytes, 4, 'uint', true, false, false);
    color(`${prefix}32sint`, bytes, 4, 'sint', true, false, false);
    color(`${prefix}32float`, bytes, 4, 'unfilterable-float', true, prefix === 'r', false);
}
for (const [name, bytes] of [['depth16unorm', 2], ['depth24plus', 4], ['depth24plus-stencil8', 8],
    ['depth32float', 4], ['depth32float-stencil8', 8]])
    formats.set(name, Object.freeze({ bytes, sampleType: 'depth', color: false, multisample: true, resolve: false }));

export function textureFormatInfo(format, device) {
    const info = formats.get(format);
    if (!info) throw new RangeError(`Texture format '${format}' has no exact admitted WebGPU encoding.`);
    if (format === 'depth32float-stencil8' && !device?.features?.has('depth32float-stencil8'))
        throw new Error('Texture format depth32float-stencil8 requires the selected-device depth32float-stencil8 feature.');
    return info;
}

export function assertStorageTextureFormat(format, access, device) {
    const info = textureFormatInfo(format, device);
    if (!info.storage || !['read-only', 'write-only', 'read-write'].includes(access) ||
        access === 'read-write' && !['r32float', 'r32uint', 'r32sint'].includes(format))
        throw new Error(`Storage texture format '${format}' does not support '${access}' in the admitted WebGPU profile.`);
}

export function assertSampledTexture(view, expected, device) {
    const info = textureFormatInfo(view.format, device);
    const actual = view.aspect === 'stencil-only' ? 'uint' : info.sampleType;
    const compatible = expected.sampleType === actual || expected.sampleType === 'unfilterable-float' && actual === 'float' ||
        expected.sampleType === 'float' && actual === 'unfilterable-float' && device.features.has('float32-filterable');
    if (!compatible) throw new Error('Texture format does not match the exact binding sample type.');
    if (info.sampleType === 'depth' && view.format.includes('stencil') && view.aspect === 'all')
        throw new Error('Depth/stencil sampling requires an explicit single-aspect view.');
    if ((expected.multisampled ?? false) !== (view.sampleCount > 1) || expected.viewDimension !== view.dimension)
        throw new Error('Texture dimension or sample count does not match the binding layout.');
}

export function assertColorTargets(targets, device, sampleCount = 1) {
    let cost = 0;
    for (const target of targets) {
        if (!target) continue;
        const info = textureFormatInfo(target.format, device);
        if (!info.color || sampleCount > 1 && !info.multisample)
            throw new Error(`Format '${target.format}' cannot be a color target at sample count ${sampleCount}.`);
        if (target.blend && (info.sampleType === 'uint' || info.sampleType === 'sint' ||
            info.sampleType === 'unfilterable-float' && !device.features.has('float32-blendable')))
            throw new Error(`Format '${target.format}' does not support blending on this device.`);
        cost = Math.ceil(cost / info.alignment) * info.alignment + info.cost;
    }
    if (cost > device.limits.maxColorAttachmentBytesPerSample)
        throw new RangeError('Color targets exceed maxColorAttachmentBytesPerSample including component alignment.');
}

export function assertColorClear(format, components) {
    const info = formats.get(format);
    if (info?.sampleType !== 'uint' && info?.sampleType !== 'sint') return;
    const minimum = info.sampleType === 'uint' ? 0 : -0x80000000;
    const maximum = info.sampleType === 'uint' ? 0xffffffff : 0x7fffffff;
    if (components.some(value => !Number.isInteger(value) || value < minimum || value > maximum))
        throw new RangeError('Integer attachment clears require exactly representable components in the format numeric range.');
}
