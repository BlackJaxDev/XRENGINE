import { createHash } from 'node:crypto';
import { browserLaunchOptions } from './smoke.config.mjs';
import { captureGpuProcessState } from './gpu-diagnostics.mjs';
import { startNativeCompileTrace } from './native-compile-trace.mjs';
import { captureNativeProfileCapabilities } from './native-profile-capabilities.mjs';
import { createOwnedGpuProfile } from './owned-gpu-profile.mjs';

/** Passive capture: return every original WebGPU object/promise unchanged. Never retain WGSL in evidence. */
export function installNativeCompileCapture(additionalPasses = []) {
    const shadowPasses = ['shade-native-depth', 'shade-native-depth-msaa',
        'shade-surface-exports-depth', 'shade-surface-exports-depth-msaa',
        'shade-native-depth-no-decals', 'shade-native-depth-no-decals-msaa',
        'shade-surface-exports-depth-no-decals', 'shade-surface-exports-depth-no-decals-msaa'];
    if (!Array.isArray(additionalPasses) || additionalPasses.length > shadowPasses.length ||
        additionalPasses.some(pass => !shadowPasses.includes(pass)) ||
        new Set(additionalPasses).size !== additionalPasses.length)
        throw new Error('Native compile capture requires a bounded supported shadow pass list.');
    const nativePassByLabel = new Map([
        'shade-native', 'shade-native-no-modifiers',
        'shade-native-msaa', 'shade-native-no-modifiers-msaa',
        'shade-surface-exports-no-modifiers', 'shade-surface-exports-no-modifiers-msaa',
        'shade-native-no-decals', 'shade-native-no-decals-msaa',
        'shade-surface-exports-no-decals', 'shade-surface-exports-no-decals-msaa',
        ...additionalPasses,
    ].map(pass => [`engine-advanced-${pass}`, pass]));
    const adapters = new WeakMap(), devices = new WeakMap(), bindings = new WeakMap();
    const layouts = new WeakMap(), modules = new WeakMap();
    const evidence = { timeOriginMs: performance.timeOrigin, calls: 0, records: [], captureErrors: [] };
    const clone = value => structuredClone(value);
    const limits = value => {
        const names = new Set();
        for (let prototype = value; prototype; prototype = Object.getPrototypeOf(prototype))
            for (const name of Object.getOwnPropertyNames(prototype)) names.add(name);
        return Object.fromEntries([...names].sort().filter(name => typeof value[name] === 'number')
            .map(name => [name, value[name]]));
    };
    const capabilities = value => ({ features: [...value.features].sort(), limits: limits(value.limits) });
    const observe = action => {
        try { action(); }
        catch (error) {
            if (evidence.captureErrors.length < 8) evidence.captureErrors.push(String(error).slice(0, 1024));
        }
    };
    globalThis.advancedNativeCompileSnapshot = () => {
        const now = performance.now();
        return clone({ ...evidence, records: evidence.records.map(record => ({ ...record,
            elapsedMs: record.elapsedMs ?? now - record.startedAtMs })) });
    };
    if (!globalThis.GPU || !globalThis.GPUAdapter || !globalThis.GPUDevice) return;
    const requestAdapter = GPU.prototype.requestAdapter;
    GPU.prototype.requestAdapter = function (...args) {
        const startedAtMs = performance.now(), promise = requestAdapter.apply(this, args);
        observe(() => {
            const request = clone(args[0] ?? {});
            void promise.then(adapter => observe(() => {
                if (!adapter) return;
                const info = adapter.info ?? {};
                adapters.set(adapter, { request, startedAtMs, elapsedMs: performance.now() - startedAtMs,
                    ...capabilities(adapter), info: { vendor: info.vendor ?? '', architecture: info.architecture ?? '',
                        device: info.device ?? '', description: info.description ?? '',
                        subgroupMinSize: info.subgroupMinSize ?? null, subgroupMaxSize: info.subgroupMaxSize ?? null,
                        fallback: adapter.isFallbackAdapter ?? info.isFallbackAdapter ?? null } });
            }), () => {});
        });
        return promise;
    };
    const requestDevice = GPUAdapter.prototype.requestDevice;
    GPUAdapter.prototype.requestDevice = function (...args) {
        const startedAtMs = performance.now(), promise = requestDevice.apply(this, args);
        observe(() => {
            const request = clone(args[0] ?? {}), adapter = adapters.get(this);
            void promise.then(device => observe(() => devices.set(device, {
                request, adapter, startedAtMs, elapsedMs: performance.now() - startedAtMs, ...capabilities(device),
            })), () => {});
        });
        return promise;
    };
    const createBinding = GPUDevice.prototype.createBindGroupLayout;
    GPUDevice.prototype.createBindGroupLayout = function (...args) {
        const result = createBinding.apply(this, args);
        observe(() => bindings.set(result, clone(args[0])));
        return result;
    };
    const createLayout = GPUDevice.prototype.createPipelineLayout;
    GPUDevice.prototype.createPipelineLayout = function (...args) {
        const result = createLayout.apply(this, args);
        observe(() => {
            const { bindGroupLayouts, ...descriptor } = args[0];
            layouts.set(result, { ...clone(descriptor), bindGroupLayouts: [...bindGroupLayouts].map(binding => {
                const captured = bindings.get(binding);
                if (!captured) throw new Error('Native compile capture is missing a bind-group layout.');
                return captured;
            }) });
        });
        return result;
    };
    const createModule = GPUDevice.prototype.createShaderModule;
    GPUDevice.prototype.createShaderModule = function (...args) {
        const startedAtMs = performance.now(), result = createModule.apply(this, args);
        observe(() => {
            const { code, ...descriptor } = args[0];
            // GPU objects in compilation hints need a separate capture contract; do not approximate them.
            if (Object.hasOwn(descriptor, 'compilationHints')) throw new Error('Native compile capture cannot replay compilation hints.');
            modules.set(result, { code, descriptor: clone(descriptor), startedAtMs,
                elapsedMs: performance.now() - startedAtMs });
        });
        return result;
    };
    const createCompute = GPUDevice.prototype.createComputePipelineAsync;
    GPUDevice.prototype.createComputePipelineAsync = function (...args) {
        const startedAtMs = performance.now();
        const promise = createCompute.apply(this, args);
        observe(() => {
            const pass = nativePassByLabel.get(args[0]?.label);
            if (!pass) return;
            evidence.calls++;
            if (evidence.records.length >= 4) return;
            const record = { startedAtMs, callReturnedAtMs: performance.now(), status: 'pending', elapsedMs: null,
                pass, recipeStatus: 'pending', recipe: null };
            evidence.records.push(record);
            void promise.then(() => {
                record.status = 'fulfilled'; record.elapsedMs = performance.now() - startedAtMs;
            }, () => {
                record.status = 'rejected'; record.elapsedMs = performance.now() - startedAtMs;
            });
            const { layout, compute, ...pipeline } = args[0];
            const { module, ...stage } = compute;
            const shader = modules.get(module), device = devices.get(this), explicitLayout = layouts.get(layout);
            if (!shader || typeof shader.code !== 'string' || !device?.adapter || !explicitLayout)
                throw new Error('Native compile capture is missing the exact module, device, or explicit layout.');
            record.recipe = { pipeline: clone(pipeline), compute: clone(stage), layout: clone(explicitLayout),
                module: { descriptor: clone(shader.descriptor), startedAtMs: shader.startedAtMs,
                    elapsedMs: shader.elapsedMs, sha256: null, byteLength: null }, device: clone(device) };
            // Hash after the original compile was launched; no extra shader/module is created.
            void Promise.resolve().then(async () => {
                const bytes = new TextEncoder().encode(shader.code);
                record.recipe.module.byteLength = bytes.byteLength;
                const digest = await crypto.subtle.digest('SHA-256', bytes);
                record.recipe.module.sha256 = [...new Uint8Array(digest)]
                    .map(value => value.toString(16).padStart(2, '0')).join('');
                record.recipeStatus = 'ready';
            }).catch(() => { record.recipeStatus = 'hash-unavailable'; });
        });
        return promise;
    };
}

