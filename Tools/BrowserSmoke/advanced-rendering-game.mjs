import path from 'node:path';
import { captureUntil } from './canvas-capture.mjs';
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
            await page.addInitScript(() => {
                const maximumCreationRecords = 128;
                const stats = { compute: {}, raster: {}, readMappings: 0, creation: {
                    computePipelines: { calls: 0, omitted: 0, records: [] },
                    shaderModules: { calls: 0, omitted: 0, records: [] },
                } };
                globalThis.advancedSubmissionEvidence = stats;
                globalThis.advancedCanvasFailureEvidence = null;
                globalThis.advancedSubmissionSnapshot = () => {
                    const pipelines = stats.creation.computePipelines;
                    const now = performance.now();
                    return { ...stats, creation: { ...stats.creation, computePipelines: {
                        calls: pipelines.calls, omitted: pipelines.omitted,
                        records: pipelines.records.map(({ startedAtMs, ...record }) => ({
                            ...record, elapsedMs: record.elapsedMs ?? now - startedAtMs,
                        })),
                    } } };
                };
                document.addEventListener('xrengine-canvas-failed', event => {
                    globalThis.advancedCanvasFailureEvidence = event.detail;
                });
                const label = descriptor => {
                    try { return String(descriptor?.label ?? '').slice(0, 128); }
                    catch { return ''; }
                };
                const createComputePipelineAsync = GPUDevice.prototype.createComputePipelineAsync;
                GPUDevice.prototype.createComputePipelineAsync = function (...args) {
                    const startedAtMs = performance.now();
                    const evidence = stats.creation.computePipelines;
                    evidence.calls++;
                    let promise;
                    try { promise = createComputePipelineAsync.apply(this, args); }
                    catch (error) {
                        if (evidence.records.length < maximumCreationRecords)
                            evidence.records.push({ label: label(args[0]), status: 'rejected',
                                elapsedMs: performance.now() - startedAtMs });
                        else evidence.omitted++;
                        throw error;
                    }
                    if (evidence.records.length < maximumCreationRecords) {
                        const record = { label: label(args[0]), status: 'pending', elapsedMs: null, startedAtMs };
                        evidence.records.push(record);
                        // Observe settlement without changing the promise returned to the renderer.
                        try { void promise.then(() => {
                            record.status = 'fulfilled'; record.elapsedMs = performance.now() - startedAtMs;
                        }, () => {
                            record.status = 'rejected'; record.elapsedMs = performance.now() - startedAtMs;
                        }); } catch { /* Instrumentation must not change the original result. */ }
                    } else evidence.omitted++;
                    return promise;
                };
                const createShaderModule = GPUDevice.prototype.createShaderModule;
                GPUDevice.prototype.createShaderModule = function (...args) {
                    const evidence = stats.creation.shaderModules;
                    evidence.calls++;
                    let module;
                    try { module = createShaderModule.apply(this, args); }
                    catch (error) {
                        if (evidence.records.length < maximumCreationRecords)
                            evidence.records.push({ label: label(args[0]), status: 'rejected', codeBytes: null,
                                sha256: null });
                        else evidence.omitted++;
                        throw error;
                    }
                    if (evidence.records.length < maximumCreationRecords) {
                        const record = { label: label(args[0]), status: 'pending', codeBytes: null, sha256: null };
                        evidence.records.push(record);
                        let code;
                        try { code = args[0]?.code; }
                        catch { record.status = 'unavailable'; return module; }
                        if (typeof code !== 'string') { record.status = 'unavailable'; return module; }
                        // Hash asynchronously; no shader source is stored in report evidence.
                        void Promise.resolve().then(async () => {
                            const bytes = new TextEncoder().encode(code);
                            record.codeBytes = bytes.byteLength;
                            const digest = await crypto.subtle.digest('SHA-256', bytes);
                            record.sha256 = [...new Uint8Array(digest)]
                                .map(byte => byte.toString(16).padStart(2, '0')).join('');
                            record.status = 'fulfilled';
                        }).catch(() => { record.status = 'unavailable'; });
                    } else evidence.omitted++;
                    return module;
                };
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
            const submissions = await page.evaluate(() => globalThis.advancedSubmissionSnapshot());
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
