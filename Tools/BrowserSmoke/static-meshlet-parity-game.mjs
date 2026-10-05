import fs from 'node:fs/promises';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { capture, captureUntil } from './canvas-capture.mjs';

function assert(condition, message) { if (!condition) throw new Error(message); }
const panelVisible = pixels => pixels.colorfulLeft > 100;
const stages = ['engine-meshlets-select-lod', 'engine-meshlets-cull-expand', 'engine-meshlets-finalize-indexed'];
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const mappedVariant = { semantic: 'StandardLitTexture', semanticVersion: 1,
    target: 'WebGPUWgsl', pass: 'opaque-forward', vertexProfile: 'position-normal-tangent-uv-v1' };
const outputPrecedence = ['linear-hdr-local-shadows-v1', 'linear-hdr-directional-shadow-v1', 'linear-hdr-v1'];

async function publishedMappedArtifact(root) {
    const manifest = JSON.parse(await fs.readFile(path.join(root, 'content', 'manifest.json'), 'utf8'));
    const matches = manifest.materialVariants?.filter(entry =>
        Object.entries(mappedVariant).every(([key, value]) => entry[key] === value)) ?? [];
    const outputProfile = outputPrecedence.find(profile => matches.some(entry => entry.outputProfile === profile));
    const variants = matches.filter(entry => entry.outputProfile === outputProfile);
    assert(outputProfile && variants.length === 1 && /^[a-f0-9]{64}$/.test(variants[0].descriptorIdentity),
        `BrowserSmoke.StaticMeshletVariant: expected one published mapped panel variant: ${JSON.stringify(matches)}`);
    const variant = variants[0];
    const shader = manifest.shaderArtifacts?.filter(entry => entry.identity === variant.descriptorIdentity) ?? [];
    assert(shader.length === 1 &&
        shader[0].descriptor === `/engine/Shaders/Cooked/${variant.descriptorIdentity}.json` &&
        shader[0].source === `/engine/Shaders/Cooked/${variant.descriptorIdentity}.wgsl`,
    'BrowserSmoke.StaticMeshletVariant: selected descriptor is absent from the published shader catalog.');
    const readPayload = async virtualPath => {
        const entries = manifest.assets?.filter(entry => entry.path === virtualPath) ?? [];
        assert(entries.length === 1 && /^[a-f0-9]{64}$/.test(entries[0].hash) &&
            entries[0].url === `payload/${entries[0].hash}.bin` &&
            Number.isSafeInteger(entries[0].bytes) && entries[0].bytes > 0 && entries[0].bytes <= 1048576,
        `BrowserSmoke.StaticMeshletArtifactPayload: invalid published asset ${virtualPath}.`);
        const bytes = await fs.readFile(path.join(root, 'content', entries[0].url));
        assert(bytes.length === entries[0].bytes && sha256(bytes) === entries[0].hash,
            `BrowserSmoke.StaticMeshletArtifactPayload: hash mismatch for ${virtualPath}.`);
        return { bytes, hash: entries[0].hash };
    };
    const descriptorPayload = await readPayload(shader[0].descriptor);
    assert(descriptorPayload.hash === variant.descriptorIdentity,
        'BrowserSmoke.StaticMeshletArtifactDescriptor: the manifest selected a different descriptor identity.');
    const descriptor = JSON.parse(descriptorPayload.bytes.toString('utf8'));
    const sourcePayload = await readPayload(shader[0].source);
    const source = new TextDecoder('utf-8', { fatal: true }).decode(sourcePayload.bytes);
    assert(Buffer.from(source, 'utf8').equals(sourcePayload.bytes) &&
        descriptor.schemaVersion === 3 && typeof descriptor.name === 'string' && descriptor.name.length > 0 &&
        descriptor.pass === variant.pass &&
        descriptor.target === variant.target &&
        Object.entries(mappedVariant).every(([key, value]) => key === 'semanticVersion'
            ? descriptor.materialVariant?.semanticVersion === value
            : key === 'pass' || key === 'target' || descriptor.materialVariant?.[key] === value) &&
        descriptor.materialVariant?.outputProfile === variant.outputProfile &&
        typeof descriptor.entryPoints?.vertex === 'string' && descriptor.entryPoints.vertex.length > 0 &&
        typeof descriptor.entryPoints?.fragment === 'string' && descriptor.entryPoints.fragment.length > 0 &&
        descriptor.source?.sha256 === sourcePayload.hash &&
        descriptor.source?.byteLength === sourcePayload.bytes.length &&
        descriptor.source?.url === `${sourcePayload.hash}.wgsl`,
    `BrowserSmoke.StaticMeshletArtifactDescriptor: selected mapped program disagrees with its verified source: ${variant.descriptorIdentity}.`);
    return { descriptorIdentity: variant.descriptorIdentity, outputProfile: variant.outputProfile,
        name: descriptor.name, vertexEntry: descriptor.entryPoints.vertex,
        fragmentEntry: descriptor.entryPoints.fragment, wgslSha256: sourcePayload.hash };
}