/** Complete compile-only ABI for the existing Uber consumer; never a substitute material pipeline. */
export function uberNativeCompileContract() {
    const name = 'engine-advanced-shade-uber-native', bindings = [];
    const bindGroupLayouts = Array.from({ length: 3 }, () => ({ label: name, entries: [] }));
    const add = (group, binding, resourceName, kind, shape, bytes = 0, dynamic = false) => {
        bindings.push({ group, binding, name: resourceName, kind, bytes, dynamic, visibility: ['compute'] });
        bindGroupLayouts[group].entries.push({ binding, visibility: 4, ...shape });
    };
    ['SceneArena', 'GeometryArena', 'PreparedDrawDeformations', 'MaterialGroups', 'ShadeTiles', 'ShadeCounts', 'TextureBindings']
        .forEach((resourceName, binding) => add(0, binding, resourceName, 'read-only-storage',
            { buffer: { type: 'read-only-storage', hasDynamicOffset: false, minBindingSize: 4 } }, 4));
    for (const [binding, resourceName, bytes] of [[7, 'FrozenView', 944], [8, 'Parameters', 160]])
        add(0, binding, resourceName, 'uniform', { buffer: { type: 'uniform', hasDynamicOffset: true, minBindingSize: bytes } }, bytes, true);
    const texture = (binding, resourceName, kind, sampleType, viewDimension = '2d') =>
        add(1, binding, resourceName, kind, { texture: { sampleType, viewDimension, multisampled: false } });
    texture(0, 'VisibilityIdentity', 'texture-2d-uint', 'uint');
    texture(1, 'VisibilityMetadata', 'texture-2d-uint', 'uint');
    texture(2, 'VisibilityDepth', 'texture-depth-2d', 'depth');
    texture(3, 'AmbientOcclusion', 'texture-2d-unfilterable-float', 'unfilterable-float');
    for (let slot = 0; slot < 12; slot++) {
        if (slot === 8) {
            texture(20, 'UberRasterSurface', 'texture-2d-array-unfilterable-float', 'unfilterable-float', '2d-array');
            continue; // The load-only raster surface has no binding21 sampler.
        }
        const dimension = slot === 10 ? 'cube' : slot === 11 ? '2d-array' : '2d';
        texture(4 + 2 * slot, `NativeTexture${slot}`, `texture-${dimension}-float`, 'float', dimension);
        add(1, 5 + 2 * slot, `NativeTexture${slot}`, 'filtering-sampler', { sampler: { type: 'filtering' } });
    }
    ['HDRSceneColor', 'Velocity', 'ReactiveMask', 'ShadingDiagnostics'].forEach((resourceName, binding) => {
        const format = ['rgba16float', 'rgba16float', 'r32float', 'r32uint'][binding];
        add(2, binding, resourceName, `storage-texture-2d-write-${format}`,
            { storageTexture: { access: 'write-only', format, viewDimension: '2d' } });
    });
    return { name, pass: 'shade-uber-native', semanticSchemaIdentity: 'xrengine.engine.uber-raster-consumer.v2',
        entryPoints: { compute: 'advancedShadeNative' }, workgroupSize: [16, 16, 1], bindings,
        layout: { bindGroupLayouts } };
}

