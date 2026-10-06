import fs from 'node:fs/promises';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { inspectCanvas } from './canvas-capture.mjs';
import { installAdvancedSubmissionObservation } from './advanced-submission-observer.mjs';
import { installNativeCompileCapture } from './native-compile-isolation.mjs';

function assert(condition, message) { if (!condition) throw new Error(message); }
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const hasSurface = pixels => pixels.colorfulLeft > 100;
const worldPath = '/game/Worlds/AdvancedRenderingParityWorld.asset';
const fixtureHashes = {
    on: '8f537639aa8260d935fda46c82a3ca4d4a983efbb37bf9c00fb17f8738e96f9b',
    off: 'b3dee24dfce5d5515c69cef8df62738547ae128aba74a673a1ad058f6e712484',
};
const canonical = value => Array.isArray(value) ? value.map(canonical) : value && typeof value === 'object'
    ? Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])])) : value;
const equal = (a, b) => JSON.stringify(canonical(a)) === JSON.stringify(canonical(b));

/** Binds the observation to the pinned fixture and the published, hash-checked programs. */
async function publishedShadowArtifacts(root, state) {
    const manifest = JSON.parse(await fs.readFile(path.join(root, 'content', 'manifest.json'), 'utf8'));
    const receipt = JSON.parse(await fs.readFile(path.join(root, 'shadow-comparison.json'), 'utf8'));
    const launch = JSON.parse(await fs.readFile(path.join(root, 'browser-publish.json'), 'utf8'));
    assert(launch.schema === 2 && launch.format === 'xrengine-engine-launch' &&
        launch.manifest === './content/manifest.json' && manifest.startupWorld === worldPath &&
        manifest.startupSettings === '/game/startup.asset' &&
        receipt.schema === 1 && receipt.state === state && receipt.sourceWorldSha256 === fixtureHashes[state] &&
        /^[a-f0-9]{64}$/.test(receipt.canonicalCatalogManifestSha256) &&
        /^[a-f0-9]{64}$/.test(receipt.sharedSourceSha256) &&
        /^[a-f0-9]{64}$/.test(receipt.verifiedStartupSemanticSha256),
    `BrowserSmoke.ShadowFixture: ${state} must use the pinned published receiver and occluders.`);
    const readPayload = async virtualPath => {
        const entries = manifest.assets?.filter(entry => entry.path === virtualPath) ?? [];
        assert(entries.length === 1 && /^[a-f0-9]{64}$/.test(entries[0].hash) &&
            entries[0].url === `payload/${entries[0].hash}.bin` &&
            Number.isSafeInteger(entries[0].bytes) && entries[0].bytes > 0 && entries[0].bytes <= 1048576,
        `BrowserSmoke.ShadowPayload: invalid payload for ${virtualPath}.`);
        const bytes = await fs.readFile(path.join(root, 'content', entries[0].url));
        assert(bytes.length === entries[0].bytes && sha256(bytes) === entries[0].hash,
            `BrowserSmoke.ShadowPayload: hash mismatch for ${virtualPath}.`);
        return { bytes, hash: entries[0].hash };
    };
    const world = await readPayload(worldPath);
    assert(world.hash === receipt.publishedWorldSha256,
        `BrowserSmoke.ShadowFixture: ${state} cooked startup world differs from the staging receipt.`);
    const startup = await readPayload('/game/startup.asset');
    assert(startup.hash === receipt.publishedStartupSettingsSha256,
        `BrowserSmoke.ShadowFixture: ${state} cooked startup settings differ from the verified receipt.`);
    const artifact = async entry => {
        assert(entry && /^[a-f0-9]{64}$/.test(entry.descriptorIdentity),
            'BrowserSmoke.ShadowArtifact: required program is absent.');
        const identity = entry.descriptorIdentity;
        const shaders = manifest.shaderArtifacts?.filter(shader => shader.identity === identity) ?? [];
        assert(shaders.length === 1 && shaders[0].descriptor === `/engine/Shaders/Cooked/${identity}.json` &&
            shaders[0].source === `/engine/Shaders/Cooked/${identity}.wgsl`,
        'BrowserSmoke.ShadowArtifact: program is absent from the shader catalog.');
        const descriptorPayload = await readPayload(shaders[0].descriptor);
        const source = await readPayload(shaders[0].source);
        const descriptor = JSON.parse(descriptorPayload.bytes.toString('utf8'));
        assert(descriptorPayload.hash === identity && descriptor.schemaVersion === 3 &&
            descriptor.target === 'WebGPUWgsl' && descriptor.pass === entry.pass &&
            descriptor.source?.sha256 === source.hash && descriptor.source?.byteLength === source.bytes.length &&
            descriptor.source?.url === `${source.hash}.wgsl`,
        'BrowserSmoke.ShadowArtifact: descriptor and source identity disagree.');
        return { descriptorIdentity: identity, wgslSha256: source.hash, codeBytes: source.bytes.length, descriptor };
    };
    const pipeline = async pass => {
        const entries = manifest.pipelineArtifacts?.filter(entry => entry.scope === 'advanced' && entry.pass === pass) ?? [];
        assert(entries.length === 1, `BrowserSmoke.ShadowArtifact: expected one advanced::${pass}.`);
        const result = await artifact(entries[0]);
        assert(result.descriptor.name === `engine-advanced-${pass}` &&
            result.descriptor.materialVariant === undefined &&
            result.descriptor.entryPoints?.compute === 'advancedShadeNative' &&
            Object.keys(result.descriptor.entryPoints).length === 1 &&
            equal(result.descriptor.workgroupSize, [16, 16, 1]),
        'BrowserSmoke.ShadowArtifact: native pipeline identity disagrees.');
        return result;
    };
    const material = async (semantic, pass, outputProfile, name) => {
        const entries = manifest.materialVariants?.filter(entry => entry.semantic === semantic && entry.semanticVersion === 1 &&
            entry.target === 'WebGPUWgsl' && entry.pass === pass && entry.vertexProfile === 'static-position-v1' &&
            entry.outputProfile === outputProfile) ?? [];
        assert(entries.length === 1, `BrowserSmoke.ShadowArtifact: expected one ${semantic} caster.`);
        const result = await artifact(entries[0]);
        const variant = result.descriptor.materialVariant;
        assert(result.descriptor.name === name && variant?.semantic === semantic && variant.semanticVersion === 1 &&
            variant.vertexProfile === 'static-position-v1' && variant.outputProfile === outputProfile,
        'BrowserSmoke.ShadowArtifact: caster material identity disagrees.');
        return result;
    };
    const programs = {};
    for (const pass of ['shade-native-depth', 'shade-native', 'shade-native-no-modifiers'])
        programs[pass] = await pipeline(pass);
    programs.directional = await material('OpaqueShadowDepth', 'depth', 'depth-normal-v1', 'engine-shadow-depth');
    programs.point = await material('OpaquePointShadowDepth', 'point-shadow-depth', 'radial-r16f-v1', 'engine-point-shadow-depth');
    return { receipt, programs };
}

