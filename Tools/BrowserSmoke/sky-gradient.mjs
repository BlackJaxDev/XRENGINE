import path from 'node:path';

const names = ['gradient', 'translated', 'pitched', 'yawed', 'hdr-intensity', 'solid', 'restored'];
const positions = [[0.125, 0.125], [0.875, 0.125], [0.125, 0.875], [0.875, 0.875], [0.5, 0.5]];
const top = [1.2, 0.15, 0.05], bottom = [0.05, 0.2, 0.9];

function assert(condition, message) { if (!condition) throw new Error(message); }

// Analytic camera rays and authored colors supply only expected values. The
// renderer receives no reference pixels or replacement shader from this module.
function reference(sampleCase, x, y, width, height, foreground) {
    if (foreground) return [0.4 * 0.33, 0.2 * 0.33, 0.1 * 0.33, 0.7];
    const dx = (2 * (x + 0.5) / width - 1) * width / height;
    const dy = 1 - 2 * (y + 0.5) / height;
    const length = Math.hypot(dx, dy, 1);
    const worldY = sampleCase === 2 ? (dy * Math.cos(Math.PI / 6) + Math.sin(Math.PI / 6)) / length : dy / length;
    const t = Math.max(0, Math.min(1, worldY * 0.5 + 0.5));
    const intensity = sampleCase === 4 ? 4 : 1;
    return [...top.map((value, channel) => (sampleCase === 5 ? value : bottom[channel] * (1 - t) + value * t) * intensity), 1];
}

function display(hdr) {
    return [...hdr.slice(0, 3).map(value => Math.round(255 * Math.min(1, value * 1.6 / (value + 0.6)) ** (1 / 2.2))), 255];
}

function expectedRegion(sampleCase, x, y, width, height, foreground) {
    const hdr = [], pixels = [];
    for (let row = y - 2; row <= y + 2; row++)
        for (let column = x - 2; column <= x + 2; column++) {
            const values = reference(sampleCase, column, row, width, height, foreground);
            hdr.push(values); pixels.push(display(values));
        }
    const range = values => ({
        min: [0, 1, 2, 3].map(channel => Math.min(...values.map(pixel => pixel[channel]))),
        max: [0, 1, 2, 3].map(channel => Math.max(...values.map(pixel => pixel[channel]))),
    });
    return { hdr: range(hdr), pixels: range(pixels) };
}

async function settled(page) {
    await page.waitForFunction(() => {
        const host = window.engineMeshDiagnostic;
        return host?.failure || host?.readyFrames >= 3 && host.lastReadySession === host.session &&
            host.statistics()?.resources?.retiring === 0 &&
            document.querySelector('#status')?.textContent.startsWith('Engine mesh sky diagnostic rendered');
    });
    const failure = await page.evaluate(() => window.engineMeshDiagnostic.failure);
    assert(!failure, `BrowserSmoke.SkyFrameFailed: ${JSON.stringify(failure)}`);
}

