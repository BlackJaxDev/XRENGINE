import path from 'node:path';
import { canvasGeometry } from './canvas-capture.mjs';

// Independent authored-input expectations: all nine original centers remain qualified.
const coordinates = [0.175, 0.5, 0.825];
const source = [64, 128, 192, 96];
const background = [0.125, 0.25, 0.375, 1];
const arrayLayers = [[16, 160, 96, 224], [200, 32, 240, 80]];
const cases = [
    { semantic: 'UnlitColorV1', semanticVersion: 1, factory: 'CreateUnlitColorMaterialForward',
        coverage: 'opaque', authoredColor: [1.5, 0.25, 0.5, 0.75], hdr: [1.5, 0.25, 0.5, 0.75] },
    { semantic: 'UnlitTextureV2', semanticVersion: 2, factory: 'CreateUnlitTextureMaterialForward',
        coverage: 'opaque', textureFormat: 'Rgba8', authoredRgbaBytes: source, hdr: source.map(value => value / 255) },
    { semantic: 'UnlitTextureV2', semanticVersion: 2, factory: 'CreateUnlitTextureMaterialForward',
        coverage: 'opaque', textureFormat: 'Srgb8Alpha8', authoredRgbaBytes: source,
        hdr: [...source.slice(0, 3).map(value => srgbToLinear(value / 255)), source[3] / 255] },
    { semantic: 'UnlitOpaqueTextureV3', semanticVersion: 3, factory: 'CreateUnlitOpaqueTextureMaterialForward',
        coverage: 'opaque', textureFormat: 'Rgba8', authoredRgbaBytes: source,
        hdr: [...source.slice(0, 3).map(value => value / 255), 1] },
    ...[127, 128, 129].map(alpha => ({ semantic: 'UnlitAlphaTextureV4', semanticVersion: 4,
        factory: 'CreateUnlitAlphaTextureMaterialForward', coverage: 'masked', textureFormat: 'Rgba8',
        authoredRgbaBytes: [200, 40, 80, alpha], alphaCutoffNumerator: 128, alphaCutoffDenominator: 255,
        backgroundColor: background, hdr: alpha < 128 ? background : [200, 40, 80, alpha].map(value => value / 255) })),
    ...[arrayLayers, [...arrayLayers].reverse()].map(layers => ({ semantic: 'UnlitTextureArraySliceV5',
        semanticVersion: 5, factory: 'CreateUnlitTextureArraySliceMaterialForward', coverage: 'opaque',
        textureFormat: 'Rgba8', sampledLayer: 0, arrayLayerBytes: layers, hdr: layers[0].map(value => value / 255) })),
];
const witnessInputs = {
    far: { hdr: [0.25, 0.5, 0.75, 1], depth: 0.625, normal: [0.5, 0.75, 0, 1] },
    near: { hdr: [1.25, 0.25, 0.125, 0.5], depth: 0.375, normal: [0.75, 0.5, 0, 1] },
    diagonal: { left: [-0.86, 0.285], right: [0.86, 0.375] },
    roi: { left: 0.09, top: 0.312, right: 0.91, bottom: 0.358 },
};
const aoNames = ['WebGtaoRawTexture', 'WebGtaoHorizontalTexture', 'WebGtaoFinalTexture'];
const sidecarNames = ['DepthStencil', 'WebNormalTexture', 'WebMsaaNormalTexture', ...aoNames];

function assert(condition, message) { if (!condition) throw new Error(message); }
function same(left, right) { return JSON.stringify(left) === JSON.stringify(right); }
function srgbToLinear(value) { return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4; }
function display(hdr) {
    return [...hdr.slice(0, 3).map(value => Math.round(255 * Math.min(1, value * 1.6 / (value + 0.6)) ** (1 / 2.2))), 255];
}
function nativePrograms(state) {
    const identities = entries => entries.map(({ label, nativeId }) => ({ label, nativeId }))
        .sort((left, right) => left.label.localeCompare(right.label) || left.nativeId - right.nativeId);
    return { shaders: identities(state.shaders), pipelines: identities(state.pipelines) };
}
function cacheSnapshot(statistics) {
    const resources = statistics.resources;
    return { shaderModuleCacheEntries: resources.shaderModuleCacheEntries,
        shaderModuleCacheKeyBytes: resources.shaderModuleCacheKeyBytes,
        shaderModuleCacheHits: resources.shaderModuleCacheHits,
        shaderModuleCacheMisses: resources.shaderModuleCacheMisses,
        pipelineCacheEntries: resources.pipelineCacheEntries };
}

async function waitReady(page, previous, timeout, stage) {
    try {
        await page.waitForFunction(({ before, width, height, previousGeneration }) => {
            const host = window.engineMeshDiagnostic;
            if (host?.failure) return true;
            if (!(host?.session > 0 && host.readyFrames >= before + 2 && host.statistics()?.resources?.retiring === 0))
                return false;
            // A replacement may prepare while the old generation keeps rendering.
            const state = JSON.parse(host.exports.GetUnlitState(host.session));
            return state.committed?.width === width && state.committed?.height === height &&
                (previousGeneration === null || state.resourceGeneration > previousGeneration);
        }, { before: previous, width: stage.width, height: stage.height,
            previousGeneration: stage.previousGeneration ?? null }, { timeout: Math.min(timeout, 120000) });
    } finally {
        stage.readiness = await page.evaluate(() => ({ failure: window.engineMeshDiagnostic.failure,
            readyFrames: window.engineMeshDiagnostic.readyFrames, status: document.querySelector('#status')?.textContent }));
    }
    assert(!stage.readiness.failure, 'BrowserSmoke.UnlitMsaaFrameFailed: ' + JSON.stringify(stage.readiness.failure));
    assert(stage.readiness.status?.startsWith('Engine mesh unlit diagnostic rendered'),
        'BrowserSmoke.UnlitMsaaFrameStatus: ' + stage.readiness.status);
}

