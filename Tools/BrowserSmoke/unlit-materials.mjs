import path from 'node:path';
import { canvasGeometry } from './canvas-capture.mjs';

const bytes = [64, 128, 192, 96];
const masked = [200, 40, 80];
const teal = [16, 160, 96, 224];
const magenta = [200, 32, 240, 80];
const background = [0.125, 0.25, 0.375, 1];
const coordinates = [0.175, 0.5, 0.825];
const semanticVersions = [1, 2, 2, 3, 4, 4, 4, 5, 5];
const cases = [
    { semantic: 'UnlitColorV1', factory: 'CreateUnlitColorMaterialForward', coverage: 'opaque',
        authoredColor: [1.5, 0.25, 0.5, 0.75], hdr: [1.5, 0.25, 0.5, 0.75] },
    { semantic: 'UnlitTextureV2', factory: 'CreateUnlitTextureMaterialForward', coverage: 'opaque',
        textureFormat: 'Rgba8', authoredRgbaBytes: bytes, hdr: bytes.map(value => value / 255) },
    { semantic: 'UnlitTextureV2', factory: 'CreateUnlitTextureMaterialForward', coverage: 'opaque',
        textureFormat: 'Srgb8Alpha8', authoredRgbaBytes: bytes,
        hdr: [...bytes.slice(0, 3).map(value => srgbToLinear(value / 255)), bytes[3] / 255] },
    { semantic: 'UnlitOpaqueTextureV3', factory: 'CreateUnlitOpaqueTextureMaterialForward', coverage: 'opaque',
        textureFormat: 'Rgba8', authoredRgbaBytes: bytes,
        hdr: [...bytes.slice(0, 3).map(value => value / 255), 1] },
    ...[127, 128, 129].map(alpha => ({ semantic: 'UnlitAlphaTextureV4',
        factory: 'CreateUnlitAlphaTextureMaterialForward', coverage: 'masked', textureFormat: 'Rgba8',
        authoredRgbaBytes: [...masked, alpha], alphaCutoffNumerator: 128, alphaCutoffDenominator: 255,
        backgroundColor: background, hdr: alpha < 128 ? background : [...masked, alpha].map(value => value / 255) })),
    { semantic: 'UnlitTextureArraySliceV5', factory: 'CreateUnlitTextureArraySliceMaterialForward',
        coverage: 'opaque', textureFormat: 'Rgba8', sampledLayer: 0, arrayLayerBytes: [teal, magenta],
        hdr: teal.map(value => value / 255) },
    { semantic: 'UnlitTextureArraySliceV5', factory: 'CreateUnlitTextureArraySliceMaterialForward',
        coverage: 'opaque', textureFormat: 'Rgba8', sampledLayer: 0, arrayLayerBytes: [magenta, teal],
        hdr: magenta.map(value => value / 255) },
];

function srgbToLinear(value) {
    return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
}

function display(hdr) {
    return [...hdr.slice(0, 3).map(value =>
        Math.round(255 * Math.min(1, value * 1.6 / (value + 0.6)) ** (1 / 2.2))), 255];
}

function assert(condition, message) { if (!condition) throw new Error(message); }

function same(actual, expected) { return JSON.stringify(actual) === JSON.stringify(expected); }

function handles(state) {
    return { shaders: state.shaders, pipelines: state.pipelines };
}

function nativePrograms(state) {
    const entries = values => values.map(({ label, nativeId }) => ({ label, nativeId }))
        .sort((left, right) => left.label.localeCompare(right.label) || left.nativeId - right.nativeId);
    return { shaders: entries(state.shaders), pipelines: entries(state.pipelines) };
}

function cacheSnapshot(statistics) {
    const resources = statistics.resources;
    return { shaderModuleCacheEntries: resources.shaderModuleCacheEntries,
        shaderModuleCacheKeyBytes: resources.shaderModuleCacheKeyBytes,
        shaderModuleCacheHits: resources.shaderModuleCacheHits,
        shaderModuleCacheMisses: resources.shaderModuleCacheMisses,
        pipelineCacheEntries: resources.pipelineCacheEntries };
}

