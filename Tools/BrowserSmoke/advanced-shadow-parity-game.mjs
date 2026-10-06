import fs from 'node:fs/promises';
import { writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { Worker } from 'node:worker_threads';
import { shadowReceiverRegions } from './shadow-image-math.mjs';
export { shadowReceiverRegions, inspectShadowCaptureImage } from './shadow-image-math.mjs';
import { installAdvancedSubmissionObservation } from './advanced-submission-observer.mjs';
import { installNativeCompileCapture } from './native-compile-isolation.mjs';

function assert(condition, message) { if (!condition) throw new Error(message); }
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const hasSurface = pixels => pixels.colorfulLeft > 100;
const shadowResizeObservers = new WeakMap();
const worldPath = '/game/Worlds/AdvancedRenderingParityWorld.asset';
const fixtureHashes = {
    on: '8f537639aa8260d935fda46c82a3ca4d4a983efbb37bf9c00fb17f8738e96f9b',
    off: 'b3dee24dfce5d5515c69cef8df62738547ae128aba74a673a1ad058f6e712484',
};
const canonical = value => Array.isArray(value) ? value.map(canonical) : value && typeof value === 'object'
    ? Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])])) : value;
const equal = (a, b) => JSON.stringify(canonical(a)) === JSON.stringify(canonical(b));

/** Owns one interruptible decoder and exactly three baseline slots plus the current image. */
function createShadowImageAnalysis(profile) {
    const worker = new Worker(new URL('./shadow-image-worker.mjs', import.meta.url), { workerData: { profile } });
    let pending = null, sequence = 0, stopped = null, failure = null;
    let terminationRequested = false, unexpectedExit = false, exitCode = null;
    const error = code => Object.assign(new Error(`BrowserSmoke.ShadowImageAnalysis: ${code}.`),
        { code: code === 'deadline' ? 'SHADOW_IMAGE_DEADLINE' : 'SHADOW_IMAGE_ANALYSIS' });
    const stop = () => {
        if (!stopped) {
            terminationRequested = true;
            stopped = worker.terminate().then(code =>
                !unexpectedExit && Number.isInteger(code) && exitCode === code, () => false);
        }
        return stopped;
    };
    const reject = code => {
        failure = code;
        if (pending) {
            const current = pending;
            pending = null;
            clearTimeout(current.timer);
            current.reject(error(code));
        }
        void stop();
    };
    worker.on('error', () => { unexpectedExit = true; reject('worker-failed'); });
    worker.on('exit', code => {
        exitCode = code;
        if (!terminationRequested) { unexpectedExit = true; reject('worker-exited'); }
    });
    worker.on('message', message => {
        if (!pending || message.id !== pending.id) return;
        if (Date.now() >= pending.deadline) { reject('deadline'); return; }
        if (message.error) { reject(message.error); return; }
        const current = pending;
        pending = null;
        clearTimeout(current.timer);
        current.resolve(message.result);
    });
    return {
        request(operation, payload, deadline) {
            if (failure || stopped) return Promise.reject(error(failure ?? 'worker-closed'));
            if (pending) return Promise.reject(error('concurrent-analysis'));
            if (Date.now() >= deadline) { reject('deadline'); return Promise.reject(error('deadline')); }
            return new Promise((resolve, rejectRequest) => {
                const current = { id: ++sequence, deadline, resolve, reject: rejectRequest, timer: null };
                pending = current;
                current.timer = setTimeout(() => { if (pending === current) reject('deadline'); },
                    Math.max(0, deadline - Date.now()));
                try { worker.postMessage({ id: current.id, operation, deadline, ...payload }); }
                catch { reject('worker-send-failed'); }
            });
        },
        async close() {
            if (pending) reject('worker-closed');
            let timer;
            try {
                return await Promise.race([stop(), new Promise(resolve => { timer = setTimeout(() => resolve(false), 1000); })]);
            } finally { clearTimeout(timer); }
        },
    };
}

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
    for (const pass of ['shade-native-depth', 'shade-native', 'shade-native-no-modifiers',
        'shade-native-no-decals', 'shade-native-depth-no-decals',
        'shade-native-no-decals-msaa', 'shade-native-depth-no-decals-msaa',
        'shade-surface-exports-no-decals', 'shade-surface-exports-depth-no-decals',
        'shade-surface-exports-no-decals-msaa', 'shade-surface-exports-depth-no-decals-msaa'])
        programs[pass] = await pipeline(pass);
    for (const pass of ['shade-native-no-decals', 'shade-native-depth-no-decals',
        'shade-native-no-decals-msaa', 'shade-native-depth-no-decals-msaa',
        'shade-surface-exports-no-decals', 'shade-surface-exports-depth-no-decals',
        'shade-surface-exports-no-decals-msaa', 'shade-surface-exports-depth-no-decals-msaa']) {
        const optional = programs[pass].descriptor;
        const fullPass = pass.replace('-no-decals', '');
        const full = programs[fullPass]?.descriptor ?? (await pipeline(fullPass)).descriptor;
        const expectedBindings = pass.startsWith('shade-native-') && pass.endsWith('-msaa') ? 40 : 41;
        const sourceDependencies = value => value.dependencies.filter(dependency => !dependency.path.endsWith('.recipe.json'));
        assert(full.name === `engine-advanced-${fullPass}` &&
            full.semanticSchemaIdentity === 'xrengine.engine.compute.v1' &&
            optional.semanticSchemaIdentity === 'xrengine.engine.native-no-decals.v1' &&
            optional.layout.bindings.length === expectedBindings &&
            equal(optional.defines, [...full.defines, 'XR_ADV_NATIVE_DECALS_ABSENT_SCHEMA_VERSION=1']) &&
            optional.defines.includes('XR_ADV_STANDALONE_SHADOW_SCHEMA_VERSION=1') &&
            (!pass.includes('-depth-') || optional.defines.includes('XR_ADV_DEPTH_COMPARISON_BANK=1')) &&
            !optional.defines.includes('XR_ADV_NATIVE_MODIFIERS_ABSENT_SCHEMA_VERSION=1') &&
            ['entryPoints', 'workgroupSize', 'layout', 'requiredFeatures', 'requiredLimits',
                'compilerIdentity', 'sourceLanguage', 'matrixLayout', 'coordinates', 'includes',
                'sourceMap', 'pipeline', 'specialization'].every(field => equal(optional[field], full[field])) &&
            equal(sourceDependencies(optional), sourceDependencies(full)),
        `BrowserSmoke.ShadowArtifact: ${pass} differs from its full native program.`);
    }
    programs.directional = await material('OpaqueShadowDepth', 'depth', 'depth-normal-v1', 'engine-shadow-depth');
    programs.point = await material('OpaquePointShadowDepth', 'point-shadow-depth', 'radial-r16f-v1', 'engine-point-shadow-depth');
    return { receipt, programs };
}