/** Observes real resources, commands and submissions; retains at most 128 unique records per kind. */
export function installShadowGpuObservation() {
    const limit = 128;
    const evidence = { textures: [], modules: [], pipelines: [], submitted: [], shadowPasses: [], consumers: [],
        queueSubmits: 0, captureErrors: [], deviceErrors: [], overflow: 0, queueCompletions: 0, completedSerial: 0 };
    const textures = new WeakMap(), views = new WeakMap(), modules = new WeakMap(), pipelines = new WeakMap();
    const groups = new WeakMap(), passes = new WeakMap(), encoders = new WeakMap(), commands = new WeakMap();
    let queue;
    const observe = action => {
        try { action(); }
        catch (error) {
            if (evidence.captureErrors.length < 8) evidence.captureErrors.push(String(error).slice(0, 1024));
        }
    };
    const record = (list, value) => {
        if (list.length >= limit) { evidence.overflow++; return null; }
        list.push(value);
        return value;
    };
    const label = value => String(value?.label ?? '').slice(0, 128);
    const wrap = (prototype, name, callback) => {
        const original = prototype[name];
        prototype[name] = function (...args) {
            const result = original.apply(this, args);
            observe(() => callback(this, args, result));
            return result;
        };
    };
    wrap(GPUAdapter.prototype, 'requestDevice', (_owner, _args, promise) => {
        void promise.then(device => observe(() => {
            const append = error => { if (evidence.deviceErrors.length < 8) evidence.deviceErrors.push(String(error).slice(0, 1024)); };
            device.addEventListener('uncapturederror', event => append(event.error));
            void device.lost.then(info => append(`Device lost: ${info.reason}: ${info.message}`), append);
        }), () => {});
    });
    wrap(GPUDevice.prototype, 'createTexture', (_owner, [descriptor], texture) => {
        const size = descriptor.size;
        const value = record(evidence.textures, { id: evidence.textures.length + 1, label: label(descriptor),
            width: texture.width ?? size.width ?? size[0], height: texture.height ?? size.height ?? size[1] ?? 1,
            layers: texture.depthOrArrayLayers ?? size.depthOrArrayLayers ?? size[2] ?? 1,
            format: descriptor.format, usage: descriptor.usage, sampleCount: descriptor.sampleCount ?? 1 });
        if (value) textures.set(texture, value);
    });
    wrap(GPUTexture.prototype, 'createView', (texture, [descriptor = {}], view) => {
        const target = textures.get(texture);
        if (target) views.set(view, { textureId: target.id, layer: descriptor.baseArrayLayer ?? 0,
            layers: descriptor.arrayLayerCount ?? target.layers, mip: descriptor.baseMipLevel ?? 0,
            dimension: descriptor.dimension ?? (target.layers > 1 ? '2d-array' : '2d'),
            aspect: descriptor.aspect ?? 'all' });
    });
    wrap(GPUDevice.prototype, 'createShaderModule', (_owner, [descriptor], module) => {
        const value = record(evidence.modules, { id: evidence.modules.length + 1, label: label(descriptor),
            sha256: null, codeBytes: null, status: 'pending' });
        if (!value) return;
        modules.set(module, value);
        const code = descriptor.code;
        void Promise.resolve().then(async () => {
            const bytes = new TextEncoder().encode(code);
            const digest = await crypto.subtle.digest('SHA-256', bytes);
            value.sha256 = [...new Uint8Array(digest)].map(byte => byte.toString(16).padStart(2, '0')).join('');
            value.codeBytes = bytes.length;
            value.status = 'fulfilled';
        }).catch(() => { value.status = 'unavailable'; });
    });
    const rememberPipeline = (pipeline, descriptor) => {
        const stage = value => value && ({ moduleId: modules.get(value.module)?.id ?? null,
            entryPoint: value.entryPoint, constants: value.constants ?? {} });
        const value = record(evidence.pipelines, { id: evidence.pipelines.length + 1, label: label(descriptor),
            vertex: stage(descriptor.vertex), fragment: stage(descriptor.fragment), compute: stage(descriptor.compute),
            primitive: descriptor.primitive ?? null, depthStencil: descriptor.depthStencil ?? null,
            targets: descriptor.fragment?.targets ?? null });
        if (value) pipelines.set(pipeline, value);
    };
    for (const method of ['createRenderPipeline', 'createComputePipeline'])
        wrap(GPUDevice.prototype, method, (_owner, [descriptor], pipeline) => rememberPipeline(pipeline, descriptor));
    for (const method of ['createRenderPipelineAsync', 'createComputePipelineAsync'])
        wrap(GPUDevice.prototype, method, (_owner, [descriptor], promise) => {
            void promise.then(pipeline => observe(() => rememberPipeline(pipeline, descriptor)), () => {});
        });
    wrap(GPUDevice.prototype, 'createBindGroup', (_owner, [descriptor], group) => {
        groups.set(group, descriptor.entries.filter(entry => views.has(entry.resource))
            .map(entry => ({ binding: entry.binding, ...views.get(entry.resource) })));
    });
    wrap(GPUDevice.prototype, 'createCommandEncoder', (_owner, _args, encoder) => {
        encoders.set(encoder, { passes: [], overflow: 0 });
    });
    const beginPass = (encoder, descriptor, pass, kind) => {
        const parent = encoders.get(encoder);
        if (!parent) return;
        if (parent.passes.length >= limit) { parent.overflow++; return; }
        const value = { kind, label: label(descriptor), ended: false, draws: [],
            depth: views.get(descriptor?.depthStencilAttachment?.view) ?? null,
            colors: (descriptor?.colorAttachments ?? []).filter(Boolean).map(attachment => views.get(attachment.view) ?? null),
            depthLoadOp: descriptor?.depthStencilAttachment?.depthLoadOp ?? null,
            depthStoreOp: descriptor?.depthStencilAttachment?.depthStoreOp ?? null,
            colorOps: (descriptor?.colorAttachments ?? []).filter(Boolean)
                .map(attachment => ({ loadOp: attachment.loadOp, storeOp: attachment.storeOp })),
            pipeline: null, group1: null, group2: null };
        parent.passes.push(value);
        passes.set(pass, value);
    };
    wrap(GPUCommandEncoder.prototype, 'beginRenderPass', (encoder, [descriptor], pass) => beginPass(encoder, descriptor, pass, 'raster'));
    wrap(GPUCommandEncoder.prototype, 'beginComputePass', (encoder, [descriptor], pass) => beginPass(encoder, descriptor, pass, 'compute'));
    for (const prototype of [GPURenderPassEncoder.prototype, GPUComputePassEncoder.prototype]) {
        wrap(prototype, 'setPipeline', (pass, [pipeline]) => {
            const value = passes.get(pass);
            if (value) value.pipeline = pipelines.get(pipeline) ?? null;
        });
        wrap(prototype, 'setBindGroup', (pass, [index, group]) => {
            const value = passes.get(pass);
            if (value && index === 1) value.group1 = groups.get(group) ?? null;
            if (value && index === 2) value.group2 = groups.get(group) ?? null;
        });
        wrap(prototype, 'end', pass => { const value = passes.get(pass); if (value) value.ended = true; });
    }
    const draw = (pass, args, operation) => {
        const value = passes.get(pass);
        if (!value || !value.pipeline) return;
        const name = value.pipeline.label;
        // Ordinary native stages are counted by the shared observer. Retain only this shadow family's calls.
        if (value.kind === 'raster' && !['engine-shadow-depth', 'engine-point-shadow-depth'].includes(name) ||
            value.kind === 'compute' && !['engine-advanced-shade-native-depth', 'engine-advanced-shade-native',
                'engine-advanced-shade-native-no-modifiers'].includes(name)) return;
        const indirect = operation.includes('Indirect');
        if (value.draws.length >= limit) { evidence.overflow++; return; }
        value.draws.push({ pipelineId: value.pipeline.id, operation,
            count: indirect ? null : args[0], instances: indirect ? null : args[1] ?? 1,
            group1: value.group1, group2: value.group2 });
    };
    for (const operation of ['draw', 'drawIndexed', 'drawIndirect', 'drawIndexedIndirect'])
        wrap(GPURenderPassEncoder.prototype, operation, (pass, args) => draw(pass, args, operation));
    for (const operation of ['dispatchWorkgroups', 'dispatchWorkgroupsIndirect'])
        wrap(GPUComputePassEncoder.prototype, operation, (pass, args) => draw(pass, args, operation));
    wrap(GPUCommandEncoder.prototype, 'finish', (encoder, _args, command) => {
        const value = encoders.get(encoder);
        if (value) commands.set(command, value);
    });
    const increment = (list, key, value, serial) => {
        let current = list.find(item => item.key === key);
        if (!current) current = record(list, { key, ...value, calls: 0, firstSerial: serial, lastSerial: serial });
        if (current) { current.calls++; current.lastSerial = serial; }
    };
    wrap(GPUQueue.prototype, 'submit', (owner, [buffers]) => {
        queue = owner;
        const serial = ++evidence.queueSubmits;
        for (const buffer of buffers) {
            const command = commands.get(buffer);
            if (!command) continue;
            evidence.overflow += command.overflow;
            for (const pass of command.passes) {
                if (!pass.ended) continue;
                const targetIds = [pass.depth, ...pass.colors].filter(Boolean).map(view => view.textureId);
                if (pass.kind === 'raster' && targetIds.some(id => {
                    const texture = evidence.textures.find(texture => texture.id === id);
                    return /Point shadow|Directional.*Shadow|Shadow.*Directional/i.test(texture?.label ?? '');
                })) {
                    const value = { passLabel: pass.label, depth: pass.depth, colors: pass.colors,
                        depthLoadOp: pass.depthLoadOp, depthStoreOp: pass.depthStoreOp, colorOps: pass.colorOps,
                        positiveDrawCalls: pass.draws.filter(call => call.count > 0 && call.instances > 0).length };
                    increment(evidence.shadowPasses, JSON.stringify(value), value, serial);
                }
                for (const call of pass.draws) {
                    const { group1, group2, ...draw } = call;
                    if (pass.kind === 'compute') {
                        const value = { ...draw, resources: group1 ?? [], outputs: group2 ?? [] };
                        increment(evidence.consumers, JSON.stringify(value), value, serial);
                    } else {
                        const value = { ...draw, passLabel: pass.label, depth: pass.depth, colors: pass.colors };
                        increment(evidence.submitted, JSON.stringify(value), value, serial);
                    }
                }
            }
        }
    });
    globalThis.advancedShadowSnapshot = () => structuredClone(evidence);
    globalThis.advancedShadowComplete = async () => {
        if (!queue) throw new Error('BrowserSmoke.ShadowCompletion: no real queue submission.');
        const serial = evidence.queueSubmits;
        await queue.onSubmittedWorkDone();
        evidence.queueCompletions++;
        evidence.completedSerial = Math.max(evidence.completedSerial, serial);
        return structuredClone(evidence);
    };
}