function checkState(state, index, width, height) {
    const expected = cases[index];
    assert(state.sampleCase === index && state.pipeline === 'DefaultRenderPipeline' &&
        state.submission === 'CpuDirect' && state.target === 'HDRSceneTex' &&
        state.semantic === expected.semantic && state.semanticVersion === semanticVersions[index] &&
        state.factory === expected.factory && state.coverage === expected.coverage && state.blend === 'disabled',
    `BrowserSmoke.UnlitState: case ${index} lost its factory, default pipeline or CPU-direct contract.`);
    assert(state.sampleU === coordinates[index % 3] && state.sampleV === coordinates[Math.floor(index / 3)],
        `BrowserSmoke.UnlitCoordinates: case ${index} is no longer at its fixed board position.`);
    for (const key of ['authoredColor', 'textureFormat', 'authoredRgbaBytes', 'alphaCutoffNumerator',
        'alphaCutoffDenominator', 'backgroundColor', 'sampledLayer', 'arrayLayerBytes'])
        assert(same(state[key], expected[key]), `BrowserSmoke.UnlitAuthoredInput: case ${index} ${key} changed.`);
    if (expected.textureFormat)
        assert(state.textureColorSpace === (expected.textureFormat === 'Srgb8Alpha8' ? 'srgb' : 'linear'),
            `BrowserSmoke.UnlitTextureColorSpace: case ${index} lost its authored texture interpretation.`);
    assert(typeof state.artifactIdentity === 'string' && state.artifactIdentity.length > 0,
        `BrowserSmoke.UnlitCatalogIdentity: case ${index} has no verified package artifact.`);
    assert(state.hdrTargets.length === 1 && state.hdrTargets[0].width === width &&
        state.hdrTargets[0].height === height && state.hdrTargets[0].format === 'rgba16float' &&
        state.hdrTargets[0].sampleCount === 1,
    `BrowserSmoke.UnlitHdrTarget: expected one ${width}x${height} single-sample RGBA16F HDRSceneTex.`);
    assert(state.shaders.length >= 6 && state.pipelines.length >= 6,
        'BrowserSmoke.UnlitPrograms: five material variants and presentation require real shader and pipeline resources.');
    assert([...state.shaders, ...state.pipelines].every(entry => Number.isInteger(entry.nativeId) && entry.nativeId > 0),
        'BrowserSmoke.UnlitNativeIdentity: a shader or pipeline lacks its native GPU object identity.');
}

async function waitReady(page, previous, timeout) {
    await page.waitForFunction(before => {
        const host = window.engineMeshDiagnostic;
        return !!host?.failure || (host?.session > 0 && host.readyFrames >= before + 2 &&
            host.statistics()?.resources?.retiring === 0);
    }, previous, { timeout: Math.min(timeout, 120000) });
    const failure = await page.evaluate(() => window.engineMeshDiagnostic.failure);
    assert(!failure, `BrowserSmoke.UnlitFrameFailed: ${JSON.stringify(failure)}`);
    const status = await page.locator('#status').textContent();
    assert(status.startsWith('Engine mesh unlit diagnostic rendered'), `BrowserSmoke.UnlitFrameStatus: ${status}`);
}

async function sampleBoard(page, output, label, width, height, capturePixels, boards) {
    const board = { label, width, height, pixels: null, hdr: [], comparisons: [] };
    boards.push(board);
    const samples = cases.map((expected, index) => ({ name: `case-${index}`,
        x: Math.floor(width * coordinates[index % 3]),
        y: Math.floor(height * coordinates[Math.floor(index / 3)]),
        expected: display(expected.hdr) }));
    board.canvasGeometry = await canvasGeometry(page);
    const png = await page.locator('canvas').screenshot({ path: path.join(output, `engine-unlit-${label}.png`) });
    board.pixels = await capturePixels(page, png, samples);
    for (let index = 0; index < cases.length; index++) {
        const expected = cases[index];
        const site = await page.evaluate(({ u, v }) => window.engineMeshDiagnostic.readHdrAt(u, v),
            { u: coordinates[index % 3], v: coordinates[Math.floor(index / 3)] });
        board.hdr.push(site);
        board.comparisons.push({ index, expectedHdr: expected.hdr, expectedDisplay: samples[index].expected,
            observedHdr: site, observedDisplay: board.pixels.samples[index] });
    }
    return board;
}

