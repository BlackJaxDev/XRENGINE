import { textureFormatInfo, assertSampledTexture, assertStorageTextureFormat, assertColorTargets } from './gpu-texture-formats.js';
import { GpuEngineFrame } from './gpu-engine-frame.js';
import { GpuPassPlan } from './gpu-pass-plan.js';
import { GpuCommandUsageScope } from './gpu-command-usage-scope.js';
import { assertPipelineBindingLimits, computeWorkgroupMetadata } from './gpu-command-limits.js';

const maxDescription = 262144;
const maxCommands = 4096;
const maxIndirectDraws = 65536;
const maxReplayDraws = 262144;
function integer(value, minimum, maximum, name) {
    if (!Number.isSafeInteger(value) || value < minimum || value > maximum) throw new RangeError(`Invalid ${name}.`);
    return value;
}
function oneOf(value, choices, name) {
    if (!choices.includes(value)) throw new TypeError(`Unsupported ${name}: ${value}.`);
    return value;
}
function object(value, keys) {
    if (!value || typeof value !== 'object' || Array.isArray(value)) throw new TypeError('A GPU description object is required.');
    for (const key of Object.keys(value)) if (!keys.includes(key)) throw new TypeError(`Unknown GPU description field: ${key}.`);
    return value;
}
function array(value, maximum, name) {
    if (!Array.isArray(value) || value.length > maximum) throw new RangeError(`Invalid ${name} list.`);
    return value;
}
function parse(json, keys) {
    if (typeof json !== 'string' || !json.length || json.length > maxDescription) throw new RangeError('GPU description exceeds its bounded size.');
    return object(JSON.parse(json), keys);
}
function drawRectangle(value, width, height, allowEmpty, name) {
    object(value, ['x', 'y', 'width', 'height']);
    const x = integer(value.x, 0, width, `${name} x`);
    const y = integer(value.y, 0, height, `${name} y`);
    const rectWidth = integer(value.width, allowEmpty ? 0 : 1, width - x, `${name} width`);
    const rectHeight = integer(value.height, allowEmpty ? 0 : 1, height - y, `${name} height`);
    return Object.freeze({ x, y, width: rectWidth, height: rectHeight });
}
function label(value = '') {
    if (typeof value !== 'string' || value.length > 128) throw new RangeError('GPU debug names are limited to 128 characters.');
    return value;
}
function entryPoint(value) {
    if (typeof value !== 'string' || !/^[A-Za-z_][A-Za-z_0-9]{0,127}$/.test(value)) throw new TypeError('Invalid shader entry point.');
    return value;
}
function hold(dependencies, value) {
    if (value.retired) throw new Error('A retired GPU resource cannot acquire new command dependencies.');
    if (!dependencies.includes(value)) { dependencies.push(value); value.references = (value.references ?? 0) + 1; }
    return value;
}
function release(dependencies) {
    for (const value of dependencies) {
        value.references--;
        if (!value.references) value.tryRetire?.();
    }
    dependencies.length = 0;
}

/** Cold, bounded preparation with allocation-free command traversal during replay. */
export class GpuCommands {
    constructor(renderer) {
        this.renderer = renderer;
        this.pending = new Set();
        this.preparations = new Set();
        this.preparationFailures = [];
        this.engineFrame = new GpuEngineFrame(this);
        this.canvasColor = { view: undefined, width: 0, height: 0, format: '', sampleCount: 1, usage: 16 };
        this.canvasDepth = { view: undefined, width: 0, height: 0, format: 'depth24plus', sampleCount: 1, usage: 16 };
    }

    get(handle, kind) {
        const value = this.renderer._resources.getHandle(handle, kind, this.renderer._owner);
        if (value.retired) throw new Error('A retired GPU resource cannot be submitted or rebound.');
        return value;
    }

    publish(kind, value, dependencies = []) {
        if (dependencies.some(dependency => dependency.retired)) {
            release(dependencies);
            throw new Error('GPU preparation completed after a dependency was retired.');
        }
        value.state = 'ready';
        value.references = 0;
        value.release = () => {
            release(dependencies);
            value.state = 'released';
            value.native = undefined;
            value.operations = undefined;
        };
        try { return this.renderer._resources.add(kind, value, this.renderer._owner); }
        catch (error) { value.release(); throw error; }
    }

    preparationSnapshot(preparation, now = performance.now()) {
        return { stage: preparation.stage, label: preparation.label, owner: preparation.owner,
            outcome: preparation.outcome, elapsedMs: preparation.elapsedMs ?? now - preparation.startedAt,
            error: preparation.error,
            native: { ...preparation.native }, validationScope: { ...preparation.validationScope },
            memoryScope: { ...preparation.memoryScope } };
    }

    /** Detached cold snapshots; failed operations retain their state at failure even if GPU promises settle later. */
    getPreparationDiagnostics() {
        const now = performance.now();
        return { pending: Array.from(this.preparations, value => this.preparationSnapshot(value, now)),
            failed: this.preparationFailures.map(value => ({ ...value, native: { ...value.native },
                validationScope: { ...value.validationScope }, memoryScope: { ...value.memoryScope } })) };
    }

    retainPreparationFailure(preparation, error, outcome) {
        if (preparation.outcome !== 'pending') return;
        preparation.outcome = outcome;
        preparation.elapsedMs = performance.now() - preparation.startedAt;
        preparation.error = String(error?.message ?? error).slice(0, 2048);
        // Preserve the earliest failures within the same bound as concurrent preparation.
        if (this.preparationFailures.length < 64)
            this.preparationFailures.push(this.preparationSnapshot(preparation));
    }

    observePreparation(promise, preparation, phase, scope) {
        return Promise.resolve(promise).then(value => {
            phase.status = 'fulfilled';
            phase.settledAfterMs = performance.now() - preparation.startedAt;
            if (scope && value) phase.error = String(value.message ?? value).slice(0, 2048);
            return value;
        }, error => {
            phase.status = 'rejected';
            phase.settledAfterMs = performance.now() - preparation.startedAt;
            phase.error = String(error?.message ?? error).slice(0, 2048);
            throw error;
        });
    }