function nativeBinding(value) {
    const entry = { binding: value.binding, visibility: 4 };
    if (['read-only-storage', 'storage', 'uniform'].includes(value.kind))
        entry.buffer = { type: value.kind, hasDynamicOffset: value.dynamic, minBindingSize: value.bytes };
    else if (value.kind.endsWith('-sampler')) entry.sampler = { type: value.kind.replace('-sampler', '') };
    else if (value.kind.startsWith('storage-texture-')) {
        const match = /^storage-texture-(2d|2d-array)-write-(.+)$/.exec(value.kind);
        assert(match, 'BrowserSmoke.ShadowNativeLayout: unsupported storage texture.');
        entry.storageTexture = { viewDimension: match[1], format: match[2], access: 'write-only' };
    } else {
        const match = /^texture-(?:(depth-multisampled-2d|depth-2d)|(multisampled-2d|2d-array|cube|2d)-(uint|sint|unfilterable-float|float))$/.exec(value.kind);
        assert(match, 'BrowserSmoke.ShadowNativeLayout: unsupported sampled texture.');
        const dimension = match[1] ?? match[2];
        entry.texture = { viewDimension: dimension.includes('array') ? '2d-array' : dimension.includes('cube') ? 'cube' : '2d',
            sampleType: match[1] ? 'depth' : match[3], multisampled: dimension.includes('multisampled') };
    }
    return entry;
}