async function sampleStage(page, stage, config, capturePixels) {
    const startedAt = performance.now();
    stage.pause = await page.evaluate(async () => {
        const host = window.engineMeshDiagnostic;
        const token = await host.pauseUnlitFrames();
        return { token, session: host.session, readyFrames: host.readyFrames,
            frameSubmitCalls: host.statistics().frameSubmitCalls, startedAt: performance.now(),
            nativeEvidence: globalThis.indirectSnapshot?.() ?? null };
    });
    let samplingFailed = false;
    try {
        stage.states = await page.evaluate(() => {
            const host = window.engineMeshDiagnostic;
            return Array.from({ length: 9 }, (_, index) => { host.setUnlitCase(index); return host.unlitState(); });
        });
        stage.statistics = await page.evaluate(() => window.engineMeshDiagnostic.statistics());
        stage.cache = cacheSnapshot(stage.statistics);
        stage.nativePrograms = nativePrograms(stage.states[0]);
        stage.catalogIdentities = stage.states.map(state => state.artifactIdentity);
        const samples = cases.map((expected, index) => ({ name: `case-${index}`,
            x: Math.floor(stage.width * coordinates[index % 3]),
            y: Math.floor(stage.height * coordinates[Math.floor(index / 3)]), expected: display(expected.hdr) }));
        stage.canvasGeometry = await canvasGeometry(page);
        const png = await page.locator('canvas').screenshot({ path: path.join(config.output, `${stage.label}.png`) });
        stage.display = await capturePixels(page, png, samples);
        stage.centers = [];
        for (let index = 0; index < cases.length; index++) {
            const hdr = await page.evaluate(({ u, v }) => window.engineMeshDiagnostic.readUnlitTarget('HDRSceneTex', u, v),
                { u: coordinates[index % 3], v: coordinates[Math.floor(index / 3)] });
            stage.centers.push({ index, expectedHdr: cases[index].hdr,
                expectedDisplay: samples[index].expected, hdr, display: stage.display.samples[index] });
        }
        stage.witness = stage.profile.endsWith('x4') || stage.profile === 'cpu-x4-ao'
            ? await page.evaluate(() => window.engineMeshDiagnostic.readUnlitWitness()) : null;
        stage.ao = {};
        if (stage.profile === 'cpu-x4-ao') {
            for (const name of aoNames) {
                stage.ao[name] = {};
                for (const [site, u, v] of [['near', 0.5, 0.317], ['edge', 0.5, 0.335], ['far', 0.5, 0.353]])
                    stage.ao[name][site] = await page.evaluate(({ name, u, v }) =>
                        window.engineMeshDiagnostic.readUnlitTarget(name, u, v), { name, u, v });
            }
        }
        stage.postReadbackStatistics = await page.evaluate(() => window.engineMeshDiagnostic.statistics());
    } catch (error) {
        samplingFailed = true;
        throw error;
    } finally {
        try {
            stage.sampling = await page.evaluate(({ token, session, readyFrames, frameSubmitCalls, startedAt }) => {
                const host = window.engineMeshDiagnostic;
                let result, resumed;
                try {
                    result = { sessionUnchanged: host.session === session, pausedMs: performance.now() - startedAt,
                        readyFramesDelta: host.readyFrames - readyFrames,
                        frameSubmitCallsDelta: (host.statistics()?.frameSubmitCalls ?? 0) - frameSubmitCalls,
                        nativeEvidence: globalThis.indirectSnapshot?.() ?? null };
                } finally { resumed = host.resumeUnlitFrames(token); }
                return { ...result, resumed };
            }, stage.pause);
            stage.sampling.elapsedMs = performance.now() - startedAt;
        } catch (error) {
            stage.resumeFailure = String(error);
            if (!samplingFailed) throw error;
        }
    }
}

function assertCenters(stage, submission = 'CpuDirect') {
    assert(stage.display.width === stage.width && stage.display.height === stage.height,
        `BrowserSmoke.UnlitMsaaCanvasExtent: ${stage.label} captured the wrong extent.`);
    for (const center of stage.centers) {
        const { index, hdr, display: visible } = center;
        const expected = cases[index];
        const state = stage.states[index];
        assert(state.sampleCase === index && state.pipeline === 'DefaultRenderPipeline' && state.submission === submission &&
            state.target === 'HDRSceneTex' && state.blend === 'disabled' &&
            state.sampleU === coordinates[index % 3] && state.sampleV === coordinates[Math.floor(index / 3)],
        `BrowserSmoke.UnlitMsaaMaterialState: case ${index} changed its producer or fixed board position.`);
        for (const key of ['semantic', 'semanticVersion', 'factory', 'coverage', 'authoredColor', 'textureFormat',
            'authoredRgbaBytes', 'alphaCutoffNumerator', 'alphaCutoffDenominator', 'backgroundColor', 'sampledLayer', 'arrayLayerBytes'])
            assert(same(state[key], expected[key]), `BrowserSmoke.UnlitMsaaAuthoredInput: case ${index} ${key} changed.`);
        if (expected.textureFormat)
            assert(state.textureColorSpace === (expected.textureFormat === 'Srgb8Alpha8' ? 'srgb' : 'linear'),
                `BrowserSmoke.UnlitMsaaTextureSpace: case ${index} changed its authored transfer function.`);
        assert(typeof state.artifactIdentity === 'string' && state.artifactIdentity.length > 0 &&
            same(nativePrograms(state), stage.nativePrograms) &&
            same(state.shaders, stage.states[0].shaders) && same(state.pipelines, stage.states[0].pipelines),
        `BrowserSmoke.UnlitMsaaCaseMutation: case ${index} changed native program identities.`);
        assert(hdr.label === 'HDRSceneTex' && hdr.format === 'rgba16float' &&
            hdr.width === stage.width && hdr.height === stage.height,
        `BrowserSmoke.UnlitMsaaCenterTarget: case ${index} did not read the resolved HDR target.`);
        for (let channel = 0; channel < 4; channel++) {
            const value = expected.hdr[channel], tolerance = Math.max(0.004, Math.abs(value) * 0.012);
            assert(hdr.min[channel] >= value - tolerance && hdr.max[channel] <= value + tolerance,
                `BrowserSmoke.UnlitMsaaHdr: ${stage.label} case ${index} channel ${channel}: expected ${value} ±${tolerance}, got ${hdr.min[channel]}..${hdr.max[channel]}.`);
            assert(visible.min[channel] >= center.expectedDisplay[channel] - 3 &&
                visible.max[channel] <= center.expectedDisplay[channel] + 3,
            `BrowserSmoke.UnlitMsaaDisplay: ${stage.label} case ${index} channel ${channel} changed.`);
        }
    }
    const catalog = stage.catalogIdentities;
    assert(catalog[1] === catalog[2] && catalog[4] === catalog[5] && catalog[5] === catalog[6] &&
        catalog[7] === catalog[8] && new Set(catalog).size === 5,
    'BrowserSmoke.UnlitMsaaCatalog: the nine centers must retain their five cooked material variants.');
}