    async operation(action, stage, label) {
        const r = this.renderer;
        r._requireOwner();
        if (this.pending.size >= 64) throw new Error('Too many GPU resources are being prepared concurrently.');
        const device = r.device, owner = r._owner;
        const preparation = { stage, label, owner, startedAt: performance.now(), elapsedMs: null, outcome: 'pending', error: null,
            native: { status: 'pending', settledAfterMs: null, error: null },
            validationScope: { status: 'pending', settledAfterMs: null, error: null },
            memoryScope: { status: 'pending', settledAfterMs: null, error: null } };
        r._setOperation(stage, label);
        device.pushErrorScope('out-of-memory');
        device.pushErrorScope('validation');
        let pending;
        try { pending = Promise.resolve(action()); }
        catch (error) { pending = Promise.reject(error); }
        const validation = device.popErrorScope(), memory = device.popErrorScope();
        let timer, cancel;
        const canceled = new Promise((_, reject) => { cancel = () => {
            const error = new Error('GPU resource preparation was canceled by renderer teardown.');
            // Capture before device destruction can resolve the outstanding scope promises.
            this.retainPreparationFailure(preparation, error, 'canceled');
            reject(error);
        }; });
        this.pending.add(cancel);
        this.preparations.add(preparation);
        const all = Promise.allSettled([
            this.observePreparation(pending, preparation, preparation.native, false),
            this.observePreparation(validation, preparation, preparation.validationScope, true),
            this.observePreparation(memory, preparation, preparation.memoryScope, true),
        ]);
        try {
            const result = await Promise.race([all, canceled, new Promise((_, reject) => {
                timer = setTimeout(() => {
                    const error = new Error('GPU resource preparation exceeded 45000 ms.');
                    this.retainPreparationFailure(preparation, error, 'timed-out');
                    reject(error);
                }, 45000);
            })]);
            r._requireOwner();
            if (r.device !== device || r._owner !== owner) throw new Error('GPU resource preparation belongs to an obsolete device.');
            for (let i = 0; i < result.length; i++) {
                if (result[i].status === 'rejected') throw result[i].reason;
                if (i && result[i].value) throw new Error(result[i].value.message);
            }
            preparation.outcome = 'fulfilled';
            return result[0].value;
        } catch (error) {
            this.retainPreparationFailure(preparation, error, 'failed');
            r._recordError(error, stage, label);
            r.pipelineCache?.clear();
            throw error;
        } finally { clearTimeout(timer); this.pending.delete(cancel); this.preparations.delete(preparation); }
    }

    async createShaderModule(wgsl, debugName = '') {
        this.renderer._requireOwner();
        if (typeof wgsl !== 'string' || !wgsl.length || wgsl.length > 1048576) throw new RangeError('WGSL modules require 1 to 1048576 characters.');
        const name = label(debugName);
        const native = await this.operation(async () => {
            const module = this.renderer.device.createShaderModule({ code: wgsl, label: name });
            const info = await module.getCompilationInfo();
            const errors = info.messages.filter(message => message.type === 'error');
            if (errors.length) throw new Error(errors.map(message => `${name}:${message.lineNum}:${message.linePos}: ${message.message}`).join('\n'));
            return module;
        }, 'create-shader-module', name);
        return this.publish('shader', { native, label: name, size: wgsl.length });
    }

    createBindingLayout(json) {
        const r = this.renderer;
        r._requireOwner();
        const d = parse(json, ['label', 'entries']);
        const entries = array(d.entries, Math.min(32, r.device.limits.maxBindingsPerBindGroup), 'binding layout');
        const seen = new Set();
        for (const e of entries) {
            object(e, ['binding', 'visibility', 'buffer', 'sampler', 'texture', 'storageTexture']);
            integer(e.binding, 0, r.device.limits.maxBindingsPerBindGroup - 1, 'binding');
            if (seen.has(e.binding)) throw new Error('Duplicate binding.');
            seen.add(e.binding);
            integer(e.visibility, 1, 7, 'shader visibility');
            if ([e.buffer, e.sampler, e.texture, e.storageTexture].filter(Boolean).length !== 1) throw new Error('Exactly one resource binding kind is required.');
            if (e.buffer) {
                object(e.buffer, ['type', 'hasDynamicOffset', 'minBindingSize']);
                oneOf(e.buffer.type, ['uniform', 'storage', 'read-only-storage'], 'buffer binding type');
                if (e.buffer.type === 'storage' && (e.visibility & 1)) throw new Error('Writable storage buffers cannot be visible to the vertex stage.');
                if (e.buffer.hasDynamicOffset !== undefined && typeof e.buffer.hasDynamicOffset !== 'boolean') throw new TypeError('Dynamic-offset flag must be boolean.');
                integer(e.buffer.minBindingSize ?? 0, 0, e.buffer.type === 'uniform' ? r.device.limits.maxUniformBufferBindingSize : r.device.limits.maxStorageBufferBindingSize, 'minimum binding size');
            } else if (e.sampler) {
                object(e.sampler, ['type']);
                oneOf(e.sampler.type, ['filtering', 'non-filtering', 'comparison'], 'sampler binding type');
            } else if (e.storageTexture) {
                object(e.storageTexture, ['access', 'format', 'viewDimension']);
                assertStorageTextureFormat(e.storageTexture.format, e.storageTexture.access, r.device);
                oneOf(e.storageTexture.viewDimension, ['2d', '2d-array'], 'storage texture dimension');
                if ((e.visibility & 1) && e.storageTexture.access !== 'read-only')
                    throw new Error('Writable storage textures cannot be visible to the vertex stage.');
            } else {
                object(e.texture, ['sampleType', 'viewDimension', 'multisampled']);
                oneOf(e.texture.sampleType, ['float', 'unfilterable-float', 'uint', 'sint', 'depth'], 'texture sample type');
                if (!['2d', '2d-array', 'cube'].includes(e.texture.viewDimension) ||
                    (e.texture.multisampled !== undefined && typeof e.texture.multisampled !== 'boolean') ||
                    e.texture.multisampled === true && (e.texture.viewDimension !== '2d' || e.texture.sampleType === 'float'))
                    throw new TypeError('Texture binding dimension or multisample declaration is unsupported.');
            }
        }
        entries.sort((a, b) => a.binding - b.binding);
        const descriptor = { label: label(d.label), entries };
        r._setOperation('create-binding-layout', descriptor.label);
        return this.publish('binding-layout', { native: r.pipelineCache.getBindGroupLayout(descriptor), descriptor, label: descriptor.label });
    }

