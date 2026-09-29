const capacity = 8;
const argumentStride = 48;
const width = 32;
const height = 16;

function computeSource(workgroupSize) {
    return `
struct Parameters { count: u32, padding0: u32, padding1: u32, padding2: u32 }
struct Bounds { center: vec4f, extent: vec4f }
struct Arguments {
    indexCount: u32, instanceCount: u32, firstIndex: u32, baseVertex: i32, firstInstance: u32,
    vertexCount: u32, drawInstanceCount: u32, firstVertex: u32, drawFirstInstance: u32,
    checksum: u32, padding0: u32, padding1: u32
}
@group(1) @binding(0) var<uniform> parameters: Parameters;
@group(0) @binding(0) var<storage, read> scene: array<Bounds, ${capacity}>;
@group(0) @binding(1) var<storage, read_write> arguments: array<Arguments, ${capacity}>;
@group(0) @binding(2) var<storage, read_write> instances: array<vec4f, ${capacity}>;

// Clip-space AABBs use WebGPU's zero-to-one depth range. Touching a plane
// remains visible; no occlusion or hierarchy assumptions enter this reference.
@compute @workgroup_size(${workgroupSize}, 1, 1)
fn cull(@builtin(global_invocation_id) invocation: vec3u) {
    let slot = invocation.x;
    if (slot >= ${capacity}u) { return; }
    let bounds = scene[slot];
    let low = bounds.center.xyz - bounds.extent.xyz;
    let high = bounds.center.xyz + bounds.extent.xyz;
    let active = slot < parameters.count;
    let visible = active && high.x >= -1.0 && low.x <= 1.0 &&
        high.y >= -1.0 && low.y <= 1.0 && high.z >= 0.0 && low.z <= 1.0;
    let count = select(0u, 1u, visible);
    arguments[slot] = Arguments(3u, count, 1u, -1, 0u, 3u, count, 0u, 0u,
        select(0u, slot * 17u + 23u, active), 0u, 0u);
    // Each fixed draw binds its own instance slice: firstInstance is always zero.
    instances[slot] = vec4f(bounds.center.xy, 0.22, 0.44);
}`;
}

const renderSource = `
@group(0) @binding(0) var<uniform> color: vec4f;
@vertex fn vertexMain(@builtin(vertex_index) index: u32, @location(0) instance: vec4f) -> @builtin(position) vec4f {
    let positions = array<vec2f, 3>(vec2f(-1.0, -1.0), vec2f(1.0, -1.0), vec2f(0.0, 1.0));
    return vec4f(instance.xy + positions[index] * instance.zw, 0.5, 1.0);
}
@fragment fn fragmentMain() -> @location(0) vec4f { return color; }
`;

function write(resources, handle, source) {
    const bytes = new Uint8Array(source.buffer, source.byteOffset, source.byteLength);
    resources.writeBuffer(handle, 0, { byteLength: bytes.byteLength, copyTo: destination => destination.set(bytes) });
}

async function read(renderer, ticket, size) {
    try {
        await renderer.readback.wait(ticket);
        const bytes = new Uint8Array(size);
        renderer.readback.copy(ticket, bytes);
        return bytes;
    } finally { renderer.readback.release(ticket); }
}

function requireLimits(limits) {
    const minimums = { maxBindGroups: 2, maxBindingsPerBindGroup: 3, maxStorageBuffersPerShaderStage: 3,
        maxUniformBuffersPerShaderStage: 1, maxUniformBufferBindingSize: 16,
        maxStorageBufferBindingSize: capacity * argumentStride, maxBufferSize: capacity * argumentStride,
        maxVertexBuffers: 1, maxVertexAttributes: 1, maxVertexBufferArrayStride: 16,
        maxTextureDimension2D: width, maxColorAttachments: 1, maxComputeWorkgroupSizeY: 1,
        maxComputeWorkgroupSizeZ: 1, maxComputeWorkgroupsPerDimension: 1 };
    for (const [name, value] of Object.entries(minimums))
        if (!(limits[name] >= value)) throw new Error(`GPU reference requires ${name} >= ${value}.`);
    const workgroupSize = [8, 4, 1].find(value => value <= limits.maxComputeWorkgroupSizeX &&
        value <= limits.maxComputeInvocationsPerWorkgroup &&
        Math.ceil(capacity / value) <= limits.maxComputeWorkgroupsPerDimension);
    if (!workgroupSize) throw new Error('No supported GPU reference workgroup variant fits the selected device.');
    return workgroupSize;
}

/** Explicit diagnostic operation, outside animation frames. This is neither a
 * production submission strategy nor mobile correctness/performance acceptance. */