// Coverage is inferred from independently known HDR colors; no hardware sample positions are assumed.
function analyzeWitness(witness, aoEnabled) {
    const result = { expected: witnessInputs, coverageCounts: [0, 0, 0, 0, 0], fractionalPixels: 0,
        fullyNearPixels: 0, fullyFarPixels: 0, mismatchCount: 0, mismatches: [], representatives: {},
        aoMinimum: Infinity, aoMaximum: -Infinity };
    const { region } = witness;
    const failure = (x, y, reason, actual, expected) => {
        result.mismatchCount++;
        if (result.mismatches.length < 16) result.mismatches.push({ x, y, reason, actual, expected });
    };
    const line = x => 0.285 + (x + 0.86) * 0.09 / 1.72;
    for (let y = 0; y < region.height; y++) for (let x = 0; x < region.width; x++) {
        const pixel = y * region.width + x, offset = pixel * 4;
        const hdr = witness.hdr.pixels.slice(offset, offset + 4);
        const coverage = (hdr[0] - witnessInputs.far.hdr[0]) / (witnessInputs.near.hdr[0] - witnessInputs.far.hdr[0]);
        const count = Math.round(coverage * 4);
        if (!Number.isFinite(coverage) || count < 0 || count > 4) {
            failure(x, y, 'coverage', hdr, 'finite quarter coverage from zero through one');
            continue;
        }
        result.coverageCounts[count]++;
        if (count > 0 && count < 4) result.fractionalPixels++;
        const expectedHdr = witnessInputs.far.hdr.map((far, channel) => far +
            (witnessInputs.near.hdr[channel] - far) * count / 4);
        if (!hdr.every((value, channel) => Number.isFinite(value) && Math.abs(value - expectedHdr[channel]) <= 0.004))
            failure(x, y, 'color-resolve', hdr, expectedHdr);
        const px = region.x + x, py = region.y + y;
        const left = 2 * px / region.targetWidth - 1, right = 2 * (px + 1) / region.targetWidth - 1;
        const top = 1 - 2 * py / region.targetHeight, bottom = 1 - 2 * (py + 1) / region.targetHeight;
        // A whole pixel square on one side has known coverage for every legal sample pattern.
        if (bottom > line(right) + 0.000001) {
            result.fullyNearPixels++;
            if (count !== 4) failure(x, y, 'near-interior', count, 4);
        } else if (top < line(left) - 0.000001) {
            result.fullyFarPixels++;
            if (count !== 0) failure(x, y, 'far-interior', count, 0);
        }
        const surface = count > 0 ? witnessInputs.near : witnessInputs.far;
        const depth = witness.depth?.pixels[pixel];
        const normal = witness.normal?.pixels.slice(offset, offset + 4);
        if (aoEnabled) {
            if (!Number.isFinite(depth) || Math.abs(depth - surface.depth) > 0.0001)
                failure(x, y, 'closest-depth', depth, surface.depth);
            if (!normal?.every((value, channel) => Number.isFinite(value) && Math.abs(value - surface.normal[channel]) <= 0.004))
                failure(x, y, 'closest-normal', normal, surface.normal);
            const ao = witness.ao?.pixels[offset];
            if (!Number.isFinite(ao) || ao < -0.005 || ao > 1.005)
                failure(x, y, 'ao-bounds', ao, '[0,1]');
            else { result.aoMinimum = Math.min(result.aoMinimum, ao); result.aoMaximum = Math.max(result.aoMaximum, ao); }
        }
        result.representatives[count] ??= { x: px, y: py, coverage: count / 4, hdr, depth, normal };
    }
    if (!aoEnabled) { result.aoMinimum = null; result.aoMaximum = null; }
    return result;
}

function assertWitness(stage) {
    const witness = stage.witness, aoEnabled = stage.profile === 'cpu-x4-ao';
    const { region } = witness;
    const state = stage.states[0];
    assert(witness.session === stage.pause.session && witness.executionProfile === stage.profile &&
        witness.resourceGeneration === state.resourceGeneration && witness.frameSequence === state.submittedFrame.sequence,
    'BrowserSmoke.UnlitMsaaWitnessOwnership: copies did not belong to the paused submitted generation.');
    assert(same(witness.metadata, state.witness) && same(witness.metadata.roi, witnessInputs.roi) &&
        witness.metadata.coverageDenominator === 4 && witness.metadata.cameraNear === 0 && witness.metadata.cameraFar === 4 &&
        witness.metadata.semantic === 'UnlitColorV1' && witness.metadata.factory === 'CreateUnlitColorMaterialForward' &&
        same(witness.metadata.rear.color, witnessInputs.far.hdr) && same(witness.metadata.front.color, witnessInputs.near.hdr) &&
        same(witness.metadata.rear.encodedNormal, witnessInputs.far.normal) &&
        same(witness.metadata.front.encodedNormal, witnessInputs.near.normal) &&
        witness.metadata.rear.worldZ === -2.5 && witness.metadata.front.worldZ === -1.5 &&
        witness.metadata.rear.normalDepth === witnessInputs.far.depth && witness.metadata.front.normalDepth === witnessInputs.near.depth &&
        same(witness.metadata.rear.vertices, [[-.86,.26],[.86,.26],[.86,.4],[-.86,.4]]) &&
        same(witness.metadata.front.vertices, [[-.86,.285],[.86,.375],[.86,.4],[-.86,.4]]),
    'BrowserSmoke.UnlitMsaaWitnessInputs: authored gutter geometry, material or camera changed.');
    assert(region && Number.isInteger(region.x) && Number.isInteger(region.y) &&
        Number.isInteger(region.width) && Number.isInteger(region.height) && region.width > 0 && region.height > 0 &&
        region.width * region.height <= 65536 && region.targetWidth === stage.width && region.targetHeight === stage.height,
    'BrowserSmoke.UnlitMsaaWitnessRegion: the aligned diagnostic ROI is absent or exceeds its budget.');
    // Permit either inward or outward integer rounding by at most one pixel.
    for (const [actual, expected] of [[region.x, witnessInputs.roi.left * stage.width],
        [region.y, witnessInputs.roi.top * stage.height],
        [region.x + region.width, witnessInputs.roi.right * stage.width],
        [region.y + region.height, witnessInputs.roi.bottom * stage.height]])
        assert(Math.abs(actual - expected) <= 1,
            'BrowserSmoke.UnlitMsaaWitnessExtent: the sampled gutter no longer matches the authored geometry.');
    const planes = [['hdr', 'HDRSceneTex', 'rgba16float', 4],
        ...(aoEnabled ? [['depth', 'DepthStencil', 'depth32float', 1],
            ['normal', 'WebNormalTexture', 'rgba16float', 4], ['ao', 'WebGtaoFinalTexture', 'rgba16float', 4]] : [])];
    for (const [key, label, format, channels] of planes) {
        const plane = witness[key];
        const target = state.targets.find(target => target.label === label);
        assert(plane?.slot === target.slot && plane.generation === target.generation &&
            plane.sampleCount === 1 && plane.nativeSampleCount === 1 &&
            plane.width === stage.width && plane.height === stage.height && plane.channels === channels &&
            same(plane.region, { x: region.x, y: region.y, width: region.width, height: region.height }),
        'BrowserSmoke.UnlitMsaaWitnessAlignment: ' + key + ' changed target identity, native samples or pixel alignment.');
        assert(plane?.label === label && plane.format === format &&
            Array.isArray(plane.pixels) && plane.pixels.length === region.width * region.height * channels,
        `BrowserSmoke.UnlitMsaaWitnessPlane: ${key} is not an aligned ${label} readback.`);
    }
    if (!aoEnabled)
        assert(witness.depth === null && witness.normal === null && witness.ao === null && Object.keys(stage.ao).length === 0,
            'BrowserSmoke.UnlitMsaaDisabledAo: the no-AO profile reported unavailable sidecars.');
    stage.witnessAnalysis = analyzeWitness(witness, aoEnabled);
    const analysis = stage.witnessAnalysis;
    assert(analysis.mismatchCount === 0,
        `BrowserSmoke.UnlitMsaaWitnessPixels: ${stage.label}: ${JSON.stringify(analysis.mismatches)}`);
    assert(analysis.fractionalPixels > 0 && analysis.coverageCounts[0] > 0 && analysis.coverageCounts[4] > 0 &&
        analysis.fullyNearPixels > 0 && analysis.fullyFarPixels > 0,
    'BrowserSmoke.UnlitMsaaCoverage: require full near/far interiors and fractional x4 silhouette coverage.');
    if (!aoEnabled) return;
    for (const name of aoNames) {
        assert(stage.ao[name] && Object.keys(stage.ao[name]).length === 3,
            `BrowserSmoke.UnlitMsaaAoStage: ${name} has no live readback.`);
        for (const reading of Object.values(stage.ao[name]))
            assert(reading.label === name && reading.format === 'rgba16float' &&
                reading.min.every(Number.isFinite) && reading.max.every(Number.isFinite) &&
                reading.min[0] >= -0.005 && reading.max[0] <= 1.005,
            `BrowserSmoke.UnlitMsaaAoBounds: ${name} produced invalid visibility.`);
    }
    assert(analysis.aoMinimum < 0.995 && analysis.aoMaximum - analysis.aoMinimum > 0.001,
        'BrowserSmoke.UnlitMsaaAoSignal: the resolved-depth witness produced only neutral or constant AO.');
}