    createBindingGroup(json) {
        const r = this.renderer;
        r._requireOwner();
        const d = parse(json, ['label', 'layout', 'entries']);
        const dependencies = [];
        try {
            const layout = hold(dependencies, this.get(d.layout, 'binding-layout'));
            const input = array(d.entries, 32, 'binding group');
            if (input.length !== layout.descriptor.entries.length) throw new Error('Binding group must fill its layout exactly.');
            const entries = [], dynamic = [], resources = [];
            const seen = new Set();
            for (const e of input) {
                object(e, ['binding', 'resource', 'offset', 'size']);
                if (seen.has(e.binding)) throw new Error('Duplicate binding resource.');
                seen.add(e.binding);
                const expected = layout.descriptor.entries.find(item => item.binding === e.binding);
                if (!expected) throw new Error('Binding group contains an unknown binding.');
                let resource;
                if (expected.buffer) {
                    const b = hold(dependencies, this.get(e.resource, 'buffer'));
                    const offset = e.offset ?? 0, size = e.size ?? b.size - offset;
                    resource = r.resources.bufferBinding(e.resource, offset, size, expected.buffer.type !== 'uniform');
                    if (size < (expected.buffer.minBindingSize ?? 0)) throw new Error('Buffer binding is smaller than the layout minimum.');
                    resources.push({ kind: 'buffer', binding: e.binding, value: b, offset, size,
                        writable: expected.buffer.type === 'storage', role: expected.buffer.type });
                    if (expected.buffer.hasDynamicOffset) dynamic.push({ binding: e.binding, buffer: b, offset, size, alignment: expected.buffer.type === 'uniform' ? r.device.limits.minUniformBufferOffsetAlignment : r.device.limits.minStorageBufferOffsetAlignment });
                } else {
                    if (e.offset !== undefined || e.size !== undefined) throw new Error('Only buffers accept binding ranges.');
                    const value = hold(dependencies, this.get(e.resource, expected.texture || expected.storageTexture ? 'texture-view' : 'sampler'));
                    if (expected.texture && !(value.texture.usage & 4)) throw new Error('Texture view lacks texture-binding usage.');
                    if (expected.texture) {
                        resources.push({ kind: 'texture', value, writable: false, role: 'sampled texture' });
                        assertSampledTexture(value, expected.texture, r.device);
                    } else if (expected.storageTexture) {
                        const storage = expected.storageTexture;
                        if (!(value.usage & GPUTextureUsage.STORAGE_BINDING) || value.sampleCount !== 1 || value.mipCount !== 1 ||
                            value.aspect !== 'all' || value.format !== storage.format || value.dimension !== storage.viewDimension)
                            throw new Error('Storage image view usage, sample count, mip range, aspect, format or dimension does not match its exact layout.');
                        resources.push({ kind: 'texture', value, writable: storage.access !== 'read-only', role: 'storage texture' });
                    } else if (expected.sampler.type === 'comparison' && value.compare !== 'less-equal')
                        throw new Error('Comparison sampler binding requires a less-equal comparison sampler.');
                    else if (expected.sampler.type !== 'comparison' && value.compare)
                        throw new Error('Comparison sampler cannot fill an ordinary sampler binding.');
                    else if (expected.sampler.type === 'non-filtering' && value.filtering)
                        throw new Error('Filtering sampler cannot fill a non-filtering binding.');
                    resource = expected.texture || expected.storageTexture ? value.view : value.sampler;
                }
                entries.push({ binding: e.binding, resource });
            }
            dynamic.sort((a, b) => a.binding - b.binding);
            const name = label(d.label);
            r._setOperation('create-binding-group', name);
            const native = r.device.createBindGroup({ label: name, layout: layout.native, entries });
            return this.publish('binding-group', { native, layout, dynamic, resources, label: name }, dependencies);
        } catch (error) { release(dependencies); throw error; }
    }

    async createRenderPipeline(json) { return this.createPipeline(json, false); }
    async createComputePipeline(json) { return this.createPipeline(json, true); }