export async function runGpuBaselineReference(renderer) {
    renderer._requireOwner();
    const device = renderer.device, owner = renderer._owner;
    const commands = renderer.commands, resources = renderer.resources;
    const workgroupSize = requireLimits(device.limits);
    const handles = [];
    const own = handle => { handles.push(handle); return handle; };
    const sameOwner = () => {
        renderer._requireOwner();
        if (renderer.device !== device || renderer._owner !== owner)
            throw new Error('GPU reference belongs to an obsolete renderer session.');
    };
    const createBuffer = (size, usage, name) => own(resources.createBuffer(size, usage, name));
    const prepare = list => commands.prepareCommands(JSON.stringify({ label: 'GPU baseline reference', commands: list }));
    const cases = [], rejections = [], preparationCases = [];
    try {
        const storageUsage = GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST;
        const params = createBuffer(16, GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST, 'Reference parameters');
        const scene = createBuffer(capacity * 32, storageUsage, 'Reference scene bounds');
        const argumentsBuffer = createBuffer(capacity * argumentStride, storageUsage | GPUBufferUsage.INDIRECT | GPUBufferUsage.COPY_SRC, 'Reference draw arguments');
        const instances = createBuffer(capacity * 16, storageUsage | GPUBufferUsage.VERTEX | GPUBufferUsage.COPY_SRC, 'Reference instances');
        const material = createBuffer(16, GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST, 'Reference material');
        const indices16 = createBuffer(12, GPUBufferUsage.INDEX | GPUBufferUsage.COPY_DST, 'Reference uint16 indices');
        const indices32 = createBuffer(20, GPUBufferUsage.INDEX | GPUBufferUsage.COPY_DST, 'Reference uint32 indices');
        const texture = own(resources.createTexture(width, height, 1, 1, 'rgba8unorm',
            GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.COPY_SRC, 'Reference color'));
        const view = own(resources.createTextureView(texture, 0, 1, 'all', 'Reference color view'));
        write(resources, material, new Float32Array([0, 1, 0, 1]));
        // A skipped allocation prefix and bound-range prefix catch lost byte
        // offsets or firstIndex. Signed baseVertex then maps 1,2,3 back to 0,1,2.
        write(resources, indices16, new Uint16Array([65535, 65535, 1, 2, 3, 0]));
        write(resources, indices32, new Uint32Array([0xffffffff, 0xffffffff, 1, 2, 3]));
        const computeLayout = own(commands.createBindingLayout(JSON.stringify({ label: 'Reference compute layout', entries: [
            { binding: 0, visibility: 4, buffer: { type: 'read-only-storage', minBindingSize: capacity * 32 } },
            { binding: 1, visibility: 4, buffer: { type: 'storage', minBindingSize: capacity * argumentStride } },
            { binding: 2, visibility: 4, buffer: { type: 'storage', minBindingSize: capacity * 16 } }
        ] })));
        const parameterLayout = own(commands.createBindingLayout(JSON.stringify({ label: 'Reference parameter layout', entries: [
            { binding: 0, visibility: 4, buffer: { type: 'uniform', minBindingSize: 16 } }
        ] })));
        const materialLayout = own(commands.createBindingLayout(JSON.stringify({ label: 'Reference material layout', entries: [
            { binding: 0, visibility: 2, buffer: { type: 'uniform', minBindingSize: 16 } }
        ] })));
        const computeEntries = [scene, argumentsBuffer, instances].map((resource, binding) => ({ binding, resource }));
        const computeGroup = own(commands.createBindingGroup(JSON.stringify({ layout: computeLayout, entries: computeEntries })));
        const parameterGroup = own(commands.createBindingGroup(JSON.stringify({ layout: parameterLayout, entries: [{ binding: 0, resource: params }] })));
        const materialGroup = own(commands.createBindingGroup(JSON.stringify({ layout: materialLayout, entries: [{ binding: 0, resource: material }] })));
        const computeShader = own(await commands.createShaderModule(computeSource(workgroupSize), 'Reference culling'));
        sameOwner();
        const renderShader = own(await commands.createShaderModule(renderSource, 'Reference raster'));
        sameOwner();
        const computePipeline = own(await commands.createComputePipeline(JSON.stringify({ label: 'Reference culling', layouts: [computeLayout, parameterLayout],
            compute: { shader: computeShader, entryPoint: 'cull', workgroupSize: [workgroupSize, 1, 1], workgroupStorageSize: 0 } })));
        sameOwner();
        const renderPipeline = own(await commands.createRenderPipeline(JSON.stringify({ label: 'Reference indirect raster', layouts: [materialLayout],
            vertex: { shader: renderShader, entryPoint: 'vertexMain', buffers: [{ arrayStride: 16, stepMode: 'instance',
                attributes: [{ format: 'float32x4', offset: 0, shaderLocation: 0 }] }] },
            fragment: { shader: renderShader, entryPoint: 'fragmentMain', targets: [{ format: 'rgba8unorm' }] },
            primitive: { topology: 'triangle-list', frontFace: 'ccw', cullMode: 'none' }, multisample: { count: 1 } })));
        sameOwner();
        const computeCommand = (group = computeGroup, count = Math.ceil(capacity / workgroupSize)) => ({
            type: 'compute', pipeline: computePipeline, bindings: [{ index: 0, group }, { index: 1, group: parameterGroup }], workgroups: [count, 1, 1]
        });
        const renderCommand = (slot, mode) => {
            const indexed = mode !== 'nonindexed';
            const command = { type: 'render', pipeline: renderPipeline,
                pass: { colors: [{ viewHandle: view, loadOp: slot ? 'load' : 'clear', storeOp: 'store', clearValue: [0, 0, 0, 1] }] },
                bindings: [{ index: 0, group: materialGroup }], vertexBuffers: [{ buffer: instances, offset: slot * 16, size: 16 }],
                draws: [{ type: indexed ? 'drawIndexedIndirect' : 'drawIndirect', buffer: argumentsBuffer,
                    offset: slot * argumentStride + (indexed ? 0 : 20), firstInstancePolicy: 'zero' }] };
            if (indexed) command.indexBuffer = { buffer: mode === 'uint16' ? indices16 : indices32,
                format: mode, offset: mode === 'uint16' ? 2 : 4, size: mode === 'uint16' ? 8 : 16 };
            return command;
        };
        const inputs = [
            { name: 'empty', count: 0 },
            { name: 'zero-dispatch', count: capacity, zeroDispatch: true },
            { name: 'zero-visible', count: capacity, outside: true },
            { name: 'mixed-visible', count: capacity, mixed: true },
            { name: 'maximum-capacity', count: capacity }
        ];
        for (const mode of ['uint16', 'uint32', 'nonindexed']) {
            for (const input of inputs) {
                sameOwner();
                const bounds = new Float32Array(capacity * 8);
                for (let slot = 0; slot < capacity; slot++) {
                    const outside = input.outside || input.mixed && slot % 2 !== 0;
                    bounds.set([outside ? 4 : -0.75 + (slot % 4) * 0.5, 0.5 - Math.floor(slot / 4), 0.5, 0,
                        0.22, 0.44, 0.1, 0], slot * 8);
                }
                write(resources, params, new Uint32Array([input.count, 0, 0, 0]));
                write(resources, scene, bounds);
                // Every byte is initialized before any indirect consumption, including
                // dispatch-zero cases where no invocation can overwrite old arguments.
                write(resources, argumentsBuffer, new Uint32Array(capacity * argumentStride / 4));
                write(resources, instances, new Float32Array(capacity * 4));
                const list = [computeCommand(computeGroup, input.zeroDispatch ? 0 : Math.ceil(capacity / workgroupSize))];
                for (let slot = 0; slot < capacity; slot++) list.push(renderCommand(slot, mode));
                const sequence = prepare(list);
                try {
                    commands.submitPreparedCommands(sequence);
                    const argumentBytes = await read(renderer, renderer.readback.beginBuffer(argumentsBuffer, 0, capacity * argumentStride), capacity * argumentStride);
                    sameOwner();
                    const instanceBytes = await read(renderer, renderer.readback.beginBuffer(instances, 0, capacity * 16), capacity * 16);
                    sameOwner();
                    const pixels = await read(renderer, renderer.readback.beginTexture(texture, 0, 0, 0, width, height), width * height * 4);
                    sameOwner();
                    const argumentsView = new DataView(argumentBytes.buffer);
                    const instanceView = new DataView(instanceBytes.buffer);
                    let visibleCount = 0;
                    for (let slot = 0; slot < capacity; slot++) {
                        const active = slot < input.count && !input.zeroDispatch;
                        const visible = active && !input.outside && !(input.mixed && slot % 2 !== 0);
                        const count = visible ? 1 : 0;
                        visibleCount += count;
                        const expected = input.zeroDispatch ? new Array(12).fill(0) :
                            [3, count, 1, 0xffffffff, 0, 3, count, 0, 0, active ? slot * 17 + 23 : 0, 0, 0];
                        for (let field = 0; field < expected.length; field++)
                            if (argumentsView.getUint32(slot * argumentStride + field * 4, true) !== expected[field])
                                throw new Error(`${mode}/${input.name}: incorrect argument slot ${slot}, field ${field}.`);
                        const expectedInstance = input.zeroDispatch ? [0, 0, 0, 0] : [bounds[slot * 8], bounds[slot * 8 + 1], Math.fround(0.22), Math.fround(0.44)];
                        for (let field = 0; field < 4; field++)
                            if (instanceView.getFloat32(slot * 16 + field * 4, true) !== expectedInstance[field])
                                throw new Error(`${mode}/${input.name}: incorrect instance slot ${slot}, field ${field}.`);
                        const pixel = ((4 + Math.floor(slot / 4) * 8) * width + 4 + slot % 4 * 8) * 4;
                        if (pixels[pixel] !== 0 || pixels[pixel + 1] !== (visible ? 255 : 0) || pixels[pixel + 2] !== 0 || pixels[pixel + 3] !== 255)
                            throw new Error(`${mode}/${input.name}: incorrect color at slot ${slot}.`);
                    }
                    if (pixels[0] !== 0 || pixels[1] !== 0 || pixels[2] !== 0 || pixels[3] !== 255)
                        throw new Error(`${mode}/${input.name}: clear color was not preserved.`);
                    cases.push({ name: input.name, mode, visibleCount, passed: true,
                        indexAddressing: mode === 'nonindexed' ? null : {
                            bindingByteOffset: mode === 'uint16' ? 2 : 4,
                            firstIndex: input.zeroDispatch ? 0 : 1, baseVertex: input.zeroDispatch ? 0 : -1
                        } });
                } finally {
                    // Teardown owns all handles once the session is replaced or lost.
                    if (renderer.device === device && renderer._owner === owner && !renderer._disposed && !renderer._failed)
                        renderer.destroyResource(sequence);
                }
            }
        }
        const expectRejection = (name, list) => {
            let sequence;
            try { sequence = prepare(list); }
            catch (error) { rejections.push({ name, passed: true, message: String(error.message ?? error) }); return; }
            renderer.destroyResource(sequence);
            throw new Error(`GPU reference expected ${name} to be rejected during command preparation.`);
        };
        const expectPreparation = (name, list) => {
            const sequence = prepare(list);
            renderer.destroyResource(sequence);
            preparationCases.push({ name, passed: true, submitted: false });
        };
        // These boundary descriptions are never submitted: a device's maximum
        // dispatch dimension is a bounds case, not a sensible diagnostic workload.
        expectPreparation('maximum-device-dispatch-dimension', [computeCommand(computeGroup, device.limits.maxComputeWorkgroupsPerDimension)]);
        let boundary = renderCommand(0, 'uint32');
        boundary.draws[0].offset = capacity * argumentStride - 20;
        expectPreparation('exact-end-indexed-argument-range', [boundary]);
        boundary = renderCommand(0, 'nonindexed');
        boundary.draws[0].offset = capacity * argumentStride - 16;
        expectPreparation('exact-end-nonindexed-argument-range', [boundary]);
        let invalid = renderCommand(0, 'uint32');
        invalid.draws[0].offset = 2;
        expectRejection('unaligned-indirect-offset', [invalid]);
        invalid = renderCommand(0, 'uint32');
        invalid.draws[0].offset = capacity * argumentStride - 16;
        expectRejection('indirect-argument-range', [invalid]);
        invalid = renderCommand(0, 'uint32');
        invalid.draws[0].buffer = scene;
        expectRejection('missing-indirect-usage', [invalid]);
        invalid = renderCommand(0, 'uint32');
        invalid.draws[0].type = 'multiDrawIndexedIndirect';
        expectRejection('unsupported-multi-draw', [invalid]);
        expectRejection('dispatch-device-limit', [computeCommand(computeGroup, device.limits.maxComputeWorkgroupsPerDimension + 1)]);
        if (!device.features.has('indirect-first-instance')) {
            invalid = renderCommand(0, 'uint32');
            invalid.draws[0].firstInstancePolicy = 'feature';
            expectRejection('unavailable-indirect-first-instance', [invalid]);
        }
        const aliasEntries = computeEntries.map(entry => ({ ...entry }));
        aliasEntries[0].resource = argumentsBuffer;
        const aliasGroup = own(commands.createBindingGroup(JSON.stringify({ layout: computeLayout, entries: aliasEntries })));
        expectRejection('compute-storage-alias', [computeCommand(aliasGroup)]);
        return { passed: true, status: 'passed', scope: 'Explicit diagnostic reference; mobile acceptance and production enablement remain separate.',
            referenceCapacity: capacity, workgroupSize: [workgroupSize, 1, 1], firstInstancePolicy: 'zero', materialGroups: 1,
            cases, preparationCases, rejections };
    } finally {
        // Reverse creation order releases command, pipeline, group and view references
        // before their dependencies. Native retirement uses the renderer queue lifetime.
        if (renderer.device === device && renderer._owner === owner && !renderer._disposed && !renderer._failed) {
            for (let index = handles.length - 1; index >= 0; index--) renderer.destroyResource(handles[index]);
        }
    }
}