function assertNativeIdentity(snapshot, programs, state) {
    const labels = Object.keys(snapshot.submissions.compute).filter(label =>
        /^engine-advanced-shade-(?:native|surface-exports|uber-native)/.test(label) && snapshot.submissions.compute[label] > 0);
    assert(labels.length === 1 && (state === 'on' ? labels[0] === 'engine-advanced-shade-native-depth' :
        ['engine-advanced-shade-native', 'engine-advanced-shade-native-no-modifiers'].includes(labels[0])),
    `BrowserSmoke.ShadowNativeSelection: ${state} selected ${JSON.stringify(labels)}.`);
    const artifact = programs[labels[0].replace('engine-advanced-', '')];
    const descriptor = artifact.descriptor;
    const records = snapshot.nativeCompile?.records.filter(record => record.recipe?.pipeline?.label === labels[0]) ?? [];
    assert(snapshot.nativeCompile?.captureErrors.length === 0 && records.length === 1 &&
        records[0].status === 'fulfilled' && records[0].recipeStatus === 'ready',
    'BrowserSmoke.ShadowNativeIdentity: selected program needs one complete native compile recipe.');
    const recipe = records[0].recipe;
    const groups = Array.from({ length: 3 }, () => ({ label: descriptor.name, entries: [] }));
    for (const binding of descriptor.layout.bindings) groups[binding.group].entries.push(nativeBinding(binding));
    assert(recipe.module.sha256 === artifact.wgslSha256 && recipe.module.byteLength === artifact.codeBytes &&
        equal(recipe.module.descriptor, { label: descriptor.name }) && equal(recipe.pipeline, { label: descriptor.name }) &&
        equal(recipe.compute, { entryPoint: descriptor.entryPoints.compute }) &&
        equal(recipe.layout, { bindGroupLayouts: groups }),
    'BrowserSmoke.ShadowNativeIdentity: full captured native descriptor differs from the published program.');
    if (state === 'on') assert(descriptor.defines.includes('XR_ADV_STANDALONE_SHADOW_SCHEMA_VERSION=1') &&
        descriptor.defines.includes('XR_ADV_DEPTH_COMPARISON_BANK=1'),
    'BrowserSmoke.ShadowNativeIdentity: selected program lacks the full standalone depth-shadow family.');
    return labels[0];
}

function assertShadowEvidence(snapshot, programs, state, extent, afterSerial = 0) {
    const { gpu, submissions } = snapshot;
    assert(gpu && gpu.captureErrors.length === 0 && gpu.deviceErrors.length === 0 && gpu.overflow === 0 &&
        gpu.completedSerial > afterSerial && gpu.queueCompletions > 0 && submissions.readMappings === 0 && !snapshot.failure,
    'BrowserSmoke.ShadowSubmission: require completed real work, bounded capture, and zero GPU READ maps.');
    for (const pass of ['depth-pyramid', 'gtao', 'shade-classify', 'shade-finalize', 'shade-background'])
        assert(submissions.compute[`engine-advanced-${pass}`] > 0,
            `BrowserSmoke.ShadowStageMissing: no native ${pass} dispatch.`);
    for (const pass of ['visibility-pull', 'present'])
        assert(submissions.raster[`engine-advanced-${pass}`] > 0,
            `BrowserSmoke.ShadowStageMissing: no native ${pass} draw.`);
    const selectedNative = assertNativeIdentity(snapshot, programs, state);
    const texture = id => gpu.textures.find(value => value.id === id);
    const completed = value => value.firstSerial <= gpu.completedSerial && value.firstSerial > afterSerial;
    // GPU-written indirect dimensions remain opaque. Match the real submitted operation and its output.
    const consumers = gpu.consumers.filter(value => completed(value) &&
        (value.operation === 'dispatchWorkgroupsIndirect' || value.operation === 'dispatchWorkgroups' && value.count > 0) &&
        gpu.pipelines.find(pipeline => pipeline.id === value.pipelineId)?.label === selectedNative &&
        value.outputs.some(view => view.binding === 0 && texture(view.textureId)?.width === extent[0] &&
            texture(view.textureId)?.height === extent[1]));
    assert(consumers.length > 0,
        'BrowserSmoke.ShadowOutput: no completed native consumer for the current canvas extent.');
    if (state === 'off') {
        assert(gpu.submitted.length === 0 && gpu.shadowPasses.length === 0,
            'BrowserSmoke.ShadowOff: disabled lights submitted real shadow passes.');
        return { selectedNative, completedSerial: gpu.completedSerial };
    }
    const directional = gpu.textures.filter(value => /^DirectionalShadow\..*\.PrimaryRasterDepth$/.test(value.label));
    const radial = gpu.textures.filter(value => value.label === 'Point shadow radial distance');
    const depth = gpu.textures.filter(value => value.label === 'Point shadow face depth');
    assert(directional.length > 0 && radial.length > 0 && depth.length > 0 &&
        [...directional, ...radial, ...depth].every(value => value.width === 256 && value.height === 256 && value.sampleCount === 1) &&
        directional.every(value => value.layers === 1 && value.format === 'depth24plus') &&
        radial.every(value => value.layers === 6 && value.format === 'r16float') &&
        depth.every(value => value.layers === 6 && value.format === 'depth24plus'),
    'BrowserSmoke.ShadowDimensions: expected real 256² directional depth and six-layer point radial/depth targets.');
    const rasterPipeline = (call, artifact) => {
        const pipeline = gpu.pipelines.find(value => value.id === call.pipelineId);
        const module = stage => gpu.modules.find(value => value.id === stage?.moduleId);
        return pipeline?.label === artifact.descriptor.name && pipeline.depthStencil?.depthWriteEnabled === true &&
            module(pipeline.vertex)?.sha256 === artifact.wgslSha256 &&
            module(pipeline.vertex)?.codeBytes === artifact.codeBytes &&
            pipeline.vertex.entryPoint === artifact.descriptor.entryPoints.vertex &&
            (!artifact.descriptor.entryPoints.fragment ? !pipeline.fragment :
                module(pipeline.fragment)?.sha256 === artifact.wgslSha256 &&
                pipeline.fragment.entryPoint === artifact.descriptor.entryPoints.fragment &&
                pipeline.targets?.[0]?.format === 'r16float');
    };
    const positiveDraws = gpu.submitted.filter(value => value.firstSerial <= gpu.completedSerial &&
        ['draw', 'drawIndexed'].includes(value.operation) && value.count > 0 && value.instances > 0);
    // A static shadow may be reused after resize. Its producer must have completed before this consumer.
    const matches = consumers.map(consumer => {
        const directionalView = consumer.resources.find(view => view.binding === 22);
        const pointView = consumer.resources.find(view => view.binding === 24);
        if (!directionalView || !pointView || directionalView.dimension !== '2d' ||
            pointView.dimension !== 'cube' || pointView.layers !== 6 ||
            !directional.some(value => value.id === directionalView.textureId) ||
            !radial.some(value => value.id === pointView.textureId)) return null;
        const directionalDraws = positiveDraws.filter(value => rasterPipeline(value, programs.directional) &&
            value.depth?.textureId === directionalView.textureId && value.depth.layer === 0 &&
            value.firstSerial <= consumer.firstSerial);
        const pointDraws = positiveDraws.filter(value => rasterPipeline(value, programs.point) &&
            value.colors.some(view => view?.textureId === pointView.textureId) &&
            depth.some(target => target.id === value.depth?.textureId) && value.firstSerial <= consumer.firstSerial);
        const faces = gpu.shadowPasses.filter(value => value.firstSerial <= gpu.completedSerial &&
            value.firstSerial <= consumer.firstSerial && value.depthLoadOp === 'clear' && value.depthStoreOp === 'store' &&
            depth.some(target => target.id === value.depth?.textureId) && value.depth.layers === 1 &&
            value.colors.some((view, index) => view?.textureId === pointView.textureId && view.layers === 1 &&
                view.layer === value.depth.layer && value.colorOps[index]?.loadOp === 'clear' &&
                value.colorOps[index]?.storeOp === 'store'));
        const layers = [...new Set(faces.map(value => value.depth.layer))].sort();
        return directionalDraws.length && pointDraws.length && equal(layers, [0, 1, 2, 3, 4, 5])
            ? { directionalTextureId: directionalView.textureId, pointRadialTextureId: pointView.textureId,
                directionalCasterDraws: directionalDraws, pointCasterDraws: pointDraws, pointFaces: layers,
                consumer, completedSerial: gpu.completedSerial } : null;
    }).filter(Boolean);
    assert(matches.length > 0,
        'BrowserSmoke.ShadowProducer: require real directional and point caster draws, all six clear/store faces, and matching consumed textures.');
    return { selectedNative, production: matches, completedSerial: gpu.completedSerial };
}