async function installHostObservation(page) {
    await page.evaluate(async () => {
        const { EngineCanvasHost } = await import('./engine-canvas-host.js');
        const original = EngineCanvasHost.prototype.start;
        const createRenderer = EngineCanvasHost.prototype.createRenderer;
        const frame = EngineCanvasHost.prototype.frame;
        let host, renderer, accepted;
        const hostSnapshot = () => {
            const frameStats = host.renderer?.getStatistics()?.engineFrame;
            return Object.freeze({
                status: host.engine.GetCanvasRenderingStatus(),
                frame: frameStats ? Object.freeze({ ...frameStats,
                    errorScopes: frameStats.errorScopes && Object.freeze({ ...frameStats.errorScopes }) }) : null,
                rendererReady: host.rendererReady, failed: host.failed,
                preparationState: host.engine.GetCanvasPreparationState(),
                session: host.session, epoch: host.epoch, surfaceGeneration: host.surfaceGeneration,
                canvasExtent: Object.freeze([host.canvas.width, host.canvas.height]),
            });
        };
        const restore = () => {
            EngineCanvasHost.prototype.start = original;
            EngineCanvasHost.prototype.createRenderer = createRenderer;
            EngineCanvasHost.prototype.frame = frame;
            globalThis.staticMeshletRestoreGpuObservation?.();
        };
        function observeStart(...args) {
            host = this;
            accepted = null;
            globalThis.staticMeshletHostSnapshot = hostSnapshot;
            return original.apply(this, args);
        }
        EngineCanvasHost.prototype.start = observeStart;
        // The constructor binds this method, so install the wrapper before the player creates its host.
        EngineCanvasHost.prototype.frame = function (...args) {
            const submittedBefore = this.renderer?.getStatistics()?.engineFrame?.submittedFrames ?? 0;
            const result = frame.apply(this, args);
            const submittedAfter = this.renderer?.getStatistics()?.engineFrame?.submittedFrames ?? 0;
            if (this === host && submittedAfter > submittedBefore && this.rendererReady && !this.failed) {
                const snapshot = hostSnapshot();
                if (snapshot.preparationState > 0 &&
                    /indexed strategy=GpuMeshletZeroReadback; indexed reason=Ready(?:;|$)/.test(snapshot.status))
                    accepted = Object.freeze({ host: snapshot, renderer: this.renderer,
                        gpu: globalThis.staticMeshletGpuRawSnapshot?.() });
            }
            return result;
        };
        globalThis.staticMeshletAcceptedFrameSnapshot = () => {
            if (!accepted) return null;
            const selected = accepted;
            return { host: selected.host, gpu: selected.gpu,
                isOwnerCurrent: () => selected.renderer === host.renderer };
        };
        EngineCanvasHost.prototype.createRenderer = function (...args) {
            renderer = createRenderer.apply(this, args);
            return renderer;
        };
        globalThis.staticMeshletRestoreHostObservation = restore;
        globalThis.staticMeshletStop = async () => {
            try {
                if (!host) throw new Error('The owned canvas host was never captured.');
                await host.stop();
                return { rendererDisposed: renderer?._disposed === true,
                    resources: renderer?.getStatistics()?.resources ?? null,
                    readMaps: globalThis.staticMeshletGpuCounts?.().readMaps ?? null };
            } finally { restore(); }
        };
    });
}

