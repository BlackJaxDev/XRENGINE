import path from 'node:path';
import { captureUntil } from './canvas-capture.mjs';

function assert(condition, message) { if (!condition) throw new Error(message); }

async function waitRunning(page) {
    await page.waitForFunction(() => ['running', 'failed'].includes(document.querySelector('#status')?.dataset.state));
    const state = await page.locator('#status').getAttribute('data-state');
    const detail = await page.locator('#status').textContent();
    assert(state === 'running', `BrowserSmoke.RollingBallStartupFailed: ${detail}`);
    assert(/RollingBall|Rolling Ball/i.test(detail) && detail.includes('Jolt physics'),
        `BrowserSmoke.RollingBallWorldMissing: ${detail}`);
    return detail;
}

/** Exercises the unmodified, Editor-activated shipping game on the real Chromium canvas. */
export async function rollingBallGameCheck(browser, origin, report, config, instrumentedPage, assertNoBrowserErrors) {
    report.rollingBallIterations = [];
    for (let iteration = 0; iteration < 2; iteration++) {
        const { page, context, events } = await instrumentedPage(browser, origin, report,
            `rollingball-game-${iteration}`, config);
        try {
            await page.goto(`${origin}/__game/index.html`, { waitUntil: 'domcontentloaded' });
            assert(await page.locator('#manifest-url').count() === 0 &&
                await page.locator('#world-form').count() === 0,
            'BrowserSmoke.RollingBallDiagnosticShell: the game did not use the shipping player.');
            const detail = await waitRunning(page);
            const baseline = await captureUntil(page, config, `rollingball-${iteration}-playing`, null,
                pixels => pixels.colorful > 200 && pixels.green > 100,
                'BrowserSmoke.RollingBallClearOnly: the published game canvas contains no substantial authored color.');
            const result = { iteration, detail, playing: baseline.pixels };

            const canvas = page.locator('#input-surface');
            await canvas.focus();
            assert(await canvas.evaluate(element => document.activeElement === element),
                'BrowserSmoke.RollingBallInputFocus: the production canvas did not receive keyboard focus.');
            if (iteration === 0) {
                await page.keyboard.down('w');
                let tilted;
                try {
                    await page.waitForTimeout(700);
                    tilted = await captureUntil(page, config, 'rollingball-tilt', baseline.image,
                        pixels => pixels.changed > 100,
                        'BrowserSmoke.RollingBallTiltVisual: the real canvas did not change after focused tilt input.');
                } finally { await page.keyboard.up('w'); }
                result.tilt = tilted.pixels;

                await page.keyboard.press('r');
                const reset = await captureUntil(page, config, 'rollingball-reset', tilted.image,
                    pixels => pixels.changed > 100,
                    'BrowserSmoke.RollingBallResetVisual: the real canvas did not change after focused reset input.');
                result.reset = reset.pixels;

                await page.keyboard.press('Escape');
                const paused = await captureUntil(page, config, 'rollingball-paused', reset.image,
                    pixels => pixels.yellow > reset.pixels.yellow + 20,
                    'BrowserSmoke.RollingBallPauseHud: the authored HUD did not visibly switch to its paused color.');
                result.paused = paused.pixels;

                await page.keyboard.press('Escape');
                const resumed = await captureUntil(page, config, 'rollingball-resumed', paused.image,
                    pixels => pixels.yellow + 20 < paused.pixels.yellow,
                    'BrowserSmoke.RollingBallResumeHud: the authored HUD did not leave its paused color.');
                result.resumed = resumed.pixels;
            }

            const originalSize = await canvas.evaluate(element => [element.width, element.height]);
            await page.setViewportSize({ width: 860 + iteration * 80, height: 780 });
            await page.waitForFunction(([width, height]) => {
                const canvas = document.querySelector('#input-surface');
                return canvas.width !== width || canvas.height !== height;
            }, originalSize);
            await waitRunning(page);
            const resized = await captureUntil(page, config, `rollingball-${iteration}-resized`, null,
                pixels => pixels.colorful > 200 && pixels.green > 100,
                'BrowserSmoke.RollingBallResizeClearOnly: the resized game canvas lost its rendered world.');
            result.resized = resized.pixels;
            result.canvasSizes = { before: originalSize,
                after: await canvas.evaluate(element => [element.width, element.height]) };
            report.rollingBallIterations.push(result);
            assertNoBrowserErrors(events);
        } catch (error) {
            await page.screenshot({ path: path.join(config.output, `rollingball-${iteration}-failure.png`),
                fullPage: true }).catch(() => {});
            throw error;
        } finally {
            await context.close();
        }
    }
}
