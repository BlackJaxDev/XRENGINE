import path from 'node:path';

const sourceIds = {
    'clear-a': 'a09559ee-79e9-4bcc-9adf-43cf250bc1db',
    'clear-b': 'a09559ee-79e9-4bcc-9adf-43cf250bc1db',
    quad: '8c583188-43ac-4cd4-8b54-9673cb216585',
};
const clearRgb = [0.04 * 255, 0.32 * 255, 0.68 * 255];

function assert(condition, message) { if (!condition) throw new Error(message); }
const near = (actual, expected, tolerance) => Math.abs(actual - expected) <= tolerance;

async function graphicsState(page) {
    return page.evaluate(() => globalThis.modularPipelineGpu?.() ?? null);
}

async function waitForProfileMarker(page, events, profile, start, timeout) {
    const expected = `ModularPipelineParity active authored camera: ${profile} ` +
        `source=${sourceIds[profile]} aa=None`;
    const deadline = Date.now() + timeout;
    do {
        const marker = events.slice(start).find(event => event.type !== 'error' &&
            event.text?.includes(expected));
        if (marker) return marker;
        if (await page.locator('#status').getAttribute('data-state') === 'failed')
            throw new Error(`BrowserSmoke.ModularStartup: ${await page.locator('#status').textContent()}`);
        await page.waitForTimeout(50);
    } while (Date.now() < deadline);
    throw new Error(`BrowserSmoke.ModularMarkerMissing: expected '${expected}'.`);
}

async function waitForPresentedFrame(page, timeout, needsDraw = false) {
    const baseline = await graphicsState(page);
    assert(baseline, 'BrowserSmoke.ModularGpuObserverMissing: the WebGPU observer was not installed.');
    await page.waitForFunction(({ previous, draw }) => {
        const current = globalThis.modularPipelineGpu?.();
        return current && current.acquired > previous.acquired &&
            current.submitted > previous.submitted && (!draw || current.draws > previous.draws);
    }, { previous: baseline, draw: needsDraw }, { timeout });
    return graphicsState(page);
}

async function sampleScreenshot(page, screenshot) {
    return page.evaluate(async encoded => {
        const bytes = Uint8Array.from(atob(encoded), character => character.charCodeAt(0));
        const bitmap = await createImageBitmap(new Blob([bytes], { type: 'image/png' }));
        const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
        const context = canvas.getContext('2d', { willReadFrequently: true });
        context.drawImage(bitmap, 0, 0);
        bitmap.close();
        const image = context.getImageData(0, 0, canvas.width, canvas.height);
        const point = fraction => {
            const x = Math.floor(canvas.width * fraction), y = Math.floor(canvas.height * 0.5);
            const color = [0, 0, 0, 0];
            for (let dy = -1; dy <= 1; dy++)
                for (let dx = -1; dx <= 1; dx++) {
                    const index = ((y + dy) * canvas.width + x + dx) * 4;
                    for (let channel = 0; channel < 4; channel++) color[channel] += image.data[index + channel] / 9;
                }
            return color;
        };
        return { width: canvas.width, height: canvas.height,
            left: point(0.2), middle: point(0.5), right: point(0.8) };
    }, screenshot.toString('base64'));
}

function matchesPixels(profile, samples) {
    if (profile !== 'quad')
        return [samples.left, samples.middle, samples.right].every(pixel =>
            clearRgb.every((expected, channel) => near(pixel[channel], expected, 12)) &&
            near(pixel[3], 255, 4));
    const left = [0.2 * 255, 0.18 * 255, 0.8 * 255];
    const middle = [0.5 * 255, 0.18 * 255, 0.5 * 255];
    const right = [0.8 * 255, 0.18 * 255, 0.2 * 255];
    return [[samples.left, left], [samples.middle, middle], [samples.right, right]]
        .every(([pixel, expected]) => expected.every((value, channel) => near(pixel[channel], value, 20)) &&
            near(pixel[3], 255, 4));
}

async function captureProfile(page, config, name, profile) {
    const deadline = Date.now() + Math.min(config.timeout, 20000);
    let samples;
    do {
        const image = await page.locator('#input-surface').screenshot({ path: path.join(config.output, `${name}.png`) });
        samples = await sampleScreenshot(page, image);
        if (matchesPixels(profile, samples)) return samples;
        if (await page.locator('#status').getAttribute('data-state') === 'failed')
            throw new Error(`BrowserSmoke.ModularCanvasFailed: ${await page.locator('#status').textContent()}`);
        await page.waitForTimeout(200);
    } while (Date.now() < deadline);
    throw new Error(`BrowserSmoke.ModularPixels: '${profile}' did not reach the canvas. Last samples: ${JSON.stringify(samples)}`);
}