function installGpuObservation(expectedArtifact) {
    const counts = { compute: Object.create(null), directIndexed: 0, indirectIndexed: 0,
        readMaps: 0, readMapLabels: [], queueSubmits: 0, rasterOverflow: 0 };
    const passes = new WeakMap();
    const modules = new WeakMap(), pipelines = new WeakMap(), rasterPasses = new WeakMap();
    const rasterDraws = new Map();
    const wrappers = [];
    const wrap = (prototype, name, replacement) => {
        const original = prototype[name];
        prototype[name] = replacement(original);
        wrappers.push(() => { prototype[name] = original; });
    };
    wrap(GPUDevice.prototype, 'createShaderModule', original => function (descriptor) {
        const module = original.call(this, descriptor);
        modules.set(module, { label: String(descriptor?.label ?? ''), code: descriptor?.code ?? '' });
        return module;
    });
    const rememberPipeline = (pipeline, descriptor) => {
        pipelines.set(pipeline, {
            label: String(descriptor?.label ?? ''),
            sharedModule: descriptor?.vertex?.module === descriptor?.fragment?.module,
            vertex: { module: descriptor?.vertex?.module, entryPoint: descriptor?.vertex?.entryPoint ?? '' },
            fragment: { module: descriptor?.fragment?.module, entryPoint: descriptor?.fragment?.entryPoint ?? '' },
        });
    };
    wrap(GPUDevice.prototype, 'createRenderPipelineAsync', original => function (descriptor) {
        const result = original.call(this, descriptor);
        result.then(pipeline => rememberPipeline(pipeline, descriptor), () => {});
        return result;
    });
    wrap(GPUDevice.prototype, 'createRenderPipeline', original => function (descriptor) {
        const pipeline = original.call(this, descriptor);
        rememberPipeline(pipeline, descriptor);
        return pipeline;
    });
    const recordRaster = (pass, kind) => {
        const pipeline = rasterPasses.get(pass);
        if (!pipeline) return;
        let draws = rasterDraws.get(pipeline);
        if (!draws) {
            if (rasterDraws.size >= 64) { counts.rasterOverflow++; return; }
            draws = { direct: 0, indirect: 0 };
            rasterDraws.set(pipeline, draws);
        }
        draws[kind]++;
    };
    wrap(GPURenderPassEncoder.prototype, 'setPipeline', original => function (pipeline) {
        const result = original.call(this, pipeline);
        rasterPasses.set(this, pipeline);
        return result;
    });
    wrap(GPURenderPassEncoder.prototype, 'drawIndexed', original => function (...args) {
        const result = original.apply(this, args);
        counts.directIndexed++;
        recordRaster(this, 'direct');
        return result;
    });
    wrap(GPURenderPassEncoder.prototype, 'drawIndexedIndirect', original => function (...args) {
        const result = original.apply(this, args);
        counts.indirectIndexed++;
        recordRaster(this, 'indirect');
        return result;
    });
    const setPipeline = GPUComputePassEncoder.prototype.setPipeline;
    GPUComputePassEncoder.prototype.setPipeline = function (pipeline) {
        const result = setPipeline.call(this, pipeline);
        passes.set(this, String(pipeline?.label ?? '').slice(0, 128));
        return result;
    };
    const dispatch = GPUComputePassEncoder.prototype.dispatchWorkgroups;
    GPUComputePassEncoder.prototype.dispatchWorkgroups = function (...args) {
        const result = dispatch.apply(this, args);
        const label = passes.get(this) ?? '';
        if (Object.hasOwn(counts.compute, label) || Object.keys(counts.compute).length < 64)
            counts.compute[label] = (counts.compute[label] ?? 0) + 1;
        return result;
    };
    const dispatchIndirect = GPUComputePassEncoder.prototype.dispatchWorkgroupsIndirect;
    GPUComputePassEncoder.prototype.dispatchWorkgroupsIndirect = function (...args) {
        const result = dispatchIndirect.apply(this, args);
        const label = passes.get(this) ?? '';
        if (Object.hasOwn(counts.compute, label) || Object.keys(counts.compute).length < 64)
            counts.compute[label] = (counts.compute[label] ?? 0) + 1;
        return result;
    };
    const mapAsync = GPUBuffer.prototype.mapAsync;
    GPUBuffer.prototype.mapAsync = function (mode, ...args) {
        if ((mode & GPUMapMode.READ) !== 0) {
            counts.readMaps++;
            if (counts.readMapLabels.length < 16) counts.readMapLabels.push(String(this.label ?? '').slice(0, 128));
        }
        return mapAsync.call(this, mode, ...args);
    };
    const submit = GPUQueue.prototype.submit;
    GPUQueue.prototype.submit = function (...args) {
        const result = submit.apply(this, args);
        counts.queueSubmits++;
        return result;
    };
    globalThis.staticMeshletGpuCounts = () => ({ ...counts, compute: { ...counts.compute },
        readMapLabels: [...counts.readMapLabels] });
    const frozenStage = input => {
        const module = modules.get(input.module);
        return module ? Object.freeze({ label: module.label, code: module.code, entryPoint: input.entryPoint }) : null;
    };
    globalThis.staticMeshletGpuRawSnapshot = () => {
        const raster = [];
        for (const [pipeline, draws] of rasterDraws) {
            const description = pipelines.get(pipeline);
            raster.push(Object.freeze({ label: description?.label ?? String(pipeline?.label ?? ''),
                direct: draws.direct, indirect: draws.indirect,
                sharedModule: description?.sharedModule ?? false,
                vertex: description?.label === expectedArtifact.name ? frozenStage(description.vertex) : null,
                fragment: description?.label === expectedArtifact.name ? frozenStage(description.fragment) : null }));
        }
        const observed = globalThis.staticMeshletGpuCounts();
        Object.freeze(observed.compute);
        Object.freeze(observed.readMapLabels);
        return Object.freeze({ ...observed, raster: Object.freeze(raster) });
    };
    globalThis.staticMeshletGpuProofCounts = () => ({
        readMaps: counts.readMaps, rasterOverflow: counts.rasterOverflow,
        mappedDirect: [...rasterDraws].reduce((total, [pipeline, draws]) =>
            total + (pipelines.get(pipeline)?.label === expectedArtifact.name ? draws.direct : 0), 0),
    });
    globalThis.staticMeshletGpuSnapshot = async (frozen = globalThis.staticMeshletGpuRawSnapshot()) => {
        const digestCache = new Map();
        const stage = async input => {
            if (!input) return null;
            let sha256 = digestCache.get(input.code);
            if (!sha256) {
                const bytes = new TextEncoder().encode(input.code);
                sha256 = [...new Uint8Array(await crypto.subtle.digest('SHA-256', bytes))]
                    .map(value => value.toString(16).padStart(2, '0')).join('');
                digestCache.set(input.code, sha256);
            }
            return { label: input.label, sha256, entryPoint: input.entryPoint };
        };
        const raster = [];
        for (const draw of frozen.raster) {
            raster.push({ label: draw.label, direct: draw.direct, indirect: draw.indirect,
                sharedModule: draw.sharedModule,
                vertex: await stage(draw.vertex), fragment: await stage(draw.fragment) });
        }
        return { ...frozen, raster };
    };
    globalThis.staticMeshletRestoreGpuObservation = () => {
        GPUComputePassEncoder.prototype.setPipeline = setPipeline;
        GPUComputePassEncoder.prototype.dispatchWorkgroups = dispatch;
        GPUComputePassEncoder.prototype.dispatchWorkgroupsIndirect = dispatchIndirect;
        GPUBuffer.prototype.mapAsync = mapAsync;
        GPUQueue.prototype.submit = submit;
        for (const restore of wrappers.reverse()) restore();
    };
}