    async createPipeline(json, compute) {
        const r = this.renderer;
        r._requireOwner();
        const d = parse(json, compute ? ['label', 'layouts', 'compute'] : ['label', 'layouts', 'vertex', 'fragment', 'primitive', 'depthStencil', 'multisample']);
        const dependencies = [];
        try {
            const layouts = array(d.layouts, r.device.limits.maxBindGroups, 'pipeline layouts').map(handle => hold(dependencies, this.get(handle, 'binding-layout')));
            assertPipelineBindingLimits(layouts, r.device.limits);
            const layout = r.pipelineCache.getPipelineLayout({ bindGroupLayouts: layouts.map(value => value.native) });
            const descriptor = { label: label(d.label), layout };
            const stage = (input, vertex) => {
                object(input, vertex ? ['shader', 'entryPoint', 'buffers'] : ['shader', 'entryPoint', 'targets']);
                const shader = hold(dependencies, this.get(input.shader, 'shader'));
                return { module: shader.native, entryPoint: entryPoint(input.entryPoint) };
            };
            let workgroup;
            if (compute) {
                object(d.compute, ['shader', 'entryPoint', 'workgroupSize', 'workgroupStorageSize']);
                workgroup = computeWorkgroupMetadata(d.compute, r.device.limits);
                const shader = hold(dependencies, this.get(d.compute.shader, 'shader'));
                descriptor.compute = { module: shader.native, entryPoint: entryPoint(d.compute.entryPoint) };
            } else {
                descriptor.vertex = stage(d.vertex, true);
                descriptor.vertex.buffers = array(d.vertex.buffers, r.device.limits.maxVertexBuffers, 'vertex buffers');
                if (r.device.limits.maxBindGroupsPlusVertexBuffers !== undefined
                    && layouts.length + descriptor.vertex.buffers.length > r.device.limits.maxBindGroupsPlusVertexBuffers)
                    throw new RangeError('Pipeline exceeds the selected-device combined binding group and vertex buffer limit.');
                let attributes = 0;
                for (const buffer of descriptor.vertex.buffers) {
                    object(buffer, ['arrayStride', 'stepMode', 'attributes']);
                    integer(buffer.arrayStride, 0, r.device.limits.maxVertexBufferArrayStride, 'vertex stride');
                    if (buffer.arrayStride % 4) throw new RangeError('Vertex stride must be four-byte aligned.');
                    oneOf(buffer.stepMode, ['vertex', 'instance'], 'vertex step mode');
                    for (const attribute of array(buffer.attributes, r.device.limits.maxVertexAttributes, 'vertex attributes')) {
                        object(attribute, ['format', 'offset', 'shaderLocation']);
                        oneOf(attribute.format, ['float32', 'float32x2', 'float32x3', 'float32x4', 'uint32', 'uint32x2', 'uint32x3', 'uint32x4', 'sint32', 'sint32x2', 'sint32x3', 'sint32x4'], 'vertex format');
                        const width = attribute.format.includes('x') ? Number(attribute.format.at(-1)) * 4 : 4;
                        // A zero stride repeats one constant vertex value. Its full
                        // attribute extent is checked against the bound range below.
                        integer(attribute.offset, 0, (buffer.arrayStride || r.device.limits.maxVertexBufferArrayStride) - width, 'vertex attribute offset');
                        if (attribute.offset % 4) throw new RangeError('Vertex attributes require four-byte alignment.');
                        integer(attribute.shaderLocation, 0, r.device.limits.maxVertexAttributes - 1, 'vertex attribute location');
                        attributes++;
                    }
                }
                if (attributes > r.device.limits.maxVertexAttributes) throw new RangeError('Too many vertex attributes.');
                if (d.fragment !== undefined && d.fragment !== null) {
                    descriptor.fragment = stage(d.fragment, false);
                    descriptor.fragment.targets = array(d.fragment.targets, r.device.limits.maxColorAttachments, 'color targets');
                    for (const target of descriptor.fragment.targets) {
                        if (!target) continue;
                        object(target, ['format', 'blend', 'writeMask']);
                        if (!textureFormatInfo(target.format, r.device).color) throw new Error('Color targets require color formats.');
                        integer(target.writeMask ?? 15, 0, 15, 'color write mask');
                        if (target.blend) {
                            object(target.blend, ['color', 'alpha']);
                            for (const component of [target.blend.color, target.blend.alpha]) {
                                object(component, ['operation', 'srcFactor', 'dstFactor']);
                                oneOf(component.operation, ['add', 'subtract', 'reverse-subtract', 'min', 'max'], 'blend operation');
                                for (const factor of [component.srcFactor, component.dstFactor]) oneOf(factor, ['zero', 'one', 'src', 'one-minus-src', 'src-alpha', 'one-minus-src-alpha', 'dst', 'one-minus-dst', 'dst-alpha', 'one-minus-dst-alpha', 'src-alpha-saturated'], 'blend factor');
                            }
                        }
                    }
                }
                descriptor.primitive = object(d.primitive, ['topology', 'stripIndexFormat', 'frontFace', 'cullMode']);
                oneOf(d.primitive.topology, ['triangle-list', 'line-list', 'point-list'], 'primitive topology');
                if (d.primitive.stripIndexFormat !== undefined) throw new Error('Strip primitive pipelines are not supported.');
                oneOf(d.primitive.frontFace, ['ccw', 'cw'], 'front face');
                oneOf(d.primitive.cullMode, ['none', 'front', 'back'], 'cull mode');
                if (d.depthStencil) {
                    descriptor.depthStencil = object(d.depthStencil, ['format', 'depthWriteEnabled', 'depthCompare', 'depthBias', 'depthBiasSlopeScale', 'depthBiasClamp', 'stencilFront', 'stencilBack', 'stencilReadMask', 'stencilWriteMask']);
                    oneOf(d.depthStencil.format, ['depth16unorm', 'depth24plus', 'depth24plus-stencil8', 'depth32float', 'depth32float-stencil8'], 'depth format');
                    textureFormatInfo(d.depthStencil.format, r.device);
                    if (typeof d.depthStencil.depthWriteEnabled !== 'boolean') throw new TypeError('Depth write flag must be boolean.');
                    oneOf(d.depthStencil.depthCompare, ['never', 'less', 'equal', 'less-equal', 'greater', 'not-equal', 'greater-equal', 'always'], 'depth compare');
                    for (const key of ['depthBias', 'depthBiasSlopeScale', 'depthBiasClamp']) if (d.depthStencil[key] !== undefined && !Number.isFinite(d.depthStencil[key])) throw new RangeError('Depth bias must be finite.');
                    if (d.depthStencil.depthBias !== undefined) integer(d.depthStencil.depthBias, -0x80000000, 0x7fffffff, 'depth bias');
                    for (const key of ['stencilReadMask', 'stencilWriteMask']) if (d.depthStencil[key] !== undefined) integer(d.depthStencil[key], 0, 0xffffffff, 'stencil mask');
                    for (const face of [d.depthStencil.stencilFront, d.depthStencil.stencilBack]) {
                        if (!face) continue;
                        if (!['depth24plus-stencil8', 'depth32float-stencil8'].includes(d.depthStencil.format)) throw new Error('Stencil state requires a stencil-capable format.');
                        object(face, ['compare', 'failOp', 'depthFailOp', 'passOp']);
                        oneOf(face.compare, ['never', 'less', 'equal', 'less-equal', 'greater', 'not-equal', 'greater-equal', 'always'], 'stencil compare');
                        for (const key of ['failOp', 'depthFailOp', 'passOp']) oneOf(face[key], ['keep', 'zero', 'replace', 'invert', 'increment-clamp', 'decrement-clamp', 'increment-wrap', 'decrement-wrap'], 'stencil operation');
                    }
                }
                descriptor.multisample = object(d.multisample, ['count', 'mask', 'alphaToCoverageEnabled']);
                oneOf(d.multisample.count, [1, 4], 'sample count');
                assertColorTargets(descriptor.fragment?.targets ?? [], r.device, d.multisample.count);
                integer(d.multisample.mask ?? 0xffffffff, 0, 0xffffffff, 'sample mask');
                if (d.multisample.alphaToCoverageEnabled !== undefined && typeof d.multisample.alphaToCoverageEnabled !== 'boolean') throw new TypeError('Alpha-to-coverage must be boolean.');
            }
            const native = await this.operation(() => compute ? r.pipelineCache.getComputePipelineAsync(descriptor) : r.pipelineCache.getRenderPipelineAsync(descriptor),
                compute ? 'create-compute-pipeline' : 'create-render-pipeline', descriptor.label);
            return this.publish(compute ? 'compute-pipeline' : 'render-pipeline', { native, descriptor, layouts, workgroup, label: descriptor.label }, dependencies);
        } catch (error) { release(dependencies); throw error; }
    }

    bindings(input, pipeline, dependencies) {
        const bindings = array(input, pipeline.layouts.length, 'pass bindings');
        if (bindings.length !== pipeline.layouts.length) throw new Error('Every pipeline binding group must be supplied.');
        const result = new Array(bindings.length), seen = new Set();
        for (const item of bindings) {
            object(item, ['index', 'group', 'dynamicOffsets']);
            integer(item.index, 0, pipeline.layouts.length - 1, 'binding group index');
            if (seen.has(item.index)) throw new Error('Duplicate pass binding group.');
            seen.add(item.index);
            const group = hold(dependencies, this.get(item.group, 'binding-group'));
            if (group.layout.native !== pipeline.layouts[item.index].native) throw new Error('Binding group layout is incompatible with pipeline.');
            const offsets = array(item.dynamicOffsets ?? [], group.dynamic.length, 'dynamic offsets');
            if (offsets.length !== group.dynamic.length) throw new Error('Dynamic offset count does not match the layout.');
            for (let i = 0; i < offsets.length; i++) {
                const binding = group.dynamic[i];
                integer(offsets[i], 0, 0xffffffff, 'dynamic offset');
                if (offsets[i] % binding.alignment || offsets[i] + binding.offset + binding.size > binding.buffer.size) throw new RangeError('Dynamic buffer range is unaligned or out of bounds.');
            }
            result[item.index] = { native: group.native, offsets, engineOffsets: new Uint32Array(offsets.length), dynamic: group.dynamic, resources: group.resources };
        }
        return result;
    }