function assertProfile(stage) {
    const state = stage.states[0], aoEnabled = stage.profile === 'cpu-x4-ao';
    const requiredTargets = [
        ['HDRSceneTex', 'rgba16float', 1], ['WebMsaaHdrTexture', 'rgba16float', 4],
        ['WebMsaaDepthTexture', 'depth32float', 4],
        ...(aoEnabled ? [['WebMsaaNormalTexture', 'rgba16float', 4],
            ['DepthStencil', 'depth32float', 1], ['WebNormalTexture', 'rgba16float', 1],
            ...aoNames.map(name => [name, 'rgba16float', 1])] : []),
    ];
    stage.profileComparison = { expected: { executionProfile: stage.profile, pipeline: 'DefaultRenderPipeline',
        submission: 'CpuDirect', antiAliasing: 'Msaa', sampleCount: 4, aoEnabled,
        width: stage.width, height: stage.height, targets: requiredTargets },
        actual: { executionProfile: state.executionProfile, source: state.source, camera: state.camera,
            committed: state.committed, resourceGeneration: state.resourceGeneration,
            pipelineInstanceId: state.pipelineInstanceId, targets: state.targets } };
    assert(state.executionProfile === stage.profile && state.pipeline === 'DefaultRenderPipeline' &&
        state.submission === 'CpuDirect' && state.source?.type === 'DefaultRenderPipeline' && state.source.id != null &&
        state.source.cameraOwnsSource && state.source.instanceOwnsSource && state.source.committedOwnsSource,
    'BrowserSmoke.UnlitMsaaProducer: the camera, instance and committed generation must own the actual Default source.');
    assert(state.camera?.antiAliasing === 'Msaa' && state.camera.sampleCount === 4 && state.camera.reversedDepth === false &&
        state.camera.aoEnabled === aoEnabled && state.camera.aoQualityEnabled === true &&
        state.camera.aoType === 'GroundTruthAmbientOcclusion' && state.camera.aoResolution === 'Full',
    'BrowserSmoke.UnlitMsaaCameraProfile: actual camera AA, depth or AO settings differ from the selected profile.');
    const committed = state.committed;
    assert(committed?.pipeline === 'DefaultRenderPipeline' && committed.antiAliasing === 'Msaa' && committed.sampleCount === 4 &&
        committed.width === stage.width && committed.height === stage.height &&
        committed.displayWidth === stage.width && committed.displayHeight === stage.height &&
        committed.outputHdr === false && committed.stereo === false &&
        committed.featureMask === 2 ** 31 + (aoEnabled ? 2 ** 32 + 2 ** 6 : 0) &&
        Number.isSafeInteger(state.resourceGeneration) && state.resourceGeneration > 0 &&
        state.pipelineInstanceId != null && !state.pipelineDecline && !state.resourceFailure,
    'BrowserSmoke.UnlitMsaaCommittedProfile: no matching, successful resource generation was committed.');
    for (const [label, format, samples] of requiredTargets) {
        const matches = state.targets.filter(target => target.label === label);
        assert(matches.length === 1 && matches[0].format === format && matches[0].sampleCount === samples &&
            matches[0].nativeSampleCount === samples && matches[0].width === stage.width && matches[0].height === stage.height,
        'BrowserSmoke.UnlitMsaaTargetProfile: ' + label + ' has the wrong format, extent, descriptor or native samples.');
    }
    if (!aoEnabled)
        assert(!state.targets.some(target => sidecarNames.includes(target.label)),
            'BrowserSmoke.UnlitMsaaAoAllocation: disabled AO allocated depth/normal sidecars or AO targets.');
    assert(state.shaders.length >= 6 && state.pipelines.length >= 6 &&
        [...state.shaders, ...state.pipelines].every(program => Number.isInteger(program.nativeId) && program.nativeId > 0),
    'BrowserSmoke.UnlitMsaaPrograms: native shader and render pipeline identities are missing.');
    for (const other of stage.states)
        assert(other.executionProfile === state.executionProfile && same(other.source, state.source) &&
            same(other.camera, state.camera) && same(other.committed, state.committed) &&
            same(other.targets, state.targets) && other.resourceGeneration === state.resourceGeneration &&
            same(other.submittedFrame, state.submittedFrame),
        'BrowserSmoke.UnlitMsaaMetadataMutation: selecting a tile changed the rendered frame or generation.');
    const frame = state.submittedFrame, operations = frame?.operations ?? [];
    const attached = (operation, label, key = 'view') => operation.attachments.some(binding => binding.key === key && binding.label === label);
    const sampled = (operation, label) => operation.sampledTextures.some(binding => binding.label === label);
    const drawing = operation => operation.type === 'render' && operation.draws.length > 0 &&
        operation.raster?.scissorSuppressesDraw === false && operation.raster?.rasterAreaEmpty === false &&
        operation.drawEvidence.some(draw => draw.effective === true && draw.issued === true &&
            draw.issuedCalls > 0 && draw.geometryCount > 0 && draw.instanceCount > 0 &&
            ['draw', 'drawIndexed'].includes(draw.type));
    const color = operations.filter(operation => drawing(operation) && attached(operation, 'WebMsaaHdrTexture'));
    const colorResolve = operations.filter(operation => attached(operation, 'HDRSceneTex', 'resolveTarget'));
    const presentation = operations.filter(operation => drawing(operation) && sampled(operation, 'HDRSceneTex'));
    const prepass = operations.filter(operation => drawing(operation) && attached(operation, 'WebMsaaNormalTexture'));
    const closest = operations.filter(operation => drawing(operation) && attached(operation, 'WebNormalTexture'));
    const aoStages = aoNames.map(name => operations.filter(operation => drawing(operation) && attached(operation, name)));
    stage.execution = { sequence: frame?.sequence, records: frame?.records,
        color: color.map(operation => operation.record), colorResolve: colorResolve.map(operation => operation.record),
        presentation: presentation.map(operation => operation.record), prepass: prepass.map(operation => operation.record),
        closest: closest.map(operation => operation.record), aoStages: aoStages.map(stages => stages.map(operation => operation.record)) };
    assert(Number.isSafeInteger(frame?.sequence) && frame.sequence > 0 && frame.records === operations.length &&
        operations.length > 0 && operations.every(operation => Array.isArray(operation.preparedDraws) &&
            operation.preparedDraws.every(draw => ['draw', 'drawIndexed'].includes(draw)) &&
            Array.isArray(operation.drawEvidence) &&
            same(operation.draws, operation.drawEvidence.filter(draw => draw.effective === true).map(draw => draw.type))),
    'BrowserSmoke.UnlitMsaaSubmittedFrame: require a current successful packet with only CPU-direct draws.');
    assert(color.length >= 12 && color.every(operation => operation.sampleCount === 4 &&
        attached(operation, 'WebMsaaDepthTexture')) && colorResolve.length === 1 && presentation.length > 0 &&
        colorResolve[0].sampleCount === 4 && attached(colorResolve[0], 'WebMsaaHdrTexture') &&
        Math.max(...color.map(operation => operation.record)) < colorResolve[0].record &&
        presentation.every(operation => operation.record > colorResolve[0].record && operation.sampleCount === 1),
    'BrowserSmoke.UnlitMsaaColorRoute: x4 Default draws must precede a single HDR resolve and x1 presentation.');
    const targetProfiles = new Map(requiredTargets.map(([label, format, samples]) => [label, { format, samples }]));
    for (const operation of operations) for (const binding of [...operation.attachments, ...operation.sampledTextures]) {
        const expected = targetProfiles.get(binding.label);
        if (!expected) continue;
        assert(binding.format === expected.format && binding.sampleCount === expected.samples &&
            binding.nativeSampleCount === expected.samples,
        'BrowserSmoke.UnlitMsaaSubmittedSamples: ' + binding.label + ' used different native or descriptor samples.');
    }
    if (!aoEnabled) {
        assert(prepass.length === 0 && closest.length === 0 && aoStages.every(stages => stages.length === 0) &&
            operations.every(operation => [...operation.attachments, ...operation.sampledTextures]
                .every(binding => !sidecarNames.includes(binding.label))),
        'BrowserSmoke.UnlitMsaaDisabledAoStages: disabled AO still executed prepass, closest resolve or GTAO work.');
        return;
    }
    assert(prepass.length >= 12 && prepass.every(operation => operation.sampleCount === 4 &&
        attached(operation, 'WebMsaaDepthTexture')) && closest.length === 1 && closest[0].sampleCount === 1 &&
        attached(closest[0], 'DepthStencil') && sampled(closest[0], 'WebMsaaDepthTexture') &&
        sampled(closest[0], 'WebMsaaNormalTexture') &&
        Math.max(...prepass.map(operation => operation.record)) < closest[0].record,
    'BrowserSmoke.UnlitMsaaClosestRoute: x4 depth/normal draws did not feed the x1 closest-sample sidecars.');
    let preceding = closest[0].record;
    for (let index = 0; index < aoStages.length; index++) {
        const stages = aoStages[index];
        assert(stages.length === 1 && stages[0].sampleCount === 1 && stages[0].record > preceding &&
            sampled(stages[0], 'DepthStencil') && sampled(stages[0], 'WebNormalTexture') &&
            (index === 0 || sampled(stages[0], aoNames[index - 1])),
        'BrowserSmoke.UnlitMsaaAoRoute: ' + aoNames[index] + ' did not consume the canonical depth/normal chain.');
        preceding = stages[0].record;
    }
    assert(Math.min(...color.map(operation => operation.record)) > preceding,
        'BrowserSmoke.UnlitMsaaAoOrder: forward color ran before the GTAO sidecars were produced.');
}

