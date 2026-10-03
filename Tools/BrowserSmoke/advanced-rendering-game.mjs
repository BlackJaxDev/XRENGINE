import path from 'node:path';
import { captureUntil } from './canvas-capture.mjs';

function assert(condition, message) { if (!condition) throw new Error(message); }
const hasSurface = pixels => pixels.colorfulLeft > 100;

/** Observes the ordinary player without replacing its scene, pipelines, or commands. */
export async function advancedRenderingGameCheck(browser, origin, report, config, instrumentedPage, assertNoBrowserErrors) {
    report.advancedRenderingIterations = [];
    for (let iteration = 0; iteration < 2; iteration++) {
        const { page, context, events } = await instrumentedPage(browser, origin, report,
            `advanced-rendering-${iteration}`, config);
        try {
            await page.addInitScript(() => {
                const stats = { compute: {}, raster: {}, readMappings: 0 };
                globalThis.advancedSubmissionEvidence = stats;
                const pipelines = new WeakMap();
                function trackPass(prototype, counters, operations) {
                    const setPipeline = prototype.setPipeline;
                    prototype.setPipeline = function (pipeline) {
                        const result = setPipeline.call(this, pipeline);
                        pipelines.set(this, pipeline.label);
                        return result;
                    };
                    for (const operation of operations) {
                        const original = prototype[operation];
                        prototype[operation] = function (...args) {
                            const result = original.apply(this, args);
                            const label = pipelines.get(this) ?? '';
                            counters[label] = (counters[label] ?? 0) + 1;
                            return result;
                        };
                    }
                }
                trackPass(GPUComputePassEncoder.prototype, stats.compute, ['dispatchWorkgroups', 'dispatchWorkgroupsIndirect']);
                trackPass(GPURenderPassEncoder.prototype, stats.raster, ['draw', 'drawIndexed', 'drawIndirect', 'drawIndexedIndirect']);
                const mapAsync = GPUBuffer.prototype.mapAsync;
                GPUBuffer.prototype.mapAsync = function (mode, ...args) {
                    if ((mode & GPUMapMode.READ) !== 0) stats.readMappings++;
                    return mapAsync.call(this, mode, ...args);
                };
            });
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
            const submissions = await page.evaluate(() => globalThis.advancedSubmissionEvidence);
            for (const pass of ['depth-pyramid', 'gtao', 'shade-classify', 'shade-finalize', 'shade-native', 'shade-background'])
                assert(submissions.compute[`engine-advanced-${pass}`] > 0,
                    `BrowserSmoke.AdvancedStageMissing: no recorded native ${pass} dispatch.`);
            for (const pass of ['visibility-pull', 'present'])
                assert(submissions.raster[`engine-advanced-${pass}`] > 0,
                    `BrowserSmoke.AdvancedRasterMissing: no recorded native ${pass} draw.`);
            assert(submissions.readMappings === 0,
                'BrowserSmoke.AdvancedReadback: the native output requested a GPU read mapping.');
            assertNoBrowserErrors(events);
            report.advancedRenderingIterations.push({ iteration, detail, playing: initial.pixels,
                resized: resized.pixels, submissions, canvasSizes: { before,
                    after: await canvas.evaluate(element => [element.width, element.height]) } });
        } catch (error) {
            await page.screenshot({ path: path.join(config.output, `advanced-rendering-${iteration}-failure.png`), fullPage: true }).catch(() => {});
            throw error;
        } finally { await context.close(); }
    }
}