/** Qualifies the engine gradient sky, its foreground depth, and HDR presentation. */
export async function skyGradientCheck(browser, origin, report, config, instrumentedPage, capturePixels, assertNoBrowserErrors) {
    const { page, context, events } = await instrumentedPage(browser, origin, report, 'engine-sky-gradient', config);
    report.skyGradient = { scope: 'Gradient and solid engine sky; other projections and procedural controls remain unqualified.', cases: [] };
    try {
        await page.goto(`${origin}/diagnostics/engine-mesh.html?probe=sky&manifest=${encodeURIComponent(origin + '/__shaders/manifest.json')}` +
            `&assets=${encodeURIComponent(origin + config.engineManifest)}`, { waitUntil: 'domcontentloaded' });
        await page.waitForFunction(() => window.engineMeshDiagnostic !== undefined);
        let initialLive;
        for (let iteration = 0; iteration < 2; iteration++) {
            await page.locator('#start').click();
            await settled(page);
            const baseline = await page.evaluate(() => window.engineMeshDiagnostic.statistics());
            initialLive ??= baseline.resources.live;
            assert(baseline.resources.live <= initialLive, 'BrowserSmoke.SkyRestartRetention: a new device session retained old resources.');
            const schedule = [...names.map((_, sampleCase) => ({ sampleCase, width: 512, height: 512 })),
                { sampleCase: 6, width: 640, height: 384 }, { sampleCase: 6, width: 512, height: 512 }];
            let previousWidth = 512, previousHeight = 512, programs, previousCase, nativePrograms;
            for (const { sampleCase, width, height } of schedule) {
                const resize = width !== previousWidth || height !== previousHeight;
                await page.evaluate(({ sampleCase, width, height, resize }) => {
                    const host = window.engineMeshDiagnostic;
                    host.setSkyCase(sampleCase);
                    if (resize) host.resize(width, height);
                }, { sampleCase, width, height, resize });
                previousWidth = width; previousHeight = height;
                await settled(page);
                const pause = await page.evaluate(() => window.engineMeshDiagnostic.pauseDiagnosticFrames());
                try {
                    const state = await page.evaluate(() => window.engineMeshDiagnostic.skyState());
                    assert(state.sampleCase === sampleCase && state.semantic === 'SkyboxGradient' && state.semanticVersion === 1 &&
                        state.authoredShaderCount === 0 && state.pipeline === 'DefaultRenderPipeline' &&
                        state.mode === (sampleCase === 5 ? 'SolidColor' : 'Gradient') && state.intensity === (sampleCase === 4 ? 4 : 1),
                    'BrowserSmoke.SkyIdentity: the real engine sky or its authored case changed.');
                    const currentPrograms = JSON.stringify({ shaders: state.shaders, pipelines: state.pipelines });
                    // Mode changes replace the material. Resize can replace managed
                    // handles, but must retain the same native GPU programs.
                    const replacesMaterial = sampleCase === 5 || previousCase === 5;
                    const nativeEntries = values => values.map(({ label, nativeId }) => ({ label, nativeId }))
                        .sort((left, right) => left.label.localeCompare(right.label));
                    const currentNatives = JSON.stringify({ shaders: nativeEntries(state.shaders), pipelines: nativeEntries(state.pipelines) });
                    if (nativePrograms !== undefined && !replacesMaterial)
                        assert(currentNatives === nativePrograms, 'BrowserSmoke.SkyNativePrograms: a numeric, camera, or resize update replaced native GPU programs.');
                    nativePrograms = currentNatives;
                    if (programs !== undefined && !replacesMaterial && !resize)
                    {
                        if (currentPrograms !== programs)
                            report.skyGradient.programMismatch = { iteration, sampleCase, width, height,
                                previous: JSON.parse(programs), current: JSON.parse(currentPrograms) };
                        assert(currentPrograms === programs, 'BrowserSmoke.SkyPrograms: numeric or camera updates replaced a cooked program handle.');
                    }
                    programs = currentPrograms;
                    previousCase = sampleCase;
                    assert(state.shaders.filter(shader => shader.label === 'engine-skybox-gradient').length === 1 &&
                        state.pipelines.filter(pipeline => pipeline.label === 'engine-skybox-gradient').length === 1,
                    'BrowserSmoke.SkyArtifact: expected exactly one cooked gradient sky shader and pipeline.');
                    const samples = positions.map(([u, v], index) => ({ name: `sample-${index}`, u, v,
                        x: Math.floor(u * width), y: Math.floor(v * height) }));
                    const hdr = await page.evaluate(async samples => {
                        const result = [];
                        for (const { u, v } of samples) result.push(await window.engineMeshDiagnostic.readHdrAt(u, v));
                        return result;
                    }, samples);
                    const png = await page.locator('canvas').screenshot({ path: path.join(config.output,
                        `engine-sky-${iteration}-${names[sampleCase]}-${width}x${height}.png`) });
                    const pixels = await capturePixels(page, png, samples);
                    assert(pixels.width === width && pixels.height === height, 'BrowserSmoke.SkyCanvasExtent: unexpected canvas capture extent.');
                    const expected = samples.map(({ x, y }, index) => expectedRegion(sampleCase, x, y, width, height,
                        index === 4 && [0, 4, 5, 6].includes(sampleCase)));
                    const statistics = await page.evaluate(() => window.engineMeshDiagnostic.statistics());
                    report.skyGradient.cases.push({ iteration, sampleCase, name: names[sampleCase], width, height, state, hdr, pixels, expected, statistics });
                    for (let index = 0; index < samples.length; index++) {
                        assert(hdr[index].format === 'rgba16float' && hdr[index].width === width && hdr[index].height === height,
                            'BrowserSmoke.SkyHdrTarget: expected the current engine RGBA16F scene target.');
                        for (let channel = 0; channel < 4; channel++) {
                            const value = expected[index], tolerance = Math.max(0.002, value.hdr.max[channel] * 0.002);
                            assert(hdr[index].min[channel] >= value.hdr.min[channel] - tolerance &&
                                hdr[index].max[channel] <= value.hdr.max[channel] + tolerance,
                            `BrowserSmoke.SkyHdrMismatch: ${names[sampleCase]}, sample ${index}, channel ${channel}.`);
                            assert(pixels.samples[index].min[channel] >= value.pixels.min[channel] - 3 &&
                                pixels.samples[index].max[channel] <= value.pixels.max[channel] + 3,
                            `BrowserSmoke.SkyTonemapMismatch: ${names[sampleCase]}, sample ${index}, channel ${channel}.`);
                        }
                    }
                    assert(statistics.resources.live <= baseline.resources.live && statistics.resources.retiring === 0,
                        'BrowserSmoke.SkyRetention: sky changes or resize retained resources.');
                } finally {
                    await page.evaluate(id => window.engineMeshDiagnostic.resumeEffectsFrames(id), pause);
                }
            }
            await page.locator('#stop').click();
            assert(await page.evaluate(() => window.engineMeshDiagnostic.session === 0 && window.engineMeshDiagnostic.statistics() === null),
                'BrowserSmoke.SkyTeardown: the diagnostic renderer survived stop.');
        }
        assertNoBrowserErrors(events);
    } catch (error) {
        report.skyGradient.failure = await page.evaluate(() => window.engineMeshDiagnostic?.failure ?? null).catch(() => null);
        await page.screenshot({ path: path.join(config.output, 'engine-sky-failure.png'), fullPage: true }).catch(() => {});
        throw error;
    } finally { await context.close(); }
}