    prepareCommands(json) {
        const r = this.renderer;
        r._requireOwner();
        const d = parse(json, ['label', 'commands']);
        const name = label(d.label) || 'WebGPU commands';
        r._setOperation('prepare-commands', name);
        const input = array(d.commands, maxCommands, 'commands');
        if (!input.length) throw new Error('Command sequences must not be empty.');
        const dependencies = [], operations = [];
        let draws = 0, encodedDraws = 0, hasCanvas = false, presentsCanvas = false;
        try {
            for (const command of input) {
                const operationLabel = `${name} ${operations.length} ${command.type}`.slice(0, 128);
                if (command.type === 'render' || command.type === 'clear') {
                    const clearOnly = command.type === 'clear';
                    object(command, clearOnly ? ['type', 'pass', 'engineClearValues'] : ['type', 'pass', 'pipeline', 'bindings', 'vertexBuffers', 'indexBuffer', 'draws', 'stencilReference', 'engineInstanceStorage', 'engineInstanceCountLimit', 'viewport', 'scissor']);
                    const pipeline = clearOnly ? undefined : hold(dependencies, this.get(command.pipeline, 'render-pipeline'));
                    const metadata = { color: { width: r._width, height: r._height, format: r.format, sampleCount: 1, usage: 16 }, depth: { width: r._width, height: r._height, format: 'depth24plus', sampleCount: 1, usage: 16 } };
                    const plan = new GpuPassPlan(r._resources, r._owner, command.pass, metadata, operationLabel, r.device);
                    if (pipeline) plan.assertPipeline(pipeline.descriptor);
                    const viewport = clearOnly || command.viewport === undefined ? undefined :
                        drawRectangle(command.viewport, plan.signature.width, plan.signature.height, false, 'viewport');
                    const scissor = clearOnly || command.scissor === undefined ? undefined :
                        drawRectangle(command.scissor, plan.signature.width, plan.signature.height, true, 'scissor');
                    for (const attachment of command.pass.colors) {
                        if (!attachment) continue;
                        for (const handle of [attachment.viewHandle, attachment.resolveTargetHandle]) {
                            if (handle === undefined) continue;
                            if (handle === 0) {
                                hasCanvas = true;
                                if (attachment.resolveTargetHandle === 0 || attachment.storeOp === 'store') presentsCanvas = true;
                            }
                            else if (handle > 0) hold(dependencies, this.get(handle, 'texture-view'));
                        }
                    }
                    if (command.pass.depthStencil?.viewHandle > 0) hold(dependencies, this.get(command.pass.depthStencil.viewHandle, 'texture-view'));
                    if (command.pass.depthStencil?.viewHandle === -1) hasCanvas = true;
                    if (clearOnly) {
                        const scope = new GpuCommandUsageScope();
                        for (const attachment of plan.bindings) scope.texture(attachment.source, true, 'render attachment');
                        if (command.engineClearValues !== undefined && command.engineClearValues !== true)
                            throw new TypeError('Engine clear values require an explicit true opt-in.');
                        operations.push({ type: 'clear', label: operationLabel, plan, engineClearValues: command.engineClearValues === true });
                        continue;
                    }
                    const bindings = this.bindings(command.bindings, pipeline, dependencies);
                    const scope = new GpuCommandUsageScope();
                    scope.bindings(bindings);
                    // Conservatively reserve every attachment aspect for the pass, even read-only depth.
                    for (const attachment of plan.bindings) scope.texture(attachment.source, true, 'render attachment');
                    const vertexBuffers = array(command.vertexBuffers, pipeline.descriptor.vertex.buffers.length, 'vertex buffers').map((item, slot) => {
                        object(item, ['buffer', 'offset', 'size']);
                        const buffer = hold(dependencies, this.get(item.buffer, 'buffer'));
                        const offset = integer(item.offset ?? 0, 0, buffer.size, 'vertex offset');
                        const size = integer(item.size ?? buffer.size - offset, 1, buffer.size - offset, 'vertex range');
                        if (!(buffer.usage & 32) || offset % 4 || size % 4) throw new Error('Vertex buffer usage or alignment is invalid.');
                        const vertexLayout = pipeline.descriptor.vertex.buffers[slot];
                        if (vertexLayout.arrayStride === 0)
                            for (const attribute of vertexLayout.attributes) {
                                const width = attribute.format.includes('x') ? Number(attribute.format.at(-1)) * 4 : 4;
                                if (attribute.offset + width > size) throw new RangeError('Constant vertex attribute exceeds the bound vertex range.');
                            }
                        scope.buffer(buffer, false, 'vertex');
                        return { buffer: buffer.buffer, offset, size, slot };
                    });
                    if (vertexBuffers.length !== pipeline.descriptor.vertex.buffers.length) throw new Error('All vertex buffer slots must be supplied.');
                    let indexBuffer, indexFormat, indexBytes = 0, indexOffset = 0, indexSize = 0;
                    if (command.indexBuffer !== undefined) {
                        const index = object(command.indexBuffer, ['buffer', 'format', 'offset', 'size']);
                        indexBuffer = hold(dependencies, this.get(index.buffer, 'buffer'));
                        indexFormat = oneOf(index.format, ['uint16', 'uint32'], 'index format');
                        indexBytes = indexFormat === 'uint16' ? 2 : 4;
                        indexOffset = integer(index.offset ?? 0, 0, indexBuffer.size, 'index offset');
                        indexSize = integer(index.size ?? indexBuffer.size - indexOffset, indexBytes, indexBuffer.size - indexOffset, 'index range');
                        if (!(indexBuffer.usage & 16) || indexOffset % indexBytes || indexSize % indexBytes) throw new Error('Index buffer usage or alignment is invalid.');
                        scope.buffer(indexBuffer, false, 'index');
                    }
                    const drawList = array(command.draws, maxCommands, 'draws');
                    let replayDraws = 0;
                    let engineInstanceCountLimit = command.engineInstanceCountLimit === undefined ? 0 :
                        integer(command.engineInstanceCountLimit, 1, 0xffffffff, 'engine instance count limit');
                    if (command.engineInstanceStorage !== undefined) {
                        if (engineInstanceCountLimit) throw new Error('WebGPU.Commands.InstanceOverride: declare one instance ceiling owner.');
                        const source = object(command.engineInstanceStorage, ['group', 'binding', 'buffer', 'stride', 'limit']);
                        const groupIndex = integer(source.group, 0, bindings.length - 1, 'instance storage group');
                        const bindingIndex = integer(source.binding, 0, r.device.limits.maxBindingsPerBindGroup - 1, 'instance storage binding');
                        const buffer = this.get(source.buffer, 'buffer');
                        const stride = integer(source.stride, 4, 256, 'instance storage stride');
                        engineInstanceCountLimit = integer(source.limit, 1, 65536, 'engine instance count limit');
                        const resolved = bindings[groupIndex].resources.find(value =>
                            value.kind === 'buffer' && value.binding === bindingIndex && value.role === 'read-only-storage');
                        if (!resolved || resolved.value !== buffer || stride % 4 ||
                            engineInstanceCountLimit > Math.floor(resolved.size / stride))
                            throw new Error('WebGPU.Commands.InstanceStorage: the declared read-only binding cannot cover its immutable instance ceiling.');
                    }
                    if (engineInstanceCountLimit && (drawList.length !== 1 ||
                        (drawList[0].type !== undefined && drawList[0].type !== 'drawIndexed' && drawList[0].type !== 'draw')))
                        throw new Error('WebGPU.Commands.InstanceOverride: only one direct draw may opt into a dynamic instance count.');
                    for (const draw of drawList) {
                        const type = oneOf(draw.type ?? 'drawIndexed', ['draw', 'drawIndexed', 'drawIndirect', 'drawIndexedIndirect'], 'draw type');
                        const indexed = type === 'drawIndexed' || type === 'drawIndexedIndirect';
                        if (indexed && !indexBuffer) throw new Error('Indexed draws require an index buffer.');
                        if (type === 'drawIndirect' || type === 'drawIndexedIndirect') {
                            object(draw, ['type', 'buffer', 'offset', 'firstInstancePolicy', 'drawCount', 'stride']);
                            const buffer = hold(dependencies, this.get(draw.buffer, 'buffer'));
                            const argumentBytes = indexed ? 20 : 16;
                            const offset = integer(draw.offset ?? 0, 0, buffer.size - argumentBytes, 'indirect argument offset');
                            const drawCount = integer(draw.drawCount ?? 1, 1, maxIndirectDraws, 'indirect draw count');
                            const stride = integer(draw.stride ?? argumentBytes, argumentBytes, 0xffffffff, 'indirect stride');
                            if (!(buffer.usage & 256) || offset % 4) throw new Error('Indirect arguments require INDIRECT usage and four-byte alignment.');
                            if (stride % 4 || offset + (drawCount - 1) * stride + argumentBytes > buffer.size)
                                throw new RangeError('Indirect argument stride/alignment or batch range exceeds the source buffer.');
                            oneOf(draw.firstInstancePolicy, ['zero', 'feature'], 'indirect first-instance policy');
                            if (draw.firstInstancePolicy === 'feature' && !r.device.features.has('indirect-first-instance'))
                                throw new Error('Nonzero indirect firstInstance requires the enabled indirect-first-instance feature.');
                            // GPU producers must initialize all fields, bound counts/index/instance
                            // addressing, and honor the declared firstInstance policy on every replay.
                            scope.buffer(buffer, false, 'indirect');
                            draw.native = buffer.buffer;
                            draw.offset = offset;
                            draw.drawCount = drawCount;
                            draw.stride = stride;
                            replayDraws += drawCount;
                            continue;
                        }
                        object(draw, indexed ? ['type', 'indexCount', 'instanceCount', 'firstIndex', 'baseVertex', 'firstInstance']
                            : ['type', 'vertexCount', 'instanceCount', 'firstVertex', 'firstInstance']);
                        draw.type = type;
                        replayDraws++;
                        draw.instanceCount = integer(draw.instanceCount ?? 1, 0, 0xffffffff, 'instance count');
                        draw.firstInstance = integer(draw.firstInstance ?? 0, 0, 0xffffffff, 'first instance');
                        if (engineInstanceCountLimit && (draw.firstInstance !== 0 || draw.instanceCount > engineInstanceCountLimit))
                            throw new RangeError('WebGPU.Commands.InstanceOverride: the retained direct draw exceeds its immutable instance ceiling.');
                        if (indexed) {
                            integer(draw.indexCount, 0, 0xffffffff, 'index count');
                            draw.firstIndex = integer(draw.firstIndex ?? 0, 0, 0xffffffff, 'first index');
                            draw.baseVertex = integer(draw.baseVertex ?? 0, -0x80000000, 0x7fffffff, 'base vertex');
                            if (draw.firstIndex + draw.indexCount > indexSize / indexBytes) throw new RangeError('Draw exceeds the bound index range.');
                        } else {
                            integer(draw.vertexCount, 0, 0xffffffff, 'vertex count');
                            draw.firstVertex = integer(draw.firstVertex ?? 0, 0, 0xffffffff, 'first vertex');
                        }
                        for (let slot = 0; slot < vertexBuffers.length; slot++) {
                            const layout = pipeline.descriptor.vertex.buffers[slot];
                            if (layout.stepMode === 'instance' && (draw.firstInstance + (engineInstanceCountLimit || draw.instanceCount)) * layout.arrayStride > vertexBuffers[slot].size) throw new RangeError('Draw exceeds the bound instance range.');
                            if (!indexed && layout.stepMode === 'vertex' && (draw.firstVertex + draw.vertexCount) * layout.arrayStride > vertexBuffers[slot].size) throw new RangeError('Draw exceeds the bound vertex range.');
                        }
                    }
                    if (!Number.isSafeInteger(replayDraws) || draws > maxReplayDraws - replayDraws)
                        throw new RangeError('WebGPU.Commands.ReplayCapacity: retained commands exceed 262144 replay draws.');
                    draws += replayDraws;
                    if (!scissor || (scissor.width !== 0 && scissor.height !== 0))
                        encodedDraws += replayDraws;
                    const stencilReference = integer(command.stencilReference ?? 0, 0, 0xffffffff, 'stencil reference');
                    operations.push({ type: 'render', label: operationLabel, pipeline: pipeline.native, plan, bindings, vertexBuffers, indexBuffer: indexBuffer?.buffer, indexFormat, indexOffset, indexSize, draws: drawList, engineInstanceCountLimit, stencilReference, viewport, scissor });
                } else if (command.type === 'compute') {
                    object(command, ['type', 'pipeline', 'bindings', 'workgroups', 'indirect']);
                    const pipeline = hold(dependencies, this.get(command.pipeline, 'compute-pipeline'));
                    const bindings = this.bindings(command.bindings, pipeline, dependencies);
                    const scope = new GpuCommandUsageScope();
                    scope.bindings(bindings);
                    if ((command.workgroups === undefined) === (command.indirect === undefined))
                        throw new Error('Compute requires exactly one direct workgroup list or indirect argument range.');
                    let workgroups, indirect;
                    if (command.indirect !== undefined) {
                        object(command.indirect, ['buffer', 'offset']);
                        const buffer = hold(dependencies, this.get(command.indirect.buffer, 'buffer'));
                        const offset = integer(command.indirect.offset ?? 0, 0, buffer.size - 12, 'compute indirect offset');
                        if (!(buffer.usage & 256) || offset % 4)
                            throw new Error('Compute indirect arguments require INDIRECT usage and four-byte alignment.');
                        scope.buffer(buffer, false, 'indirect');
                        indirect = { native: buffer.buffer, offset };
                    } else {
                        workgroups = array(command.workgroups, 3, 'workgroup dimensions');
                        if (workgroups.length !== 3) throw new Error('Compute workgroups require three dimensions.');
                        for (const count of workgroups) integer(count, 0, r.device.limits.maxComputeWorkgroupsPerDimension, 'workgroup count');
                    }
                    operations.push({ type: 'compute', label: operationLabel, descriptor: { label: operationLabel }, pipeline: pipeline.native, bindings, workgroups, indirect });
                } else if (command.type === 'copyBuffer') {
                    object(command, ['type', 'source', 'destination', 'sourceOffset', 'destinationOffset', 'size']);
                    const source = hold(dependencies, this.get(command.source, 'buffer'));
                    const destination = hold(dependencies, this.get(command.destination, 'buffer'));
                    const sourceOffset = integer(command.sourceOffset ?? 0, 0, source.size, 'source offset');
                    const destinationOffset = integer(command.destinationOffset ?? 0, 0, destination.size, 'destination offset');
                    const size = integer(command.size, 4, Math.min(source.size - sourceOffset, destination.size - destinationOffset), 'copy size');
                    if (source === destination || !(source.usage & 4) || !(destination.usage & 8) || (sourceOffset | destinationOffset | size) % 4) throw new Error('Buffer copy resources, usages or alignment are invalid.');
                    operations.push({ type: 'copyBuffer', label: operationLabel, source: source.buffer, destination: destination.buffer, sourceOffset, destinationOffset, size });
                } else if (command.type === 'copyTexture') {
                    object(command, ['type', 'source', 'destination', 'sourceMip', 'destinationMip', 'sourceLayer', 'sourceX', 'sourceY', 'destinationX', 'destinationY', 'destinationLayer', 'width', 'height']);
                    const source = hold(dependencies, this.get(command.source, 'texture'));
                    const destination = hold(dependencies, this.get(command.destination, 'texture'));
                    if (source === destination || source.sampleCount !== 1 || destination.sampleCount !== 1 || source.format !== destination.format
                        || !['r8unorm', 'rgba8unorm', 'rgba8unorm-srgb', 'r16float', 'r32float', 'rgba16float', 'depth16unorm', 'depth24plus', 'depth32float'].includes(source.format) ||
                        !(source.usage & GPUTextureUsage.COPY_SRC) || !(destination.usage & GPUTextureUsage.COPY_DST))
                        throw new Error('Texture copy requires distinct, equal-format single-sample color or depth-only copy resources.');
                    const sourceMip = integer(command.sourceMip ?? 0, 0, source.mipLevelCount - 1, 'source mip');
                    const destinationMip = integer(command.destinationMip ?? 0, 0, destination.mipLevelCount - 1, 'destination mip');
                    const sourceLayer = integer(command.sourceLayer ?? 0, 0, (source.arrayLayerCount ?? 1) - 1, 'source layer');
                    const destinationLayer = integer(command.destinationLayer ?? 0, 0, (destination.arrayLayerCount ?? 1) - 1, 'destination layer');
                    const sourceWidth = Math.max(1, Math.floor(source.width / 2 ** sourceMip)), sourceHeight = Math.max(1, Math.floor(source.height / 2 ** sourceMip));
                    const destinationWidth = Math.max(1, Math.floor(destination.width / 2 ** destinationMip)), destinationHeight = Math.max(1, Math.floor(destination.height / 2 ** destinationMip));
                    const sourceX = integer(command.sourceX ?? 0, 0, sourceWidth - 1, 'source x'), sourceY = integer(command.sourceY ?? 0, 0, sourceHeight - 1, 'source y');
                    const destinationX = integer(command.destinationX ?? 0, 0, destinationWidth - 1, 'destination x'), destinationY = integer(command.destinationY ?? 0, 0, destinationHeight - 1, 'destination y');
                    const width = integer(command.width, 1, Math.min(sourceWidth - sourceX, destinationWidth - destinationX), 'copy width');
                    const height = integer(command.height, 1, Math.min(sourceHeight - sourceY, destinationHeight - destinationY), 'copy height');
                    const depth = ['depth16unorm', 'depth24plus', 'depth32float'].includes(source.format);
                    if (depth && (sourceX !== 0 || sourceY !== 0 || destinationX !== 0 || destinationY !== 0 ||
                        width !== sourceWidth || height !== sourceHeight || width !== destinationWidth || height !== destinationHeight))
                        throw new RangeError('Depth texture copies require complete matching mip subresources.');
                    const aspect = depth ? 'depth-only' : 'all';
                    operations.push({ type: 'copyTexture', label: operationLabel,
                        source: { texture: source.texture, mipLevel: sourceMip, origin: [sourceX, sourceY, sourceLayer], aspect },
                        destination: { texture: destination.texture, mipLevel: destinationMip, origin: [destinationX, destinationY, destinationLayer], aspect }, size: [width, height, 1] });
                } else throw new Error('Unsupported ordered command type.');
            }
            return this.publish('commands', { label: name, encoderDescriptor: { label: name }, commandBufferDescriptor: { label: name }, operations, hasCanvas, presentsCanvas, generation: r._generation, width: r._width, height: r._height, draws, encodedDraws }, dependencies);
        } catch (error) { release(dependencies); throw error; }
    }