/** Observes real resources, commands and submissions; retains at most 128 unique records per kind. */
export function installShadowGpuObservation() {
    const limit = 128;
    const evidence = { timeOriginMs: performance.timeOrigin,
        textures: [], modules: [], pipelines: [], submitted: [], shadowPasses: [], consumers: [],
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
            value.kind === 'compute' && !['engine-advanced-shade-native-depth',
                'engine-advanced-shade-native-depth-no-decals', 'engine-advanced-shade-native',
                'engine-advanced-shade-native-no-modifiers', 'engine-advanced-shade-native-no-decals'].includes(name)) return;
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
        const now = performance.now();
        let current = list.find(item => item.key === key);
        if (!current) current = record(list, { key, ...value, calls: 0,
            firstSerial: serial, lastSerial: serial, firstAtMs: now, lastAtMs: now });
        if (current) { current.calls++; current.lastSerial = serial; current.lastAtMs = now; }
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
    const snapshot = mode => mode === 'capture' ? {
        captureErrors: [...evidence.captureErrors], deviceErrors: [...evidence.deviceErrors], overflow: evidence.overflow,
        queueSubmits: evidence.queueSubmits, queueCompletions: evidence.queueCompletions, completedSerial: evidence.completedSerial,
        textures: evidence.textures.map(({ id, width, height }) => ({ id, width, height })),
        pipelines: evidence.pipelines.map(({ id, label }) => ({ id, label })),
        consumers: evidence.consumers.map(({ pipelineId, operation, count, firstSerial, outputs }) => ({
            pipelineId, operation, count, firstSerial,
            outputs: outputs.filter(view => view.binding === 0).map(({ binding, textureId }) => ({ binding, textureId })),
        })),
    } : structuredClone(evidence);
    globalThis.advancedShadowSnapshot = snapshot;
    globalThis.advancedShadowComplete = async (mode, deadline = Infinity) => {
        if (Date.now() >= deadline) throw new Error('BrowserSmoke.ShadowCompletion: capture deadline exhausted.');
        if (!queue) throw new Error('BrowserSmoke.ShadowCompletion: no real queue submission.');
        const serial = evidence.queueSubmits;
        await queue.onSubmittedWorkDone();
        evidence.queueCompletions++;
        evidence.completedSerial = Math.max(evidence.completedSerial, serial);
        // A late queue promise can update the existing counters, but must not start another evidence read.
        return Date.now() >= deadline ? null : snapshot(mode);
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

function selectedShadowNative(submissions, state, allowPending = false) {
    const labels = Object.keys(submissions.compute).filter(label =>
        /^engine-advanced-shade-(?:native|surface-exports|uber-native)/.test(label) && submissions.compute[label] > 0);
    if (allowPending && labels.length === 0) return null;
    assert(labels.length === 1 && (state === 'on' ?
        ['engine-advanced-shade-native-depth', 'engine-advanced-shade-native-depth-no-decals'].includes(labels[0]) :
        ['engine-advanced-shade-native', 'engine-advanced-shade-native-no-modifiers',
            'engine-advanced-shade-native-no-decals'].includes(labels[0])),
    `BrowserSmoke.ShadowNativeSelection: ${state} selected ${JSON.stringify(labels)}.`);
    return labels[0];
}

function assertNativeIdentity(snapshot, programs, state) {
    const selectedNative = selectedShadowNative(snapshot.submissions, state);
    const artifact = programs[selectedNative.replace('engine-advanced-', '')];
    const descriptor = artifact.descriptor;
    const records = snapshot.nativeCompile?.records.filter(record => record.recipe?.pipeline?.label === selectedNative) ?? [];
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
    return selectedNative;
}

function matchingShadowConsumers(gpu, selectedNative, extent, afterSerial, throughSerial) {
    const texture = id => gpu.textures.find(value => value.id === id);
    const completed = value => value.firstSerial <= throughSerial && value.firstSerial > afterSerial;
    // GPU-written indirect dimensions remain opaque. Match the real submitted operation and its output.
    return gpu.consumers.filter(value => completed(value) &&
        (value.operation === 'dispatchWorkgroupsIndirect' || value.operation === 'dispatchWorkgroups' && value.count > 0) &&
        gpu.pipelines.find(pipeline => pipeline.id === value.pipelineId)?.label === selectedNative &&
        value.outputs.some(view => view.binding === 0 && texture(view.textureId)?.width === extent[0] &&
            texture(view.textureId)?.height === extent[1]));
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
    const consumers = matchingShadowConsumers(gpu, selectedNative, extent, afterSerial, gpu.completedSerial);
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


async function boundedShadowObservation(action, deadline, unavailable, failed = unavailable) {
    if (Date.now() >= deadline) return unavailable;
    let timer;
    try {
        const value = await Promise.race([action(deadline), new Promise(resolve => {
            timer = setTimeout(() => resolve(unavailable), Math.max(0, deadline - Date.now()));
        })]);
        return Date.now() <= deadline ? value : unavailable;
    } catch { return failed; }
    finally { clearTimeout(timer); }
}

/** Observes the existing host on its next ordinary surface refresh, without invoking that refresh. */
async function installShadowResizeObservation(page, expectedPage) {
    if (shadowResizeObservers.has(page)) return;
    const unavailable = { status: 'unavailable', reason: 'observer-install-unavailable' };
    shadowResizeObservers.set(page, unavailable);
    const result = await boundedShadowObservation(deadline => page.evaluate(async ({ expectedPage, deadline }) => {
        const absent = reason => ({ status: 'unavailable', reason });
        if (location.href !== expectedPage || document.querySelector('#status')?.dataset.state !== 'running')
            return absent('running-module-unproven');
        if (Date.now() >= deadline) return absent('observer-install-deadline');
        // This running player has already evaluated this exact mount-relative module.
        const { EngineCanvasHost } = await import(new URL('./engine-canvas-host.js', location.href).href);
        if (Date.now() >= deadline) return absent('observer-install-deadline');
        const prototype = EngineCanvasHost.prototype;
        const original = prototype.syncSurface;
        let host = null, epoch = null, session = null;
        function observe(...args) {
            try {
                if (prototype.syncSurface === observe) prototype.syncSurface = original;
                host = this; epoch = this.epoch; session = this.session;
            } catch { host = null; }
            return original.apply(this, args);
        }
        const integer = value => Number.isSafeInteger(value) && value >= 0 ? value : null;
        const finite = value => Number.isFinite(value) && value >= 0 ? value : null;
        const counters = (source, keys) => Object.fromEntries(keys.map(key => [key, integer(source?.[key])]));
        const read = checkpointDeadline => {
            if (Date.now() >= checkpointDeadline) return absent('checkpoint-deadline');
            if (!host) return absent('host-unobserved');
            if (host.epoch !== epoch || host.session !== session || host.canvas !== document.querySelector('#input-surface'))
                return absent('host-owner-changed');
            const frame = host.renderer?.commands?.engineFrame;
            const scopes = frame?.scopes;
            if (!frame || !scopes) return absent('frame-state-unavailable');
            const utcMs = Date.now(), atMs = performance.now();
            const stats = frame.getStatistics();
            const active = [];
            let activeCount = 0;
            for (let index = 0; index < Math.min(scopes.receipts.length, 64); index++) {
                const receipt = scopes.receipts[index];
                if (!receipt.active) continue;
                activeCount++;
                if (active.length < 8) active.push({ sequence: integer(receipt.context.sequence),
                    generation: integer(receipt.context.generation), remaining: integer(receipt.remaining),
                    closed: Boolean(receipt.closed), submitted: Boolean(receipt.submitted) });
            }
            const requests = frame.creation.requests;
            const requestSummary = { total: integer(requests.size), sampled: 0, omitted: 0,
                // Index zero retains unknown values without copying their text or descriptors.
                kindCounts: [0, 0, 0, 0, 0, 0, 0, 0], stateCounts: [0, 0, 0, 0, 0] };
            for (const request of requests.values()) {
                if (requestSummary.sampled >= 64) break;
                requestSummary.sampled++;
                requestSummary.kindCounts[Number.isInteger(request.kind) && request.kind >= 1 && request.kind <= 7 ? request.kind : 0]++;
                requestSummary.stateCounts[Number.isInteger(request.state) && request.state >= 1 && request.state <= 4 ? request.state : 0]++;
            }
            requestSummary.omitted = Math.max(0, requests.size - requestSummary.sampled);
            // A queued evaluation must not enter managed code after either observation deadline.
            if (Date.now() >= checkpointDeadline) return absent('checkpoint-deadline');
            let preparationState = null, renderingStatus = null, statusInputTruncated = false;
            const advancedStages = { status: 'unavailable', records: [], omitted: 0, unrecognized: 0 };
            const advancedPreparation = { status: 'unavailable', draws: null, published: null, generation: null,
                deferralPresent: null, unrecognizedDeferral: null };
            try {
                const value = host.engine.GetCanvasPreparationState();
                if ([-1, 0, 1].includes(value)) preparationState = value;
            } catch { /* Missing managed state is explicit in the fixed snapshot. */ }
            if (Date.now() >= checkpointDeadline) return absent('checkpoint-deadline');
            try {
                // The full engine text can contain descriptors and failures. Retain only validated fields.
                const text = String(host.engine.GetCanvasRenderingStatus());
                const raw = text.slice(0, 16384);
                statusInputTruncated = text.length > raw.length;
                const number = expression => {
                    const value = expression.exec(raw)?.[1];
                    return value === undefined ? null : integer(Number(value));
                };
                const profile = /(?:^|; )resource profile=([^;]*)/.exec(raw)?.[1] ?? '';
                const display = /\bdisplay=(\d+)x(\d+)\b/.exec(profile);
                const internal = /\binternal=(\d+)x(\d+)\b/.exec(profile);
                const pending = /(?:^|; )pending draw=(True|False)(?=;|$)/.exec(raw)?.[1];
                const decline = /; pipeline decline=([\s\S]*?); resource failure=/.exec(raw)?.[1];
                const mismatch = decline === undefined ? null : decline.startsWith('Resources do not match the current frame profile.');
                renderingStatus = JSON.stringify({
                    renderer: /^Renderer=(Pending|Ready|Failed|Lost|Disposed|absent)(?=;|$)/.exec(raw)?.[1] ?? 'unavailable',
                    pipeline: /(?:^|; )pipeline=(AdvancedRenderPipeline|DefaultRenderPipeline|absent)(?=;|$)/.exec(raw)?.[1] ?? 'other',
                    renderFrame: number(/(?:^|; )render frame=(\d+)(?=;|$)/),
                    draws: number(/(?:^|; )draws=(\d+)(?=;|$)/),
                    commands: number(/(?:^|; )commands=(\d+)(?=;|$)/),
                    pendingDraw: pending === undefined ? null : pending === 'True',
                    retainedRequests: number(/(?:^|; )retained requests=(\d+)(?=\s|$)/),
                    displayWidth: display ? integer(Number(display[1])) : null,
                    displayHeight: display ? integer(Number(display[2])) : null,
                    internalWidth: internal ? integer(Number(internal[1])) : null,
                    internalHeight: internal ? integer(Number(internal[2])) : null,
                    profileMismatch: mismatch,
                    declinePresent: decline === undefined ? null : decline !== 'none',
                    unrecognizedDecline: decline === undefined ? null : decline !== 'none' && !mismatch,
                }).slice(0, 4096);
                const stages = /; advanced stages=([\s\S]*?); advanced preparation=/.exec(raw)?.[1];
                if (stages === 'none' || stages === 'unobserved') advancedStages.status = stages;
                else if (stages !== undefined) {
                    advancedStages.status = 'available';
                    const stageNames = ['FrameBegin', 'Deformation', 'VisibilityPreparation', 'VisibilityRaster',
                        'DepthPyramidAndLateVisibility', 'DirectionalShadowRaster', 'AmbientOcclusion', 'WorkClassification',
                        'NativeOpaqueShading', 'LatePasses', 'TemporalAndPostProcessing', 'Output', 'UserInterface'];
                    const phaseNames = ['Complete', 'LateCompute', 'LateRaster', 'MultisampleResolve'];
                    const stateNames = ['NotObserved', 'CommandScopeReached', 'BackendEnqueueAccepted',
                        'RejectedPrerequisite', 'RejectedAdmission', 'RejectedCapability', 'BackendEnqueueRejected'];
                    const parts = stages.split(' | ');
                    advancedStages.omitted = Math.max(0, parts.length - 16);
                    for (const part of parts.slice(0, 16)) {
                        const match = /^([^/=\s]+)\/([^/=\s]+)=([^/=\s]+) frame=(\d+) generation=(\d+) reason=([\s\S]*)$/.exec(part);
                        const stage = stageNames.includes(match?.[1]) ? match[1] : 'unknown';
                        const phase = phaseNames.includes(match?.[2]) ? match[2] : 'unknown';
                        const state = stateNames.includes(match?.[3]) ? match[3] : 'unknown';
                        if ([stage, phase, state].includes('unknown')) advancedStages.unrecognized++;
                        const reasonPresent = match ? match[6] !== 'none' : null;
                        advancedStages.records.push({ stage, phase, state,
                            frame: match ? integer(Number(match[4])) : null,
                            generation: match ? integer(Number(match[5])) : null,
                            reasonPresent, unrecognizedReason: reasonPresent });
                    }
                }
                const preparation = /; advanced preparation=([\s\S]*?); program preparation=/.exec(raw)?.[1];
                if (preparation === 'unused') advancedPreparation.status = 'unused';
                else if (preparation !== undefined) {
                    const match = /^draws=(\d+), published=(True|False), generation=(\d+), deferral=([\s\S]*)$/.exec(preparation);
                    if (match) Object.assign(advancedPreparation, { status: 'available',
                        draws: integer(Number(match[1])), published: match[2] === 'True',
                        generation: integer(Number(match[3])),
                        deferralPresent: !['', 'none', 'None'].includes(match[4]),
                        unrecognizedDeferral: !['', 'none', 'None'].includes(match[4]) });
                }
            }
            catch { /* Do not include browser exception text. */ }
            if (Date.now() >= checkpointDeadline) return absent('checkpoint-deadline');
            return { status: 'available', reason: null, source: { utcMs, atMs, timeOriginMs: performance.timeOrigin,
                host: { epoch: integer(epoch), session: integer(session), surfaceGeneration: integer(host.surfaceGeneration),
                    drawable: Boolean(host.drawable), presented: Boolean(host.presented), rendererReady: Boolean(host.rendererReady),
                    failed: Boolean(host.failed), recovering: Boolean(host.recovering),
                    firstFrameSeconds: finite(host.firstFrameSeconds), admissionWaitSeconds: finite(host.admissionWaitSeconds) },
                managed: { preparationState, renderingStatus, statusInputTruncated, advancedStages, advancedPreparation },
                frame: counters(stats, ['bridgeCalls', 'submittedFrames', 'preparationOnlyFrames', 'preparationUploads',
                    'preparationBytes', 'records', 'draws', 'storageUploads', 'commandBytes', 'uniformBytes', 'storageBytes']),
                production: { completedSequence: integer(scopes.completedSequence), lastSequence: integer(frame.lastSequence),
                    scopes: counters(stats.errorScopes, ['capacity', 'pending', 'peakPending', 'started', 'completed',
                        'admissionDeferrals', 'capacityFailures', 'queueCompletionPromises', 'validationErrors',
                        'outOfMemoryErrors', 'rejectedScopes']), activeReceipts: active,
                    omittedActiveReceipts: Math.max(0, activeCount - active.length) }, requests: requestSummary } };
        };
        const cleanup = () => {
            if (prototype.syncSurface === observe) prototype.syncSurface = original;
            if (globalThis.advancedShadowReadResizeState === read) delete globalThis.advancedShadowReadResizeState;
            if (globalThis.advancedShadowRestoreResizeObservation === cleanup) delete globalThis.advancedShadowRestoreResizeObservation;
            host = null;
        };
        prototype.syncSurface = observe;
        globalThis.advancedShadowReadResizeState = read;
        globalThis.advancedShadowRestoreResizeObservation = cleanup;
        return { status: 'available', reason: null };
    }, { expectedPage, deadline }), Date.now() + 500, unavailable);
    shadowResizeObservers.set(page, result);
}

async function readShadowResizeCheckpoint(page, diagnostics, name) {
    const record = diagnostics.resizeCheckpoints?.[name];
    if (!record || record.reason !== 'not-reached') return;
    // Claim before the first await: the midpoint timer and capture loop share this slot.
    record.reason = 'checkpoint-read-pending';
    const now = Date.now(), deadline = diagnostics.startedAtUtcMs + diagnostics.budgetMs;
    record.utcMs = now;
    record.atMs = now - diagnostics.startedAtUtcMs;
    record.remainingMs = Math.max(0, deadline - now);
    record.readBudgetMs = Math.min(200, record.remainingMs);
    if (now >= deadline) { record.reason = 'capture-deadline-exhausted'; record.elapsedMs = 0; return; }
    if (page.isClosed()) { record.reason = 'page-closed'; record.elapsedMs = 0; return; }
    if (shadowResizeObservers.get(page)?.status !== 'available') {
        record.reason = 'host-observer-unavailable'; record.elapsedMs = 0; return;
    }
    const result = await boundedShadowObservation(checkpointDeadline => page.evaluate(checkpointDeadline => {
        if (Date.now() >= checkpointDeadline) return { status: 'unavailable', reason: 'checkpoint-deadline' };
        return globalThis.advancedShadowReadResizeState?.(checkpointDeadline) ??
            { status: 'unavailable', reason: 'host-observer-unavailable' };
    }, checkpointDeadline), Math.min(deadline, now + 200),
    { status: 'unavailable', reason: 'checkpoint-read-timeout' },
    { status: 'unavailable', reason: 'checkpoint-read-failed' });
    // Capture cleanup can seal an in-flight slot before its bounded read settles.
    if (record.reason !== 'checkpoint-read-pending') return;
    record.status = result.status;
    record.reason = result.reason;
    record.source = result.source ?? null;
    record.elapsedMs = Date.now() - now;
}

async function cleanupShadowResizeObservation(page) {
    if (!shadowResizeObservers.has(page)) return;
    await boundedShadowObservation(deadline => page.evaluate(deadline => {
        if (Date.now() < deadline) globalThis.advancedShadowRestoreResizeObservation?.();
    }, deadline), Date.now() + 200, null);
    shadowResizeObservers.delete(page);
}

/** Waits for one ordinary frame and releases its timer and pending frame on every exit. */
async function waitForShadowCaptureFrame(deadline) {
    const expired = () => new Error('BrowserSmoke.ShadowCaptureDeadline: capture deadline exhausted.');
    if (Date.now() >= deadline) throw expired();
    let frame, timer;
    try {
        await new Promise((resolve, reject) => {
            timer = setTimeout(() => reject(expired()), Math.max(0, deadline - Date.now()));
            frame = requestAnimationFrame(() => Date.now() >= deadline ? reject(expired()) : resolve());
        });
        if (Date.now() >= deadline) throw expired();
    } finally {
        clearTimeout(timer);
        if (frame !== undefined) cancelAnimationFrame(frame);
    }
}

/** Checks the fixture assumptions that make screenshot preparation unnecessary. */
function readShadowCaptureGeometry(element, { deadline, expectedPage }) {
    const requireTime = () => {
        if (Date.now() >= deadline) throw new Error('BrowserSmoke.ShadowCaptureDeadline: geometry read expired.');
    };
    requireTime();
    if (location.href !== expectedPage || window !== window.top || element.ownerDocument !== document ||
        !(element instanceof HTMLCanvasElement) || document.querySelectorAll('#input-surface').length !== 1 ||
        document.querySelector('#input-surface') !== element || element.hasChildNodes())
        throw new Error('BrowserSmoke.ShadowCaptureDocument: require the expected main document and one empty canvas.');
    if (document.fonts?.status !== 'loaded' || document.fonts.size !== 0)
        throw new Error('BrowserSmoke.ShadowCaptureFonts: require loaded system fonts and no custom font faces.');
    // A checkbox has no caret. Reject other input types unless they also cannot have one.
    const nonCaretInputs = ['button', 'checkbox', 'color', 'file', 'hidden', 'image', 'radio', 'range', 'reset', 'submit'];
    if (document.designMode !== 'off' || document.querySelector('textarea,[contenteditable],iframe,frame,object,embed,fencedframe') ||
        [...document.querySelectorAll('input')].some(input => !nonCaretInputs.includes(input.type)) ||
        [...document.querySelectorAll('*')].some(node => node.shadowRoot))
        throw new Error('BrowserSmoke.ShadowCaptureCaret: require no editable caret targets or nested document roots.');
    const bounds = element.getBoundingClientRect();
    const result = { x: bounds.x, y: bounds.y, width: bounds.width, height: bounds.height,
        bitmapWidth: element.width, bitmapHeight: element.height, devicePixelRatio, scrollX, scrollY,
        viewportWidth: innerWidth, viewportHeight: innerHeight,
        viewportScale: visualViewport?.scale ?? null,
        viewportOffsetX: visualViewport?.offsetLeft ?? null, viewportOffsetY: visualViewport?.offsetTop ?? null };
    requireTime();
    return result;
}

/** Matches the pinned Playwright 1.63 viewport clip translation at unit device scale. */
function shadowProtocolClip(metrics, before, clip) {
    const visual = metrics?.visualViewport;
    assert(['offsetX', 'offsetY', 'pageX', 'pageY', 'clientWidth', 'clientHeight', 'scale']
        .every(field => Number.isFinite(visual?.[field])) &&
        (visual.zoom === undefined || Number.isFinite(visual.zoom)) &&
        visual.scale === 1 && visual.offsetX === 0 && visual.offsetY === 0 &&
        visual.pageX === before.scrollX && visual.pageY === before.scrollY &&
        visual.clientWidth > 0 && visual.clientHeight > 0 && before.devicePixelRatio === 1,
    'BrowserSmoke.ShadowCaptureMetrics: require finite, unscaled metrics that match the canvas scroll position.');
    // The visual viewport excludes scrollbar gutters. It need not equal window.innerWidth.
    return { x: visual.pageX + clip.x, y: visual.pageY + clip.y,
        width: Math.floor(clip.width / visual.scale + 0.001),
        height: Math.floor(clip.height / visual.scale + 0.001), scale: 1 };
}

/** Rejects oversized or malformed protocol data before allocating decoded PNG bytes. */
function decodeShadowScreenshot(data, requireTime) {
    requireTime();
    assert(typeof data === 'string' && data.length > 0 && data.length <= 11184812 && data.length % 4 === 0,
        'BrowserSmoke.ShadowImageEncoding: require bounded, padded base64 screenshot data.');
    const padding = data.endsWith('==') ? 2 : data.endsWith('=') ? 1 : 0;
    const decodedLength = data.length / 4 * 3 - padding;
    assert(decodedLength <= 8388608 && !/[^A-Za-z0-9+/]/.test(data.slice(0, data.length - padding)) &&
        (!padding || ('ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/'
            .indexOf(data[data.length - padding - 1]) & (padding === 2 ? 15 : 3)) === 0),
    'BrowserSmoke.ShadowImageEncoding: require valid base64 with at most 8 MiB of decoded data.');
    requireTime();
    const image = Buffer.from(data, 'base64');
    requireTime();
    assert(image.byteLength === decodedLength && image.byteLength <= 8388608,
        'BrowserSmoke.ShadowImageSize: encoded screenshot exceeds 8 MiB or has an invalid decoded length.');
    return image;
}

async function captureShadowSurface(page, config, imageAnalysis, slot, name, failure, captureContext,
    reference = null, observeResize = false) {
    const budget = Math.min(config.timeout, 10000);
    const startedAt = Date.now();
    const deadline = startedAt + budget;
    const { programs, state, extent, afterSerial, expectedPage } = captureContext;
    assert(programs && ['on', 'off'].includes(state) && Number.isSafeInteger(afterSerial) && afterSerial >= 0 &&
        typeof expectedPage === 'string' && expectedPage.length > 0 &&
        (extent === null || Array.isArray(extent) && extent.length === 2 && extent.every(value => Number.isSafeInteger(value) && value > 0)),
    'BrowserSmoke.ShadowCaptureContext: require programs, state, the expected page, a valid extent, and the prior completed serial.');
    const canvas = page.locator('#input-surface');
    const unrun = () => ({ status: 'unrun', attempt: null, value: null });
    const phases = ['wait-native-consumer', 'complete-native-consumer', 'verify-native-identity',
        'create-capture-session', 'scroll-into-view', 'geometry-before', 'layout-metrics', 'capture-screenshot',
        'decode-screenshot', 'write-screenshot', 'geometry-after', 'detach-capture-session',
        'inspect-pixels', 'inspect-alignment', 'read-status', 'read-failure-status', 'next-animation-frame'];
    const diagnostics = { schema: 1, name, budgetMs: budget, startedAtUtcMs: startedAt,
        elapsedMs: null, attempt: 0, activePhase: null,
        phases: Object.fromEntries(phases.map(phase => [phase, { attempt: null, phase, status: 'unrun',
            startedAtMs: null, elapsedMs: null, remainingMs: null }])),
        steps: [], omittedSteps: 0, before: unrun(), after: unrun(), clip: unrun(), latestSummary: unrun(),
        sessionCleanup: 'not-created',
        gate: { status: 'unrun', extent: null, afterSerial, selectedNative: null, selectedSerial: null, completedSerial: null } };
    if (observeResize) {
        diagnostics.resizeObserver = shadowResizeObservers.get(page) ?? { status: 'unavailable', reason: 'not-installed' };
        diagnostics.resizeCheckpoints = Object.fromEntries(['capture-start', 'first-blank', 'halfway', 'failure']
            .map(name => [name, { status: 'unavailable', reason: 'not-reached', utcMs: null, atMs: null,
                remainingMs: null, readBudgetMs: null, elapsedMs: null, source: null }]));
    }
    let halfwayTimer, captureClosed = false;
    let captureSession = null, protocolPending = false, detachRequested = false;
    const sealHalfwayCheckpoint = () => {
        clearTimeout(halfwayTimer);
        const record = diagnostics.resizeCheckpoints?.halfway;
        if (record?.reason === 'checkpoint-read-pending') {
            record.reason = 'capture-ended-during-read';
            record.elapsedMs = Date.now() - record.utcMs;
        }
    };
    const stopHalfwayCheckpoint = () => {
        captureClosed = true;
        sealHalfwayCheckpoint();
    };
    const withCaptureEvidence = error => {
        const result = error instanceof Error ? error : new Error(String(error));
        stopHalfwayCheckpoint();
        diagnostics.elapsedMs = Date.now() - startedAt;
        // Copy after bounded session cleanup and before other browser evidence reads. Late replies cannot change it.
        result.shadowCapture = structuredClone(diagnostics);
        return result;
    };
    const requireCaptureTime = (cleanup = false) => {
        if ((!cleanup && captureClosed) || Date.now() >= deadline)
            throw new Error(`${failure} Canvas capture exceeded its ${budget} ms budget.`);
    };
    const step = async (phase, action, cleanup = false) => {
        requireCaptureTime(cleanup);
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
            throw new Error(`${failure} Canvas capture exceeded its ${budget} ms budget.`);
        }
        let timer;
        let timedOut = false;
        try {
            const timeout = new Promise((_, reject) => {
                timer = setTimeout(() => {
                    timedOut = true;
                    reject(new Error(`${failure} Canvas capture exceeded its ${budget} ms budget.`));
                }, Math.max(0, deadline - Date.now()));
            });
            requireCaptureTime(cleanup);
            const result = await Promise.race([action(), timeout]);
            requireCaptureTime(cleanup);
            record.status = 'fulfilled';
            record.elapsedMs = Date.now() - stepStartedAt;
            diagnostics.activePhase = null;
            return result;
        } catch (error) {
            record.status = timedOut || Date.now() >= deadline || error?.code === 'SHADOW_IMAGE_DEADLINE' ? 'timed-out' : 'rejected';
            record.elapsedMs = Date.now() - stepStartedAt;
            stopHalfwayCheckpoint();
            throw error;
        } finally { clearTimeout(timer); }
    };
    // The public API cannot cancel creation, send, or detach. A late creation owns only its detach.
    const requestDetach = session => {
        if (detachRequested) return Promise.resolve();
        detachRequested = true;
        try { return session.detach(); }
        catch (error) { return Promise.reject(error); }
    };
    const createCaptureSession = async () => {
        requireCaptureTime();
        diagnostics.sessionCleanup = 'unverified';
        const session = await page.context().newCDPSession(page);
        if (captureClosed || Date.now() >= deadline) {
            void requestDetach(session).catch(() => {});
            requireCaptureTime();
        }
        captureSession = session;
    };
    const sendCaptureCommand = async (method, parameters) => {
        requireCaptureTime();
        assert(captureSession && !protocolPending && !detachRequested,
            'BrowserSmoke.ShadowCaptureSession: require one available owned protocol session.');
        protocolPending = true;
        try {
            const result = await captureSession.send(method, parameters);
            requireCaptureTime();
            protocolPending = false;
            return result;
        } catch (error) {
            if (!captureClosed && Date.now() < deadline) protocolPending = false;
            throw error;
        }
    };
    const detachCaptureSession = async (cleanup = false) => {
        // Do not overlap a pending protocol command or add a cleanup allowance after expiry.
        if (!captureSession || protocolPending || detachRequested || Date.now() >= deadline) return;
        await step('detach-capture-session', async () => {
            requireCaptureTime(cleanup);
            await requestDetach(captureSession);
            requireCaptureTime(cleanup);
            diagnostics.sessionCleanup = 'detached';
        }, cleanup);
    };
    const geometry = () => {
        requireCaptureTime();
        return canvas.evaluate(readShadowCaptureGeometry, { deadline, expectedPage });
    };
    const nextFrame = async () => {
        requireCaptureTime();
        await page.evaluate(waitForShadowCaptureFrame, deadline);
        requireCaptureTime();
    };
    const readGate = async includeNative => {
        requireCaptureTime();
        const result = await page.evaluate(({ deadline, includeNative }) => {
            if (Date.now() >= deadline) throw new Error('BrowserSmoke.ShadowCaptureDeadline: consumer read expired.');
            const source = globalThis.advancedSubmissionEvidence;
            const gpu = globalThis.advancedShadowSnapshot('capture');
            if (Date.now() >= deadline) throw new Error('BrowserSmoke.ShadowCaptureDeadline: native identity read expired.');
            return { gpu,
                submissions: { readMappings: source?.readMappings,
                    compute: Object.fromEntries(Object.entries(source?.compute ?? {}).filter(([label]) =>
                        /^engine-advanced-shade-(?:native|surface-exports|uber-native)/.test(label))) },
                failure: Boolean(globalThis.advancedCanvasFailureEvidence) || document.querySelector('#status')?.dataset.state === 'failed',
                nativeCompile: includeNative ? globalThis.advancedNativeCompileSnapshot() : null };
        }, { deadline, includeNative });
        requireCaptureTime();
        assert(result.gpu && result.gpu.captureErrors.length === 0 && result.gpu.deviceErrors.length === 0 &&
            result.gpu.overflow === 0 && result.submissions.readMappings === 0 && !result.failure,
        'BrowserSmoke.ShadowCaptureGate: require bounded GPU evidence, no failure, and zero GPU READ maps.');
        return result;
    };
    const completeCurrentExtent = async initialGeometry => {
        const gate = diagnostics.gate;
        gate.extent = extent ?? [initialGeometry.bitmapWidth, initialGeometry.bitmapHeight];
        assert(gate.extent.every(value => Number.isSafeInteger(value) && value > 0) &&
            equal(gate.extent, [initialGeometry.bitmapWidth, initialGeometry.bitmapHeight]),
        'BrowserSmoke.ShadowCaptureExtent: canvas backing extent differs from the requested capture.');
        gate.status = 'pending';
        await step('wait-native-consumer', async () => {
            for (;;) {
                const observed = await readGate(false);
                requireCaptureTime();
                const selectedNative = selectedShadowNative(observed.submissions, state, true);
                const consumers = selectedNative ? matchingShadowConsumers(observed.gpu, selectedNative,
                    gate.extent, afterSerial, observed.gpu.queueSubmits) : [];
                if (consumers.length > 0) {
                    gate.selectedNative = selectedNative;
                    gate.selectedSerial = consumers[0].firstSerial;
                    return;
                }
                await nextFrame();
            }
        });
        await step('complete-native-consumer', async () => {
            requireCaptureTime();
            const gpu = await page.evaluate(async deadline => {
                if (Date.now() >= deadline) throw new Error('BrowserSmoke.ShadowCaptureDeadline: completion wait expired.');
                const gpu = await globalThis.advancedShadowComplete('capture', deadline);
                if (Date.now() >= deadline) throw new Error('BrowserSmoke.ShadowCaptureDeadline: completion returned late.');
                return gpu;
            }, deadline);
            requireCaptureTime();
            assert(gpu && gpu.captureErrors.length === 0 && gpu.deviceErrors.length === 0 && gpu.overflow === 0 &&
                gpu.queueCompletions > 0 && gate.selectedSerial <= gpu.completedSerial,
            'BrowserSmoke.ShadowCaptureCompletion: the selected current-extent consumer did not complete.');
            gate.completedSerial = gpu.completedSerial;
        });
        await step('verify-native-identity', async () => {
            for (;;) {
                const observed = await readGate(true);
                requireCaptureTime();
                const selectedNative = selectedShadowNative(observed.submissions, state);
                assert(selectedNative === gate.selectedNative,
                    'BrowserSmoke.ShadowCaptureSelection: native selection changed before capture.');
                const native = observed.nativeCompile;
                const records = native?.records.filter(record => record.recipe?.pipeline?.label === selectedNative ||
                    record.pass === selectedNative.replace('engine-advanced-', '')) ?? [];
                const record = records[0];
                // Recipe hashing can still be pending after queue completion. Terminal failures must fail immediately.
                if (native?.captureErrors.length === 0 && records.length === 1 &&
                    ['pending', 'fulfilled'].includes(record.status) && ['pending', 'ready'].includes(record.recipeStatus) &&
                    (record.status === 'pending' || record.recipeStatus === 'pending')) {
                    await nextFrame();
                    continue;
                }
                assertNativeIdentity(observed, programs, state);
                gate.status = 'fulfilled';
                return;
            }
        });
    };
    const captureClip = (sample, viewport) => {
        // Match Playwright 1.63's element screenshot rounding in document coordinates.
        // Page screenshot clips use viewport coordinates and do not repeat element stability waits.
        const left = Math.floor(sample.x + sample.scrollX + 1e-3);
        const top = Math.floor(sample.y + sample.scrollY + 1e-3);
        const clip = { x: left - sample.scrollX, y: top - sample.scrollY,
            width: Math.ceil(sample.x + sample.scrollX + sample.width - 1e-3) - left,
            height: Math.ceil(sample.y + sample.scrollY + sample.height - 1e-3) - top };
        const valid = viewport && Object.values(sample).every(Number.isFinite) &&
            sample.devicePixelRatio === 1 && sample.viewportScale === 1 &&
            sample.viewportOffsetX === 0 && sample.viewportOffsetY === 0 &&
            sample.viewportWidth === viewport.width && sample.viewportHeight === viewport.height &&
            sample.width > 0 && sample.height > 0 && clip.width > 0 && clip.height > 0 &&
            clip.x >= 0 && clip.y >= 0 && clip.x + clip.width <= viewport.width && clip.y + clip.height <= viewport.height;
        return { clip, valid };
    };
    let latest;
    if (observeResize) halfwayTimer = setTimeout(() => {
        if (captureClosed) return;
        void readShadowResizeCheckpoint(page, diagnostics, 'halfway').catch(() => {
            const record = diagnostics.resizeCheckpoints.halfway;
            if (record.reason !== 'checkpoint-read-pending') return;
            record.reason = 'checkpoint-read-failed';
            record.elapsedMs = Date.now() - record.utcMs;
        });
    }, Math.max(0, startedAt + budget / 2 - Date.now()));
    try {
        if (observeResize) await readShadowResizeCheckpoint(page, diagnostics, 'capture-start');
        requireCaptureTime();
        const initialGeometry = await step('geometry-before', geometry);
        requireCaptureTime();
        diagnostics.before = { status: 'available', attempt: diagnostics.attempt, value: initialGeometry };
        await completeCurrentExtent(initialGeometry);
        requireCaptureTime();
        await step('create-capture-session', createCaptureSession);
        requireCaptureTime();
        do {
            if (observeResize && Date.now() - startedAt >= budget / 2)
                await readShadowResizeCheckpoint(page, diagnostics, 'halfway');
            requireCaptureTime();
            diagnostics.attempt++;
            diagnostics.before = unrun();
            diagnostics.after = unrun();
            diagnostics.clip = unrun();
            let before = await step('geometry-before', geometry);
            requireCaptureTime();
            diagnostics.before = { status: 'available', attempt: diagnostics.attempt, value: before };
            let viewport = page.viewportSize();
            let capture = captureClip(before, viewport);
            if (capture.valid) {
                const record = { attempt: diagnostics.attempt, phase: 'scroll-into-view', status: 'skipped-visible',
                    startedAtMs: Date.now() - startedAt, elapsedMs: 0, remainingMs: Math.max(0, deadline - Date.now()) };
                diagnostics.phases['scroll-into-view'] = record;
                if (diagnostics.steps.length < 64) diagnostics.steps.push(record);
                else diagnostics.omittedSteps++;
            } else {
                await step('scroll-into-view', () => canvas.scrollIntoViewIfNeeded({ timeout: Math.max(1, deadline - Date.now()) }));
                before = await step('geometry-before', geometry);
                requireCaptureTime();
                diagnostics.before = { status: 'available', attempt: diagnostics.attempt, value: before };
                viewport = page.viewportSize();
                capture = captureClip(before, viewport);
            }
            assert(equal(diagnostics.gate.extent, [before.bitmapWidth, before.bitmapHeight]),
                'BrowserSmoke.ShadowCaptureExtent: canvas backing extent changed after the completion gate.');
            const { clip } = capture;
            diagnostics.clip = { status: 'available', attempt: diagnostics.attempt, value: { ...clip, viewport } };
            const protocolClip = await step('layout-metrics', async () => {
                assert(capture.valid,
                'BrowserSmoke.ShadowCaptureBounds: the complete canvas must fit the unscaled viewport without clipping.');
                const metrics = await sendCaptureCommand('Page.getLayoutMetrics');
                requireCaptureTime();
                return shadowProtocolClip(metrics, before, clip);
            });
            requireCaptureTime();
            diagnostics.clip.value.protocolClip = protocolClip;
            const encoded = await step('capture-screenshot', () => sendCaptureCommand('Page.captureScreenshot', {
                format: 'png', clip: protocolClip, captureBeyondViewport: false,
            }));
            requireCaptureTime();
            const image = await step('decode-screenshot', () => decodeShadowScreenshot(encoded?.data, requireCaptureTime));
            requireCaptureTime();
            await step('write-screenshot', () => {
                requireCaptureTime();
                // An OS write cannot be interrupted. A late return cannot qualify this capture or start more work.
                writeFileSync(path.join(config.output, `${name}.png`), image);
                requireCaptureTime();
            });
            requireCaptureTime();
            const after = await step('geometry-after', geometry);
            requireCaptureTime();
            diagnostics.after = { status: 'available', attempt: diagnostics.attempt, value: after };
            let imageSha256;
            const decoded = await step('inspect-pixels', () => {
                requireCaptureTime();
                assert(image.byteLength <= 8 * 1024 * 1024, 'BrowserSmoke.ShadowImageSize: encoded screenshot exceeds 8 MiB.');
                imageSha256 = sha256(image);
                requireCaptureTime();
                return imageAnalysis.request('inspect', { slot, hash: imageSha256, png: image,
                    width: clip.width, height: clip.height, canvasWidth: before.bitmapWidth,
                    canvasHeight: before.bitmapHeight, cssWidth: after.width }, deadline);
            });
            requireCaptureTime();
            const pixels = decoded.pixels;
            const analyzed = await step('inspect-alignment', () => imageAnalysis.request('align', {
                slot, hash: imageSha256, reference: reference ? { slot: reference.analysisSlot, hash: reference.imageSha256 } : null,
            }, deadline));
            requireCaptureTime();
            const alignment = analyzed.alignment;
            const stable = equal(before, after) && before.devicePixelRatio === 1 &&
                equal(viewport, page.viewportSize()) && pixels.width === clip.width && pixels.height === clip.height &&
                Math.abs(pixels.width - before.width) <= 2 && Math.abs(pixels.height - before.height) <= 2;
            const matchesReference = !reference || reference.pixels.width === pixels.width &&
                reference.pixels.height === pixels.height &&
                equal(reference.pixels.captureValidity.alignment.observed, alignment.observed);
            latest = { ...pixels, imageSha256, captureValidity: { accepted: stable && alignment.accepted && matchesReference, stable, matchesReference,
                before, after, clip, viewport, alignment } };
            diagnostics.latestSummary = { status: 'available', attempt: diagnostics.attempt, value: latest };
            if (observeResize && pixels.colorful === 0) await readShadowResizeCheckpoint(page, diagnostics, 'first-blank');
            if (observeResize && Date.now() - startedAt >= budget / 2)
                await readShadowResizeCheckpoint(page, diagnostics, 'halfway');
            requireCaptureTime();
            if (hasSurface(pixels) && latest.captureValidity.accepted && Date.now() < deadline) {
                await detachCaptureSession();
                requireCaptureTime();
                assert(diagnostics.sessionCleanup === 'detached',
                    'BrowserSmoke.ShadowCaptureCleanup: owned protocol session detach was not acknowledged.');
                latest.captureGate = structuredClone(diagnostics.gate);
                latest.captureSessionCleanup = diagnostics.sessionCleanup;
                if (observeResize) {
                    sealHalfwayCheckpoint();
                    diagnostics.resizeCheckpoints.failure.reason = 'capture-completed';
                    latest.resizeCheckpoints = structuredClone(diagnostics.resizeCheckpoints);
                }
                requireCaptureTime();
                return { image, pixels: latest, imageSha256, analysisSlot: slot,
                    receiverComparison: analyzed.comparison, comparedToHash: reference?.imageSha256 ?? null };
            }
            if (await step('read-status', () => page.locator('#status').getAttribute('data-state')) === 'failed')
                throw new Error(`BrowserSmoke.EngineFrameFailed: ${await step('read-failure-status',
                    () => page.locator('#status').textContent())}`);
            // Retry at the next browser frame, within the same capture budget. Do not add a fixed startup delay.
            const remaining = deadline - Date.now();
            if (remaining > 0) await step('next-animation-frame', nextFrame);
        } while (Date.now() < deadline);
        throw new Error(`${failure} Last canvas summary: ${JSON.stringify(latest)}`);
    } catch (error) {
        stopHalfwayCheckpoint();
        const failedPhase = diagnostics.activePhase;
        // Preserve the capture error if bounded cleanup also fails.
        try { await detachCaptureSession(true); } catch { /* Cleanup remains unverified. */ }
        diagnostics.activePhase = failedPhase;
        throw withCaptureEvidence(error);
    } finally { stopHalfwayCheckpoint(); }
}

function compareReceiver(off, on, canvasExtent) {
    assert(off.pixels.width === on.pixels.width && off.pixels.height === on.pixels.height,
        'BrowserSmoke.ShadowComparison: ON/OFF capture dimensions differ.');
    assert(off.pixels.captureValidity?.accepted && on.pixels.captureValidity?.accepted &&
        equal(off.pixels.captureValidity.alignment.observed, on.pixels.captureValidity.alignment.observed) &&
        [off, on].every(value => value.pixels.captureValidity.before.bitmapWidth === canvasExtent[0] &&
            value.pixels.captureValidity.before.bitmapHeight === canvasExtent[1]),
    'BrowserSmoke.ShadowComparison: ON/OFF images must align with the genuine canvas and pinned scene projection.');
    assert(on.comparedToHash === off.imageSha256 && on.receiverComparison &&
        equal(on.receiverComparison.regions, shadowReceiverRegions(on.pixels.width, on.pixels.height, ...canvasExtent)),
    'BrowserSmoke.ShadowComparison: worker result must use the accepted baseline bytes and receiver regions.');
    return on.receiverComparison;
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
    await page.addInitScript(installNativeCompileCapture, ['shade-native-depth', 'shade-native-depth-no-decals']);
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
    const profiles = [
        { name: 'small', initialViewport: { width: 640, height: 780 }, widths: [560, 600],
            extents: [[593, 334], [513, 289], [553, 311]] },
        { name: 'full', initialViewport: { width: 1024, height: 1100 }, widths: [860, 940],
            extents: [[977, 550], [813, 457], [893, 502]] },
    ];
    report.advancedShadowProfiles = Object.fromEntries(profiles.map(profile => [profile.name,
        { status: 'unrun', initialViewport: profile.initialViewport, resizeWidths: profile.widths,
            expectedExtents: profile.extents, iterations: [], failures: [] }]));
    for (const profile of profiles) {
        const profileReport = report.advancedShadowProfiles[profile.name];
        profileReport.status = 'running';
        const imageAnalysis = createShadowImageAnalysis(profile.name);
        const widths = profile.widths;
        const baseline = {};
        const run = async (state, iteration, action) => {
            const name = profile.name === 'full' ? `advanced-shadow-${state}-${iteration}`
                : `advanced-shadow-small-${state}-${iteration}`;
            const { page, context, events } = await instrumentedPage(browser, origin, report, name, config);
            let primaryError = null;
            try {
                if (profile.name === 'small') await page.setViewportSize(profile.initialViewport);
                const detail = await start(page, origin, state === 'on' ? '__game' : '__baseline');
                await action(page, events, name, detail);
                assertNoBrowserErrors(events);
                assert(!events.some(event => ['crash', 'requestfailed'].includes(event.type)),
                    'BrowserSmoke.ShadowBrowserFailure: the page crashed or a request failed.');
            } catch (error) {
                primaryError = error;
                if (error?.shadowCapture?.resizeCheckpoints)
                    await readShadowResizeCheckpoint(page, error.shadowCapture, 'failure');
                const evidence = await page.evaluate(() => ({ failure: globalThis.advancedCanvasFailureEvidence ?? null,
                    nativeCompile: globalThis.advancedNativeCompileSnapshot?.() ?? null,
                    submissions: globalThis.advancedSubmissionSnapshot?.() ?? null,
                    gpu: globalThis.advancedShadowSnapshot?.() ?? null })).catch(() => null);
                const delivery = await shadowDeliverySnapshot(page,
                    `${origin}/${state === 'on' ? '__game' : '__baseline'}/index.html`);
                const failure = { profile: profile.name, state, iteration, error: String(error),
                    capture: error?.shadowCapture ?? null, ...evidence, delivery };
                profileReport.failures.push(failure);
                report.advancedShadowFailures.push(failure);
                await page.screenshot({ path: path.join(config.output, `${name}-failure.png`), fullPage: true }).catch(() => {});
                throw error;
            } finally {
                await cleanupShadowResizeObservation(page);
                try { await context.close(); }
                catch (error) {
                    profileReport.contextCleanupError = String(error);
                    if (!primaryError) throw error;
                }
            }
        };
        let imageAnalysisFailed = false;
        try {
            profileReport.imageAnalysis = await imageAnalysis.request('ready', {}, Date.now() + Math.min(config.timeout, 10000));
            assert(profileReport.imageAnalysis.profile === profile.name && profileReport.imageAnalysis.retainedImageSlots === 4,
                'BrowserSmoke.ShadowImageProfile: analysis worker profile or slot count differs from the requested case.');
            if (profile.name === 'full') report.advancedShadowImageAnalysis = profileReport.imageAnalysis;
            await run('off', 0, async (page, _events, name, detail) => {
                baseline.initial = await captureShadowSurface(page, config, imageAnalysis, 'off-initial', `${name}-playing`,
                    'BrowserSmoke.ShadowOffSurface: the authored OFF surface did not reach the canvas.',
                    { programs: offArtifacts.programs, state: 'off', extent: null, afterSerial: 0,
                        expectedPage: `${origin}/__baseline/index.html` });
                baseline.initialExtent = await page.locator('#input-surface').evaluate(element => [element.width, element.height]);
                assert(equal(baseline.initialExtent, profile.extents[0]),
                    'BrowserSmoke.ShadowExtent: initial canvas backing extent differs from the source-derived profile.');
                const initial = await snapshot(page);
                const initialQualification = assertShadowEvidence(initial, offArtifacts.programs, 'off', baseline.initialExtent);
                profileReport.baseline = { detail, playing: baseline.initial.pixels, initial, initialQualification, resized: [] };
                if (profile.name === 'full') report.advancedShadowBaseline = profileReport.baseline;
                baseline.resized = [];
                let prior = initial;
                for (let iteration = 0; iteration < widths.length; iteration++) {
                    if (iteration === 0 && profile.name === 'full')
                        await installShadowResizeObservation(page, `${origin}/__baseline/index.html`);
                    const extent = await resize(page, widths[iteration]);
                    assert(equal(extent, profile.extents[iteration + 1]),
                        'BrowserSmoke.ShadowExtent: resized canvas backing extent differs from the source-derived profile.');
                    const image = await captureShadowSurface(page, config, imageAnalysis, `off-${widths[iteration]}`, `${name}-resized-${iteration}`,
                        'BrowserSmoke.ShadowOffResize: the authored OFF surface disappeared after resize.',
                        { programs: offArtifacts.programs, state: 'off', extent, afterSerial: prior.gpu.completedSerial,
                            expectedPage: `${origin}/__baseline/index.html` }, null, iteration === 0 && profile.name === 'full');
                    const evidence = await snapshot(page);
                    const qualification = assertShadowEvidence(evidence, offArtifacts.programs, 'off', extent, prior.gpu.completedSerial);
                    baseline.resized.push({ ...image, extent });
                    profileReport.baseline.resized.push({ pixels: image.pixels, extent, evidence, qualification });
                    prior = evidence;
                }
            });
            for (let iteration = 0; iteration < 2; iteration++) await run('on', iteration, async (page, _events, name, detail) => {
                const result = { iteration, detail };
                profileReport.iterations.push(result);
                if (profile.name === 'full') report.advancedShadowIterations.push(result);
                const playing = await captureShadowSurface(page, config, imageAnalysis, 'current', `${name}-playing`,
                    'BrowserSmoke.ShadowSurface: the authored ON surface did not reach the canvas.',
                    { programs: onArtifacts.programs, state: 'on', extent: null, afterSerial: 0,
                        expectedPage: `${origin}/__game/index.html` }, baseline.initial);
                const before = await page.locator('#input-surface').evaluate(element => [element.width, element.height]);
                assert(equal(before, baseline.initialExtent), 'BrowserSmoke.ShadowComparison: initial ON/OFF canvas extents differ.');
                result.playing = playing.pixels;
                result.initial = await snapshot(page);
                result.initialQualification = assertShadowEvidence(result.initial, onArtifacts.programs, 'on', before);
                result.comparison = compareReceiver(baseline.initial, playing, before);
                assertReceiverDifference(result.comparison, 'initial');
                const after = await resize(page, widths[iteration]);
                assert(equal(after, baseline.resized[iteration].extent), 'BrowserSmoke.ShadowComparison: resized ON/OFF canvas extents differ.');
                const resized = await captureShadowSurface(page, config, imageAnalysis, 'current', `${name}-resized`,
                    'BrowserSmoke.ShadowResize: the authored ON surface disappeared after resize.',
                    { programs: onArtifacts.programs, state: 'on', extent: after, afterSerial: result.initial.gpu.completedSerial,
                        expectedPage: `${origin}/__game/index.html` },
                    baseline.resized[iteration]);
                result.resized = resized.pixels;
                result.after = await snapshot(page);
                result.resizeQualification = assertShadowEvidence(result.after, onArtifacts.programs, 'on', after,
                    result.initial.gpu.completedSerial);
                result.resizedComparison = compareReceiver(baseline.resized[iteration], resized, after);
                assertReceiverDifference(result.resizedComparison, 'resized');
                result.canvasSizes = { before, after };
            });
            profileReport.status = 'passed';
        } catch (error) {
            imageAnalysisFailed = true;
            profileReport.status = 'failed';
            throw error;
        } finally {
            const stopped = await imageAnalysis.close();
            profileReport.imageAnalysis ??= { codec: 'unavailable' };
            profileReport.imageAnalysis.workerStopped = stopped;
            if (profile.name === 'full') report.advancedShadowImageAnalysis = profileReport.imageAnalysis;
            if (!stopped) profileReport.status = 'failed';
            if (!stopped && !imageAnalysisFailed) throw new Error('BrowserSmoke.ShadowImageCleanup: owned analysis worker did not stop within 1000 ms.');
        }
    }
}
