import path from 'node:path';
import { captureUntil } from './canvas-capture.mjs';
import { installAdvancedSubmissionObservation } from './advanced-submission-observer.mjs';
import { installNativeCompileCapture } from './native-compile-isolation.mjs';

function assert(condition, message) { if (!condition) throw new Error(message); }
const hasSurface = pixels => pixels.colorfulLeft > 100;

/** Observes the ordinary player without replacing its scene, pipelines, or commands. */
export async function advancedRenderingGameCheck(browser, origin, report, config, instrumentedPage, assertNoBrowserErrors) {
    report.advancedRenderingIterations = [];
    report.advancedRenderingFailures = [];
    for (let iteration = 0; iteration < 2; iteration++) {
        const { page, context, events } = await instrumentedPage(browser, origin, report,
            `advanced-rendering-${iteration}`, config);
        let hadFailure = false;
        try {
            await page.addInitScript(installNativeCompileCapture);
            await page.addInitScript(installAdvancedSubmissionObservation);
            await page.goto(`${origin}/__game/index.html`, { waitUntil: 'domcontentloaded' });
            await page.waitForFunction(() => ['running', 'failed'].includes(document.querySelector('#status')?.dataset.state));
            const detail = await page.locator('#status').textContent();
            assert(await page.locator('#status').getAttribute('data-state') === 'running' &&
                /AdvancedRenderingParity|Advanced Rendering Parity/i.test(detail),
            `BrowserSmoke.AdvancedStartup: ${detail}`);
            const initial = await captureUntil(page, config, `advanced-rendering-${iteration}-playing`, null, hasSurface,
                'BrowserSmoke.AdvancedSurfaceMissing: the authored textured surface did not reach the canvas.');
            const canvas = page.locator('#input-surface');
            const before = await canvas.evaluate(element => [element.width, element.height]);
            await page.setViewportSize({ width: 860 + iteration * 80, height: 780 });
            await page.waitForFunction(([width, height]) => {
                const canvas = document.querySelector('#input-surface');
                return canvas.width !== width || canvas.height !== height;
            }, before);
            const resized = await captureUntil(page, config, `advanced-rendering-${iteration}-resized`, null, hasSurface,
                'BrowserSmoke.AdvancedResize: the authored surface disappeared after output replacement.');
            const submissions = await page.evaluate(() => globalThis.advancedSubmissionSnapshot());
            for (const pass of ['depth-pyramid', 'gtao', 'shade-classify', 'shade-finalize', 'shade-background'])
                assert(submissions.compute[`engine-advanced-${pass}`] > 0,
                    `BrowserSmoke.AdvancedStageMissing: no recorded native ${pass} dispatch.`);
            const nativeLabels = ['engine-advanced-shade-native', 'engine-advanced-shade-native-no-modifiers'];
            const selectedNative = nativeLabels.filter(label => submissions.compute[label] > 0);
            assert(selectedNative.length === 1,
                'BrowserSmoke.AdvancedStageMissing: one exact native shading program must dispatch.');
            const nativeCompile = await page.evaluate(() => globalThis.advancedNativeCompileSnapshot?.() ?? null);
            assert(nativeCompile?.captureErrors.length === 0 && nativeCompile.records.some(record =>
                record.recipeStatus === 'ready' && record.status === 'fulfilled' &&
                record.recipe?.pipeline?.label === selectedNative[0]),
            'BrowserSmoke.AdvancedNativeIdentity: the selected native program needs a captured compile recipe.');
            assert(submissions.creation.computePipelines.records.some(record =>
                record.label === selectedNative[0] && record.status === 'fulfilled'),
            'BrowserSmoke.AdvancedNativeIdentity: the selected native pipeline did not compile.');
            for (const pass of ['visibility-pull', 'present'])
                assert(submissions.raster[`engine-advanced-${pass}`] > 0,
                    `BrowserSmoke.AdvancedRasterMissing: no recorded native ${pass} draw.`);
            assert(submissions.readMappings === 0,
                'BrowserSmoke.AdvancedReadback: the native output requested a GPU read mapping.');
            assertNoBrowserErrors(events);
            report.advancedRenderingIterations.push({ iteration, detail, playing: initial.pixels,
                resized: resized.pixels, selectedNativeProgram: selectedNative[0], nativeCompile,
                submissions, canvasSizes: { before,
                    after: await canvas.evaluate(element => [element.width, element.height]) } });
        } catch (error) {
            hadFailure = true;
            // The host has already retired its renderer by the time status becomes failed.
            // Read the failure event before screenshot or context cleanup can also fail.
            const evidence = await page.evaluate(() => ({
                failure: globalThis.advancedCanvasFailureEvidence ?? null,
                nativeCompile: globalThis.advancedNativeCompileSnapshot?.() ?? null,
                submissions: globalThis.advancedSubmissionSnapshot?.() ?? globalThis.advancedSubmissionEvidence ?? null,
            })).catch(() => null);
            report.advancedRenderingFailures.push({ iteration,
                failure: evidence?.failure ?? null, submissions: evidence?.submissions ?? null,
                nativeCompile: evidence?.nativeCompile ?? null });
            await page.screenshot({ path: path.join(config.output, `advanced-rendering-${iteration}-failure.png`), fullPage: true }).catch(() => {});
            throw error;
        } finally {
            if (hadFailure) await context.close().catch(() => {});
            else await context.close();
        }
    }
}