function assertStatistics(stage) {
    const statistics = stage.postReadbackStatistics;
    assert(statistics.draws >= 12 && statistics.frameSubmitCalls > 0 && statistics.packets === 0 &&
        statistics.focusedPipeline === null && statistics.engineFrame.submittedFrames > 0 &&
        statistics.engineFrame.draws >= 12 && !statistics.lastPacketFailure,
    'BrowserSmoke.UnlitMsaaSubmission: require real engine draws and presented frame submissions.');
    const scopes = statistics.engineFrame.errorScopes;
    assert(scopes && ['validationErrors', 'outOfMemoryErrors', 'rejectedScopes', 'obsoleteErrors', 'capacityFailures']
        .every(key => scopes[key] === 0), 'BrowserSmoke.UnlitMsaaGpuErrors: a frame scope reported a GPU failure.');
    assert(stage.sampling.sessionUnchanged && stage.sampling.resumed &&
        stage.sampling.readyFramesDelta === 0 && stage.sampling.frameSubmitCallsDelta === 0,
    'BrowserSmoke.UnlitMsaaSamplingLifetime: diagnostic copies raced or failed to resume the owning frame pump.');
    assert(statistics.resources.retiring === 0 && statistics.resources.readbackTickets === 0 &&
        statistics.resources.readbackResidentBytes === 0,
    'BrowserSmoke.UnlitMsaaReadbackRetention: settled sampling retained resources or readback tickets.');
    assert(Object.values(stage.cache).every(value => Number.isInteger(value) && value >= 0) &&
        stage.cache.shaderModuleCacheEntries > 0 && stage.cache.pipelineCacheEntries > 0,
    'BrowserSmoke.UnlitMsaaNativeCache: live shader and pipeline cache counters are absent.');
}

function compareWarmStage(stage, initial, previous) {
    stage.reuse = { expectedCache: initial.cache, actualCache: stage.cache,
        expectedPrograms: initial.nativePrograms, actualPrograms: stage.nativePrograms,
        expectedCatalog: initial.catalogIdentities, actualCatalog: stage.catalogIdentities,
        previousGeneration: previous.states[0].resourceGeneration,
        generation: stage.states[0].resourceGeneration };
    assert(same(stage.nativePrograms, initial.nativePrograms) && same(stage.catalogIdentities, initial.catalogIdentities),
        'BrowserSmoke.UnlitMsaaNativeReuse: a warmed profile resize replaced native shader/pipeline or catalog identity.');
    for (const key of ['shaderModuleCacheEntries', 'shaderModuleCacheKeyBytes', 'shaderModuleCacheMisses', 'pipelineCacheEntries'])
        assert(stage.cache[key] === initial.cache[key], `BrowserSmoke.UnlitMsaaCacheGrowth: resize changed ${key}.`);
    assert(stage.cache.shaderModuleCacheHits >= initial.cache.shaderModuleCacheHits,
        'BrowserSmoke.UnlitMsaaCacheHits: native shader cache hit counters went backwards.');
    assert(same(stage.states[0].source, initial.states[0].source) &&
        stage.states[0].pipelineInstanceId === initial.states[0].pipelineInstanceId,
    'BrowserSmoke.UnlitMsaaResizeSource: resize replaced the authored source or physical output owner.');
    for (const label of stage.profile === 'gpu-indirect-x1' ? ['HDRSceneTex', 'DepthStencil'] :
        ['HDRSceneTex', 'WebMsaaHdrTexture', 'WebMsaaDepthTexture',
            ...(stage.profile === 'cpu-x4-ao' ? sidecarNames : [])]) {
        const oldTarget = previous.states[0].targets.find(target => target.label === label);
        const target = stage.states[0].targets.find(target => target.label === label);
        assert(oldTarget.slot !== target.slot || oldTarget.generation !== target.generation,
            'BrowserSmoke.UnlitMsaaResizeTarget: ' + label + ' retained the previous generation handle.');
    }
    assert(stage.reuse.generation > stage.reuse.previousGeneration,
        'BrowserSmoke.UnlitMsaaResizeGeneration: resize did not commit a replacement resource generation.');
    assert(stage.postReadbackStatistics.resources.live <= initial.postReadbackStatistics.resources.live,
        'BrowserSmoke.UnlitMsaaResizeRetention: resize retained additional GPU resources.');
}