function assertBoard(board) {
    const { label, width, height } = board;
    assert(board.pixels.width === width && board.pixels.height === height,
        `BrowserSmoke.UnlitCanvasExtent: ${label} captured ${board.pixels.width}x${board.pixels.height}.`);
    for (const comparison of board.comparisons) {
        const { index, observedHdr: site, observedDisplay: visible } = comparison;
        const expected = cases[index];
        assert(site.label === 'HDRSceneTex' && site.format === 'rgba16float' &&
            site.width === width && site.height === height,
        `BrowserSmoke.UnlitReadbackTarget: case ${index} used the wrong target.`);
        for (let channel = 0; channel < 4; channel++) {
            const target = expected.hdr[channel];
            const tolerance = Math.max(0.004, Math.abs(target) * 0.012);
            assert(site.min[channel] >= target - tolerance && site.max[channel] <= target + tolerance,
                `BrowserSmoke.UnlitHdrMismatch: ${label} case ${index} channel ${channel}, expected ${target} ±${tolerance}, observed ${site.min[channel]}..${site.max[channel]}.`);
            const displayExpected = visible.expected[channel];
            assert(visible.min[channel] >= displayExpected - 3 && visible.max[channel] <= displayExpected + 3,
                `BrowserSmoke.UnlitDisplayMismatch: ${label} case ${index} channel ${channel}, expected ${displayExpected} ±3, observed ${visible.min[channel]}..${visible.max[channel]}.`);
        }
    }
}

function compareStates(stage, width, height, expectedCatalog, expectedNative) {
    const stageHandles = handles(stage.states[0]);
    const nativeBaseline = expectedNative ?? nativePrograms(stage.states[0]);
    const currentCatalog = stage.states.map(state => state.artifactIdentity);
    stage.catalogIdentities = currentCatalog;
    stage.handleBaseline = stageHandles;
    stage.nativeBaseline = nativeBaseline;
    for (let index = 0; index < cases.length; index++) {
        const state = stage.states[index];
        stage.comparisons.push({ index,
            expected: { semantic: cases[index].semantic, factory: cases[index].factory,
                artifactIdentity: expectedCatalog?.[index] ?? currentCatalog[index],
                handles: stageHandles, nativePrograms: nativeBaseline },
            actual: { semantic: state.semantic, factory: state.factory,
                artifactIdentity: state.artifactIdentity, handles: handles(state),
                nativePrograms: nativePrograms(state) } });
    }
    return { currentCatalog, nativeBaseline };
}

function assertComparedStates(stage, width, height) {
    for (const comparison of stage.comparisons) {
        const { index, expected, actual } = comparison;
        checkState(stage.states[index], index, width, height);
        assert(same(actual.handles, expected.handles),
            `BrowserSmoke.UnlitCaseMutation: case ${index} metadata selection replaced live GPU handles.`);
        assert(actual.artifactIdentity === expected.artifactIdentity,
            `BrowserSmoke.UnlitCatalogIdentity: case ${index} changed its package artifact.`);
        assert(same(actual.nativePrograms, expected.nativePrograms),
            `BrowserSmoke.UnlitNativePrograms: case ${index} changed native shader or render pipeline identity.`);
    }
}