/** Projection constants come from the two SHA-pinned worlds, not from sampled successful pixels. */
export function shadowReceiverRegions(width, height, canvasWidth, canvasHeight) {
    const scaleY = height / (2 * Math.tan(48 * Math.PI / 360));
    const scaleX = width / (2 * Math.tan(48 * Math.PI / 360) * (canvasWidth / canvasHeight));
    const project = (x, y, z) => [width / 2 + x * scaleX / (6.5 - z), height / 2 - y * scaleY / (6.5 - z)];
    const box = (scale, translation, margin) => {
        const topLeft = project(-1.95 * scale + translation[0], 1 * scale + translation[1], translation[2]);
        const bottomRight = project(-0.75 * scale + translation[0], -1 * scale + translation[1], translation[2]);
        return { left: topLeft[0] - margin, top: topLeft[1] - margin,
            right: bottomRight[0] + margin, bottom: bottomRight[1] + margin };
    };
    // Four screenshot pixels exclude raster edges, AO fringes, and CSS outline rounding.
    return { receiver: box(1, [0, 0, 0], -4),
        occluders: [box(0.35, [-0.8775, 0, 1], 4), box(0.35, [0.23916667, 0.5, 1], 4)],
        surfaces: [box(1, [0, 0, 0], 0), box(0.35, [-0.8775, 0, 1], 0), box(0.35, [0.23916667, 0.5, 1], 0)] };
}

/** Checks the full projected scene and its uniform exterior. No successful sample pixels define this mask. */
export function inspectShadowCaptureImage({ data, width, height }, regions) {
    const inside = (x, y, box, margin = 0) => x >= box.left - margin && x < box.right + margin &&
        y >= box.top - margin && y < box.bottom + margin;
    const surfaces = regions.surfaces;
    const expected = { left: Math.min(...surfaces.map(box => box.left)), top: Math.min(...surfaces.map(box => box.top)),
        right: Math.max(...surfaces.map(box => box.right)), bottom: Math.max(...surfaces.map(box => box.bottom)) };
    const histogram = Array.from({ length: 3 }, () => new Uint32Array(256));
    let exteriorPixels = 0;
    // Exclude four edge pixels for fractional CSS outlines. The remaining exterior is authored empty space.
    for (let y = 4; y < height - 4; y++) for (let x = 4; x < width - 4; x++) {
        if (surfaces.some(box => inside(x + 0.5, y + 0.5, box, 4))) continue;
        exteriorPixels++;
        const offset = 4 * (y * width + x);
        for (let channel = 0; channel < 3; channel++) histogram[channel][data[offset + channel]]++;
    }
    // Use the exterior median so this check does not assume an exact tonemapped clear-color value.
    const background = histogram.map(values => {
        let sum = 0;
        for (let value = 0; value < values.length; value++) {
            sum += values[value];
            if (sum >= exteriorPixels / 2) return value;
        }
        return 0;
    });
    const observed = { left: width, top: height, right: 0, bottom: 0 };
    let foregroundPixels = 0, exteriorMismatchPixels = 0;
    for (let y = 4; y < height - 4; y++) for (let x = 4; x < width - 4; x++) {
        const offset = 4 * (y * width + x);
        if (Math.max(Math.abs(background[0] - data[offset]), Math.abs(background[1] - data[offset + 1]),
            Math.abs(background[2] - data[offset + 2])) <= 8) continue;
        foregroundPixels++;
        observed.left = Math.min(observed.left, x);
        observed.top = Math.min(observed.top, y);
        observed.right = Math.max(observed.right, x + 1);
        observed.bottom = Math.max(observed.bottom, y + 1);
        if (!surfaces.some(box => inside(x + 0.5, y + 0.5, box, 4))) exteriorMismatchPixels++;
    }
    const maximumEdgeError = Math.max(...Object.keys(expected).map(edge => Math.abs(expected[edge] - observed[edge])));
    const exteriorMismatchFraction = exteriorPixels ? exteriorMismatchPixels / exteriorPixels : 1;
    return { accepted: exteriorPixels > 1000 && foregroundPixels > 100 &&
            exteriorMismatchFraction <= 0.001 && maximumEdgeError <= 3,
        expected, observed, background, foregroundPixels, exteriorPixels, exteriorMismatchPixels,
        exteriorMismatchFraction, maximumEdgeError, edgeTolerancePixels: 3, backgroundToleranceRgb: 8 };
}