export async function unlitMsaaCheck(browser, origin, report, config, instrumentedPage, capturePixels, assertNoBrowserErrors) {
    const { page, context, events } = await instrumentedPage(browser, origin, report, 'engine-unlit-msaa', config);
    report.unlitMsaa = { scope: 'DefaultRenderPipeline CpuDirect x4 only', profiles: [], expectedWitness: witnessInputs };
    try {
        await page.goto(`${origin}/diagnostics/engine-mesh.html?probe=unlit&assets=${encodeURIComponent(`${origin}${config.engineManifest}`)}`,
            { waitUntil: 'domcontentloaded' });
        await page.waitForFunction(() => window.engineMeshDiagnostic !== undefined);
        let catalog;
        for (const profile of ['cpu-x4', 'cpu-x4-ao']) {
            await page.locator('#execution-profile').selectOption(profile);
            const qualification = { profile, lifecycles: [] };
            report.unlitMsaa.profiles.push(qualification);
            let startupLive;
            for (let lifecycle = 0; lifecycle < 2; lifecycle++) {
                const record = { lifecycle, stages: [] };
                qualification.lifecycles.push(record);
                await page.evaluate(({ assets, profile }) => window.engineMeshDiagnostic.start(null, assets,
                    'engine-unlit-materials', profile), { assets: `${origin}${config.engineManifest}`, profile });
                for (const [width, height] of [[512, 512], [640, 384], [512, 512]]) {
                    const index = record.stages.length;
                    const stage = { profile, width, height,
                        previousGeneration: index > 0 ? record.stages[index - 1].states[0].resourceGeneration : null,
                        label: `engine-unlit-${profile}-${lifecycle}-${index}-${width}x${height}` };
                    record.stages.push(stage);
                    const before = await page.evaluate(() => window.engineMeshDiagnostic.readyFrames);
                    if (index > 0) await page.evaluate(([w, h]) => window.engineMeshDiagnostic.resize(w, h), [width, height]);
                    await waitReady(page, before, config.timeout, stage);
                    await sampleStage(page, stage, config, capturePixels);
                    assertCenters(stage);
                    assertProfile(stage);
                    assertWitness(stage);
                    assertStatistics(stage);
                    if (index > 0) compareWarmStage(stage, record.stages[0], record.stages[index - 1]);
                    else {
                        stage.restart = { expectedMaximumLive: startupLive ?? stage.postReadbackStatistics.resources.live,
                            actualLive: stage.postReadbackStatistics.resources.live,
                            expectedCatalog: catalog ?? stage.catalogIdentities, actualCatalog: stage.catalogIdentities };
                        assert(stage.restart.actualLive <= stage.restart.expectedMaximumLive &&
                            same(stage.restart.actualCatalog, stage.restart.expectedCatalog),
                        'BrowserSmoke.UnlitMsaaRestart: startup retained prior resources or changed cooked material variants.');
                        startupLive ??= stage.postReadbackStatistics.resources.live;
                        catalog ??= stage.catalogIdentities;
                    }
                    // Verify a held diagnostic frame actually resumes before another resize or stop.
                    const beforeResume = stage.pause.readyFrames;
                    await waitReady(page, beforeResume, config.timeout, stage);
                    stage.resumedStatistics = await page.evaluate(() => window.engineMeshDiagnostic.statistics());
                    assert(stage.resumedStatistics.frameSubmitCalls > stage.postReadbackStatistics.frameSubmitCalls,
                        'BrowserSmoke.UnlitMsaaResumeSubmission: no real frame was submitted after diagnostic sampling.');
                }
                record.teardown = await page.evaluate(async () => {
                    const host = window.engineMeshDiagnostic, renderer = host.renderer, session = host.session;
                    await host.stop();
                    return { session: host.session, statistics: host.statistics(), request: host.request,
                        rendererRegistered: host.renderers.has(session), released: renderer.getStatistics() };
                });
                assert(record.teardown.session === 0 && record.teardown.statistics === null && record.teardown.request === 0 &&
                    !record.teardown.rendererRegistered && record.teardown.released.resources.live === 0 &&
                    record.teardown.released.resources.retiring === 0 && record.teardown.released.resources.readbackTickets === 0 &&
                    record.teardown.released.resources.shaderModuleCacheEntries === 0 &&
                    record.teardown.released.resources.pipelineCacheEntries === 0,
                'BrowserSmoke.UnlitMsaaTeardown: stop retained the session, resources, or native programs.');
            }
        }
        assertNoBrowserErrors(events);
    } catch (error) {
        report.unlitMsaa.failure = await page.evaluate(() => window.engineMeshDiagnostic?.failure ?? null).catch(() => null);
        report.unlitMsaa.assertion = String(error);
        await page.screenshot({ path: path.join(config.output, 'engine-unlit-msaa-failure.png'), fullPage: true }).catch(() => {});
        throw error;
    } finally { await context.close(); }
}

function installIndirectEvidence() {
    const counts = { readMaps: 0, computeDispatches: 0, indexedIndirectDraws: 0 };
    globalThis.indirectSnapshot = () => ({ ...counts });
    const mapAsync = GPUBuffer.prototype.mapAsync;
    GPUBuffer.prototype.mapAsync = function (mode, ...args) {
        if ((mode & GPUMapMode.READ) !== 0) counts.readMaps++;
        return mapAsync.call(this, mode, ...args);
    };
    const dispatch = GPUComputePassEncoder.prototype.dispatchWorkgroups;
    GPUComputePassEncoder.prototype.dispatchWorkgroups = function (...args) {
        const result = dispatch.apply(this, args);
        counts.computeDispatches++;
        return result;
    };
    const draw = GPURenderPassEncoder.prototype.drawIndexedIndirect;
    GPURenderPassEncoder.prototype.drawIndexedIndirect = function (...args) {
        const result = draw.apply(this, args);
        counts.indexedIndirectDraws++;
        return result;
    };
}