async function start(page, origin, prefix, observe) {
    let installed = false;
    await page.route(`${origin}/${prefix}/engine-player.js`, async route => {
        try {
            await page.locator('#input-surface').waitFor({ state: 'attached', timeout: 5000 });
            if (observe) await installHostObservation(page);
            installed = true;
            await route.continue();
        } catch {
            await route.abort('failed');
        }
    }, { times: 1 });
    await page.goto(`${origin}/${prefix}/index.html`, { waitUntil: 'domcontentloaded' });
    assert(installed, `BrowserSmoke.StaticMeshletObserver: ${prefix} player started before fixture observation.`);
    await page.waitForFunction(() => ['running', 'failed'].includes(document.querySelector('#status')?.dataset.state));
    const detail = await page.locator('#status').textContent();
    assert(await page.locator('#status').getAttribute('data-state') === 'running' &&
        /StaticMeshletParity|Static Meshlet Parity/i.test(detail),
        `BrowserSmoke.StaticMeshletStartup: ${detail}`);
    return detail;
}

function ready(snapshot, expectedArtifact) {
    const current = snapshot?.host, accepted = snapshot?.accepted;
    const frame = accepted?.host, gpu = accepted?.gpu;
    return current?.rendererReady && !current.failed && current.preparationState >= 0 &&
        /indexed strategy=GpuMeshletZeroReadback;/.test(current.status) &&
        frame?.rendererReady && !frame.failed && frame.preparationState > 0 && frame.ownerCurrent &&
        /indexed strategy=GpuMeshletZeroReadback; indexed reason=Ready(?:;|$)/.test(frame.status) &&
        frame.session > 0 && frame.session === current.session &&
        frame.epoch > 0 && frame.epoch === current.epoch &&
        frame.surfaceGeneration != null && frame.surfaceGeneration === current.surfaceGeneration &&
        frame.canvasExtent[0] > 0 && frame.canvasExtent[1] > 0 &&
        frame.canvasExtent[0] === current.canvasExtent[0] &&
        frame.canvasExtent[1] === current.canvasExtent[1] &&
        frame.frame?.submittedFrames > 0 &&
        stages.every(stage => gpu?.compute[stage] > 0) &&
        mappedDraw(gpu, 'indirect', expectedArtifact) > 0 &&
        mappedDraw(gpu, 'direct', expectedArtifact) === 0 &&
        gpu.readMaps === 0 && gpu.rasterOverflow === 0 &&
        snapshot.currentProof?.readMaps === 0 && snapshot.currentProof?.rasterOverflow === 0 &&
        snapshot.currentProof?.mappedDirect === 0;
}