async function captureShadowSurface(page, config, name, failure, reference = null) {
    const budget = Math.min(config.timeout, 10000);
    const startedAt = Date.now();
    const deadline = startedAt + budget;
    const canvas = page.locator('#input-surface');
    const unrun = () => ({ status: 'unrun', attempt: null, value: null });
    const phases = ['scroll-into-view', 'geometry-before', 'canvas-screenshot', 'geometry-after',
        'inspect-pixels', 'inspect-alignment', 'read-status', 'read-failure-status', 'next-animation-frame'];
    const diagnostics = { schema: 1, name, budgetMs: budget, startedAtUtcMs: startedAt,
        elapsedMs: null, attempt: 0, activePhase: null,
        phases: Object.fromEntries(phases.map(phase => [phase, { attempt: null, phase, status: 'unrun',
            startedAtMs: null, elapsedMs: null, remainingMs: null }])),
        steps: [], omittedSteps: 0, before: unrun(), after: unrun(), clip: unrun(), latestSummary: unrun() };
    const withCaptureEvidence = error => {
        const result = error instanceof Error ? error : new Error(String(error));
        diagnostics.elapsedMs = Date.now() - startedAt;
        // Copy before browser evidence collection or cleanup can run. Pending work cannot change this record.
        result.shadowCapture = structuredClone(diagnostics);
        return result;
    };
    const step = async (phase, action) => {
        const stepStartedAt = Date.now();
        const remaining = deadline - stepStartedAt;
        const record = { attempt: diagnostics.attempt, phase, status: 'pending',
            startedAtMs: stepStartedAt - startedAt, elapsedMs: null, remainingMs: Math.max(0, remaining) };
        diagnostics.activePhase = phase;
        diagnostics.phases[phase] = record;
        if (diagnostics.steps.length < 64) diagnostics.steps.push(record);
        else diagnostics.omittedSteps++;
        if (remaining <= 0) {
            record.status = 'not-started-budget';
            record.elapsedMs = 0;
            throw withCaptureEvidence(new Error(`${failure} Canvas capture exceeded its ${budget} ms budget.`));
        }
        let timer;
        let timedOut = false;
        try {
            const result = await Promise.race([action(), new Promise((_, reject) => {
                timer = setTimeout(() => {
                    timedOut = true;
                    reject(new Error(`${failure} Canvas capture exceeded its ${budget} ms budget.`));
                }, remaining);
            })]);
            record.status = 'fulfilled';
            record.elapsedMs = Date.now() - stepStartedAt;
            diagnostics.activePhase = null;
            return result;
        } catch (error) {
            record.status = timedOut ? 'timed-out' : 'rejected';
            record.elapsedMs = Date.now() - stepStartedAt;
            throw withCaptureEvidence(error);
        } finally { clearTimeout(timer); }
    };
    const geometry = () => canvas.evaluate(element => {
        const bounds = element.getBoundingClientRect();
        return { x: bounds.x, y: bounds.y, width: bounds.width, height: bounds.height,
            bitmapWidth: element.width, bitmapHeight: element.height, devicePixelRatio, scrollX, scrollY,
            viewportWidth: innerWidth, viewportHeight: innerHeight,
            viewportScale: visualViewport?.scale ?? null,
            viewportOffsetX: visualViewport?.offsetLeft ?? null, viewportOffsetY: visualViewport?.offsetTop ?? null };
    });
    let latest;
    do {
        diagnostics.attempt++;
        diagnostics.before = unrun();
        diagnostics.after = unrun();
        diagnostics.clip = unrun();
        await step('scroll-into-view', () => canvas.scrollIntoViewIfNeeded({ timeout: Math.max(1, deadline - Date.now()) }));
        const before = await step('geometry-before', geometry);
        diagnostics.before = { status: 'available', attempt: diagnostics.attempt, value: before };
        const viewport = page.viewportSize();
        // Match Playwright 1.63's element screenshot rounding in document coordinates.
        // Page screenshot clips use viewport coordinates and do not repeat element stability waits.
        const left = Math.floor(before.x + before.scrollX + 1e-3);
        const top = Math.floor(before.y + before.scrollY + 1e-3);
        const clip = { x: left - before.scrollX, y: top - before.scrollY,
            width: Math.ceil(before.x + before.scrollX + before.width - 1e-3) - left,
            height: Math.ceil(before.y + before.scrollY + before.height - 1e-3) - top };
        diagnostics.clip = { status: 'available', attempt: diagnostics.attempt, value: { ...clip, viewport } };
        const image = await step('canvas-screenshot', () => {
            assert(viewport && Object.values(before).every(Number.isFinite) &&
                before.devicePixelRatio === 1 && before.viewportScale === 1 &&
                before.viewportOffsetX === 0 && before.viewportOffsetY === 0 &&
                before.viewportWidth === viewport.width && before.viewportHeight === viewport.height &&
                before.width > 0 && before.height > 0 && clip.width > 0 && clip.height > 0 &&
                clip.x >= 0 && clip.y >= 0 && clip.x + clip.width <= viewport.width && clip.y + clip.height <= viewport.height,
            'BrowserSmoke.ShadowCaptureBounds: the complete canvas must fit the unscaled viewport without clipping.');
            return page.screenshot({ path: path.join(config.output, `${name}.png`), clip, fullPage: false,
                timeout: Math.max(1, deadline - Date.now()) });
        });
        const after = await step('geometry-after', geometry);
        diagnostics.after = { status: 'available', attempt: diagnostics.attempt, value: after };
        const pixels = await step('inspect-pixels', () => inspectCanvas(page, null, image));
        const regions = shadowReceiverRegions(pixels.width, pixels.height, before.bitmapWidth, before.bitmapHeight);
        const alignment = await step('inspect-alignment', () => page.evaluate(async ({ encoded, regions }) => {
            const bytes = Uint8Array.from(atob(encoded), character => character.charCodeAt(0));
            const bitmap = await createImageBitmap(new Blob([bytes], { type: 'image/png' }));
            const surface = new OffscreenCanvas(bitmap.width, bitmap.height);
            const context = surface.getContext('2d', { willReadFrequently: true });
            context.drawImage(bitmap, 0, 0);
            bitmap.close();
            return globalThis.advancedShadowInspectCaptureImage(context.getImageData(0, 0, surface.width, surface.height), regions);
        }, { encoded: image.toString('base64'), regions }));
        const stable = equal(before, after) && before.devicePixelRatio === 1 &&
            equal(viewport, page.viewportSize()) && pixels.width === clip.width && pixels.height === clip.height &&
            Math.abs(pixels.width - before.width) <= 2 && Math.abs(pixels.height - before.height) <= 2;
        const matchesReference = !reference || reference.pixels.width === pixels.width &&
            reference.pixels.height === pixels.height &&
            equal(reference.pixels.captureValidity.alignment.observed, alignment.observed);
        latest = { ...pixels, captureValidity: { accepted: stable && alignment.accepted && matchesReference, stable, matchesReference,
            before, after, clip, viewport, alignment } };
        diagnostics.latestSummary = { status: 'available', attempt: diagnostics.attempt, value: latest };
        if (hasSurface(pixels) && latest.captureValidity.accepted && Date.now() <= deadline) return { image, pixels: latest };
        if (await step('read-status', () => page.locator('#status').getAttribute('data-state')) === 'failed')
            throw withCaptureEvidence(new Error(`BrowserSmoke.EngineFrameFailed: ${await step('read-failure-status',
                () => page.locator('#status').textContent())}`));
        // Retry at the next browser frame, within the same capture budget. Do not add a fixed startup delay.
        const remaining = deadline - Date.now();
        if (remaining > 0) await step('next-animation-frame',
            () => page.evaluate(() => new Promise(resolve => requestAnimationFrame(resolve))));
    } while (Date.now() < deadline);
    throw withCaptureEvidence(new Error(`${failure} Last canvas summary: ${JSON.stringify(latest)}`));
}