    submitPreparedCommands(handle) {
        const r = this.renderer;
        r._requireOwner();
        const commands = this.get(handle, 'commands');
        const operations = commands.operations;
        if (commands.hasCanvas && (commands.generation !== r._generation || commands.width !== r._width || commands.height !== r._height || !r._configured)) throw new Error('Canvas command sequence must be prepared again after surface replacement.');
        r._executing = true;
        try {
            if (commands.hasCanvas) {
                r._setOperation('acquire-canvas', commands.label);
                this.canvasColor.view = r.context.getCurrentTexture().createView();
                this.canvasColor.width = this.canvasDepth.width = r._width;
                this.canvasColor.height = this.canvasDepth.height = r._height;
                this.canvasColor.format = r.format;
                this.canvasDepth.view = r.depthView;
            }
            r._setOperation('create-command-encoder', commands.label);
            const encoder = r.device.createCommandEncoder(commands.encoderDescriptor);
            for (let i = 0; i < operations.length; i++) {
                const operation = operations[i];
                this.encodeOperation(encoder, operation, null, 0, i);
            }
            r._setOperation('finish-command-encoder', commands.label);
            r._submission[0] = encoder.finish(commands.commandBufferDescriptor);
            r._setOperation('submit-commands', commands.label);
            r.device.queue.submit(r._submission);
            r._stats.draws += commands.encodedDraws;
            r._stats.frameSubmitCalls++;
            return commands.presentsCanvas;
        } catch (error) { r._fail(error); throw error; }
        finally {
            for (let i = 0; i < operations.length; i++) operations[i].plan?.release();
            this.canvasColor.view = this.canvasDepth.view = undefined;
            r._submission[0] = null;
            r._executing = false;
        }
    }