async function gpuSnapshot(page) {
    return page.evaluate(async () => {
        const accepted = globalThis.staticMeshletAcceptedFrameSnapshot?.();
        const currentGpu = globalThis.staticMeshletGpuRawSnapshot?.();
        const gpu = await globalThis.staticMeshletGpuSnapshot?.(currentGpu);
        const acceptedGpu = accepted && await globalThis.staticMeshletGpuSnapshot?.(accepted.gpu);
        return { host: globalThis.staticMeshletHostSnapshot?.(), gpu,
            currentProof: globalThis.staticMeshletGpuProofCounts?.(),
            accepted: accepted && { host: { ...accepted.host, ownerCurrent: accepted.isOwnerCurrent() },
                gpu: acceptedGpu } };
    });
}

function mappedDraw(gpu, kind, expectedArtifact) {
    return gpu?.raster?.filter(entry => entry.label === expectedArtifact.name)
        .reduce((total, entry) => total + entry[kind], 0) ?? 0;
}

function mappedRaster(gpu, kind, expectedArtifact) {
    const candidates = gpu?.raster?.filter(entry => entry.label === expectedArtifact.name && entry[kind] > 0) ?? [];
    assert(candidates.length === 1 && candidates[0].sharedModule &&
        candidates[0].vertex && candidates[0].fragment &&
        candidates[0].vertex.label === expectedArtifact.name &&
        candidates[0].fragment.label === expectedArtifact.name &&
        candidates[0].vertex.entryPoint === expectedArtifact.vertexEntry &&
        candidates[0].fragment.entryPoint === expectedArtifact.fragmentEntry &&
        candidates[0].vertex.sha256 === expectedArtifact.wgslSha256 &&
        candidates[0].fragment.sha256 === expectedArtifact.wgslSha256,
    `BrowserSmoke.StaticMeshletMappedRaster: expected one original mapped program for ${kind}: ${JSON.stringify(gpu?.raster)}`);
    return candidates[0];
}

function assertSameRaster(cpu, gpu) {
    assert(cpu.label === gpu.label &&
        cpu.sharedModule && gpu.sharedModule &&
        cpu.vertex.label === gpu.vertex.label && cpu.vertex.sha256 === gpu.vertex.sha256 &&
        cpu.vertex.entryPoint === gpu.vertex.entryPoint &&
        cpu.fragment.label === gpu.fragment.label && cpu.fragment.sha256 === gpu.fragment.sha256 &&
        cpu.fragment.entryPoint === gpu.fragment.entryPoint,
    `BrowserSmoke.StaticMeshletRasterIdentity: CPU and GPU indexed draws changed original mapped modules: ${JSON.stringify({ cpu, gpu })}`);
}

async function stopObserved(page) {
    return page.evaluate(async () => {
        const stop = globalThis.staticMeshletStop;
        if (typeof stop !== 'function') {
            globalThis.staticMeshletRestoreGpuObservation?.();
            throw new Error('The engine host cleanup hook was not installed.');
        }
        let timer;
        try {
            return await Promise.race([stop(), new Promise((_, reject) => {
                timer = setTimeout(() => reject(new Error('Owned engine host stop exceeded 45 seconds.')), 45000);
            })]);
        } finally {
            clearTimeout(timer);
            globalThis.staticMeshletRestoreHostObservation?.();
        }
    });
}

function assertDisposed(disposal) {
    const resources = disposal?.resources;
    assert(disposal?.rendererDisposed && resources?.live === 0 && resources?.retiring === 0 &&
        resources?.readbackTickets === 0 && resources?.pipelineCacheEntries === 0 &&
        resources?.shaderModuleCacheEntries === 0 && disposal?.readMaps === 0,
    `BrowserSmoke.StaticMeshletDisposal: ${JSON.stringify(disposal)}`);
}