async function compareReceiver(page, off, on, canvasExtent) {
    assert(off.pixels.width === on.pixels.width && off.pixels.height === on.pixels.height,
        'BrowserSmoke.ShadowComparison: ON/OFF capture dimensions differ.');
    assert(off.pixels.captureValidity?.accepted && on.pixels.captureValidity?.accepted &&
        equal(off.pixels.captureValidity.alignment.observed, on.pixels.captureValidity.alignment.observed) &&
        [off, on].every(value => value.pixels.captureValidity.before.bitmapWidth === canvasExtent[0] &&
            value.pixels.captureValidity.before.bitmapHeight === canvasExtent[1]),
    'BrowserSmoke.ShadowComparison: ON/OFF images must align with the genuine canvas and pinned scene projection.');
    const regions = shadowReceiverRegions(on.pixels.width, on.pixels.height, ...canvasExtent);
    return page.evaluate(async ({ offPng, onPng, regions }) => {
        const decode = async value => {
            const bytes = Uint8Array.from(atob(value), character => character.charCodeAt(0));
            const bitmap = await createImageBitmap(new Blob([bytes], { type: 'image/png' }));
            const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
            const context = canvas.getContext('2d', { willReadFrequently: true });
            context.drawImage(bitmap, 0, 0);
            bitmap.close();
            return context.getImageData(0, 0, canvas.width, canvas.height);
        };
        const [baseline, current] = await Promise.all([decode(offPng), decode(onPng)]);
        const inside = (x, y, box) => x >= box.left && x < box.right && y >= box.top && y < box.bottom;
        let receiverPixels = 0, darkenedPixels = 0, brightenedPixels = 0, difference = 0;
        let offColorful = 0, onColorful = 0;
        const colorful = (data, offset) => Math.max(...data.slice(offset, offset + 3)) > 70 &&
            Math.max(...data.slice(offset, offset + 3)) - Math.min(...data.slice(offset, offset + 3)) > 35;
        for (let y = 0; y < current.height; y++) for (let x = 0; x < current.width; x++) {
            if (!inside(x + 0.5, y + 0.5, regions.receiver) ||
                regions.occluders.some(box => inside(x + 0.5, y + 0.5, box))) continue;
            const offset = 4 * (y * current.width + x);
            const delta = (baseline.data[offset] + baseline.data[offset + 1] + baseline.data[offset + 2] -
                current.data[offset] - current.data[offset + 1] - current.data[offset + 2]) / 3;
            receiverPixels++;
            difference += delta;
            if (delta > 5) darkenedPixels++;
            if (delta < -5) brightenedPixels++;
            if (colorful(baseline.data, offset)) offColorful++;
            if (colorful(current.data, offset)) onColorful++;
        }
        return { regions, receiverPixels, darkenedPixels, brightenedPixels, offColorful, onColorful,
            meanRgbDarkening: receiverPixels ? difference / receiverPixels : null,
            darkenedFraction: receiverPixels ? darkenedPixels / receiverPixels : 0, thresholdRgb: 5 };
    }, { offPng: off.image.toString('base64'), onPng: on.image.toString('base64'), regions });
}

function assertReceiverDifference(comparison, label) {
    assert(comparison.receiverPixels > 1000 && comparison.offColorful > 100 && comparison.onColorful > 100 &&
        comparison.darkenedPixels >= 100 && comparison.darkenedFraction >= 0.01 && comparison.meanRgbDarkening > 0,
    `BrowserSmoke.ShadowReceiverDifference: ${label} needs an objective shadow effect outside both occluders: ${JSON.stringify(comparison)}`);
}

async function snapshot(page) {
    return page.evaluate(async () => {
        let timer;
        try {
            const gpu = await Promise.race([globalThis.advancedShadowComplete(), new Promise((_, reject) => {
                timer = setTimeout(() => reject(new Error('BrowserSmoke.ShadowCompletion: GPU queue did not complete within 10000 ms.')), 10000);
            })]);
            return { gpu, failure: globalThis.advancedCanvasFailureEvidence,
                nativeCompile: globalThis.advancedNativeCompileSnapshot(),
                submissions: globalThis.advancedSubmissionSnapshot() };
        } finally { clearTimeout(timer); }
    });
}

/** Reads fixed delivery counters after failure without extending a blocked browser evaluation. */
async function shadowDeliverySnapshot(page, expectedPage) {
    const deadline = Date.now() + 1000;
    const unavailable = { status: 'unavailable', reason: 'page-evaluation-failed', state: null,
        verifiedAssets: null, essentialVerifiedAssets: null, essentialAssets: null, failedReads: null,
        cancelledReads: null, activeRequests: null, queuedReads: null, retainedAssets: null };
    const timeout = { ...unavailable, reason: 'counter-read-timeout' };
    let timer;
    try {
        const result = await Promise.race([
            page.evaluate(async ({ expectedPage, deadline, unavailable }) => {
                const absent = reason => ({ ...unavailable, reason });
                if (location.href !== expectedPage) return absent('unexpected-document');
                if (document.querySelector('#status')?.dataset.state !== 'running')
                    return absent('running-module-unproven');
                if (Date.now() >= deadline) return absent('counter-read-timeout');
                try {
                    // A running player has evaluated engine-runtime.js and its static engine-assets.js import.
                    // Resolve the same module-map key under this publish mount; do not request another module.
                    const module = await import(new URL('./engine-assets.js', location.href).href);
                    if (Date.now() >= deadline) return absent('counter-read-timeout');
                    const progress = JSON.parse(module.engineAssetImports.currentProgress());
                    if (!progress) return absent('no-current-source');
                    const counters = ['verifiedAssets', 'essentialVerifiedAssets', 'essentialAssets', 'failedReads',
                        'cancelledReads', 'activeRequests', 'queuedReads', 'retainedAssets'];
                    if (!['opening', 'ready'].includes(progress.state) || counters.some(key =>
                        !Number.isSafeInteger(progress[key]) || progress[key] < 0))
                        return absent('invalid-counters');
                    const delivery = { ...unavailable, status: 'available', reason: null, state: progress.state };
                    for (const key of counters) delivery[key] = progress[key];
                    return delivery;
                } catch { return absent('counter-read-failed'); }
            }, { expectedPage, deadline, unavailable }).catch(() => unavailable),
            new Promise(resolve => {
                timer = setTimeout(() => resolve(timeout), Math.max(0, deadline - Date.now()));
            }),
        ]);
        return Date.now() <= deadline ? result : timeout;
    } finally { clearTimeout(timer); }
}