    encodeOperation(encoder, operation, packet = null, offsetBase = 0, commandIndex = -1, statistics = null) {
        const r = this.renderer;
        const recordBase = offsetBase;
        const operationLabel = operation.label ?? operation.type;
        const engineInstanceCount = packet && operation.engineInstanceCountLimit && (packet.getUint32(offsetBase + 68, true) & 1)
            ? packet.getUint32(offsetBase + 64, true) : undefined;
        r._setOperation('encode-operation', operationLabel, commandIndex);
        if (operation.type === 'copyBuffer') {
            encoder.copyBufferToBuffer(operation.source, operation.sourceOffset, operation.destination, operation.destinationOffset, operation.size);
            return;
        }
        if (operation.type === 'copyTexture') {
            encoder.copyTextureToTexture(operation.source, operation.destination, operation.size);
            return;
        }
        r._setOperation('begin-pass', operationLabel, commandIndex);
        let passDescriptor;
        if (operation.type !== 'compute') {
            passDescriptor = operation.plan.prepare(this.canvasColor, this.canvasDepth);
            if (packet && operation.engineClearValues && (packet.getUint32(offsetBase + 68, true) & 8)) {
                for (const color of passDescriptor.colorAttachments) if (color && color.loadOp === 'clear') {
                    color.clearValue.r = packet.getFloat32(offsetBase, true);
                    color.clearValue.g = packet.getFloat32(offsetBase + 4, true);
                    color.clearValue.b = packet.getFloat32(offsetBase + 8, true);
                    color.clearValue.a = packet.getFloat32(offsetBase + 12, true);
                }
                if (passDescriptor.depthStencilAttachment?.depthLoadOp === 'clear')
                    passDescriptor.depthStencilAttachment.depthClearValue = packet.getFloat32(offsetBase + 16, true);
            }
        }
        const pass = operation.type === 'compute' ? encoder.beginComputePass(operation.descriptor) : encoder.beginRenderPass(passDescriptor);
        if (statistics) {
            if (operation.type === 'compute') statistics.computePassCreates++;
            else statistics.renderPassCreates++;
        }
        if (operation.type === 'clear') {
            r._setOperation('end-pass', operationLabel, commandIndex);
            pass.end();
            return;
        }
        r._setOperation('set-pipeline', operationLabel, commandIndex);
        pass.setPipeline(operation.pipeline);
        for (let binding = 0; binding < operation.bindings.length; binding++) {
            const group = operation.bindings[binding];
            const offsets = packet ? group.engineOffsets : group.offsets;
            if (packet) {
                for (let dynamic = 0; dynamic < offsets.length; dynamic++) {
                    offsets[dynamic] = packet.getUint32(offsetBase, true);
                    offsetBase += 4;
                }
            }
            r._setOperation('set-bind-group', operationLabel, commandIndex);
            pass.setBindGroup(binding, group.native, offsets);
        }
        if (operation.type === 'compute') {
            r._setOperation('dispatch-workgroups', operationLabel, commandIndex);
            if (operation.indirect) pass.dispatchWorkgroupsIndirect(operation.indirect.native, operation.indirect.offset);
            else pass.dispatchWorkgroups(operation.workgroups[0], operation.workgroups[1], operation.workgroups[2]);
        }
        else {
            const flags = packet ? packet.getUint32(recordBase + 68, true) : 0;
            let suppressDraw = false;
            const viewport = operation.viewport;
            if (flags & 2) {
                const at = recordBase + 72;
                pass.setViewport(packet.getUint32(at, true), packet.getUint32(at + 4, true),
                    packet.getUint32(at + 8, true), packet.getUint32(at + 12, true), 0, 1);
            } else if (viewport) pass.setViewport(viewport.x, viewport.y, viewport.width, viewport.height, 0, 1);
            const scissor = operation.scissor;
            if (flags & 4) {
                const at = recordBase + 88;
                const width = packet.getUint32(at + 8, true), height = packet.getUint32(at + 12, true);
                suppressDraw = width === 0 || height === 0;
                if (!suppressDraw)
                    pass.setScissorRect(packet.getUint32(at, true), packet.getUint32(at + 4, true), width, height);
            } else if (scissor) {
                suppressDraw = scissor.width === 0 || scissor.height === 0;
                if (!suppressDraw) pass.setScissorRect(scissor.x, scissor.y, scissor.width, scissor.height);
            }
            r._setOperation('set-stencil-reference', operationLabel, commandIndex);
            pass.setStencilReference(operation.stencilReference);
            for (let slot = 0; slot < operation.vertexBuffers.length; slot++) {
                const buffer = operation.vertexBuffers[slot];
                r._setOperation('set-vertex-buffer', operationLabel, commandIndex);
                pass.setVertexBuffer(slot, buffer.buffer, buffer.offset, buffer.size);
            }
            if (operation.indexBuffer) {
                r._setOperation('set-index-buffer', operationLabel, commandIndex);
                pass.setIndexBuffer(operation.indexBuffer, operation.indexFormat, operation.indexOffset, operation.indexSize);
            }
            for (let draw = 0; !suppressDraw && draw < operation.draws.length; draw++) {
                const value = operation.draws[draw];
                r._setOperation('draw', operationLabel, commandIndex, draw);
                if (value.type === 'drawIndirect' || value.type === 'drawIndexedIndirect') {
                    for (let slot = 0; slot < value.drawCount; slot++) {
                        const offset = value.offset + slot * value.stride;
                        if (value.type === 'drawIndexedIndirect') pass.drawIndexedIndirect(value.native, offset);
                        else pass.drawIndirect(value.native, offset);
                    }
                }
                else if (value.type === 'draw') pass.draw(value.vertexCount, engineInstanceCount ?? value.instanceCount, value.firstVertex, value.firstInstance);
                else pass.drawIndexed(value.indexCount, engineInstanceCount ?? value.instanceCount, value.firstIndex, value.baseVertex, value.firstInstance);
            }
        }
        r._setOperation('end-pass', operationLabel, commandIndex);
        pass.end();
    }

    submitEngineFrame(commands, uniforms, storage, preparations, payload, resourceDescriptions, resourceReceipts) {
        return this.engineFrame.accept(commands, uniforms, storage, preparations, payload, resourceDescriptions, resourceReceipts);
    }

    dispose() {
        this.engineFrame.dispose();
        for (const cancel of this.pending) cancel();
        this.pending.clear();
        this.canvasColor.view = this.canvasDepth.view = undefined;
    }
}