async function selectProfile(page, events, config, iteration, profile, key, stage) {
    if (key) {
        const markerStart = events.length;
        await page.keyboard.press(key);
        await waitForProfileMarker(page, events, profile, markerStart, config.timeout);
    } else await waitForProfileMarker(page, events, profile, 0, config.timeout);
    const gpu = await waitForPresentedFrame(page, config.timeout, profile === 'quad');
    const pixels = await captureProfile(page, config, `modular-${iteration}-${stage}-${profile}`, profile);
    return { profile, sourceId: sourceIds[profile], pixels, gpu };
}

/** Runs the Editor-published player and the saved game's ordinary numbered-camera controls. */
export async function modularPipelineGameCheck(browser, origin, report, config, instrumentedPage, assertNoBrowserErrors) {
    report.modularPipelineIterations = [];
    for (let iteration = 0; iteration < 2; iteration++) {
        const { page, context, events } = await instrumentedPage(browser, origin, report,
            `modular-pipeline-${iteration}`, config);
        try {
            await page.addInitScript(() => {
                const counters = { configured: 0, acquired: 0, submitted: 0, draws: 0 };
                const playerQueues = new WeakSet();
                globalThis.modularPipelineGpu = () => ({ ...counters });
                const configure = GPUCanvasContext.prototype.configure;
                GPUCanvasContext.prototype.configure = function (...args) {
                    const result = configure.apply(this, args);
                    if (this.canvas?.id === 'input-surface') {
                        counters.configured++;
                        if (args[0]?.device?.queue) playerQueues.add(args[0].device.queue);
                    }
                    return result;
                };
                const acquire = GPUCanvasContext.prototype.getCurrentTexture;
                GPUCanvasContext.prototype.getCurrentTexture = function (...args) {
                    const result = acquire.apply(this, args);
                    if (this.canvas?.id === 'input-surface') counters.acquired++;
                    return result;
                };
                const submit = GPUQueue.prototype.submit;
                GPUQueue.prototype.submit = function (...args) {
                    const result = submit.apply(this, args);
                    if (playerQueues.has(this)) counters.submitted++;
                    return result;
                };
                const draw = GPURenderPassEncoder.prototype.draw;
                GPURenderPassEncoder.prototype.draw = function (...args) {
                    const result = draw.apply(this, args);
                    counters.draws++;
                    return result;
                };
                const drawIndexed = GPURenderPassEncoder.prototype.drawIndexed;
                GPURenderPassEncoder.prototype.drawIndexed = function (...args) {
                    const result = drawIndexed.apply(this, args);
                    counters.draws++;
                    return result;
                };
            });
            await page.goto(`${origin}/__game/index.html`, { waitUntil: 'domcontentloaded' });
            assert(await page.locator('#manifest-url').count() === 0 &&
                await page.locator('#world-form').count() === 0,
            'BrowserSmoke.ModularDiagnosticShell: expected the Editor-published player.');
            await page.waitForFunction(() => ['running', 'failed'].includes(document.querySelector('#status')?.dataset.state));
            const state = await page.locator('#status').getAttribute('data-state');
            const detail = await page.locator('#status').textContent();
            assert(state === 'running' && /Modular Pipeline Parity World/i.test(detail),
                `BrowserSmoke.ModularStartup: ${detail}`);
            const canvas = page.locator('#input-surface');
            await canvas.focus();
            assert(await canvas.evaluate(element => document.activeElement === element),
                'BrowserSmoke.ModularInputFocus: the player canvas did not receive keyboard focus.');

            const phases = [];
            phases.push(await selectProfile(page, events, config, iteration, 'clear-a', null, 'playing'));
            phases.push(await selectProfile(page, events, config, iteration, 'clear-b', '2', 'playing'));
            phases.push(await selectProfile(page, events, config, iteration, 'quad', '3', 'playing'));

            const before = await canvas.evaluate(element => [element.width, element.height]);
            await page.setViewportSize({ width: 860 + iteration * 80, height: 780 });
            await page.waitForFunction(([width, height]) => {
                const element = document.querySelector('#input-surface');
                return element.width !== width || element.height !== height;
            }, before);
            const resizedGpu = await waitForPresentedFrame(page, config.timeout, true);
            const resizedQuad = await captureProfile(page, config, `modular-${iteration}-resized-quad`, 'quad');
            phases.push({ profile: 'quad', sourceId: sourceIds.quad, resized: true,
                pixels: resizedQuad, gpu: resizedGpu });
            phases.push(await selectProfile(page, events, config, iteration, 'clear-a', '1', 'resized'));
            phases.push(await selectProfile(page, events, config, iteration, 'clear-b', '2', 'resized'));
            assertNoBrowserErrors(events);
            report.modularPipelineIterations.push({ iteration, detail, canvasSizes: { before,
                after: await canvas.evaluate(element => [element.width, element.height]) }, phases });
        } catch (error) {
            await page.screenshot({ path: path.join(config.output, `modular-${iteration}-failure.png`), fullPage: true }).catch(() => {});
            throw error;
        } finally { await context.close(); }
    }
}