/** Runs on a blank loopback page in a fresh process; the optional comparison uses its own verified published ABI. */
export async function replayNativeCompile({ recipe, manifestUrl, compileBudgetMs, comparison = null, shadowTarget = null }) {
    const result = { status: 'preparing', stage: 'load-cooked-module', compileBudgetMs, timeOriginMs: performance.timeOrigin,
        stages: [], compile: null, compilationInfo: null, deviceLoss: null, uncapturedErrors: [],
        explicitDestroyRequested: false, cleanup: {} };
    let device, loader, deadline, finished = false;
    const controller = new AbortController();
    const snapshot = () => JSON.parse(JSON.stringify(result));
    const dispose = () => {
        if (finished) return;
        finished = true;
        result.cleanup.startedAtMs = performance.now();
        controller.abort();
        loader?.dispose();
        result.cleanup.loaderDisposed = !!loader;
        if (device) {
            result.explicitDestroyRequested = true;
            result.cleanup.deviceDestroyRequestedAtMs = performance.now();
            device.destroy();
            result.cleanup.deviceDestroyed = true;
            result.cleanup.deviceDestroyedAtMs = performance.now();
        }
    };
    globalThis.nativeCompileIsolation = { snapshot, dispose };
    const bounded = async (stage, budgetMs, action) => {
        result.stage = stage;
        const timing = { stage, startedAtMs: performance.now(), elapsedMs: null, status: 'pending' };
        result.stages.push(timing);
        let timer;
        try {
            const value = await Promise.race([Promise.resolve().then(action), new Promise((_, reject) => {
                timer = setTimeout(() => {
                    timing.status = 'timed-out';
                    reject(new Error(`Native compile isolation ${stage} exceeded ${budgetMs} ms.`));
                }, budgetMs);
            })]);
            timing.status = 'fulfilled';
            return value;
        } catch (error) {
            if (timing.status === 'pending') timing.status = 'rejected';
            throw error;
        } finally { clearTimeout(timer); timing.elapsedMs = performance.now() - timing.startedAtMs; }
    };
    const limits = value => {
        const names = new Set();
        for (let prototype = value; prototype; prototype = Object.getPrototypeOf(prototype))
            for (const name of Object.getOwnPropertyNames(prototype)) names.add(name);
        return Object.fromEntries([...names].sort().filter(name => typeof value[name] === 'number')
            .map(name => [name, value[name]]));
    };
    const capabilities = value => ({ features: [...value.features].sort(), limits: limits(value.limits) });
    const same = (actual, expected, label) => {
        if (JSON.stringify(actual) !== JSON.stringify(expected)) throw new Error(`Native compile isolation ${label} differs from the application.`);
    };
    try {
        // Transport, manifest graph validation, payload budgets, and SHA-256 verification use production helpers.
        const source = await bounded('load-cooked-module', 45000, async () => {
            const [{ BrowserContentLoader }, { validateEngineAssetManifest }] = await Promise.all([
                import(new URL('../content-loader.js', manifestUrl).href),
                import(new URL('../engine-assets.js', manifestUrl).href),
            ]);
            if (finished) throw new Error('Native compile isolation was disposed.');
            loader = new BrowserContentLoader(new URL(manifestUrl), controller.signal);
            const { manifest, assets } = validateEngineAssetManifest(await loader.readManifest(), new URL(manifestUrl));
            const readArtifact = async pass => {
                const binding = manifest.pipelineArtifacts?.find(value => value.scope === 'advanced' && value.pass === pass);
                const shader = manifest.shaderArtifacts?.find(value => value.identity === binding?.descriptorIdentity);
                if (!shader) throw new Error(`Native compile isolation requires the published advanced::${pass} artifact.`);
                const descriptorEntry = assets.get(shader.descriptor), sourceEntry = assets.get(shader.source);
                const bytes = await loader.readVerifiedPayload(descriptorEntry.url, descriptorEntry.bytes, shader.identity, shader.descriptor);
                try { return { shader, sourceEntry, descriptor: JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes)) }; }
                finally { loader.releasePayload(bytes); }
            };
            const selectedPass = String(recipe.pipeline?.label ?? '').replace(/^engine-advanced-/, '');
            if (!['shade-native', 'shade-native-no-modifiers', 'shade-native-msaa', 'shade-native-no-modifiers-msaa',
                'shade-surface-exports-no-modifiers', 'shade-surface-exports-no-modifiers-msaa',
                'shade-native-no-decals', 'shade-native-depth-no-decals',
                'shade-native-no-decals-msaa', 'shade-native-depth-no-decals-msaa',
                'shade-surface-exports-no-decals', 'shade-surface-exports-depth-no-decals',
                'shade-surface-exports-no-decals-msaa', 'shade-surface-exports-depth-no-decals-msaa'].includes(selectedPass)
                || recipe.pipeline.label !== `engine-advanced-${selectedPass}`)
                throw new Error('Native compile isolation captured an unsupported native program label.');
            let { shader, sourceEntry, descriptor } = await readArtifact(selectedPass);
            if (shadowTarget && (selectedPass !== shadowTarget.pass ||
                shader.identity !== shadowTarget.descriptorIdentity ||
                sourceEntry.hash !== shadowTarget.wgslSha256 || sourceEntry.bytes !== shadowTarget.wgslBytes ||
                descriptor.entryPoints?.compute !== shadowTarget.entryPoint))
                throw new Error('Native compile isolation shadow artifact differs from the pinned target.');
            if (descriptor.name !== recipe.pipeline.label || descriptor.pass !== selectedPass || descriptor.target !== 'WebGPUWgsl'
                || descriptor.entryPoints?.compute !== recipe.compute.entryPoint
                || sourceEntry.hash !== recipe.module.sha256 || sourceEntry.bytes !== recipe.module.byteLength
                || descriptor.source?.sha256 !== recipe.module.sha256 || descriptor.source?.byteLength !== recipe.module.byteLength)
                throw new Error('Native compile isolation published descriptor/source does not match the observed module and entry point.');
            result.selectedPass = selectedPass;
            if (selectedPass.includes('-no-modifiers') || selectedPass.includes('-no-decals')) {
                const noDecals = selectedPass.includes('-no-decals');
                const basePass = selectedPass.replace(noDecals ? '-no-decals' : '-no-modifiers', '');
                const { shader: baseShader, sourceEntry: baseSourceEntry, descriptor: base } = await readArtifact(basePass);
                if (base.name !== `engine-advanced-${basePass}` || base.pass !== basePass ||
                    base.semanticSchemaIdentity !== 'xrengine.engine.compute.v1' ||
                    base.source?.sha256 !== baseSourceEntry.hash || base.source?.byteLength !== baseSourceEntry.bytes)
                    throw new Error('Native compile isolation full-program control identity differs from its published source.');
                const baseBytes = await loader.readVerifiedPayload(baseSourceEntry.url, baseSourceEntry.bytes,
                    baseSourceEntry.hash, baseShader.source);
                loader.releasePayload(baseBytes);
                const canonical = value => Array.isArray(value) ? value.map(canonical) : value && typeof value === 'object'
                    ? Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])])) : value;
                const equal = (actual, expected, label) => {
                    if (JSON.stringify(canonical(actual)) !== JSON.stringify(canonical(expected)))
                        throw new Error(`Native compile isolation selected ${label} differs from its full-program control.`);
                };
                equal(descriptor.semanticSchemaIdentity, noDecals ?
                    'xrengine.engine.native-no-decals.v1' : 'xrengine.engine.native-unmodified.v1', 'schema identity');
                equal(descriptor.schemaVersion, 3, 'schema version');
                equal(descriptor.target, 'WebGPUWgsl', 'target');
                equal(descriptor.sourceLanguage, 'Slang', 'source language');
                equal(descriptor.workgroupSize, [16, 16, 1], 'workgroup size');
                equal(descriptor.layout?.vertexBuffers, [], 'vertex buffers');
                const expectedBindings = selectedPass.startsWith('shade-native-') &&
                    selectedPass.endsWith('-msaa') ? 40 : 41;
                equal(descriptor.layout?.bindings?.length, expectedBindings, 'binding count');
                equal(descriptor.defines, [...base.defines, noDecals ?
                    'XR_ADV_NATIVE_DECALS_ABSENT_SCHEMA_VERSION=1' :
                    'XR_ADV_NATIVE_MODIFIERS_ABSENT_SCHEMA_VERSION=1'], 'schema defines');
                if (noDecals && (!descriptor.defines.includes('XR_ADV_STANDALONE_SHADOW_SCHEMA_VERSION=1') ||
                    selectedPass.includes('-depth-') && !descriptor.defines.includes('XR_ADV_DEPTH_COMPARISON_BANK=1') ||
                    descriptor.defines.includes('XR_ADV_NATIVE_MODIFIERS_ABSENT_SCHEMA_VERSION=1')))
                    throw new Error('Native compile isolation selected decal-free program lost its shadow schema.');
                equal(descriptor.layout, base.layout, 'complete binding layout');
                for (const key of ['schemaVersion', 'compilerIdentity', 'sourceLanguage', 'matrixLayout', 'coordinates',
                    'entryPoints', 'workgroupSize', 'requiredFeatures', 'requiredLimits', 'includes', 'sourceMap', 'pipeline', 'specialization'])
                    equal(descriptor[key], base[key], key);
                const sourceDependencies = value => value.dependencies.filter(dependency => !dependency.path.endsWith('.recipe.json'));
                equal(sourceDependencies(descriptor), sourceDependencies(base), 'source dependencies');
                equal(recipe.pipeline, { label: descriptor.name }, 'pipeline descriptor');
                equal(recipe.compute, { entryPoint: descriptor.entryPoints.compute }, 'compute stage');
                equal(recipe.module.descriptor, { label: descriptor.name }, 'module descriptor');
                const gpuBinding = value => {
                    const entry = { binding: value.binding, visibility: 4 };
                    if (['read-only-storage', 'storage', 'uniform'].includes(value.kind))
                        entry.buffer = { type: value.kind, hasDynamicOffset: value.dynamic, minBindingSize: value.bytes };
                    else if (value.kind.endsWith('-sampler'))
                        entry.sampler = { type: value.kind.replace('-sampler', '') };
                    else if (value.kind.startsWith('storage-texture-')) {
                        const match = /^storage-texture-(2d|2d-array)-write-(.+)$/.exec(value.kind);
                        if (!match) throw new Error('Native compile isolation selected storage texture shape is unsupported.');
                        entry.storageTexture = { viewDimension: match[1], format: match[2], access: 'write-only' };
                    } else {
                        const match = /^texture-(?:(depth-multisampled-2d|depth-2d)|(multisampled-2d|2d-array|cube|2d)-(uint|sint|unfilterable-float|float))$/.exec(value.kind);
                        if (!match) throw new Error('Native compile isolation selected texture shape is unsupported.');
                        const dimension = match[1] ?? match[2];
                        entry.texture = { viewDimension: dimension.includes('array') ? '2d-array' : dimension.includes('cube') ? 'cube' : '2d',
                            sampleType: match[1] ? 'depth' : match[3], multisampled: dimension.includes('multisampled') };
                    }
                    return entry;
                };
                const expectedGroups = Array.from({ length: 3 }, () => ({ label: descriptor.name, entries: [] }));
                for (const binding of descriptor.layout.bindings) expectedGroups[binding.group].entries.push(gpuBinding(binding));
                equal(recipe.layout.bindGroupLayouts, expectedGroups, 'captured GPU binding layout');
                equal(Object.keys(recipe.layout).sort(), ['bindGroupLayouts'], 'pipeline layout fields');
                result.selectedContract = { basePass, schemaIdentity: descriptor.semanticSchemaIdentity,
                    bindingCount: descriptor.layout.bindings.length, layoutVerified: true };
                result.fullProgramControl = { descriptorIdentity: baseShader.identity, sha256: baseSourceEntry.hash,
                    byteLength: baseSourceEntry.bytes, layoutVerified: true };
            }
            if (comparison) {
                const canonical = value => Array.isArray(value) ? value.map(canonical) : value && typeof value === 'object'
                    ? Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])])) : value;
                const requireEqual = (actual, expected, label) => {
                    if (JSON.stringify(canonical(actual)) !== JSON.stringify(canonical(expected)))
                        throw new Error(`Native compile isolation comparison ${label} mismatch.`);
                };
                // The current manifest must still identify the exact control that just ran, including its descriptor.
                requireEqual({ descriptorIdentity: shader.identity, sha256: sourceEntry.hash, byteLength: sourceEntry.bytes },
                    comparison.controlArtifact, 'same-manifest control identity');
                result.controlArtifact = comparison.controlArtifact;
                const selectedDescriptor = descriptor, expected = comparison.contract;
                const controlDescriptor = ['shade-native-no-modifiers', 'shade-native-no-decals'].includes(selectedPass)
                    ? (await readArtifact('shade-native')).descriptor : selectedDescriptor;
                ({ shader, sourceEntry, descriptor } = await readArtifact(expected.pass));
                for (const key of ['name', 'pass', 'semanticSchemaIdentity', 'entryPoints', 'workgroupSize'])
                    requireEqual(descriptor[key], expected[key], key);
                for (const [key, value] of Object.entries({ schemaVersion: 3, target: 'WebGPUWgsl', sourceLanguage: 'Slang',
                    matrixLayout: 'column-major', coordinates: 'xrengine.webgpu.coordinates.v1', specialization: {}, pipeline: {} }))
                    requireEqual(descriptor[key], value, key);
                requireEqual(descriptor.compilerIdentity, controlDescriptor.compilerIdentity, 'compiler identity');
                requireEqual([...descriptor.defines].sort(), [...controlDescriptor.defines, 'XR_ADV_UBER_RASTER_SURFACE_SCHEMA_VERSION=2'].sort(), 'schema defines');
                requireEqual(descriptor.includes, controlDescriptor.includes, 'includes');
                requireEqual(descriptor.sourceMap, { kind: 'unmapped', path: 'WebGPU/AdvancedShadeNative.slang' }, 'entry source');
                requireEqual(descriptor.sourceMap, controlDescriptor.sourceMap, 'shared entry source');
                const sourceDependencies = value => value.dependencies.filter(dependency => !dependency.path.endsWith('.recipe.json'));
                requireEqual(sourceDependencies(descriptor), sourceDependencies(controlDescriptor), 'source dependency revision');
                requireEqual(descriptor.requiredFeatures, controlDescriptor.requiredFeatures, 'required features');
                requireEqual(descriptor.requiredLimits, { ...controlDescriptor.requiredLimits, maxSamplersPerShaderStage: 11 }, 'required limits');
                requireEqual(recipe.compute, { entryPoint: expected.entryPoints.compute }, 'constants-free control entry');
                requireEqual(descriptor.layout?.vertexBuffers, [], 'vertex buffers');
                const declared = descriptor.layout?.bindings;
                if (!Array.isArray(declared) || declared.length !== 40)
                    throw new Error('Native compile isolation comparison requires all 40 declared bindings.');
                const sorted = values => [...values].sort((a, b) => a.group - b.group || a.binding - b.binding);
                requireEqual(sorted(declared).map(({ group, binding, name, kind, bytes, dynamic, visibility }) =>
                    ({ group, binding, name, kind, bytes, dynamic, visibility })), expected.bindings, 'complete explicit layout');
                // Check shared uniform members/ownership and all other declared metadata against the verified control.
                const expectedDeclared = controlDescriptor.layout.bindings.filter(value => !(value.group === 1 && value.binding === 21))
                    .map(value => value.group === 1 && value.binding === 20 ? { ...value, name: 'UberRasterSurface',
                        physicalName: 'UberRasterSurface_0', kind: 'texture-2d-array-unfilterable-float' } : value);
                requireEqual(sorted(declared), sorted(expectedDeclared), 'shared binding contracts');
                if (descriptor.source?.sha256 !== sourceEntry.hash || descriptor.source?.byteLength !== sourceEntry.bytes)
                    throw new Error('Native compile isolation comparison source disagrees with its verified descriptor.');
                recipe = { pipeline: { label: expected.name }, compute: { entryPoint: descriptor.entryPoints.compute },
                    layout: expected.layout, module: { descriptor: { label: expected.name },
                        sha256: sourceEntry.hash, byteLength: sourceEntry.bytes }, device: recipe.device };
                result.recipe = recipe;
                result.comparisonContract = { catalogKey: `advanced::${descriptor.pass}`, name: descriptor.name,
                    semanticSchemaIdentity: descriptor.semanticSchemaIdentity, workgroupSize: descriptor.workgroupSize,
                    bindingCount: declared.length, layoutVerified: true };
            }
            const bytes = await loader.readVerifiedPayload(sourceEntry.url, sourceEntry.bytes, recipe.module.sha256, shader.source);
            try {
                result.cookedArtifact = { descriptorIdentity: shader.identity, descriptorPath: shader.descriptor,
                    sourcePath: shader.source, sha256: sourceEntry.hash, byteLength: sourceEntry.bytes,
                    entryPoint: descriptor.entryPoints.compute, specialization: descriptor.specialization };
                return new TextDecoder('utf-8', { fatal: true }).decode(bytes);
            } finally { loader.releasePayload(bytes); }
        });
        if (!navigator.gpu) throw new Error('Native compile isolation requires navigator.gpu.');
        const adapter = await bounded('request-adapter', 10000, () => navigator.gpu.requestAdapter(recipe.device.adapter.request));
        if (!adapter) throw new Error('Native compile isolation requestAdapter returned null.');
        const info = adapter.info ?? {};
        result.adapter = { ...capabilities(adapter), info: { vendor: info.vendor ?? '', architecture: info.architecture ?? '',
            device: info.device ?? '', description: info.description ?? '',
            subgroupMinSize: info.subgroupMinSize ?? null, subgroupMaxSize: info.subgroupMaxSize ?? null,
            fallback: adapter.isFallbackAdapter ?? info.isFallbackAdapter ?? null } };
        same(result.adapter.info, recipe.device.adapter.info, 'adapter');
        same(result.adapter.features, recipe.device.adapter.features, 'adapter features');
        same(result.adapter.limits, recipe.device.adapter.limits, 'adapter limits');
        device = await bounded('request-device', 10000, async () => {
            const requested = await adapter.requestDevice(recipe.device.request);
            if (finished) { requested.destroy(); throw new Error('Native compile isolation device arrived after disposal.'); }
            return requested;
        });
        result.device = capabilities(device);
        same(result.device.features, recipe.device.features, 'enabled features');
        same(result.device.limits, recipe.device.limits, 'device limits');
        device.addEventListener('uncapturederror', event => {
            // Driver messages may echo WGSL. Report classification only, never source text.
            if (result.uncapturedErrors.length < 8) result.uncapturedErrors.push({ type: event.error?.constructor?.name ?? 'GPUError', stage: result.stage });
        });
        void device.lost.then(info => {
            if (!result.explicitDestroyRequested) result.deviceLoss = { reason: info.reason, stage: result.stage };
        });
        const { bindGroupLayouts, ...layoutDescriptor } = recipe.layout;
        const layout = device.createPipelineLayout({ ...layoutDescriptor,
            bindGroupLayouts: bindGroupLayouts.map(value => device.createBindGroupLayout(value)) });
        const moduleStart = performance.now();
        const module = device.createShaderModule({ ...recipe.module.descriptor, code: source });
        result.moduleCreationMs = performance.now() - moduleStart;
        await bounded('shader-compilation-info', 45000, async () => {
            const info = await module.getCompilationInfo();
            result.compilationInfo = { messages: info.messages.map(message => ({ type: message.type,
                lineNum: message.lineNum, linePos: message.linePos, offset: message.offset, length: message.length })) };
            if (info.messages.some(message => message.type === 'error')) throw new Error('Native compile isolation module reported WGSL errors.');
        });
        // No application frames, uploads, other pipelines, or GPU submissions accompany this one native compile.
        // Start an independent Node watchdog before entering the GPU API, including a wedged synchronous call.
        await globalThis.nativeCompileStarting({ descriptorIdentity: result.cookedArtifact.descriptorIdentity,
            sha256: result.cookedArtifact.sha256, byteLength: result.cookedArtifact.byteLength,
            entryPoint: result.cookedArtifact.entryPoint });
        result.stage = 'create-compute-pipeline';
        result.compile = { startedAtMs: performance.now(), callReturnedAtMs: null, elapsedMs: null, status: 'pending' };
        const compile = result.compile;
        let pending;
        try { pending = device.createComputePipelineAsync({ ...recipe.pipeline, layout, compute: { ...recipe.compute, module } }); }
        catch (error) {
            compile.status = 'rejected'; compile.elapsedMs = performance.now() - compile.startedAtMs;
            compile.error = { name: error?.name ?? 'Error', reason: error?.reason ?? null };
            throw new Error('Native compute pipeline call rejected; see error classification.');
        }
        compile.callReturnedAtMs = performance.now();
        deadline = setTimeout(() => {
            if (compile.status === 'pending') { compile.status = 'timed-out'; compile.elapsedMs = performance.now() - compile.startedAtMs; }
        }, compileBudgetMs);
        await bounded('create-compute-pipeline', compileBudgetMs, async () => {
            try { await pending; if (compile.status === 'pending') compile.status = 'fulfilled'; }
            catch (error) {
                if (compile.status === 'pending') compile.status = 'rejected';
                compile.error = { name: error?.name ?? 'Error', reason: error?.reason ?? null };
                throw new Error('Native compute pipeline compilation rejected; see error classification.');
            } finally { if (compile.elapsedMs === null) compile.elapsedMs = performance.now() - compile.startedAtMs; }
        });
        result.status = compile.status === 'timed-out' ? 'compile-timeout'
            : result.deviceLoss || result.uncapturedErrors.length ? 'gpu-error' : 'compiled';
    } catch (error) {
        result.status = result.compile?.status === 'timed-out' ? 'compile-timeout' : 'failed';
        // Our explicit messages contain identities/classification, never compiler diagnostic text.
        result.error = String(error?.message ?? error).startsWith('Native compile isolation ')
            || String(error?.message ?? error).startsWith('Native compute pipeline ')
            ? String(error.message).slice(0, 2048) : `Isolation stage failed (${error?.name ?? 'Error'}).`;
    } finally { clearTimeout(deadline); dispose(); }
    return snapshot();
}

