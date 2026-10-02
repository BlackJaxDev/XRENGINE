import path from 'node:path';

function assert(condition, message) { if (!condition) throw new Error(message); }

async function inspectCanvas(page, before, after) {
    return page.evaluate(async ({ oldPng, newPng }) => {
        async function pixels(encoded) {
            const bytes = Uint8Array.from(atob(encoded), character => character.charCodeAt(0));
            const bitmap = await createImageBitmap(new Blob([bytes], { type: 'image/png' }));
            const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
            const context = canvas.getContext('2d', { willReadFrequently: true });
            context.drawImage(bitmap, 0, 0);
            bitmap.close();
            return { width: canvas.width, height: canvas.height,
                data: context.getImageData(0, 0, canvas.width, canvas.height).data };
        }
        const current = await pixels(newPng);
        const prior = oldPng ? await pixels(oldPng) : null;
        let colorful = 0, yellow = 0, green = 0, changed = 0;
        for (let index = 0; index < current.data.length; index += 4) {
            const red = current.data[index], greenChannel = current.data[index + 1];
            const blue = current.data[index + 2];
            if (Math.max(red, greenChannel, blue) > 70 &&
                Math.max(red, greenChannel, blue) - Math.min(red, greenChannel, blue) > 35) colorful++;
            if (red > 90 && greenChannel > 90 && blue * 1.5 < Math.min(red, greenChannel)) yellow++;
            if (greenChannel > 90 && greenChannel > red * 1.3 && greenChannel > blue * 1.3) green++;
            if (prior && prior.width === current.width && prior.height === current.height &&
                Math.abs(red - prior.data[index]) +
                Math.abs(greenChannel - prior.data[index + 1]) +
                Math.abs(blue - prior.data[index + 2]) > 45) changed++;
        }
        return { width: current.width, height: current.height, colorful, yellow, green,
            changed: prior?.width === current.width && prior?.height === current.height ? changed : null };
    }, { oldPng: before?.toString('base64') ?? null, newPng: after.toString('base64') });
}

async function capture(page, output, name, previous) {
    const image = await page.locator('#input-surface').screenshot({ path: path.join(output, `${name}.png`) });
    return { image, pixels: await inspectCanvas(page, previous, image) };
}

async function captureUntil(page, config, name, previous, accepts, failure) {
    const deadline = Date.now() + Math.min(config.timeout, 10000);
    let latest;
    do {
        latest = await capture(page, config.output, name, previous);
        if (accepts(latest.pixels)) return latest;
        if (await page.locator('#status').getAttribute('data-state') === 'failed')
            throw new Error(`BrowserSmoke.RollingBallFrameFailed: ${await page.locator('#status').textContent()}`);
        await page.waitForTimeout(250);
    } while (Date.now() < deadline);
    throw new Error(`${failure} Last canvas summary: ${JSON.stringify(latest?.pixels)}`);
}

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
                pixels => pixels.colorful > 200,
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
                pixels => pixels.colorful > 200,
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