function assertIndirectProfile(stage) {
    const state = stage.states[0], x4 = stage.profile === 'gpu-indirect-x4';
    const sampleCount = x4 ? 4 : 1;
    const requiredTargets = [['HDRSceneTex', 'rgba16float', 1],
        ...(x4 ? [['WebMsaaHdrTexture', 'rgba16float', 4], ['WebMsaaDepthTexture', 'depth32float', 4]] :
            [['DepthStencil', 'depth32float', 1]])];
    stage.profileComparison = { expected: { executionProfile: stage.profile,
        submission: 'GpuIndirectZeroReadback', sampleCount, width: stage.width, height: stage.height,
        targets: requiredTargets }, actual: { executionProfile: state.executionProfile,
        submission: state.submission, indexedSubmission: state.indexedSubmission,
        source: state.source, camera: state.camera, committed: state.committed,
        resourceGeneration: state.resourceGeneration, targets: state.targets } };
    assert(state.executionProfile === stage.profile && state.pipeline === 'DefaultRenderPipeline' &&
        state.submission === 'GpuIndirectZeroReadback' &&
        state.indexedSubmission?.strategy === 'GpuIndirectZeroReadback' &&
        state.indexedSubmission.reason?.startsWith('Ready') &&
        state.source?.type === 'DefaultRenderPipeline' && state.source.id != null &&
        state.source.cameraOwnsSource && state.source.instanceOwnsSource && state.source.committedOwnsSource &&
        state.pipelineInstanceId != null && state.renderDraws >= 10 &&
        !state.pipelineDecline && !state.resourceFailure,
    'BrowserSmoke.UnlitIndirectSelection: the live Default source did not select successful GPU indexed submission.');
    assert(state.camera?.antiAliasing === (x4 ? 'Msaa' : 'None') &&
        state.camera.sampleCount === sampleCount && state.camera.aoEnabled === false &&
        state.camera.reversedDepth === false && state.committed?.pipeline === 'DefaultRenderPipeline' &&
        state.committed.antiAliasing === (x4 ? 'Msaa' : 'None') &&
        state.committed.sampleCount === sampleCount && state.committed.width === stage.width &&
        state.committed.height === stage.height && state.committed.displayWidth === stage.width &&
        state.committed.displayHeight === stage.height && state.committed.outputHdr === false &&
        state.committed.stereo === false &&
        Number.isSafeInteger(state.resourceGeneration) && state.resourceGeneration > 0,
    'BrowserSmoke.UnlitIndirectGeneration: the committed output does not match the selected profile and extent.');
    for (const [label, format, samples] of requiredTargets) {
        const matches = state.targets.filter(target => target.label === label);
        assert(matches.length === 1 && matches[0].format === format &&
            matches[0].sampleCount === samples && matches[0].nativeSampleCount === samples &&
            matches[0].width === stage.width && matches[0].height === stage.height,
        `BrowserSmoke.UnlitIndirectTarget: ${label} has the wrong native profile.`);
    }
    assert(!state.targets.some(target => sidecarNames.includes(target.label) &&
            (x4 || target.label !== 'DepthStencil')) &&
        (x4 || !state.targets.some(target => target.label.startsWith('WebMsaa'))) &&
        state.shaders.length >= 6 && state.pipelines.length >= 6 &&
        [...state.shaders, ...state.pipelines].every(program => Number.isInteger(program.nativeId) && program.nativeId > 0),
    'BrowserSmoke.UnlitIndirectResources: the selected profile has missing native programs or unexpected sidecars.');
    for (const other of stage.states)
        assert(other.executionProfile === state.executionProfile && same(other.source, state.source) &&
            same(other.camera, state.camera) && same(other.committed, state.committed) &&
            same(other.indexedSubmission, state.indexedSubmission) &&
            other.pipelineInstanceId === state.pipelineInstanceId &&
            same(other.targets, state.targets) && other.resourceGeneration === state.resourceGeneration &&
            same(other.submittedFrame, state.submittedFrame),
        'BrowserSmoke.UnlitIndirectMetadataMutation: selecting a tile changed the submitted frame.');
    const frame = state.submittedFrame, operations = frame?.operations ?? [];
    const attached = (operation, label, key = 'view') => operation.attachments.some(binding =>
        binding.key === key && binding.label === label);
    const sampled = (operation, label) => operation.sampledTextures.some(binding => binding.label === label);
    const indirect = operation => operation.type === 'render' &&
        operation.raster?.scissorSuppressesDraw === false && operation.raster?.rasterAreaEmpty === false &&
        operation.drawEvidence.some(item => item.type === 'drawIndexedIndirect' && item.issued && item.issuedCalls > 0);
    const colorTarget = x4 ? 'WebMsaaHdrTexture' : 'HDRSceneTex';
    const sceneRasters = operations.filter(operation => operation.type === 'render' &&
        attached(operation, colorTarget) && operation.drawEvidence.length > 0);
    const color = sceneRasters.filter(indirect);
    const select = operations.filter(operation => operation.type === 'compute' &&
        operation.label === 'engine-meshlets-select-lod 0 compute' &&
        operation.pipeline === 'engine-meshlets-select-lod');
    const cull = operations.filter(operation => operation.type === 'compute' &&
        operation.label === 'engine-indirect-cull-primitive 0 compute' &&
        operation.pipeline === 'engine-indirect-cull-primitive');
    const resolve = operations.filter(operation => attached(operation, 'HDRSceneTex', 'resolveTarget'));
    const presentation = operations.filter(operation => operation.type === 'render' &&
        attached(operation, 'canvas') && sampled(operation, 'HDRSceneTex') &&
        operation.raster?.rasterAreaEmpty === false && operation.drawEvidence.length > 0 &&
        operation.drawEvidence.every(item => ['draw', 'drawIndexed'].includes(item.type) &&
            item.issued && item.effective === true && item.issuedCalls > 0 &&
            item.geometryCount > 0 && item.instanceCount > 0));
    const issuedColorCalls = color.reduce((sum, operation) => sum + operation.drawEvidence.reduce((count, item) =>
        count + (item.type === 'drawIndexedIndirect' && item.issued ? item.issuedCalls : 0), 0), 0);
    stage.execution = { sequence: frame?.sequence, records: frame?.records,
        select: select.map(operation => operation.record), cull: cull.map(operation => operation.record),
        color: color.map(operation => operation.record), resolve: resolve.map(operation => operation.record),
        presentation: presentation.map(operation => operation.record), issuedColorCalls };
    assert(Number.isSafeInteger(frame?.sequence) && frame.sequence > 0 && frame.records === operations.length &&
        select.length > 0 && cull.length > 0 && color.length > 0 &&
        color.length === sceneRasters.length && issuedColorCalls >= 10 && presentation.length > 0 &&
        Math.min(...color.map(operation => operation.record)) > Math.min(...select.map(operation => operation.record)) &&
        Math.min(...color.map(operation => operation.record)) > Math.min(...cull.map(operation => operation.record)) &&
        color.every(operation => operation.sampleCount === sampleCount &&
            attached(operation, x4 ? 'WebMsaaDepthTexture' : 'DepthStencil') &&
            operation.drawEvidence.every(item => item.type === 'drawIndexedIndirect' && item.issued &&
                item.issuedCalls > 0 && item.effective === null && item.geometryCount === null &&
                item.instanceCount === null)) &&
        operations.every(operation => operation.pipeline !== 'engine-authored-rank-sources' &&
            operation.pipeline !== 'engine-authored-mask-ranked-arguments' &&
            !operation.label?.startsWith('engine-authored-rank-sources ') &&
            !operation.label?.startsWith('engine-authored-mask-ranked-arguments ')),
    'BrowserSmoke.UnlitIndirectPacket: require GPU LOD/cull and indexed-indirect scene draws without opaque-pass ordering work.');
    if (x4)
        assert(resolve.length === 1 && resolve[0].sampleCount === 4 &&
            attached(resolve[0], 'WebMsaaHdrTexture') &&
            Math.max(...color.map(operation => operation.record)) < resolve[0].record &&
            presentation.every(operation => operation.record > resolve[0].record && operation.sampleCount === 1),
        'BrowserSmoke.UnlitIndirectResolve: x4 scene output did not resolve before presentation.');
    else
        assert(resolve.length === 0 && presentation.every(operation => operation.sampleCount === 1 &&
            operation.record > Math.max(...color.map(item => item.record))),
        'BrowserSmoke.UnlitIndirectX1Route: x1 scene output did not feed presentation.');
}