async function captureSettled(page, config, name) {
    let previous = await captureUntil(page, config, name, null, panelVisible,
        'BrowserSmoke.StaticMeshletSurfaceMissing: the saved mapped panel did not reach the canvas.');
    const deadline = Date.now() + Math.min(config.timeout, 10000);
    let stable = 0;
    while (Date.now() < deadline) {
        await page.waitForTimeout(350);
        const current = await capture(page, config.output, name, previous.image);
        stable = current.pixels.changed <= Math.max(16, current.pixels.colorfulLeft * 0.002) ? stable + 1 : 0;
        if (stable === 2) return current;
        previous = current;
    }
    throw new Error(`BrowserSmoke.StaticMeshletUnsettled: ${name} did not hold a matching static pose.`);
}

async function comparePanels(page, baselineImage, gpuImage) {
    return page.evaluate(async images => {
        async function decode(base64) {
            const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
            const bitmap = await createImageBitmap(new Blob([bytes], { type: 'image/png' }));
            const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
            const ctx = canvas.getContext('2d', { willReadFrequently: true });
            ctx.drawImage(bitmap, 0, 0); bitmap.close();
            return { width: canvas.width, height: canvas.height,
                data: ctx.getImageData(0, 0, canvas.width, canvas.height).data };
        }
        const cpu = await decode(images.cpu), gpu = await decode(images.gpu);
        if (cpu.width !== gpu.width || cpu.height !== gpu.height)
            return { sizeMismatch: { cpu: [cpu.width, cpu.height], gpu: [gpu.width, gpu.height] } };
        let union = 0, intersection = 0, difference = 0, mismatchedColorPixels = 0;
        let imagePixels = 0, imageDifference = 0, imageMismatchedPixels = 0;
        let cpuForeground = 0, gpuForeground = 0;
        const visible = (data, offset) => {
            const r = data[offset], g = data[offset + 1], b = data[offset + 2];
            return Math.max(r, g, b) > 70 && Math.max(r, g, b) - Math.min(r, g, b) > 35;
        };
        for (let y = 2; y < cpu.height - 2; y++)
            for (let x = 2; x < cpu.width - 2; x++) {
                const offset = (y * cpu.width + x) * 4;
                imagePixels++;
                let imageMaximumDifference = 0;
                for (let channel = 0; channel < 3; channel++) {
                    const delta = Math.abs(cpu.data[offset + channel] - gpu.data[offset + channel]);
                    imageDifference += delta;
                    imageMaximumDifference = Math.max(imageMaximumDifference, delta);
                }
                if (imageMaximumDifference > 8) imageMismatchedPixels++;
                if (y < cpu.height * 0.12 || y >= cpu.height * 0.88 ||
                    x < cpu.width * 0.03 || x >= cpu.width * 0.48) continue;
                const a = visible(cpu.data, offset), b = visible(gpu.data, offset);
                if (a) cpuForeground++;
                if (b) gpuForeground++;
                if (!a && !b) continue;
                union++;
                if (a && b) {
                    intersection++;
                    let maximumDifference = 0;
                    for (let channel = 0; channel < 3; channel++)
                    {
                        const delta = Math.abs(cpu.data[offset + channel] - gpu.data[offset + channel]);
                        difference += delta;
                        maximumDifference = Math.max(maximumDifference, delta);
                    }
                    if (maximumDifference > 8) mismatchedColorPixels++;
                }
            }
        return { imagePixels, imageMeanRgbDifference: imageDifference / (3 * imagePixels),
            imageMismatchedPixels, imageMismatchedFraction: imageMismatchedPixels / imagePixels,
            cpuForeground, gpuForeground, cpuGpuForegroundUnion: union, intersection,
            overlap: union ? intersection / union : 0,
            meanRgbDifference: intersection ? difference / (3 * intersection) : null,
            mismatchedColorPixels, mismatchedColorFraction: intersection ? mismatchedColorPixels / intersection : null };
    }, { cpu: baselineImage.toString('base64'), gpu: gpuImage.toString('base64') });
}