async function start(page, origin, mount) {
    await page.addInitScript({ content: `globalThis.advancedShadowInspectCaptureImage = (${inspectShadowCaptureImage.toString()});` });
    await page.addInitScript(installNativeCompileCapture, ['shade-native-depth']);
    await page.addInitScript(installAdvancedSubmissionObservation);
    await page.addInitScript(installShadowGpuObservation);
    await page.goto(`${origin}/${mount}/index.html`, { waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => ['running', 'failed'].includes(document.querySelector('#status')?.dataset.state));
    const detail = await page.locator('#status').textContent();
    assert(await page.locator('#status').getAttribute('data-state') === 'running' &&
        /AdvancedRenderingParity|Advanced Rendering Parity/i.test(detail), `BrowserSmoke.ShadowStartup: ${detail}`);
    return detail;
}

async function resize(page, width) {
    const before = await page.locator('#input-surface').evaluate(element => [element.width, element.height]);
    await page.setViewportSize({ width, height: 780 });
    await page.waitForFunction(([width, height]) => {
        const canvas = document.querySelector('#input-surface');
        return canvas.width !== width || canvas.height !== height;
    }, before);
    return page.locator('#input-surface').evaluate(element => [element.width, element.height]);
}

/** Runs the ordinary published player with both lights ON, against the same saved scene with both lights OFF. */
export async function advancedShadowParityGameCheck(browser, origin, report, config, instrumentedPage, assertNoBrowserErrors) {
    const [onArtifacts, offArtifacts] = await Promise.all([
        publishedShadowArtifacts(config.gamePublish, 'on'), publishedShadowArtifacts(config.baselinePublish, 'off')]);
    assert(onArtifacts.receipt.canonicalCatalogManifestSha256 === offArtifacts.receipt.canonicalCatalogManifestSha256 &&
        onArtifacts.receipt.sharedSourceSha256 === offArtifacts.receipt.sharedSourceSha256 &&
        onArtifacts.receipt.verifiedStartupSemanticSha256 === offArtifacts.receipt.verifiedStartupSemanticSha256 &&
        equal(onArtifacts.programs, offArtifacts.programs),
    'BrowserSmoke.ShadowComparison: ON/OFF must have identical shared source and cooked program identities.');
    report.advancedShadowArtifacts = { on: onArtifacts, off: offArtifacts };
    report.advancedShadowScope = 'Combined directional and point ON/OFF shadow effect on the pinned receiver; real 256px shadow production and full native consumption. Does not establish isolated point-shadow visual occlusion, exact PCSS or numeric material parity, native compile speed, or physical-GPU performance.';
    report.advancedShadowIterations = [];
    report.advancedShadowFailures = [];
    const widths = [860, 940];
    const baseline = {};
    const run = async (state, iteration, action) => {
        const name = `advanced-shadow-${state}-${iteration}`;
        const { page, context, events } = await instrumentedPage(browser, origin, report, name, config);
        let hadFailure = false;
        try {
            const detail = await start(page, origin, state === 'on' ? '__game' : '__baseline');
            await action(page, events, name, detail);
            assertNoBrowserErrors(events);
            assert(!events.some(event => ['crash', 'requestfailed'].includes(event.type)),
                'BrowserSmoke.ShadowBrowserFailure: the page crashed or a request failed.');
        } catch (error) {
            hadFailure = true;
            const evidence = await page.evaluate(() => ({ failure: globalThis.advancedCanvasFailureEvidence ?? null,
                nativeCompile: globalThis.advancedNativeCompileSnapshot?.() ?? null,
                submissions: globalThis.advancedSubmissionSnapshot?.() ?? null,
                gpu: globalThis.advancedShadowSnapshot?.() ?? null })).catch(() => null);
            const delivery = await shadowDeliverySnapshot(page,
                `${origin}/${state === 'on' ? '__game' : '__baseline'}/index.html`);
            report.advancedShadowFailures.push({ state, iteration, error: String(error),
                capture: error?.shadowCapture ?? null, ...evidence, delivery });
            await page.screenshot({ path: path.join(config.output, `${name}-failure.png`), fullPage: true }).catch(() => {});
            throw error;
        } finally {
            if (hadFailure) await context.close().catch(() => {});
            else await context.close();
        }
    };
    await run('off', 0, async (page, _events, name, detail) => {
        baseline.initial = await captureShadowSurface(page, config, `${name}-playing`,
            'BrowserSmoke.ShadowOffSurface: the authored OFF surface did not reach the canvas.');
        baseline.initialExtent = await page.locator('#input-surface').evaluate(element => [element.width, element.height]);
        const initial = await snapshot(page);
        const initialQualification = assertShadowEvidence(initial, offArtifacts.programs, 'off', baseline.initialExtent);
        report.advancedShadowBaseline = { detail, playing: baseline.initial.pixels, initial, initialQualification, resized: [] };
        baseline.resized = [];
        let prior = initial;
        for (let iteration = 0; iteration < widths.length; iteration++) {
            const extent = await resize(page, widths[iteration]);
            const image = await captureShadowSurface(page, config, `${name}-resized-${iteration}`,
                'BrowserSmoke.ShadowOffResize: the authored OFF surface disappeared after resize.');
            const evidence = await snapshot(page);
            const qualification = assertShadowEvidence(evidence, offArtifacts.programs, 'off', extent, prior.gpu.completedSerial);
            baseline.resized.push({ ...image, extent });
            report.advancedShadowBaseline.resized.push({ pixels: image.pixels, extent, evidence, qualification });
            prior = evidence;
        }
    });
    for (let iteration = 0; iteration < 2; iteration++) await run('on', iteration, async (page, _events, name, detail) => {
        const result = { iteration, detail };
        report.advancedShadowIterations.push(result);
        const playing = await captureShadowSurface(page, config, `${name}-playing`,
            'BrowserSmoke.ShadowSurface: the authored ON surface did not reach the canvas.', baseline.initial);
        const before = await page.locator('#input-surface').evaluate(element => [element.width, element.height]);
        assert(equal(before, baseline.initialExtent), 'BrowserSmoke.ShadowComparison: initial ON/OFF canvas extents differ.');
        result.playing = playing.pixels;
        result.initial = await snapshot(page);
        result.initialQualification = assertShadowEvidence(result.initial, onArtifacts.programs, 'on', before);
        result.comparison = await compareReceiver(page, baseline.initial, playing, before);
        assertReceiverDifference(result.comparison, 'initial');
        const after = await resize(page, widths[iteration]);
        assert(equal(after, baseline.resized[iteration].extent), 'BrowserSmoke.ShadowComparison: resized ON/OFF canvas extents differ.');
        const resized = await captureShadowSurface(page, config, `${name}-resized`,
            'BrowserSmoke.ShadowResize: the authored ON surface disappeared after resize.', baseline.resized[iteration]);
        result.resized = resized.pixels;
        result.after = await snapshot(page);
        result.resizeQualification = assertShadowEvidence(result.after, onArtifacts.programs, 'on', after,
            result.initial.gpu.completedSerial);
        result.resizedComparison = await compareReceiver(page, baseline.resized[iteration], resized, after);
        assertReceiverDifference(result.resizedComparison, 'resized');
        result.canvasSizes = { before, after };
    });
}
