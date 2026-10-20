import path from 'node:path';
import { capture, captureUntil } from './canvas-capture.mjs';

function assert(condition, message) { if (!condition) throw new Error(message); }
const hasSurfaces = pixels => pixels.colorfulLeft > 100 && pixels.colorfulRight > 100;

async function waitRunning(page) {
    await page.waitForFunction(() => ['running', 'failed'].includes(document.querySelector('#status')?.dataset.state));
    const state = await page.locator('#status').getAttribute('data-state');
    const detail = await page.locator('#status').textContent();
    assert(state === 'running' && /RenderingParity|Rendering Parity/i.test(detail) && detail.includes('Jolt physics'),
        `BrowserSmoke.RenderingParityStartup: ${detail}`);
    return detail;
}

async function waitStable(page, config, name) {
    const deadline = Date.now() + Math.min(config.timeout, 20000);
    let previous = await capture(page, config.output, name, null), stable = 0;
    do {
        await page.waitForTimeout(500);
        const current = await capture(page, config.output, name, previous.image);
        assert(hasSurfaces(current.pixels), 'BrowserSmoke.RenderingParityPauseBlank: authored surfaces disappeared.');
        // Small differences may come from bounded post-effect sampling. Large
        // silhouette changes must stop for several independently captured frames.
        if (current.pixels.changedRight <= Math.max(30, current.pixels.colorfulRight * 0.002)) stable++;
        else stable = 0;
        previous = current;
        if (stable === 3) return current;
        if (await page.locator('#status').getAttribute('data-state') === 'failed')
            throw new Error(`BrowserSmoke.RenderingParityFrameFailed: ${await page.locator('#status').textContent()}`);
    } while (Date.now() < deadline);
    throw new Error(`BrowserSmoke.RenderingParityPause: animation did not settle after focused Space input: ${JSON.stringify(previous.pixels)}`);
}

/** Runs the unmodified Editor player and authored fixed-camera skeletal/morph scene. */
export async function renderingParityGameCheck(browser, origin, report, config, instrumentedPage, assertNoBrowserErrors) {
    report.renderingParityIterations = [];
    for (let iteration = 0; iteration < 2; iteration++) {
        const { page, context, events } = await instrumentedPage(browser, origin, report,
            `rendering-parity-${iteration}`, config);
        try {
            await page.goto(`${origin}/__game/index.html`, { waitUntil: 'domcontentloaded' });
            assert(await page.locator('#manifest-url').count() === 0 && await page.locator('#world-form').count() === 0,
                'BrowserSmoke.RenderingParityDiagnosticShell: the authored project did not use the shipping player.');
            const detail = await waitRunning(page);
            const initial = await captureUntil(page, config, `rendering-parity-${iteration}-playing`, null, hasSurfaces,
                'BrowserSmoke.RenderingParitySurfacesMissing: both authored mapped surfaces must reach the canvas interior.');
            const result = { iteration, detail, playing: initial.pixels };
            const canvas = page.locator('#input-surface');
            await canvas.focus();
            assert(await canvas.evaluate(element => document.activeElement === element),
                'BrowserSmoke.RenderingParityInputFocus: the engine canvas did not receive focus.');
            if (iteration === 0) {
                const animated = await captureUntil(page, config, 'rendering-parity-animated', initial.image,
                    pixels => hasSurfaces(pixels) && pixels.changedRight > 100,
                    'BrowserSmoke.RenderingParityAnimation: the authored ribbon did not change on the presented canvas.');
                result.animated = animated.pixels;
                await page.keyboard.press('Space');
                const paused = await waitStable(page, config, 'rendering-parity-paused');
                result.paused = paused.pixels;
                await page.keyboard.press('r');
                const reset = await captureUntil(page, config, 'rendering-parity-reset', paused.image,
                    pixels => hasSurfaces(pixels) && pixels.changedRight > 50,
                    'BrowserSmoke.RenderingParityReset: paused ribbon did not return to its bind pose.');
                result.reset = reset.pixels;
                await page.keyboard.press('Space');
                const resumed = await captureUntil(page, config, 'rendering-parity-resumed', reset.image,
                    pixels => hasSurfaces(pixels) && pixels.changedRight > 50,
                    'BrowserSmoke.RenderingParityResume: ribbon did not resume after focused Space input.');
                result.resumed = resumed.pixels;
            }
            const beforeSize = await canvas.evaluate(element => [element.width, element.height]);
            await page.setViewportSize({ width: 860 + iteration * 80, height: 780 });
            await page.waitForFunction(([width, height]) => {
                const canvas = document.querySelector('#input-surface');
                return canvas.width !== width || canvas.height !== height;
            }, beforeSize);
            await waitRunning(page);
            const resized = await captureUntil(page, config, `rendering-parity-${iteration}-resized`, null, hasSurfaces,
                'BrowserSmoke.RenderingParityResize: resized output lost an authored mapped surface.');
            result.resized = resized.pixels;
            result.canvasSizes = { before: beforeSize, after: await canvas.evaluate(element => [element.width, element.height]) };
            report.renderingParityIterations.push(result);
            assertNoBrowserErrors(events);
        } catch (error) {
            await page.screenshot({ path: path.join(config.output, `rendering-parity-${iteration}-failure.png`), fullPage: true }).catch(() => {});
            throw error;
        } finally { await context.close(); }
    }
}