function assertParity(comparison, extent) {
    assert(comparison.cpuForeground > 100 && comparison.gpuForeground > 100 &&
        comparison.overlap >= 0.99 && comparison.meanRgbDifference <= 3 &&
        comparison.mismatchedColorFraction <= 0.01 &&
        comparison.imageMeanRgbDifference <= 2 && comparison.imageMismatchedFraction <= 0.01,
    `BrowserSmoke.StaticMeshletCpuParity: ${extent} mapped-panel captures diverged: ${JSON.stringify(comparison)}`);
}

/** Qualifies an Editor-published GPU scene against the same Editor-published CPU scene. */
export async function staticMeshletParityGameCheck(browser, origin, report, config, instrumentedPage, assertNoBrowserErrors) {
    report.staticMeshletIterations = [];
    const [cpuArtifact, gpuArtifact] = await Promise.all([
        publishedMappedArtifact(config.baselinePublish), publishedMappedArtifact(config.gamePublish)]);
    assert(cpuArtifact.descriptorIdentity === gpuArtifact.descriptorIdentity &&
        cpuArtifact.wgslSha256 === gpuArtifact.wgslSha256 &&
        cpuArtifact.outputProfile === gpuArtifact.outputProfile,
    `BrowserSmoke.StaticMeshletPublishedRaster: CPU and GPU bundles selected different mapped programs: ${JSON.stringify({ cpuArtifact, gpuArtifact })}`);
    report.staticMeshletArtifacts = { cpu: cpuArtifact, gpu: gpuArtifact };
    const baselinePage = await instrumentedPage(browser, origin, report, 'static-meshlet-cpu-baseline', config);
    const resizedExtents = [860, 940];
    const baselineResized = [];
    let baseline, baselineRaster;
    try {
        await baselinePage.page.addInitScript(installGpuObservation, cpuArtifact);
        const detail = await start(baselinePage.page, origin, '__baseline', true);
        assert(baselinePage.events.some(event => event.text?.includes('StaticMeshletParity requested=CpuDirect ')),
            'BrowserSmoke.StaticMeshletCpuRequested: the explicit CPU startup marker was absent.');
        baseline = await captureSettled(baselinePage.page, config, 'static-meshlet-cpu-baseline');
        const snapshot = await gpuSnapshot(baselinePage.page);
        assert(snapshot.host?.frame?.submittedFrames > 0 && snapshot.gpu?.directIndexed > 0 &&
            mappedDraw(snapshot.gpu, 'direct', cpuArtifact) > 0 &&
            mappedDraw(snapshot.gpu, 'indirect', cpuArtifact) === 0 &&
            snapshot.gpu.readMaps === 0 && snapshot.gpu.rasterOverflow === 0,
        `BrowserSmoke.StaticMeshletCpuBaseline: ${JSON.stringify(snapshot)}`);
        baselineRaster = mappedRaster(snapshot.gpu, 'direct', cpuArtifact);
        report.staticMeshletBaseline = { detail, pixels: baseline.pixels, snapshot };
        for (let iteration = 0; iteration < resizedExtents.length; iteration++) {
            const canvas = baselinePage.page.locator('#input-surface');
            const beforeSize = await canvas.evaluate(element => [element.width, element.height]);
            await baselinePage.page.setViewportSize({ width: resizedExtents[iteration], height: 780 });
            await baselinePage.page.waitForFunction(([width, height]) => {
                const canvas = document.querySelector('#input-surface');
                return canvas.width !== width || canvas.height !== height;
            }, beforeSize);
            const image = await captureSettled(baselinePage.page, config, `static-meshlet-cpu-resized-${iteration}`);
            const resizedSnapshot = await gpuSnapshot(baselinePage.page);
            assert(resizedSnapshot.host?.frame?.submittedFrames > snapshot.host.frame.submittedFrames &&
                mappedDraw(resizedSnapshot.gpu, 'direct', cpuArtifact) >
                    mappedDraw(snapshot.gpu, 'direct', cpuArtifact) &&
                resizedSnapshot.gpu.readMaps === 0,
            `BrowserSmoke.StaticMeshletCpuResize: ${JSON.stringify(resizedSnapshot)}`);
            assertSameRaster(baselineRaster, mappedRaster(resizedSnapshot.gpu, 'direct', cpuArtifact));
            baselineResized.push(image);
            report.staticMeshletBaseline.resized ??= [];
            report.staticMeshletBaseline.resized.push({ pixels: image.pixels, snapshot: resizedSnapshot });
        }
        assertNoBrowserErrors(baselinePage.events);
    } finally {
        try {
            const disposal = await stopObserved(baselinePage.page);
            report.staticMeshletBaseline ??= {};
            report.staticMeshletBaseline.disposal = disposal;
            assertDisposed(disposal);
            assertNoBrowserErrors(baselinePage.events);
        } finally { await baselinePage.context.close(); }
    }

    for (let iteration = 0; iteration < 2; iteration++) {
        const { page, context, events } = await instrumentedPage(browser, origin, report,
            `static-meshlet-gpu-${iteration}`, config);
        const iterationReport = { iteration };
        report.staticMeshletIterations.push(iterationReport);
        try {
            await page.addInitScript(installGpuObservation, gpuArtifact);
            const detail = await start(page, origin, '__game', true);
            iterationReport.detail = detail;
            assert(events.some(event => event.text?.includes('StaticMeshletParity requested=GpuMeshletZeroReadback ')),
                'BrowserSmoke.StaticMeshletGpuRequested: the explicit GPU startup marker was absent.');
            await page.waitForFunction(() => {
                const accepted = globalThis.staticMeshletAcceptedFrameSnapshot?.();
                const gpu = accepted?.gpu;
                return accepted?.host?.frame?.submittedFrames > 0 && gpu?.indirectIndexed > 0 &&
                    ['engine-meshlets-select-lod', 'engine-meshlets-cull-expand', 'engine-meshlets-finalize-indexed']
                        .every(stage => gpu.compute[stage] > 0);
            }, null, { timeout: config.timeout });
            const playing = await captureSettled(page, config, `static-meshlet-gpu-${iteration}-playing`);
            const comparison = await comparePanels(page, baseline.image, playing.image);
            iterationReport.playing = playing.pixels;
            iterationReport.comparison = comparison;
            assertParity(comparison, 'initial');
            const beforeEvidence = await gpuSnapshot(page);
            iterationReport.before = beforeEvidence;
            assert(ready(beforeEvidence, gpuArtifact), `BrowserSmoke.StaticMeshletStrategy: ${JSON.stringify(beforeEvidence)}`);
            const before = beforeEvidence.accepted;
            const raster = mappedRaster(before.gpu, 'indirect', gpuArtifact);
            assertSameRaster(baselineRaster, raster);
            const canvas = page.locator('#input-surface');
            const beforeSize = await canvas.evaluate(element => [element.width, element.height]);
            iterationReport.canvasSizes = { before: beforeSize };
            await page.setViewportSize({ width: resizedExtents[iteration], height: 780 });
            await page.waitForFunction(([width, height]) => {
                const canvas = document.querySelector('#input-surface');
                return canvas.width !== width || canvas.height !== height;
            }, beforeSize);
            const resized = await captureSettled(page, config, `static-meshlet-gpu-${iteration}-resized`);
            const resizedComparison = await comparePanels(page, baselineResized[iteration].image, resized.image);
            iterationReport.resized = resized.pixels;
            iterationReport.resizedComparison = resizedComparison;
            assertParity(resizedComparison, 'resized');
            const afterEvidence = await gpuSnapshot(page);
            iterationReport.after = afterEvidence;
            const after = afterEvidence.accepted;
            assert(ready(afterEvidence, gpuArtifact) &&
                mappedDraw(after.gpu, 'indirect', gpuArtifact) >
                    mappedDraw(before.gpu, 'indirect', gpuArtifact) &&
                after.host.frame.submittedFrames > before.host.frame.submittedFrames &&
                after.host.surfaceGeneration !== before.host.surfaceGeneration &&
                (after.host.canvasExtent[0] !== before.host.canvasExtent[0] ||
                    after.host.canvasExtent[1] !== before.host.canvasExtent[1]),
                `BrowserSmoke.StaticMeshletResizeSubmission: ${JSON.stringify({ beforeEvidence, afterEvidence })}`);
            assertSameRaster(baselineRaster, mappedRaster(after.gpu, 'indirect', gpuArtifact));
            assertNoBrowserErrors(events);
            iterationReport.canvasSizes.after = await canvas.evaluate(element => [element.width, element.height]);
        } catch (error) {
            const evidence = await gpuSnapshot(page).catch(() => null);
            iterationReport.error = String(error);
            iterationReport.evidence = evidence;
            await page.screenshot({ path: path.join(config.output, `static-meshlet-gpu-${iteration}-failure.png`), fullPage: true }).catch(() => {});
            throw error;
        } finally {
            try {
                const disposal = await stopObserved(page);
                iterationReport.disposal = disposal;
                assertDisposed(disposal);
                assertNoBrowserErrors(events);
            } finally { await context.close(); }
        }
    }
}