/** Diagnostic only: called after the failed application's entire browser process has closed. */
export async function runNativeCompileIsolation(chromium, origin, report, config, instrumentedPage) {
    if (config.nativeOwnedShadowProfileOnce) {
        const target = { profile: 'small', state: 'on', iteration: 0,
            pass: 'shade-native-depth-no-decals', descriptorIdentity: 'f81f44c6868773f0936efebaf72f03b146bc6d6c98aacf4e0f462d044eaf8a6c',
            wgslSha256: '15c26ae39bfb45b1640e9091e381c357fd889429b84888fe61f49f05b66269df',
            wgslBytes: 344708, entryPoint: 'advancedShadeNative' };
        const failures = report.advancedShadowFailures?.filter(value => value.profile === target.profile &&
            value.state === target.state && value.iteration === target.iteration) ?? [];
        const capture = failures.length === 1 ? failures[0].nativeCompile : null;
        const pending = capture?.records?.filter(value => value.status === 'pending') ?? [];
        const record = pending.length === 1 ? pending[0] : null;
        const result = report.nativeCompileIsolation = {
            scope: 'One isolated native compile diagnostic for the failed shadow application.',
            catalogKey: `advanced::${target.pass}`, shadowTarget: target, status: 'skipped', reason: null,
            compileBudgetMs: 45000, freshBrowserProcess: true, applicationBrowserClosed: true,
            cleanup: {} };
        if (!record || !Array.isArray(capture.captureErrors) || capture.captureErrors.length ||
            record.recipeStatus !== 'ready' ||
            record.pass !== target.pass || record.recipe?.pipeline?.label !== `engine-advanced-${target.pass}` ||
            record.recipe?.compute?.entryPoint !== target.entryPoint ||
            record.recipe?.module?.sha256 !== target.wgslSha256 ||
            record.recipe?.module?.byteLength !== target.wgslBytes) {
            result.reason = 'The exact small ON first-iteration pending shadow recipe is unavailable.';
            return;
        }
        const application = report.gpuProcessSnapshots?.find(value => value.stage === 'after-failed-shadow-application');
        if (!application?.gpu?.devices?.length) {
            result.reason = 'The failed application GPU backend is unavailable.';
            return;
        }
        result.recipe = record.recipe;
        result.recipeSha256 = createHash('sha256').update(JSON.stringify(record.recipe)).digest('hex');
        await runNativeCompileArm(chromium, origin, report, config, instrumentedPage, result, record.recipe,
            'advanced-shadow-native-compile-isolation', null, null, target);
        return;
    }
    const capture = report.advancedRenderingFailures?.find(value => value.nativeCompile)?.nativeCompile;
    if (!capture) return;
    const pending = capture.records.filter(value => value.status === 'pending');
    const record = pending.length === 1 ? pending[0] : null;
    const result = report.nativeCompileIsolation = { scope: 'Diagnostic only; does not change the failed application check.',
        status: 'skipped', reason: null, compileBudgetMs: 45000, freshBrowserProcess: true,
        applicationBrowserClosed: true, applicationCapture: capture, cleanup: {} };
    if (!record || record.recipeStatus !== 'ready' || capture.captureErrors.length) {
        result.reason = 'Requires one exact pending native compile with a complete captured recipe and no capture errors.';
        return;
    }
    result.recipe = record.recipe;
    result.recipeSha256 = createHash('sha256').update(JSON.stringify(record.recipe)).digest('hex');
    await runNativeCompileArm(chromium, origin, report, config, instrumentedPage, result, record.recipe,
        'advanced-native-compile-isolation');

    const comparison = report.nativeCompileIsolationComparison = {
        scope: 'Compile-only diagnostic for the existing Uber consumer; no application, material, or image acceptance.',
        catalogKey: 'advanced::shade-uber-native', status: 'skipped', reason: null, compileBudgetMs: 45000,
        freshBrowserProcess: true, applicationBrowserClosed: true, controlBrowserClosed: result.cleanup.browserClosed === true,
        controlRecipeSha256: result.recipeSha256, cleanup: {},
        interpretation: 'Compares the combined Uber helper/body and texture-bank difference. Timeouts are right-censored; neither result changes application checks.' };
    if (!['shade-native', 'shade-native-no-modifiers', 'shade-native-no-decals'].includes(record.pass)) {
        comparison.reason = `The observed ${record.pass} program has no equivalent x1 full-native Uber comparison contract.`;
        return;
    }
    if (record.pass === 'shade-native-no-modifiers')
        comparison.interpretation = 'Compares the selected no-modifiers native program with Uber x1. The full native x1 descriptor and layout are verified; the two compiled programs differ in modifier specialization and Uber texture-bank/body. Timeouts are right-censored; neither result changes application checks.';
    if (record.pass === 'shade-native-no-decals')
        comparison.interpretation = 'Compares the selected no-decals native program with Uber x1. The full native x1 descriptor and layout are verified; the two compiled programs differ in decal specialization and Uber texture-bank/body. Timeouts are right-censored; neither result changes application checks.';
    const control = result.replay?.cookedArtifact;
    if (result.ownedGpuProfile?.requiresJobTermination) {
        comparison.reason = 'Owned Native profiler cleanup could not be verified; requires ephemeral job termination.';
        return;
    }
    if (!comparison.controlBrowserClosed || !control || !result.compileWatchdog) {
        comparison.reason = 'Requires the verified native control to attempt compilation and fully close its owned browser.';
        return;
    }
    comparison.controlArtifact = { descriptorIdentity: control.descriptorIdentity, sha256: control.sha256, byteLength: control.byteLength };
    await runNativeCompileArm(chromium, origin, report, config, instrumentedPage, comparison, record.recipe,
        'advanced-uber-native-compile-isolation', { controlArtifact: comparison.controlArtifact, contract: uberNativeCompileContract() }, result);
    if (comparison.replay?.recipe) {
        comparison.recipe = comparison.replay.recipe;
        comparison.recipeSha256 = createHash('sha256').update(JSON.stringify(comparison.recipe)).digest('hex');
    }
}