export async function unlitMaterialsCheck(browser, origin, report, config, instrumentedPage, capturePixels, assertNoBrowserErrors) {
    const { page, context, events } = await instrumentedPage(browser, origin, report, 'engine-unlit-materials', config);
    try {
        await page.goto(`${origin}/diagnostics/engine-mesh.html?probe=unlit&assets=${encodeURIComponent(`${origin}${config.engineManifest}`)}`,
            { waitUntil: 'domcontentloaded' });
        await page.waitForFunction(() => window.engineMeshDiagnostic !== undefined);
        report.unlitMaterials = { states: [], boards: [], lifecycles: [] };
        let catalogIdentities, initialLive;
        for (let lifecycle = 0; lifecycle < 2; lifecycle++) {
            const record = { lifecycle, initial: { states: [], statistics: null, comparisons: [] },
                resizes: [], finalStatistics: null };
            report.unlitMaterials.lifecycles.push(record);
            report.unlitMaterials.states.push(record.initial.states);
            await page.locator('#start').click();
            await waitReady(page, 0, config.timeout);
            for (let index = 0; index < cases.length; index++) {
                await page.evaluate(value => window.engineMeshDiagnostic.setUnlitCase(value), index);
                record.initial.states.push(await page.evaluate(() => window.engineMeshDiagnostic.unlitState()));
            }
            record.initial.statistics = await page.evaluate(() => window.engineMeshDiagnostic.statistics());
            const baseline = record.initial.statistics;
            record.initial.cache = cacheSnapshot(baseline);
            const { currentCatalog, nativeBaseline } = compareStates(record.initial, 512, 512, catalogIdentities);
            record.initial.board = await sampleBoard(page, config.output, `start-${lifecycle}`, 512, 512,
                capturePixels, report.unlitMaterials.boards);
            record.initial.postReadbackStatistics = await page.evaluate(() => window.engineMeshDiagnostic.statistics());
            assertBoard(record.initial.board);
            assertComparedStates(record.initial, 512, 512);
            assert(currentCatalog[1] === currentCatalog[2] &&
                currentCatalog[4] === currentCatalog[5] && currentCatalog[5] === currentCatalog[6] &&
                currentCatalog[7] === currentCatalog[8] && new Set(currentCatalog).size === 5,
            'BrowserSmoke.UnlitCatalogVariants: board cases did not resolve five package variants.');
            catalogIdentities ??= currentCatalog;
            assert(baseline.draws >= 10 && baseline.frameSubmitCalls > 0 && baseline.packets === 0 &&
                baseline.focusedPipeline === null,
            'BrowserSmoke.UnlitSubmission: the board needs real engine mesh and presentation commands.');
            initialLive ??= baseline.resources.live;
            assert(baseline.resources.live <= initialLive && baseline.resources.retiring === 0,
                'BrowserSmoke.UnlitRestartRetention: fresh startup retained resources from the previous session.');
            const baselineCache = record.initial.cache;
            assert([baselineCache.shaderModuleCacheEntries, baselineCache.shaderModuleCacheKeyBytes,
                baselineCache.shaderModuleCacheHits, baselineCache.shaderModuleCacheMisses,
                baselineCache.pipelineCacheEntries].every(Number.isInteger),
            'BrowserSmoke.UnlitCacheStatistics: native program cache counters are missing.');
            for (const [width, height] of [[640, 384], [512, 512]]) {
                const resized = { width, height, states: [], statistics: null, comparisons: [], cacheComparison: null };
                record.resizes.push(resized);
                const previous = await page.evaluate(() => window.engineMeshDiagnostic.readyFrames);
                await page.evaluate(([w, h]) => window.engineMeshDiagnostic.resize(w, h), [width, height]);
                await waitReady(page, previous, config.timeout);
                resized.statistics = await page.evaluate(() => window.engineMeshDiagnostic.statistics());
                for (let index = 0; index < cases.length; index++) {
                    await page.evaluate(value => window.engineMeshDiagnostic.setUnlitCase(value), index);
                    resized.states.push(await page.evaluate(() => window.engineMeshDiagnostic.unlitState()));
                }
                const currentCache = cacheSnapshot(resized.statistics);
                resized.cacheComparison = { expected: baselineCache, actual: currentCache };
                compareStates(resized, width, height, currentCatalog, nativeBaseline);
                resized.board = await sampleBoard(page, config.output,
                    `restart-${lifecycle}-${width}x${height}`, width, height, capturePixels,
                    report.unlitMaterials.boards);
                resized.postReadbackStatistics = await page.evaluate(() => window.engineMeshDiagnostic.statistics());
                assertBoard(resized.board);
                assertComparedStates(resized, width, height);
                assert(currentCache.shaderModuleCacheEntries === baselineCache.shaderModuleCacheEntries &&
                    currentCache.shaderModuleCacheKeyBytes === baselineCache.shaderModuleCacheKeyBytes &&
                    currentCache.shaderModuleCacheMisses === baselineCache.shaderModuleCacheMisses &&
                    currentCache.pipelineCacheEntries === baselineCache.pipelineCacheEntries &&
                    currentCache.shaderModuleCacheHits >= baselineCache.shaderModuleCacheHits,
                'BrowserSmoke.UnlitNativeCacheGrowth: resize created or retained additional native programs.');
                const statistics = resized.postReadbackStatistics;
                assert(statistics.resources.live <= baseline.resources.live && statistics.resources.retiring === 0,
                    'BrowserSmoke.UnlitResizeRetention: the resized board retained GPU resources.');
            }
            record.finalStatistics = await page.evaluate(() => window.engineMeshDiagnostic.statistics());
            await page.locator('#stop').click();
            assert(await page.evaluate(() => window.engineMeshDiagnostic.session === 0 &&
                window.engineMeshDiagnostic.statistics() === null),
            'BrowserSmoke.UnlitTeardown: an engine session survived stop.');
        }
        assertNoBrowserErrors(events);
    } catch (error) {
        report.unlitFailure = await page.evaluate(() => window.engineMeshDiagnostic?.failure ?? null).catch(() => null);
        await page.screenshot({ path: path.join(config.output, 'engine-unlit-failure.png'), fullPage: true }).catch(() => {});
        throw error;
    } finally { await context.close(); }
}