function assertIndirectStatistics(stage, normalStart) {
    const before = stage.pause.nativeEvidence, after = stage.sampling.nativeEvidence;
    const statistics = stage.postReadbackStatistics;
    stage.mapAccounting = { normalStart: normalStart.readMaps, beforeCopies: before?.readMaps,
        afterCopies: after?.readMaps, expectedCopies: stage.profile === 'gpu-indirect-x4' ? 10 : 9 };
    assert(before && after && before.readMaps === normalStart.readMaps &&
        before.computeDispatches > normalStart.computeDispatches &&
        before.indexedIndirectDraws > normalStart.indexedIndirectDraws &&
        after.readMaps - before.readMaps === stage.mapAccounting.expectedCopies &&
        after.computeDispatches === before.computeDispatches &&
        after.indexedIndirectDraws === before.indexedIndirectDraws &&
        stage.sampling.sessionUnchanged && stage.sampling.resumed &&
        stage.sampling.readyFramesDelta === 0 && stage.sampling.frameSubmitCallsDelta === 0,
    'BrowserSmoke.UnlitIndirectReadMaps: ordinary frames mapped GPU reads or paused diagnostic copies were misattributed.');
    assert(statistics.frameSubmitCalls > 0 && statistics.engineFrame.submittedFrames > 0 &&
        statistics.engineFrame.draws >= 10 && statistics.packets === 0 &&
        statistics.focusedPipeline === null && !statistics.lastPacketFailure &&
        statistics.resources.retiring === 0 && statistics.resources.readbackTickets === 0 &&
        statistics.resources.readbackResidentBytes === 0,
    'BrowserSmoke.UnlitIndirectStatistics: frame submission or resource lifetime is incomplete.');
    assert(Object.values(stage.cache).every(value => Number.isInteger(value) && value >= 0) &&
        stage.cache.shaderModuleCacheEntries > 0 && stage.cache.pipelineCacheEntries > 0,
    'BrowserSmoke.UnlitIndirectCache: native shader or pipeline cache counters are missing.');
    const scopes = statistics.engineFrame.errorScopes;
    assert(scopes && ['validationErrors', 'outOfMemoryErrors', 'rejectedScopes', 'obsoleteErrors', 'capacityFailures']
        .every(key => scopes[key] === 0), 'BrowserSmoke.UnlitIndirectGpuErrors: an engine frame reported a GPU error.');
}

export async function unlitIndirectCheck(browser, origin, report, config, instrumentedPage, capturePixels, assertNoBrowserErrors) {
    report.unlitIndirect = { scope: 'DefaultRenderPipeline GpuIndirectZeroReadback indexed x1/x4', profiles: [] };
    for (const profile of ['gpu-indirect-x1', 'gpu-indirect-x4']) {
        const qualification = { profile, lifecycles: [] };
        report.unlitIndirect.profiles.push(qualification);
        let catalog, startupLive;
        for (let lifecycle = 0; lifecycle < 2; lifecycle++) {
            const { page, context, events } = await instrumentedPage(browser, origin, report,
                `engine-unlit-${profile}-${lifecycle}`, config);
            const record = { lifecycle, stages: [] };
            qualification.lifecycles.push(record);
            let failed = false;
            try {
                await page.addInitScript(installIndirectEvidence);
                await page.goto(`${origin}/diagnostics/engine-mesh.html?probe=unlit&assets=${encodeURIComponent(`${origin}${config.engineManifest}`)}`,
                    { waitUntil: 'domcontentloaded' });
                await page.waitForFunction(() => window.engineMeshDiagnostic !== undefined);
                await page.locator('#execution-profile').selectOption(profile);
                let normalStart = await page.evaluate(() => window.indirectSnapshot());
                assert(normalStart.readMaps === 0,
                    'BrowserSmoke.UnlitIndirectInitialMaps: GPU reads were mapped before the engine session started.');
                await page.evaluate(({ assets, selected }) => window.engineMeshDiagnostic.start(null, assets,
                    'engine-unlit-materials', selected), { assets: `${origin}${config.engineManifest}`, selected: profile });
                for (const [width, height] of [[512, 512], [640, 384], [512, 512]]) {
                    const index = record.stages.length;
                    const stage = { profile, width, height,
                        previousGeneration: index > 0 ? record.stages[index - 1].states[0].resourceGeneration : null,
                        label: `engine-unlit-${profile}-${lifecycle}-${index}-${width}x${height}` };
                    record.stages.push(stage);
                    const before = await page.evaluate(() => window.engineMeshDiagnostic.readyFrames);
                    if (index > 0) await page.evaluate(([w, h]) => window.engineMeshDiagnostic.resize(w, h), [width, height]);
                    await waitReady(page, before, config.timeout, stage);
                    await sampleStage(page, stage, config, capturePixels);
                    assertCenters(stage, 'GpuIndirectZeroReadback');
                    assertIndirectProfile(stage);
                    if (profile === 'gpu-indirect-x4') assertWitness(stage);
                    else assert(stage.witness === null && stage.states[0].witness === null,
                        'BrowserSmoke.UnlitIndirectX1Witness: single-sample profile unexpectedly has a gutter witness.');
                    assertIndirectStatistics(stage, normalStart);
                    if (index > 0) compareWarmStage(stage, record.stages[0], record.stages[index - 1]);
                    else {
                        assert(stage.postReadbackStatistics.resources.live <=
                            (startupLive ?? stage.postReadbackStatistics.resources.live) &&
                            (catalog === undefined || same(catalog, stage.catalogIdentities)),
                        'BrowserSmoke.UnlitIndirectRestart: startup retained resources or changed cooked variants.');
                        startupLive ??= stage.postReadbackStatistics.resources.live;
                        catalog ??= stage.catalogIdentities;
                    }
                    await waitReady(page, stage.pause.readyFrames, config.timeout, stage);
                    stage.resumedStatistics = await page.evaluate(() => window.engineMeshDiagnostic.statistics());
                    stage.resumedNativeEvidence = await page.evaluate(() => window.indirectSnapshot());
                    assert(stage.resumedStatistics.frameSubmitCalls > stage.postReadbackStatistics.frameSubmitCalls &&
                        stage.resumedNativeEvidence.readMaps === stage.sampling.nativeEvidence.readMaps,
                    'BrowserSmoke.UnlitIndirectResume: resumed frames made a GPU read mapping or failed to submit.');
                    normalStart = stage.resumedNativeEvidence;
                }
                record.teardown = await page.evaluate(async () => {
                    const host = window.engineMeshDiagnostic, renderer = host.renderer, session = host.session;
                    await host.stop();
                    return { session: host.session, statistics: host.statistics(), request: host.request,
                        rendererRegistered: host.renderers.has(session), released: renderer.getStatistics(),
                        nativeEvidence: window.indirectSnapshot() };
                });
                assert(record.teardown.session === 0 && record.teardown.statistics === null &&
                    record.teardown.request === 0 && !record.teardown.rendererRegistered &&
                    record.teardown.released.resources.live === 0 &&
                    record.teardown.released.resources.retiring === 0 &&
                    record.teardown.released.resources.readbackTickets === 0 &&
                    record.teardown.released.resources.shaderModuleCacheEntries === 0 &&
                    record.teardown.released.resources.pipelineCacheEntries === 0 &&
                    record.teardown.nativeEvidence.readMaps === normalStart.readMaps,
                'BrowserSmoke.UnlitIndirectTeardown: session, native programs or GPU read maps survived stop.');
                assertNoBrowserErrors(events);
            } catch (error) {
                failed = true;
                record.failure = await page.evaluate(() => window.engineMeshDiagnostic?.failure ?? null).catch(() => null);
                record.assertion = String(error);
                await page.screenshot({ path: path.join(config.output, `engine-unlit-${profile}-${lifecycle}-failure.png`),
                    fullPage: true }).catch(() => {});
                throw error;
            } finally {
                try { await context.close(); }
                catch (error) { record.cleanupFailure = String(error); if (!failed) throw error; }
            }
        }
    }
}