/** One owned browser/device lifecycle for both arms; no compilation overlaps another arm. */
async function runNativeCompileArm(chromium, origin, report, config, instrumentedPage, result, recipe, logName,
    comparison = null, controlResult = null, shadowTarget = null) {
    result.launchOptions = browserLaunchOptions(config);
    let browser, context, page, timer, compileTimer, trace, ownedProfile;
    try {
        browser = await chromium.launch(result.launchOptions);
        result.browser = browser.version();
        if (result.browser !== report.browser) throw new Error('Isolation browser version differs from the application.');
        await captureGpuProcessState(browser, result, comparison ? 'before-isolated-uber-native-compile' : 'before-isolated-native-compile');
        if ((config.nativeCompileTrace || config.nativeOwnedShadowProfileOnce) && !comparison)
            result.nativeProfileCapabilities = await captureNativeProfileCapabilities(result.gpuProcessSnapshots[0]?.processes);
        const backend = snapshot => {
            if (!Array.isArray(snapshot?.gpu?.devices) || snapshot.gpu.devices.length === 0) return null;
            const gpu = snapshot.gpu;
            return { devices: gpu.devices, attributes: Object.fromEntries(Object.entries(gpu.auxAttributes ?? {})
                .filter(([key, value]) => /backend|renderer|vendor|version|displayType/i.test(key)
                    && ['string', 'number', 'boolean'].includes(typeof value)).sort(([a], [b]) => a.localeCompare(b))) };
        };
        const application = backend(report.gpuProcessSnapshots?.find(value => value.stage ===
            (shadowTarget ? 'after-failed-shadow-application' : 'after-failed-advanced-application')));
        const isolated = backend(result.gpuProcessSnapshots[0]);
        result.backendComparison = { application, isolated,
            status: !application || !isolated ? 'unavailable' : JSON.stringify(application) === JSON.stringify(isolated) ? 'matched' : 'different' };
        if (result.backendComparison.status === 'different' ||
            shadowTarget && result.backendComparison.status !== 'matched')
            throw new Error('Isolation GPU backend is unavailable or differs from the application.');
        if (controlResult) {
            const control = backend(controlResult.gpuProcessSnapshots?.[0]);
            result.controlBackendComparison = { control, isolated,
                status: !control || !isolated ? 'unavailable' : JSON.stringify(control) === JSON.stringify(isolated) ? 'matched' : 'different' };
            if (result.controlBackendComparison.status === 'different') throw new Error('Isolation GPU backend differs from the native control.');
        }
        ({ page, context } = await instrumentedPage(browser, origin, report, logName, config));
        await page.goto(`${origin}/__audio-probe/`, { waitUntil: 'domcontentloaded' });
        // Driver console/page errors can quote shader text. Keep this diagnostic's logs source-free.
        page.removeAllListeners('console');
        page.removeAllListeners('pageerror');
        const logs = report.browserLogs[logName];
        page.on('console', event => { if (logs.length < 2000) logs.push({ type: event.type(), text: 'Diagnostic console message omitted to avoid shader source disclosure.' }); });
        page.on('pageerror', () => { if (logs.length < 2000) logs.push({ type: 'pageerror', text: 'Diagnostic page error; source text omitted.' }); });
        if (config.nativeCompileTrace && !comparison)
            trace = await startNativeCompileTrace(browser, config.output, result);
        if (!comparison) ownedProfile = createOwnedGpuProfile({ browser, page, config, result });
        let rejectCompileDeadline;
        const compileDeadline = new Promise((_, reject) => { rejectCompileDeadline = reject; });
        await page.exposeFunction('nativeCompileStarting', artifact => {
            if (shadowTarget && (artifact?.descriptorIdentity !== shadowTarget.descriptorIdentity ||
                artifact?.sha256 !== shadowTarget.wgslSha256 || artifact?.byteLength !== shadowTarget.wgslBytes ||
                artifact?.entryPoint !== shadowTarget.entryPoint))
                throw new Error('Native compile isolation verified source differs from the pinned shadow target.');
            if (shadowTarget) result.verifiedCookedArtifact = artifact;
            result.compileWatchdog = { startedUtc: new Date().toISOString(), startedAtNodeMonotonicMs: performance.now(),
                budgetMs: result.compileBudgetMs };
            compileTimer = setTimeout(() => {
                result.compileWatchdog.expired = true;
                ownedProfile?.cancel();
                trace?.mark('compile-deadline');
                rejectCompileDeadline(new Error('Native compile isolation exceeded its 45000 ms external compile deadline.'));
            }, result.compileBudgetMs);
            trace?.mark('compile-start');
            trace?.startProcessCpuObservation(result.compileBudgetMs, result.compileWatchdog.startedAtNodeMonotonicMs);
            ownedProfile?.begin();
        });
        // Also bound a wedged page/GPU IPC path, whose in-page timer might never run.
        const replay = page.evaluate(replayNativeCompile, { recipe,
            manifestUrl: `${origin}/__game/content/manifest.json`, compileBudgetMs: result.compileBudgetMs,
            comparison, shadowTarget });
        result.replay = await Promise.race([replay, compileDeadline,
            ...(ownedProfile ? [ownedProfile.fatal.then(() => {
                throw new Error('Owned Native profiler cleanup requires immediate ephemeral job termination.');
            })] : []), new Promise((_, reject) => {
            timer = setTimeout(() => reject(new Error('Native compile isolation page exceeded its 160000 ms total envelope.')), 160000);
        })]);
        result.status = result.replay.status;
        ownedProfile?.cancel();
    } catch (error) {
        ownedProfile?.cancel();
        result.status = result.ownedGpuProfile?.requiresJobTermination ? 'aborted-for-profile-cleanup'
            : result.compileWatchdog?.expired ? 'compile-watchdog-timeout' : 'diagnostic-failed';
        result.error = String(error).slice(0, 2048);
    } finally {
        clearTimeout(timer);
        await ownedProfile?.finish();
        clearTimeout(compileTimer);
        const fatalProfileCleanup = result.ownedGpuProfile?.requiresJobTermination === true;
        if (fatalProfileCleanup) result.status = 'aborted-for-profile-cleanup';
        const fatalBoundedCleanup = async (operation, milliseconds) => {
            let cleanupTimer;
            try { return await Promise.race([operation.then(() => true, () => false),
                new Promise(resolve => { cleanupTimer = setTimeout(() => resolve(false), milliseconds); })]); }
            finally { clearTimeout(cleanupTimer); }
        };
        trace?.mark('node-cleanup-started');
        if (page && !result.replay) {
            let cleanupTimer;
            try {
                result.replay = await Promise.race([page.evaluate(() => {
                    const probe = globalThis.nativeCompileIsolation;
                    probe?.dispose();
                    return probe?.snapshot() ?? null;
                }), new Promise(resolve => { cleanupTimer = setTimeout(() => resolve(null), fatalProfileCleanup ? 250 : 1000); })]);
            } catch { /* Closing the owned process releases a wedged device. */ }
            finally { clearTimeout(cleanupTimer); }
        }
        if (page && result.replay) result.cleanup.deviceDestroyed = result.replay.cleanup.deviceDestroyed === true;
        if (trace) {
            if (fatalProfileCleanup) await fatalBoundedCleanup(trace.finish(), 250);
            else await trace.finish();
        }
        if (context) {
            let closeTimer;
            try {
                result.cleanup.contextClosed = await Promise.race([context.close().then(() => true, () => false),
                    new Promise(resolve => { closeTimer = setTimeout(() => resolve(false), fatalProfileCleanup ? 250 : 1000); })]);
            } finally { clearTimeout(closeTimer); }
        }
        if (browser) {
            if (fatalProfileCleanup) result.cleanup.browserClosed = await fatalBoundedCleanup(browser.close(), 750);
            else await browser.close().then(() => { result.cleanup.browserClosed = true; }, () => { result.cleanup.browserClosed = false; });
        }
        if (trace) {
            if (fatalProfileCleanup) await fatalBoundedCleanup(trace.browserClosed(result.cleanup.browserClosed === true), 250);
            else await trace.browserClosed(result.cleanup.browserClosed === true);
        }
    }
}
