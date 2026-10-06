import fs from 'node:fs/promises';
import { createReadStream } from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { pipeline } from 'node:stream/promises';
import { createRequire } from 'node:module';
import { chromium } from 'playwright';
import { readConfig, browserLaunchOptions, depthSamples, help } from './smoke.config.mjs';
import { captureGpuProcessState, initializeGpuCanary } from './gpu-diagnostics.mjs';
import { runOfflineAudioProbe } from './audio-diagnostics.mjs';
import { rollingBallGameCheck } from './rollingball-game.mjs';
import { renderingParityGameCheck } from './rendering-parity-game.mjs';
import { uiParityGameCheck } from './ui-parity-game.mjs';
import { claimUiFrameTrace, launchUiTraceBrowser, closeUiTraceBrowser } from './ui-frame-trace.mjs';
import { advancedRenderingGameCheck } from './advanced-rendering-game.mjs';
import { advancedShadowParityGameCheck } from './advanced-shadow-parity-game.mjs';
import { modularPipelineGameCheck } from './modular-pipeline-game.mjs';
import { staticMeshletParityGameCheck } from './static-meshlet-parity-game.mjs';
import { runNativeCompileIsolation } from './native-compile-isolation.mjs';
import { unlitMaterialsCheck } from './unlit-materials.mjs';
import { unlitMsaaCheck, unlitIndirectCheck } from './unlit-msaa.mjs';

const require = createRequire(import.meta.url);
const mime = {
    '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8',
    '.mjs': 'text/javascript; charset=utf-8', '.json': 'application/json',
    '.wasm': 'application/wasm', '.css': 'text/css', '.wgsl': 'text/plain; charset=utf-8',
    '.png': 'image/png', '.svg': 'image/svg+xml', '.ico': 'image/x-icon',
};

function assert(condition, message) { if (!condition) throw new Error(message); }
function inside(root, file) {
    const relative = path.relative(root, file);
    return relative === '' || (!relative.startsWith(`..${path.sep}`) && relative !== '..' && !path.isAbsolute(relative));
}
async function directory(value) {
    const root = await fs.realpath(value);
    assert((await fs.stat(root)).isDirectory(), 'BrowserSmoke.Config: each supplied root must be a directory.');
    return root;
}

async function startServer(config, requests) {
    const mounts = [
        ...(config.shaderArtifacts ? [{ prefix: '/__shaders/', root: await directory(config.shaderArtifacts) }] : []),
        ...(config.joltSpike ? [{ prefix: '/__jolt/', root: await directory(config.joltSpike) }] : []),
        ...(config.gamePublish ? [{ prefix: '/__game/', root: await directory(config.gamePublish) }] : []),
        ...(config.baselinePublish ? [{ prefix: '/__baseline/', root: await directory(config.baselinePublish) }] : []),
        { prefix: '/', root: await directory(config.browserPublish ?? config.gamePublish) },
    ];
    let authority;
    const server = http.createServer(async (request, response) => {
        let pathname = '<invalid>', status = 500;
        try {
            if (request.headers.host !== authority) { status = 403; throw new Error('Invalid host'); }
            if (!['GET', 'HEAD'].includes(request.method)) { status = 405; throw new Error('Read-only server'); }
            pathname = decodeURIComponent(new URL(request.url, `http://${authority}`).pathname);
            if (pathname === '/favicon.ico') { status = 204; response.writeHead(status); response.end(); return; }
            if (pathname === '/__audio-probe/') {
                status = 200;
                response.writeHead(status, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' });
                response.end('<!doctype html><title>Offline engine audio qualification</title>');
                return;
            }
            if (config.gpuDiagnostics && pathname === '/__gpu-canary/') {
                status = 200;
                response.writeHead(status, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' });
                response.end('<!doctype html><title>Independent WebGPU diagnostic</title><canvas width="128" height="128"></canvas>');
                return;
            }
            if (pathname.includes('\0') || pathname.includes('\\') || pathname.split('/').some(part => part.startsWith('.'))) {
                status = 403; throw new Error('Invalid path');
            }
            const mount = mounts.find(candidate => pathname.startsWith(candidate.prefix));
            if (!mount) { status = 404; throw new Error('Unknown mount'); }
            const relative = pathname.slice(mount.prefix.length) + (pathname.endsWith('/') ? 'index.html' : '');
            const candidate = path.resolve(mount.root, relative);
            if (!inside(mount.root, candidate)) { status = 403; throw new Error('Path escape'); }
            const file = await fs.realpath(candidate);
            if (!inside(mount.root, file)) { status = 403; throw new Error('Symlink escape'); }
            const stat = await fs.stat(file);
            if (!stat.isFile()) { status = 404; throw new Error('Not a file'); }
            status = 200;
            response.writeHead(status, {
                'content-type': mime[path.extname(file).toLowerCase()] ?? 'application/octet-stream',
                'content-length': stat.size, 'cache-control': 'no-store', 'x-content-type-options': 'nosniff',
            });
            if (request.method === 'HEAD') response.end();
            else await pipeline(createReadStream(file), response);
        } catch (error) {
            if (!response.headersSent) {
                if (status === 500 && ['ENOENT', 'ENOTDIR'].includes(error.code)) status = 404;
                response.writeHead(status, { 'content-type': 'text/plain', 'cache-control': 'no-store' });
                response.end(`Browser smoke server: ${status}`);
            } else response.destroy();
        } finally {
            if (requests.length < 10000) requests.push({ method: request.method, path: pathname, status });
        }
    });
    await new Promise((resolve, reject) => {
        server.once('error', reject);
        server.listen(0, '127.0.0.1', resolve);
    });
    authority = `127.0.0.1:${server.address().port}`;
    return { server, origin: `http://${authority}` };
}

async function instrumentedPage(browser, origin, report, name, config) {
    const context = await browser.newContext({ viewport: { width: 1024, height: 1100 }, deviceScaleFactor: 1 });
    await context.route('**/*', async route => {
        const url = new URL(route.request().url());
        if (url.origin === origin || ['data:', 'blob:'].includes(url.protocol)) await route.continue();
        else {
            report.externalRequests.push({ check: name, url: url.origin });
            await route.abort('blockedbyclient');
        }
    });
    const page = await context.newPage();
    page.setDefaultTimeout(config.timeout);
    page.setDefaultNavigationTimeout(config.timeout);
    const events = [];
    const append = entry => { if (events.length < 2000) events.push({ time: Date.now(), ...entry }); };
    page.on('console', event => append({ type: event.type(), text: event.text().slice(0, 8192) }));
    page.on('pageerror', error => append({ type: 'pageerror', text: String(error).slice(0, 8192) }));
    page.on('crash', () => append({ type: 'crash', text: 'Browser page process crashed.' }));
    page.on('requestfailed', request => append({ type: 'requestfailed', url: request.url(), error: request.failure()?.errorText }));
    report.browserLogs[name] = events;
    return { page, context, events };
}

function assertNoBrowserErrors(events) {
    const errors = events.filter(event => event.type === 'error' || event.type === 'pageerror');
    assert(errors.length === 0, `BrowserSmoke.BrowserErrors: ${errors.map(error => error.text).join('\n')}`);
}

async function capturePixels(page, png, samples) {
    return page.evaluate(async ({ encoded, samples }) => {
        const bytes = Uint8Array.from(atob(encoded), character => character.charCodeAt(0));
        const bitmap = await createImageBitmap(new Blob([bytes], { type: 'image/png' }));
        const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
        const context = canvas.getContext('2d', { willReadFrequently: true });
        context.drawImage(bitmap, 0, 0);
        bitmap.close();
        return { width: canvas.width, height: canvas.height, samples: samples.map(sample => {
            const values = context.getImageData(sample.x - 2, sample.y - 2, 5, 5).data;
            const min = [255, 255, 255, 255], max = [0, 0, 0, 0], sum = [0, 0, 0, 0];
            for (let i = 0; i < values.length; i++) {
                const channel = i % 4;
                min[channel] = Math.min(min[channel], values[i]);
                max[channel] = Math.max(max[channel], values[i]);
                sum[channel] += values[i];
            }
            return { ...sample, min, max, average: sum.map(value => value / 25) };
        }) };
    }, { encoded: png.toString('base64'), samples });
}

async function depthCheck(browser, origin, report, config) {
    const { page, context, events } = await instrumentedPage(browser, origin, report, 'engine-depth', config);
    try {
        const url = `${origin}/diagnostics/engine-mesh.html?manifest=${encodeURIComponent(`${origin}/__shaders/manifest.json`)}` +
            `&assets=${encodeURIComponent(`${origin}${config.engineManifest}`)}`;
        await page.goto(url, { waitUntil: 'domcontentloaded' });
        await page.waitForFunction(() => window.engineMeshDiagnostic !== undefined);
        const adapter = await page.evaluate(async () => {
            if (!navigator.gpu) return { supported: false, reason: 'navigator.gpu is unavailable' };
            const adapter = await navigator.gpu.requestAdapter();
            if (!adapter) return { supported: false, reason: 'requestAdapter returned null' };
            const info = adapter.info ?? await adapter.requestAdapterInfo?.() ?? {};
            return { supported: true, vendor: info.vendor ?? '', architecture: info.architecture ?? '',
                device: info.device ?? '', description: info.description ?? '',
                fallback: adapter.isFallbackAdapter ?? info.isFallbackAdapter ?? null,
                features: [...adapter.features].sort(), maxTextureDimension2D: adapter.limits.maxTextureDimension2D };
        });
        report.adapter = adapter;
        assert(adapter.supported, `BrowserSmoke.WebGPUUnsupported: ${adapter.reason}`);
        const software = adapter.fallback === true || /swiftshader|llvmpipe|software/i.test(`${adapter.description} ${adapter.device} ${adapter.architecture}`);
        if (config.gpuMode === 'native')
            assert(!software, 'BrowserSmoke.SoftwareAdapterNotAllowed: select --gpu-mode software explicitly for software qualification.');
        report.qualification = config.gpuMode === 'software' || software ? 'software-api-and-shader-correctness-only' : 'reported-native-adapter-correctness-only';

        await page.locator('#start').click();
        await page.waitForFunction(() => {
            const text = document.querySelector('#status')?.textContent ?? '';
            return text.startsWith('Engine mesh depth diagnostic rendered') || text.startsWith('Failed:') || text.startsWith('Error:');
        });
        const status = await page.locator('#status').textContent();
        assert(status.startsWith('Engine mesh depth diagnostic rendered'), `BrowserSmoke.EngineFrameFailed: ${status}`);
        // Screenshot the actual composited WebGPU canvas; decode those captured pixels,
        // rather than drawing a mock shader or trusting the page's completion label.
        const png = await page.locator('canvas').screenshot({ path: path.join(config.output, 'engine-depth-canvas.png') });
        const pixels = await capturePixels(page, png, depthSamples);
        report.depthPixels = pixels;
        assert(pixels.width === 512 && pixels.height === 512, 'BrowserSmoke.UnexpectedCanvasExtent: expected a 512x512 capture.');
        for (const sample of pixels.samples)
            for (let channel = 0; channel < 4; channel++)
                assert(sample.min[channel] >= sample.expected[channel] - sample.tolerance &&
                    sample.max[channel] <= sample.expected[channel] + sample.tolerance,
                `BrowserSmoke.DepthPixelMismatch: ${sample.name} channel ${channel} expected ${sample.expected[channel]}±${sample.tolerance}, got ${sample.min[channel]}..${sample.max[channel]}.`);
        report.rendererStatistics = await page.evaluate(() => window.engineMeshDiagnostic.statistics());
        assert(report.rendererStatistics?.draws >= 3 && report.rendererStatistics.frameSubmitCalls > 0,
            'BrowserSmoke.EngineSubmissionMissing: no real engine mesh submissions were recorded.');
        assert(report.rendererStatistics.packets === 0 && report.rendererStatistics.focusedPipeline === null,
            'BrowserSmoke.ReferencePipelineUsed: the diagnostic submitted reference-scene packets instead of engine commands.');
        await page.screenshot({ path: path.join(config.output, 'engine-depth-page.png'), fullPage: true });
        await page.locator('#stop').click();
        assert(await page.evaluate(() => window.engineMeshDiagnostic.session === 0 && window.engineMeshDiagnostic.statistics() === null),
            'BrowserSmoke.EngineTeardownFailed: the diagnostic renderer session survived stop.');
        assertNoBrowserErrors(events);
    } catch (error) {
        if (config.gpuDiagnostics)
            report.engineFailure = await page.evaluate(() => window.engineMeshDiagnostic?.failure ?? null).catch(() => null);
        await page.screenshot({ path: path.join(config.output, 'engine-depth-failure.png'), fullPage: true }).catch(() => {});
        throw error;
    } finally { await context.close(); }
}

async function textureCheck(browser, origin, report, config) {
    const { page, context, events } = await instrumentedPage(browser, origin, report, 'engine-texture', config);
    try {
        await page.goto(`${origin}/diagnostics/engine-mesh.html?probe=texture&manifest=${encodeURIComponent(`${origin}/__shaders/manifest.json`)}` +
            `&assets=${encodeURIComponent(`${origin}${config.engineManifest}`)}`, { waitUntil: 'domcontentloaded' });
        await page.waitForFunction(() => window.engineMeshDiagnostic !== undefined);
        await page.locator('#start').click();
        report.textureCases = [];
        let initialLive;
        for (let iteration = 0; iteration < 5; iteration++) {
            const sampleCase = iteration % 2;
            if (iteration) await page.evaluate(value => window.engineMeshDiagnostic.setTextureCase(value), sampleCase);
            await page.waitForFunction(() => {
                const status = document.querySelector('#status')?.textContent ?? '';
                return status.startsWith('Engine mesh texture diagnostic rendered') || status.startsWith('Failed:') || status.startsWith('Error:');
            });
            const status = await page.locator('#status').textContent();
            assert(status.startsWith('Engine mesh texture diagnostic rendered'), `BrowserSmoke.EngineTextureFrameFailed: ${status}`);
            const colors = sampleCase ? [[55, 55, 55, 255], [55, 55, 55, 255], [55, 55, 55, 255], [55, 55, 55, 255]]
                : [[255, 0, 0, 255], [0, 255, 0, 255], [0, 0, 255, 255], [255, 255, 255, 255]];
            const samples = [[100, 210], [190, 210], [100, 300], [190, 300]].map(([x, y], index) =>
                ({ name: `corner-${index}`, x, y, expected: colors[index], tolerance: 3 }));
            const png = await page.locator('canvas').screenshot({ path: path.join(config.output, `engine-texture-${iteration}.png`) });
            const pixels = await capturePixels(page, png, samples);
            for (const sample of pixels.samples)
                for (let channel = 0; channel < 4; channel++)
                    assert(sample.min[channel] >= sample.expected[channel] - sample.tolerance && sample.max[channel] <= sample.expected[channel] + sample.tolerance,
                        `BrowserSmoke.TexturePixelMismatch: case ${sampleCase} ${sample.name} channel ${channel} expected ${sample.expected[channel]}, got ${sample.min[channel]}..${sample.max[channel]}.`);
            await page.waitForFunction(() => window.engineMeshDiagnostic.statistics()?.resources?.retiring === 0, null,
                { timeout: Math.min(config.timeout, 15000) });
            const statistics = await page.evaluate(() => window.engineMeshDiagnostic.statistics());
            assert(statistics.draws >= 3 && statistics.frameSubmitCalls > 0 && statistics.packets === 0 && statistics.focusedPipeline === null,
                'BrowserSmoke.EngineTextureSubmission: sampling must use real engine mesh commands.');
            initialLive ??= statistics.resources.live;
            assert(statistics.resources.live <= initialLive,
                `BrowserSmoke.TextureRetention: repeated replacement grew live GPU resources from ${initialLive} to ${statistics.resources.live}.`);
            report.textureCases.push({ sampleCase, pixels, statistics });
        }
        await page.locator('#stop').click();
        assert(await page.evaluate(() => window.engineMeshDiagnostic.session === 0 && window.engineMeshDiagnostic.statistics() === null),
            'BrowserSmoke.EngineTextureTeardown: texture diagnostic survived stop.');
        assertNoBrowserErrors(events);
    } catch (error) {
        report.textureFailure = await page.evaluate(() => window.engineMeshDiagnostic?.failure ?? null).catch(() => null);
        await page.screenshot({ path: path.join(config.output, 'engine-texture-failure.png'), fullPage: true }).catch(() => {});
        throw error;
    } finally { await context.close(); }
}

// Independent analytic reference at the center of the normal-bearing engine
// quad. It never supplies pixels or shader source to the renderer.
function litReference(sampleCase) {
    const baseColor = sampleCase === 5 ? [0.1, 0.45, 0.25] : [0.4, 0.2, 0.1];
    const roughness = sampleCase === 6 ? 0.9 : 0.6, metallic = sampleCase === 7 ? 1 : 0.3;
    const specular = sampleCase === 8 ? 0 : 0.5, opacity = sampleCase === 9 ? 0.35 : 0.7;
    const emission = sampleCase === 10 ? 8 : 0.25, exposure = sampleCase === 11 ? 0.25 : 1;
    const directional = [0, 2, 3].includes(sampleCase) ? 0 : sampleCase === 12 ? 0.5 : 2;
    const point = [2, 4].includes(sampleCase) ? 2 : 0, spot = [3, 4].includes(sampleCase) ? 2 : 0;
    const attenuation = (1 - (2 / 10) ** 4) ** 2 / (2 ** 2 + 1) * 3;
    const light = [0.7, 0.5, 0.3], distribution = 1 / (Math.PI * roughness ** 4);
    const hdr = baseColor.map((albedo, channel) => {
        const f0 = 0.04 * (1 - metallic) + albedo * metallic;
        const fresnel = f0 + (1 - f0) * 2 ** (-5.55473 - 6.98316);
        const direct = (1 - fresnel) * (1 - metallic) * albedo / Math.PI + specular * distribution * fresnel / 4.0001;
        return albedo * (0.08 + emission) + direct * light[channel] * (directional + (point + spot) * attenuation);
    });
    const display = hdr.map(value => {
        const exposed = value * exposure;
        return Math.round(255 * Math.min(1, exposed * 1.6 / (exposed + 0.6)) ** (1 / 2.2));
    });
    return { baseColor, roughness, metallic, specular, opacity, emission, exposure,
        lightIntensities: { directional, point, spot }, hdr: [...hdr, opacity], display: [...display, 255] };
}

async function litCheck(browser, origin, report, config) {
    const { page, context, events } = await instrumentedPage(browser, origin, report, 'engine-lit-hdr-tonemap', config);
    try {
        await page.goto(`${origin}/diagnostics/engine-mesh.html?probe=lit&manifest=${encodeURIComponent(`${origin}/__shaders/manifest.json`)}` +
            `&assets=${encodeURIComponent(`${origin}${config.engineManifest}`)}`, { waitUntil: 'domcontentloaded' });
        await page.waitForFunction(() => window.engineMeshDiagnostic !== undefined);
        await page.locator('#start').click();
        const names = ['ambient', 'directional', 'point', 'spot', 'combined', 'base-color', 'roughness',
            'metallic', 'specular', 'opacity', 'hdr-emission', 'exposure', 'light-intensity', 'restored'];
        report.litCases = [];
        let identity, initialLive;
        for (let sampleCase = 0; sampleCase < names.length; sampleCase++) {
            if (sampleCase) await page.evaluate(value => window.engineMeshDiagnostic.setLitCase(value), sampleCase);
            await page.waitForFunction(() => {
                const status = document.querySelector('#status')?.textContent ?? '';
                return status.startsWith('Engine mesh lit diagnostic rendered') || status.startsWith('Failed:') || status.startsWith('Error:');
            });
            const status = await page.locator('#status').textContent();
            assert(status.startsWith('Engine mesh lit diagnostic rendered'), `BrowserSmoke.EngineLitFrameFailed: ${status}`);
            const expected = litReference(sampleCase);
            const png = await page.locator('canvas').screenshot({ path: path.join(config.output, `engine-lit-${sampleCase}-${names[sampleCase]}.png`) });
            const pixels = await capturePixels(page, png,
                [{ name: names[sampleCase], x: 256, y: 256, expected: expected.display, tolerance: 3 }]);
            const hdr = await page.evaluate(() => window.engineMeshDiagnostic.readHdrCenter());
            const state = await page.evaluate(() => window.engineMeshDiagnostic.litState());
            const statistics = await page.evaluate(() => window.engineMeshDiagnostic.statistics());
            report.litCases.push({ sampleCase, name: names[sampleCase], expected, hdr, pixels, state, statistics });
            assert(hdr.format === 'rgba16float' && hdr.width === 512 && hdr.height === 512,
                'BrowserSmoke.EngineHdrTargetMissing: the scene must render into the real 512x512 RGBA16F engine target.');
            for (let channel = 0; channel < 4; channel++) {
                const tolerance = Math.max(0.004, expected.hdr[channel] * 0.015);
                assert(hdr.min[channel] >= expected.hdr[channel] - tolerance && hdr.max[channel] <= expected.hdr[channel] + tolerance,
                    `BrowserSmoke.LitHdrMismatch: ${names[sampleCase]} channel ${channel} expected ${expected.hdr[channel]}±${tolerance}, got ${hdr.min[channel]}..${hdr.max[channel]}.`);
                const sample = pixels.samples[0];
                assert(sample.min[channel] >= expected.display[channel] - 3 && sample.max[channel] <= expected.display[channel] + 3,
                    `BrowserSmoke.LitTonemapMismatch: ${names[sampleCase]} channel ${channel} expected ${expected.display[channel]}±3, got ${sample.min[channel]}..${sample.max[channel]}.`);
            }
            assert(state.sampleCase === sampleCase && state.semantic === 'StandardLitColorV1' && state.pipeline === 'DefaultRenderPipeline' &&
                state.shaderRevision === state.initialShaderRevision && state.authoredShaderCount === 0 &&
                state.directionalLights === 1 && state.pointLights === 1 && state.spotLights === 1 && state.castsShadows === false,
                'BrowserSmoke.EngineLitIdentity: the real engine material, pipeline, and unshadowed light contract changed.');
            assert(state.shaders.length >= 2 && state.pipelines.length >= 2,
                'BrowserSmoke.EngineLitProgramsMissing: lit HDR and presentation require separate cooked GPU programs.');
            const currentIdentity = JSON.stringify({ shaders: state.shaders, pipelines: state.pipelines });
            identity ??= currentIdentity;
            assert(currentIdentity === identity, 'BrowserSmoke.EngineLitShaderReplacement: numeric updates replaced GPU shader/pipeline identity.');
            assert(statistics.draws >= 2 && statistics.frameSubmitCalls > 0 && statistics.packets === 0 && statistics.focusedPipeline === null,
                'BrowserSmoke.EngineLitSubmission: lighting must use real engine mesh and tonemap commands.');
            initialLive ??= statistics.resources.live;
            assert(statistics.resources.live <= initialLive,
                'BrowserSmoke.EngineLitRetention: numeric changes grew live GPU resources.');
        }
        assert(report.litCases[10].hdr.min[0] > 1,
            'BrowserSmoke.EngineHdrClamped: emission above one did not survive the linear HDR target.');
        report.litResizes = [];
        for (const extent of [384, 256, 640, 320, 512]) {
            await page.evaluate(size => window.engineMeshDiagnostic.resize(size, size), extent);
            await page.waitForFunction(() => {
                const status = document.querySelector('#status')?.textContent ?? '';
                return status.startsWith('Engine mesh lit diagnostic rendered') || status.startsWith('Failed:') || status.startsWith('Error:');
            });
            const status = await page.locator('#status').textContent();
            assert(status.startsWith('Engine mesh lit diagnostic rendered'), `BrowserSmoke.EngineLitResizeFailed: ${status}`);
            await page.waitForFunction(size => {
                const host = window.engineMeshDiagnostic;
                if (!host.session || host.statistics()?.resources?.retiring !== 0) return false;
                const targets = host.litState().hdrTargets;
                return targets.length === 1 && targets[0].width === size && targets[0].height === size;
            }, extent,
                { timeout: Math.min(config.timeout, 15000) });
            const hdr = await page.evaluate(() => window.engineMeshDiagnostic.readHdrCenter());
            assert(hdr.width === extent && hdr.height === extent,
                `BrowserSmoke.EngineLitStaleExtent: expected ${extent}, got ${hdr.width}x${hdr.height}.`);
            const expected = litReference(13);
            const png = await page.locator('canvas').screenshot({ path: path.join(config.output, `engine-lit-resize-${extent}.png`) });
            const pixels = await capturePixels(page, png,
                [{ name: `resize-${extent}`, x: extent / 2, y: extent / 2, expected: expected.display, tolerance: 3 }]);
            assert(pixels.width === extent && pixels.height === extent, 'BrowserSmoke.EngineLitResizeScreenshotExtent.');
            for (let channel = 0; channel < 4; channel++) {
                assert(Math.abs(hdr.average[channel] - expected.hdr[channel]) <= Math.max(0.004, expected.hdr[channel] * 0.015),
                    `BrowserSmoke.EngineLitResizeHdrMismatch: extent ${extent}, channel ${channel}.`);
                assert(Math.abs(pixels.samples[0].average[channel] - expected.display[channel]) <= 3,
                    `BrowserSmoke.EngineLitResizeTonemapMismatch: extent ${extent}, channel ${channel}.`);
            }
            const statistics = await page.evaluate(() => window.engineMeshDiagnostic.statistics());
            report.litResizes.push({ extent, hdr, pixels, statistics });
            assert(statistics.resources.live <= initialLive,
                `BrowserSmoke.EngineLitResizeRetention: extent ${extent} retained ${statistics.resources.live}, initially ${initialLive}.`);
        }
        await page.screenshot({ path: path.join(config.output, 'engine-lit-page.png'), fullPage: true });
        await page.locator('#stop').click();
        assert(await page.evaluate(() => window.engineMeshDiagnostic.session === 0 && window.engineMeshDiagnostic.statistics() === null),
            'BrowserSmoke.EngineLitTeardown: the lit diagnostic renderer survived stop.');
        assertNoBrowserErrors(events);
    } catch (error) {
        report.litFailure = await page.evaluate(() => window.engineMeshDiagnostic?.failure ?? null).catch(() => null);
        await page.screenshot({ path: path.join(config.output, 'engine-lit-failure.png'), fullPage: true }).catch(() => {});
        throw error;
    } finally { await context.close(); }
}

async function effectsCheck(browser, origin, report, config) {
    const { page, context, events } = await instrumentedPage(browser, origin, report, 'engine-shared-gtao-bloom', config);
    try {
        await page.goto(`${origin}/diagnostics/engine-mesh.html?probe=effects&manifest=${encodeURIComponent(`${origin}/__shaders/manifest.json`)}` +
            `&assets=${encodeURIComponent(`${origin}${config.engineManifest}`)}`, { waitUntil: 'domcontentloaded' });
        await page.waitForFunction(() => window.engineMeshDiagnostic !== undefined);
        await page.locator('#start').click();
        const waitReady = async previous => {
            await page.waitForFunction(before => {
                const host = window.engineMeshDiagnostic;
                // Scene/light and postprocess values publish across the engine's
                // collect/swap boundary. Read after the next completed render,
                // not the frame that first observes the mutation.
                return !!host.failure || (host.session > 0 && host.readyFrames >= before + 2);
            }, previous, { timeout: Math.min(config.timeout, 120000) });
            const failure = await page.evaluate(() => window.engineMeshDiagnostic.failure);
            assert(!failure, `BrowserSmoke.EffectsFrameFailed: ${JSON.stringify(failure)}`);
            await page.waitForFunction(() => window.engineMeshDiagnostic.statistics()?.resources?.retiring === 0,
                null, { timeout: Math.min(config.timeout, 30000) });
        };
        const read = (name, u, v) => page.evaluate(({ name, u, v }) =>
            window.engineMeshDiagnostic.readEffectTarget(name, u, v), { name, u, v });
        const depth = (u, v) => page.evaluate(({ u, v }) =>
            window.engineMeshDiagnostic.readEffectDepthAt(u, v), { u, v });
        const sample = async (sampleCase, full) => {
            const startedAt = performance.now();
            // waitReady has already observed two accepted ready frames and drained retirements.
            const pause = await page.evaluate(() => {
                const host = window.engineMeshDiagnostic;
                const id = host.pauseEffectsFrames();
                return { id, readyFrames: host.readyFrames,
                    frameSubmitCalls: host.statistics().frameSubmitCalls, startedAt: performance.now() };
            });
            let result;
            let sampleFailed = false;
            try {
                const state = await page.evaluate(() => window.engineMeshDiagnostic.effectsState());
                const statistics = await page.evaluate(() => window.engineMeshDiagnostic.statistics());
                result = { sampleCase, state, statistics, targets: {} };
                const names = full
                    ? ['HDRSceneTex', 'WebNormalTexture', 'WebGtaoRawTexture', 'WebGtaoHorizontalTexture',
                        'WebGtaoFinalTexture', 'WebBloomMip0', 'WebBloomMip1', 'WebBloomMip2',
                        'WebBloomMip3', 'WebBloomMip4', 'WebBloomCombinedTexture']
                    : ['HDRSceneTex', 'WebGtaoFinalTexture', 'WebBloomMip0', 'WebBloomMip1', 'WebBloomCombinedTexture'];
                for (const name of names) {
                    if (!state.targets.some(target => target.label === name)) continue;
                    result.targets[name] = {};
                    for (const [site, u, v] of [['contact', .36, .60], ['flat', .75, .75],
                        ['emitter', .71, .44], ['halo', .78, .44]])
                        result.targets[name][site] = await read(name, u, v);
                }
                if (full) {
                    result.depth = { occluder: await depth(.36, .5), flat: await depth(.75, .75) };
                    const png = await page.locator('canvas').screenshot({ path: path.join(config.output, `effects-${sampleCase}.png`) });
                    result.display = await capturePixels(page, png, [
                        { name: 'occluder', x: 184, y: 256 },
                        { name: 'contact', x: 184, y: 307 }, { name: 'emitter', x: 364, y: 225 },
                        { name: 'halo', x: 399, y: 225 }]);
                }
            } catch (error) {
                sampleFailed = true;
                throw error;
            } finally {
                try {
                    const sampling = await page.evaluate(({ id, readyFrames, frameSubmitCalls, startedAt }) => {
                        const host = window.engineMeshDiagnostic;
                        let timing, resumed;
                        try {
                            const statistics = host.statistics();
                            timing = { pausedMs: performance.now() - startedAt,
                                readyFramesDelta: host.readyFrames - readyFrames,
                                frameSubmitCallsDelta: (statistics?.frameSubmitCalls ?? 0) - frameSubmitCalls };
                        } finally { resumed = host.resumeEffectsFrames(id); }
                        return { ...timing, resumed };
                    }, pause);
                    if (result) result.sampling = { ...sampling, elapsedMs: performance.now() - startedAt };
                    assert(sampling.resumed, 'BrowserSmoke.EffectsResume: settled sampling did not resume its owning frame pump.');
                } catch (resumeError) {
                    if (!sampleFailed) throw resumeError;
                    console.error('Engine effects diagnostic frame resume also failed:', resumeError);
                }
            }
            return result;
        };
        await waitReady(0);
        report.effectsCases = [await sample(0, true)];
        const first = report.effectsCases[0];
        assert(first.state.normal && first.state.gtaoRaw && first.state.gtaoHorizontal &&
            first.state.gtaoFinal && first.state.bloomCombined && first.state.authoredShaderCount === 0 &&
            first.state.shaderRevision === first.state.initialShaderRevision,
            'BrowserSmoke.EffectsSceneMissing: expected generation-owned prepass, GTAO and bloom outputs from real models.');
        assert(first.depth.occluder.average < first.depth.flat.average - .02,
            'BrowserSmoke.EffectsDepthMissing: the foreground occluder did not write nearer depth.');
        for (const name of ['WebGtaoRawTexture', 'WebGtaoHorizontalTexture', 'WebGtaoFinalTexture'])
            for (const reading of Object.values(first.targets[name]))
                assert(reading.min[0] >= -.005 && reading.max[0] <= 1.005,
                    `BrowserSmoke.EffectsAoBounds: ${name} left normalized visibility.`);
        assert(first.targets.HDRSceneTex.emitter.min[0] > 1 &&
            Math.abs(first.targets.WebBloomMip0.emitter.average[0] - first.targets.HDRSceneTex.emitter.average[0]) < .03,
            'BrowserSmoke.EffectsBloomMip0: raw HDR emission was clamped or thresholded before downsampling.');
        for (const sampleCase of [1,2,3,0,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,0]) {
            const before = await page.evaluate(() => window.engineMeshDiagnostic.readyFrames);
            await page.evaluate(value => window.engineMeshDiagnostic.setEffectsCase(value), sampleCase);
            await waitReady(before);
            report.effectsCases.push(await sample(sampleCase, sampleCase === 3 || sampleCase === 9 || sampleCase >= 12));
        }
        const cases = report.effectsCases;
        const disabled = cases.find(value => value.sampleCase === 3);
        assert(!disabled.state.gtaoFinal && !disabled.state.bloomCombined &&
            cases.find(value => value.sampleCase === 1).state.bloomCombined &&
            cases.find(value => value.sampleCase === 2).state.gtaoFinal,
            'BrowserSmoke.EffectsToggle: camera feature changes did not replace declared resources.');
        const restored = cases.find((value, index) => index > 3 && value.sampleCase === 0);
        const numeric = cases.filter((value, index) => index > 3 && value.sampleCase >= 4 && value.sampleCase <= 11);
        assert(numeric.every(value => value.state.resourceGeneration === restored.state.resourceGeneration &&
            value.state.shaderRevision === restored.state.shaderRevision),
            'BrowserSmoke.EffectsNumericIdentity: numeric camera updates replaced the resource or authored shader generation.');
        const stable = numeric.filter(value => value.sampleCase <= 8);
        const gpuIdentity = value => JSON.stringify({ shaders:value.state.shaders,
            pipelines:value.state.pipelines, targets:value.state.targets });
        assert(stable.every(value => gpuIdentity(value) === gpuIdentity(stable[0])),
            'BrowserSmoke.EffectsGpuIdentity: numeric AO and bloom updates replaced GPU resource handles.');
        const both = cases.find(value => value.sampleCase === 14);
        assert(both.state.occluderCull === 'Both' && both.state.renderDraws === first.state.renderDraws - 2,
            'BrowserSmoke.EffectsFaceCoverage: Cull Both did not omit the same prepass and lit draws.');
        const foregroundDepth = first.depth.occluder.average;
        const receiverDepth = first.depth.flat.average;
        assert(Math.abs(cases.find(value => value.sampleCase === 12).depth.occluder.average - foregroundDepth) < .01,
            'BrowserSmoke.EffectsFaceNone: clockwise winding with Cull None lost the foreground occluder.');
        for (const sampleCase of [13,14,15]) {
            const exposed = cases.find(value => value.sampleCase === sampleCase).depth.occluder.average;
            assert(exposed > foregroundDepth + .02 && Math.abs(exposed - receiverDepth) < .01,
                `BrowserSmoke.EffectsFaceCoverage: case ${sampleCase} did not expose the receiver in prepass depth.`);
        }
        const byCase = number => cases.find(value => value.sampleCase === number);
        const hdr = (number, site) => byCase(number).targets.HDRSceneTex[site].average;
        const aoContact = first.targets.WebGtaoFinalTexture.contact.average[0];
        const aoFlat = first.targets.WebGtaoFinalTexture.flat.average[0];
        assert(aoContact < aoFlat - .005 && hdr(0,'contact')[0] < hdr(1,'contact')[0] - .0005,
            'BrowserSmoke.EffectsAmbientOcclusion: the foreground contact did not darken ambient lighting.');
        assert(Math.abs(byCase(5).targets.WebGtaoFinalTexture.contact.average[0] - aoContact) < .003 &&
            hdr(5,'contact')[0] < hdr(0,'contact')[0] - .0005,
            'BrowserSmoke.EffectsPower: AO Power changed the generator instead of the ambient consumer.');
        assert(byCase(6).state.multiBounce === false && hdr(6,'contact')[0] < hdr(0,'contact')[0] - .0005,
            'BrowserSmoke.EffectsMultiBounce: the ambient-only multibounce control had no visible effect.');
        for (const site of ['contact','flat']) for (let channel = 0; channel < 3; channel++) {
            const directWithAo = hdr(11,site)[channel] - hdr(0,site)[channel];
            const directWithoutAo = hdr(17,site)[channel] - hdr(1,site)[channel];
            assert(directWithAo > .001 && Math.abs(directWithAo - directWithoutAo) < Math.max(.015, directWithAo * .03),
                `BrowserSmoke.EffectsDirectOcclusion: AO altered direct channel ${channel} at ${site}.`);
        }
        for (let channel = 0; channel < 3; channel++) {
            const emissionWithAo = hdr(0,'emitter')[channel] - hdr(16,'emitter')[channel];
            const emissionWithoutAo = hdr(1,'emitter')[channel] - hdr(18,'emitter')[channel];
            assert(emissionWithAo > 1 && Math.abs(emissionWithAo - emissionWithoutAo) < Math.max(.04, emissionWithAo * .01),
                `BrowserSmoke.EffectsEmissionOcclusion: AO altered emission channel ${channel}.`);
        }
        const rawBloom = byCase(0).targets.WebBloomMip1.emitter.average[0];
        const thresholdBloom = byCase(7).targets.WebBloomMip1.emitter.average[0];
        assert(thresholdBloom < rawBloom * .1,
            'BrowserSmoke.EffectsBrightPass: high threshold did not suppress the first downsample.');
        const strengthOff = byCase(8).targets;
        assert(Math.abs(strengthOff.WebBloomCombinedTexture.halo.average[0] -
            strengthOff.HDRSceneTex.halo.average[0]) < .015,
            'BrowserSmoke.EffectsStrengthZero: zero bloom strength still added halo energy.');
        assert(byCase(0).targets.WebBloomCombinedTexture.halo.average[0] >
            byCase(0).targets.HDRSceneTex.halo.average[0] + .001,
            'BrowserSmoke.EffectsHalo: bright emission produced no off-geometry bloom.');
        assert(byCase(9).state.bloomDebugOnly &&
            byCase(9).display.samples[1].average[0] < first.display.samples[1].average[0] - 10,
            'BrowserSmoke.EffectsDebugOnly: raw bloom did not bypass the scene tonemap output.');
        report.effectsResizes = [];
        const baselineLive = cases.at(-1).statistics.resources.live;
        for (const [width, height] of [[1,1],[3,5],[17,13],[65,67],[129,93],[384,256],[640,320],[512,512]]) {
            const before = await page.evaluate(() => window.engineMeshDiagnostic.readyFrames);
            await page.evaluate(([w,h]) => window.engineMeshDiagnostic.resize(w,h), [width,height]);
            await waitReady(before);
            const state = await page.evaluate(() => window.engineMeshDiagnostic.effectsState());
            assert(state.targets.some(target => target.label === 'HDRSceneTex' &&
                target.width === width && target.height === height),
                `BrowserSmoke.EffectsResize: HDR extent did not commit at ${width}×${height}.`);
            const mipCount = state.targets.filter(target => /^WebBloomMip[0-4]$/.test(target.label)).length;
            assert(mipCount >= 1 && mipCount <= 5 && (width > 3 || mipCount < 5),
                `BrowserSmoke.EffectsMipCap: invalid bloom allocation at ${width}×${height}.`);
            const statistics = await page.evaluate(() => window.engineMeshDiagnostic.statistics());
            assert(statistics.resources.live <= baselineLive,
                `BrowserSmoke.EffectsRetirement: resize ${width}×${height} retained more live resources than the initial frame.`);
            report.effectsResizes.push({ width, height, mipCount, state, statistics });
        }
        report.effectsOccludedEmission = {};
        for (const [label, effectCase] of [['withAo',2],['withoutAo',3]]) {
            let before = await page.evaluate(() => window.engineMeshDiagnostic.readyFrames);
            await page.evaluate(value => window.engineMeshDiagnostic.setEffectsCase(value), effectCase);
            await waitReady(before);
            if (effectCase === 2) {
                const contactVisibility = await read('WebGtaoFinalTexture',.36,.60);
                report.effectsOccludedEmission.contactVisibility = contactVisibility;
                assert(contactVisibility.average[0] < .98,
                    'BrowserSmoke.EffectsEmissionContact: the receiver sample is not meaningfully occluded.');
            }
            const readings = {};
            for (const litCase of [13,10]) {
                before = await page.evaluate(() => window.engineMeshDiagnostic.readyFrames);
                await page.evaluate(value => {
                    const host = window.engineMeshDiagnostic;
                    host.exports.SetLitCase(host.session,value);
                }, litCase);
                await waitReady(before);
                readings[litCase] = await read('HDRSceneTex',.36,.60);
            }
            report.effectsOccludedEmission[label] = readings;
        }
        for (let channel = 0; channel < 3; channel++) {
            const expected = [0.4,0.2,0.1][channel] * 7.75;
            const withAo = report.effectsOccludedEmission.withAo[10].average[channel] -
                report.effectsOccludedEmission.withAo[13].average[channel];
            const withoutAo = report.effectsOccludedEmission.withoutAo[10].average[channel] -
                report.effectsOccludedEmission.withoutAo[13].average[channel];
            assert(Math.abs(withAo - expected) < Math.max(.025,expected*.025) &&
                Math.abs(withoutAo - expected) < Math.max(.025,expected*.025) &&
                Math.abs(withAo - withoutAo) < Math.max(.02,expected*.015),
                `BrowserSmoke.EffectsOccludedEmission: GTAO altered receiver emission channel ${channel}.`);
        }
        const oldSession = await page.evaluate(() => window.engineMeshDiagnostic.session);
        await page.locator('#stop').click();
        assert(await page.evaluate(() => window.engineMeshDiagnostic.session === 0 &&
            window.engineMeshDiagnostic.statistics() === null), 'BrowserSmoke.EffectsStop: resources survived stop.');
        await page.locator('#start').click();
        await waitReady(0);
        report.effectsRestart = { state: await page.evaluate(() => window.engineMeshDiagnostic.effectsState()),
            statistics: await page.evaluate(() => window.engineMeshDiagnostic.statistics()) };
        assert(await page.evaluate(previous => window.engineMeshDiagnostic.session !== previous, oldSession),
            'BrowserSmoke.EffectsRestart: prior session identity was reused.');
        await page.locator('#stop').click();
        assertNoBrowserErrors(events);
    } catch (error) {
        report.effectsFailure = await page.evaluate(() => window.engineMeshDiagnostic?.failure ?? null).catch(() => null);
        await page.screenshot({ path: path.join(config.output, 'effects-failure.png'), fullPage: true }).catch(() => {});
        throw error;
    } finally { await context.close(); }
}

// World x in the centered, two-unit orthographic camera maps to canvas x by
// (x + 1) / 2. The light points 0.35 radians across the one-unit caster to
// receiver gap, placing the baseline shadow near x=-tan(0.35), clear of the
// caster's [-0.12,0.12] footprint.
function shadowSample(state, extent = 512) {
    // The near-contact map overlaps the camera-visible caster. Sample its
    // exposed left half, still >10 screen pixels inside the projected shadow.
    const visibleX = state.sampleCase === 3 ? state.projectedShadowX - 0.07 : state.projectedShadowX;
    const x = Math.round((visibleX + 1) * extent / 2);
    return { x, y: extent / 2, u: x / extent, v: 0.5 };
}

async function captureShadowEdge(page, png, state, litRed, shadowRed) {
    return page.evaluate(async ({ encoded, projectedShadowX, litRed, shadowRed }) => {
        const bytes = Uint8Array.from(atob(encoded), character => character.charCodeAt(0));
        const bitmap = await createImageBitmap(new Blob([bytes], { type: 'image/png' }));
        const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
        const context = canvas.getContext('2d', { willReadFrequently: true });
        context.drawImage(bitmap, 0, 0);
        bitmap.close();
        const edge = Math.round((projectedShadowX - 0.12 + 1) * canvas.width / 2);
        const start = Math.max(3, edge - 28), end = Math.min(canvas.width - 4, edge + 28);
        const values = [];
        for (let x = start; x <= end; x++) {
            let sum = 0;
            for (let y = canvas.height / 2 - 2; y <= canvas.height / 2 + 2; y++)
                for (let dx = -2; dx <= 2; dx++)
                    sum += context.getImageData(x + dx, y, 1, 1).data[0];
            values.push((litRed - sum / 25) / Math.max(1, litRed - shadowRed));
        }
        const crossing = threshold => values.findIndex((value, index) => index + 2 < values.length &&
            value >= threshold && values[index + 1] >= threshold && values[index + 2] >= threshold);
        const first = crossing(0.1), last = crossing(0.9);
        return { edge, start, first, last, transitionPixels: first >= 0 && last >= first ? last - first : null,
            normalizedDarkness: values };
    }, { encoded: png.toString('base64'), projectedShadowX: state.projectedShadowX, litRed, shadowRed });
}

async function shadowCheck(browser, origin, report, config) {
    const { page, context, events } = await instrumentedPage(browser, origin, report, 'engine-directional-shadow', config);
    const url = `${origin}/diagnostics/engine-mesh.html?probe=shadow&manifest=${encodeURIComponent(`${origin}/__shaders/manifest.json`)}` +
        `&assets=${encodeURIComponent(`${origin}${config.engineManifest}`)}`;
    const ready = async (previousPasses, previousReadyFrames = 0, expectedEpoch = null) => {
        await page.waitForFunction(({ previousPasses, previousReadyFrames, expectedEpoch }) => {
            const host = window.engineMeshDiagnostic;
            const status = document.querySelector('#status')?.textContent ?? '';
            if (status.startsWith('Failed:') || status.startsWith('Error:')) return true;
            if (!host || host.session <= 0 || host.readyFrames <= previousReadyFrames ||
                host.lastReadySession !== host.session ||
                (expectedEpoch !== null && host.epoch !== expectedEpoch)) return false;
            if (!status.startsWith('Engine mesh shadow diagnostic rendered')) return false;
            const state = host.shadowState();
            return !state.castsShadows || state.shadowPasses > previousPasses;
        }, { previousPasses, previousReadyFrames, expectedEpoch });
        const status = await page.locator('#status').textContent();
        assert(status.startsWith('Engine mesh shadow diagnostic rendered'), `BrowserSmoke.EngineShadowFrameFailed: ${status}`);
    };
    const sample = async (name, state) => {
        const shadow = shadowSample(state);
        const lit = { x: 410, y: 256, u: 410 / 512, v: 0.5 };
        const png = await page.locator('canvas').screenshot({ path: path.join(config.output, `engine-shadow-${name}.png`) });
        const canvas = await capturePixels(page, png, [
            { name: 'projected-shadow', x: shadow.x, y: shadow.y },
            { name: 'unoccluded-receiver', x: lit.x, y: lit.y },
        ]);
        const hdrShadow = await page.evaluate(({ u, v }) => window.engineMeshDiagnostic.readHdrAt(u, v), shadow);
        const hdrLit = await page.evaluate(({ u, v }) => window.engineMeshDiagnostic.readHdrAt(u, v), lit);
        const statistics = await page.evaluate(() => window.engineMeshDiagnostic.statistics());
        const edge = [3, 4].includes(state.sampleCase)
            ? await captureShadowEdge(page, png, state, canvas.samples[1].average[0], canvas.samples[0].average[0]) : null;
        return { state, canvas, hdrShadow, hdrLit, statistics, edge };
    };
    try {
        await page.goto(url, { waitUntil: 'domcontentloaded' });
        await page.waitForFunction(() => window.engineMeshDiagnostic !== undefined);
        await page.locator('#start').click();
        const initialEpoch = await page.evaluate(() => window.engineMeshDiagnostic.epoch);
        await ready(0, 0, initialEpoch);
        report.shadowCases = [];
        let baseline, previousPasses = 0;
        for (const [sampleCase, name] of ['baseline', 'caster-moved', 'light-moved',
            'near-contact', 'far-from-caster', 'disabled', 'restored'].entries()) {
            if (sampleCase) {
                const previousReadyFrames = await page.evaluate(() => window.engineMeshDiagnostic.readyFrames);
                await page.evaluate(value => window.engineMeshDiagnostic.setShadowCase(value), sampleCase);
                await ready(previousPasses, previousReadyFrames, initialEpoch);
            }
            const state = await page.evaluate(() => window.engineMeshDiagnostic.shadowState());
            const result = await sample(name, state);
            report.shadowCases.push({ sampleCase, name, ...result });
            assert(state.sampleCase === sampleCase && state.semantic === 'StandardLitColorV1' &&
                state.pipeline === 'DefaultRenderPipeline' && state.authoredShaderCount === 0 &&
                state.directionalLights === 1 && state.pointLights === 0 && state.spotLights === 0,
                'BrowserSmoke.EngineShadowIdentity: the real engine light, material or pipeline changed.');
            assert(state.shadowRequests >= state.shadowPasses && state.shadowPasses > 0 && state.shadowCasters >= 1,
                'BrowserSmoke.EngineShadowProducer: the real standalone shadow viewport did not draw a caster.');
            assert(state.hdrTargets.length === 1 && state.shaders.length >= 2 && state.pipelines.length >= 2 &&
                (!state.castsShadows || (state.shaders.length >= 3 && state.pipelines.length >= 3)),
                'BrowserSmoke.EngineShadowPrograms: enabled depth writer, HDR receiver, and tonemap require separate cooked programs.');
            assert(result.hdrShadow.format === 'rgba16float' && result.hdrLit.format === 'rgba16float' &&
                result.canvas.width === 512 && result.canvas.height === 512 && result.statistics.frameSubmitCalls > 0 &&
                result.statistics.packets === 0 && result.statistics.focusedPipeline === null,
                'BrowserSmoke.EngineShadowOutput: shadow acceptance must use real HDR/canvas engine frames.');
            assert(await page.evaluate(() => window.engineMeshDiagnostic.partialSubmissions) === 0,
                'BrowserSmoke.EngineShadowPartialFrame: an unready producer allowed a partial frame submission.');
            if (state.castsShadows) {
                assert(state.depthTargets.some(target => target.width === state.mapWidth && target.height === state.mapHeight),
                    'BrowserSmoke.EngineShadowDepthTarget: the authored standalone depth24 map is missing.');
                assert(result.hdrShadow.average[0] + 0.08 < result.hdrLit.average[0] &&
                    result.canvas.samples[0].average[0] + 10 < result.canvas.samples[1].average[0],
                    `BrowserSmoke.EngineShadowContrast: ${name} lacks HDR and presented shadow contrast.`);
            } else {
                assert(result.hdrShadow.average[0] > baseline.hdrShadow.average[0] + 0.08 &&
                    result.canvas.samples[0].average[0] > baseline.canvas.samples[0].average[0] + 10,
                    'BrowserSmoke.EngineShadowDisable: the depth-one disabled binding did not remove the cast shadow.');
            }
            if (sampleCase === 0) baseline = result;
            previousPasses = state.shadowPasses;
        }
        assert(report.shadowCases[1].state.projectedShadowX > baseline.state.projectedShadowX + 0.3 &&
            report.shadowCases[2].state.projectedShadowX > 0.3,
            'BrowserSmoke.EngineShadowMotion: caster and light movements did not separate the projected shadow.');
        const near = report.shadowCases[3], far = report.shadowCases[4];
        assert(near.state.casterZ < far.state.casterZ && near.edge.transitionPixels !== null &&
            far.edge.transitionPixels !== null && far.edge.transitionPixels >= near.edge.transitionPixels + 2,
            `BrowserSmoke.EngineShadowPenumbra: contact-hardening filter did not widen the far edge (${near.edge.transitionPixels} to ${far.edge.transitionPixels} pixels).`);
        report.shadowMapResizes = [];
        for (const size of [512, 256]) {
            const previousReadyFrames = await page.evaluate(() => window.engineMeshDiagnostic.readyFrames);
            await page.evaluate(value => window.engineMeshDiagnostic.setShadowMapSize(value), size);
            await ready(previousPasses, previousReadyFrames, initialEpoch);
            const state = await page.evaluate(() => window.engineMeshDiagnostic.shadowState());
            const result = await sample(`map-${size}`, state);
            assert(state.mapWidth === size && state.mapHeight === size &&
                state.depthTargets.some(target => target.width === size && target.height === size) &&
                result.hdrShadow.average[0] + 0.08 < result.hdrLit.average[0],
                `BrowserSmoke.EngineShadowMapResize: ${size} did not render an updated depth map and shadowed HDR frame.`);
            assert(await page.evaluate(() => window.engineMeshDiagnostic.partialSubmissions) === 0,
                'BrowserSmoke.EngineShadowResizePartialFrame: map preparation submitted a partial frame.');
            report.shadowMapResizes.push({ size, ...result });
            previousPasses = state.shadowPasses;
        }
        // Disabling creates the retained one-pixel depth-one binding once. Use
        // the first full toggle as the warmed reference, then require later
        // toggles to drain and return to a stable live-resource count.
        report.shadowToggleCycles = [];
        let settledDisabledLive, settledRestoredLive;
        const waitForRetirement = async () => {
            await page.waitForFunction(() => window.engineMeshDiagnostic.statistics()?.resources?.retiring === 0,
                null, { timeout: Math.min(config.timeout, 15000) });
            return page.evaluate(() => window.engineMeshDiagnostic.statistics());
        };
        for (let cycle = 0; cycle < 3; cycle++) {
            let previousReadyFrames = await page.evaluate(() => window.engineMeshDiagnostic.readyFrames);
            await page.evaluate(() => window.engineMeshDiagnostic.setShadowCase(5));
            await ready(previousPasses, previousReadyFrames, initialEpoch);
            const disabledState = await page.evaluate(() => window.engineMeshDiagnostic.shadowState());
            const disabledPixels = await sample(`toggle-${cycle}-disabled`, disabledState);
            const disabledStatistics = await waitForRetirement();
            assert(!disabledState.castsShadows && disabledStatistics.resources.retiring === 0 &&
                disabledPixels.hdrShadow.average[0] > baseline.hdrShadow.average[0] + 0.08 &&
                disabledPixels.canvas.samples[0].average[0] > baseline.canvas.samples[0].average[0] + 10,
                `BrowserSmoke.EngineShadowRepeatDisable: cycle ${cycle} did not release the cast shadow and retire resources.`);
            settledDisabledLive ??= disabledStatistics.resources.live;
            assert(disabledStatistics.resources.live <= settledDisabledLive,
                `BrowserSmoke.EngineShadowDisabledRetention: cycle ${cycle} grew live resources from ${settledDisabledLive} to ${disabledStatistics.resources.live}.`);

            previousReadyFrames = await page.evaluate(() => window.engineMeshDiagnostic.readyFrames);
            await page.evaluate(() => window.engineMeshDiagnostic.setShadowCase(6));
            await ready(previousPasses, previousReadyFrames, initialEpoch);
            const restoredState = await page.evaluate(() => window.engineMeshDiagnostic.shadowState());
            const restoredPixels = await sample(`toggle-${cycle}-restored`, restoredState);
            const restoredStatistics = await waitForRetirement();
            assert(restoredState.castsShadows && restoredState.shadowPasses > previousPasses &&
                restoredStatistics.resources.retiring === 0 &&
                restoredPixels.hdrShadow.average[0] + 0.08 < restoredPixels.hdrLit.average[0] &&
                restoredPixels.canvas.samples[0].average[0] + 10 < restoredPixels.canvas.samples[1].average[0],
                `BrowserSmoke.EngineShadowRepeatRestore: cycle ${cycle} did not restore the producer and shadowed output.`);
            settledRestoredLive ??= restoredStatistics.resources.live;
            assert(restoredStatistics.resources.live <= settledRestoredLive,
                `BrowserSmoke.EngineShadowRestoredRetention: cycle ${cycle} grew live resources from ${settledRestoredLive} to ${restoredStatistics.resources.live}.`);
            assert(await page.evaluate(() => window.engineMeshDiagnostic.partialSubmissions) === 0,
                'BrowserSmoke.EngineShadowTogglePartialFrame: a toggle submitted a partial frame.');
            report.shadowToggleCycles.push({ cycle, disabledState, disabledStatistics, restoredState,
                restoredStatistics, disabledPixels, restoredPixels,
                pipelineCacheEntries: { disabled: disabledStatistics.resources.pipelineCacheEntries,
                    restored: restoredStatistics.resources.pipelineCacheEntries } });
            previousPasses = restoredState.shadowPasses;
        }
        const oldSession = await page.evaluate(() => window.engineMeshDiagnostic.session);
        await page.locator('#stop').click();
        assert(await page.evaluate(() => window.engineMeshDiagnostic.session === 0 && window.engineMeshDiagnostic.statistics() === null),
            'BrowserSmoke.EngineShadowTeardown: the renderer survived stop.');
        await page.locator('#start').click();
        const restartEpoch = await page.evaluate(() => window.engineMeshDiagnostic.epoch);
        await ready(0, 0, restartEpoch);
        const restarted = await page.evaluate(() => window.engineMeshDiagnostic.shadowState());
        assert(restarted.shadowPasses > 0 && restarted.mapWidth === 256 && restarted.shadowCasters >= 1 &&
            await page.evaluate(previous => window.engineMeshDiagnostic.session !== previous &&
                window.engineMeshDiagnostic.lastReadySession === window.engineMeshDiagnostic.session, oldSession),
            'BrowserSmoke.EngineShadowRestart: the real producer did not resume with fresh resources.');
        report.shadowRestart = { state: restarted, statistics: await page.evaluate(() => window.engineMeshDiagnostic.statistics()) };
        await page.locator('#stop').click();
        assertNoBrowserErrors(events);
    } catch (error) {
        report.shadowFailure = await page.evaluate(() => window.engineMeshDiagnostic?.failure ?? null).catch(() => null);
        await page.screenshot({ path: path.join(config.output, 'engine-shadow-failure.png'), fullPage: true }).catch(() => {});
        throw error;
    } finally { await context.close(); }
}

async function debugOverlayCheck(browser, origin, report, config) {
    const { page, context, events } = await instrumentedPage(browser, origin, report, 'engine-shared-debug-overlay', config);
    const counts = [0, 1, 256, 384, 32, 0, 384, 512, 0, 1, 32, 32, 768, 32, 1024, 0];
    const baselineSites = [
        { name: 'point', x: 115, y: 159 }, { name: 'line', x: 256, y: 218 },
        { name: 'triangle', x: 384, y: 366 },
        { name: 'alternate-point', x: 397, y: 159 }, { name: 'alternate-line', x: 256, y: 269 },
        { name: 'alternate-triangle', x: 128, y: 366 },
    ];
    const pixel = (capture, name) => capture.samples.find(sample => sample.name === name).average;
    const near = (actual, expected, tolerance) => actual.every((value, channel) => Math.abs(value - expected[channel]) <= tolerance);
    const debugIdentity = state => JSON.stringify({
        pipelines: state.pipelines.filter(item => item.label.startsWith('engine-debug-'))
            .map(({ slot, generation, label }) => ({ slot, generation, label })).sort((a, b) => a.label.localeCompare(b.label)),
        commands: state.commands.map(({ slot, generation, label }) => ({ slot, generation, label }))
            .sort((a, b) => a.slot - b.slot),
        buffers: state.buffers.filter(item => ['PointsBuffer', 'LinesBuffer', 'TrianglesBuffer'].includes(item.label))
            .map(({ slot, generation, label }) => ({ slot, generation, label })).sort((a, b) => a.label.localeCompare(b.label)),
    });
    const waitReady = async (sampleCase, previousReadyFrames, expectedSession) => {
        await page.waitForFunction(({ sampleCase, previousReadyFrames, expectedSession, expectedCount }) => {
            const host = window.engineMeshDiagnostic;
            const status = document.querySelector('#status')?.textContent ?? '';
            if (status.startsWith('Failed:') || status.startsWith('Error:')) return true;
            if (!host || host.session !== expectedSession || host.readyFrames <= previousReadyFrames ||
                host.lastReadySession !== expectedSession) return false;
            const state = host.debugState();
            return state.sampleCase === sampleCase && state.visualizerPoints === expectedCount &&
                state.visualizerLines === expectedCount && state.visualizerTriangles === expectedCount;
        }, { sampleCase, previousReadyFrames, expectedSession, expectedCount: counts[sampleCase] });
        const status = await page.locator('#status').textContent();
        assert(status.startsWith('Engine mesh debug diagnostic rendered'), `BrowserSmoke.EngineDebugFrameFailed: ${status}`);
    };
    const captureCase = async (name, sites = baselineSites) => {
        const png = await page.locator('canvas').screenshot({ path: path.join(config.output, `engine-debug-${name}.png`) });
        return capturePixels(page, png, sites);
    };
    let initialIdentity, expandedIdentity, largeIdentity, initialPipelineIdentity, initialLive, blankPixels;
    report.debugCases = [];
    try {
        await page.goto(`${origin}/diagnostics/engine-mesh.html?probe=debug&manifest=${encodeURIComponent(`${origin}/__shaders/manifest.json`)}` +
            `&assets=${encodeURIComponent(`${origin}${config.engineManifest}`)}`, { waitUntil: 'domcontentloaded' });
        await page.waitForFunction(() => window.engineMeshDiagnostic !== undefined);
        await page.locator('#start').click();
        await page.waitForFunction(() => window.engineMeshDiagnostic.session > 0 || window.engineMeshDiagnostic.failure);
        const session = await page.evaluate(() => window.engineMeshDiagnostic.session);
        await waitReady(0, 0, session);
        for (const sampleCase of [0, 1, 9, 2, 3, 4, 10, 11, 5, 6, 7, 12, 13, 14, 15]) {
            if (sampleCase) {
                const previousReadyFrames = await page.evaluate(() => window.engineMeshDiagnostic.readyFrames);
                await page.evaluate(value => window.engineMeshDiagnostic.setDebugCase(value), sampleCase);
                await waitReady(sampleCase, previousReadyFrames, session);
            }
            if ([3, 7, 12, 14].includes(sampleCase))
                await page.waitForFunction(() => window.engineMeshDiagnostic.statistics()?.resources?.retiring === 0,
                    null, { timeout: Math.min(config.timeout, 15000) });
            const state = await page.evaluate(() => window.engineMeshDiagnostic.debugState());
            const statistics = await page.evaluate(() => window.engineMeshDiagnostic.statistics());
            const pixels = await captureCase(String(sampleCase));
            report.debugCases.push({ sampleCase, state, statistics, pixels });
            assert(state.pipeline === 'DefaultRenderPipeline' && state.authoredShaderCount === 0 && state.gizmosVisible &&
                state.debugInstanceRenderingAvailable && state.componentCallbacks > 0 &&
                state.expectedPoints === counts[sampleCase] && state.expectedLines === counts[sampleCase] &&
                state.expectedTriangles === counts[sampleCase] && state.componentShapes === counts[sampleCase] * 3,
                `BrowserSmoke.EngineDebugProducer: case ${sampleCase} did not use the registered component and shared pipeline.`);
            assert(statistics.packets === 0 && statistics.focusedPipeline === null && statistics.frameSubmitCalls > 0 &&
                await page.evaluate(() => window.engineMeshDiagnostic.partialSubmissions) === 0,
                `BrowserSmoke.EngineDebugPartialFrame: case ${sampleCase} used another renderer or submitted an unready frame.`);
            const debugPipelines = state.pipelines.filter(item => item.label.startsWith('engine-debug-'));
            const litPipelines = state.pipelines.filter(item => item.label === 'engine-standard-lit-color');
            assert(litPipelines.length > 0 && litPipelines.every(item => !item.blended),
                `BrowserSmoke.EngineDebugOpaqueBlendLeak: case ${sampleCase} enabled blending on the StandardLit background.`);
            if (counts[sampleCase]) {
                assert(debugPipelines.length === 3 && state.commands.length === 3 &&
                    debugPipelines.filter(item => ['engine-debug-point', 'engine-debug-line'].includes(item.label))
                        .every(item => item.blended) &&
                    state.resolutionTraces.filter(item => [3, 4, 5].includes(item.SourceSemantic.Semantic) && item.Stage === 'Recorded').length === 3,
                    `BrowserSmoke.EngineDebugCommands: case ${sampleCase} lacks point/line/triangle cooked GPU draws.`);
                const identity = debugIdentity(state);
                const pipelineIdentity = JSON.stringify(debugPipelines.map(({ slot, generation, label }) =>
                    ({ slot, generation, label })).sort((a, b) => a.label.localeCompare(b.label)));
                if (sampleCase === 1) {
                    initialIdentity = identity;
                    initialPipelineIdentity = pipelineIdentity;
                    initialLive = statistics.resources.live;
                }
                assert(pipelineIdentity === initialPipelineIdentity,
                    `BrowserSmoke.EngineDebugPipelineIdentity: case ${sampleCase} rebuilt cooked pipelines for a count/capacity change.`);
                if ([9, 2].includes(sampleCase))
                    assert(identity === initialIdentity, `BrowserSmoke.EngineDebugCountIdentity: case ${sampleCase} rebuilt warm debug commands.`);
                if (sampleCase === 3) expandedIdentity = identity;
                if ([4, 10, 11, 6].includes(sampleCase))
                    assert(identity === expandedIdentity, `BrowserSmoke.EngineDebugShrinkIdentity: case ${sampleCase} rebuilt bindings within capacity.`);
                if (sampleCase === 12) largeIdentity = identity;
                if (sampleCase === 13)
                    assert(identity === largeIdentity, 'BrowserSmoke.EngineDebugRepeatedShrinkIdentity: the 768-to-32 transition rebuilt bindings.');
                assert(statistics.resources.live <= initialLive + 18,
                    `BrowserSmoke.EngineDebugRetention: case ${sampleCase} grew live resources beyond the bounded debug cohort.`);
            }
            if (sampleCase === 0) blankPixels = pixels;
            const alternate = [4, 9, 11].includes(sampleCase);
            if (counts[sampleCase]) {
                const point = pixel(pixels, alternate ? 'alternate-point' : 'point');
                const line = pixel(pixels, alternate ? 'alternate-line' : 'line');
                const triangle = pixel(pixels, alternate ? 'alternate-triangle' : 'triangle');
                if (alternate) {
                    assert(point[0] > 160 && point[1] > 160 && point[2] + 80 < Math.min(point[0], point[1]) &&
                        triangle[1] > triangle[0] + 90 && triangle[2] > triangle[0] + 90,
                        `BrowserSmoke.EngineDebugAlternatePixels: case ${sampleCase} did not update point/triangle color and position.`);
                    const background = pixel(blankPixels, 'alternate-line');
                    const expected = [background[0] * 0.4 + 153, background[1] * 0.4, background[2] * 0.4 + 153];
                    assert(near(line.slice(0, 3), expected, 12),
                        `BrowserSmoke.EngineDebugAlternateAlpha: case ${sampleCase} did not blend the magenta line.`);
                } else {
                    assert(point[0] > point[1] + 60 && point[0] > point[2] + 60 &&
                        triangle[2] > triangle[0] + 90 && triangle[2] > triangle[1] + 90,
                        `BrowserSmoke.EngineDebugPixels: case ${sampleCase} lacks the red point or blue triangle.`);
                    const background = pixel(blankPixels, 'line');
                    const expected = [background[0] * 0.4, background[1] * 0.4 + 153, background[2] * 0.4];
                    assert(near(line.slice(0, 3), expected, 12),
                        `BrowserSmoke.EngineDebugLineAlpha: case ${sampleCase} did not blend the green line.`);
                }
            } else for (const site of baselineSites)
                assert(near(pixel(pixels, site.name), pixel(blankPixels, site.name), 8),
                    `BrowserSmoke.EngineDebugZeroCount: case ${sampleCase} left stale pixels at ${site.name}.`);
            if (sampleCase === 2) {
                const warm = await page.evaluate(() => {
                    const host = window.engineMeshDiagnostic;
                    const before = host.statistics();
                    const commands = host.debugState().commands;
                    const ready = host.exports.Frame(host.session);
                    return { ready, before, after: host.statistics(), commands, afterCommands: host.debugState().commands };
                });
                report.debugWarmFrame = warm;
                assert(warm.ready && warm.after.frameSubmitCalls - warm.before.frameSubmitCalls === 1 &&
                    warm.after.controlCalls === warm.before.controlCalls &&
                    warm.after.uploadSubmitCalls === warm.before.uploadSubmitCalls &&
                    warm.after.arenaGrowth === warm.before.arenaGrowth &&
                    warm.after.resources.pipelineCacheEntries === warm.before.resources.pipelineCacheEntries &&
                    JSON.stringify(warm.commands) === JSON.stringify(warm.afterCommands),
                    'BrowserSmoke.EngineDebugWarmFrame: one warmed shared frame imported unexpected control/upload work or rebuilt commands.');
            }
        }
        let previousReadyFrames = await page.evaluate(() => window.engineMeshDiagnostic.readyFrames);
        await page.evaluate(() => { window.engineMeshDiagnostic.setDebugCase(1); window.engineMeshDiagnostic.resize(640, 320); });
        await waitReady(1, previousReadyFrames, session);
        const resized = await captureCase('resize-640x320', [
            { name: 'point', x: 144, y: 99 }, { name: 'line', x: 320, y: 136 },
            { name: 'triangle', x: 480, y: 229 },
        ]);
        assert(resized.width === 640 && resized.height === 320 && pixel(resized, 'point')[0] > pixel(resized, 'point')[1] + 60 &&
            pixel(resized, 'triangle')[2] > pixel(resized, 'triangle')[0] + 90,
            'BrowserSmoke.EngineDebugResizePixels: non-square output lost point/triangle overlay color.');
        report.debugResize = { pixels: resized, state: await page.evaluate(() => window.engineMeshDiagnostic.debugState()) };
        previousReadyFrames = await page.evaluate(() => window.engineMeshDiagnostic.readyFrames);
        await page.evaluate(() => window.engineMeshDiagnostic.resize(512, 512));
        await waitReady(1, previousReadyFrames, session);
        await page.locator('#stop').click();
        assert(await page.evaluate(() => window.engineMeshDiagnostic.session === 0 && window.engineMeshDiagnostic.statistics() === null),
            'BrowserSmoke.EngineDebugStop: the debug renderer survived stop.');
        await page.locator('#start').click();
        await page.waitForFunction(() => window.engineMeshDiagnostic.session > 0 || window.engineMeshDiagnostic.failure);
        const restartedSession = await page.evaluate(() => window.engineMeshDiagnostic.session);
        await waitReady(0, 0, restartedSession);
        previousReadyFrames = await page.evaluate(() => window.engineMeshDiagnostic.readyFrames);
        await page.evaluate(() => window.engineMeshDiagnostic.setDebugCase(1));
        await waitReady(1, previousReadyFrames, restartedSession);
        const restarted = await captureCase('restart');
        assert(restartedSession !== session && pixel(restarted, 'point')[0] > pixel(restarted, 'point')[1] + 60 &&
            pixel(restarted, 'triangle')[2] > pixel(restarted, 'triangle')[0] + 90,
            'BrowserSmoke.EngineDebugRestart: fresh renderer did not restore the shared overlay.');
        report.debugRestart = { session: restartedSession, pixels: restarted,
            state: await page.evaluate(() => window.engineMeshDiagnostic.debugState()) };
        const failure = await page.evaluate(() => window.engineMeshDiagnostic.renderer.getFailureDiagnostics());
        assert(!failure.firstError && !failure.deviceLoss && !failure.failed,
            `BrowserSmoke.EngineDebugGpuFailure: ${JSON.stringify(failure)}`);
        await page.locator('#stop').click();
        assertNoBrowserErrors(events);
    } catch (error) {
        report.debugFailure = await page.evaluate(() => window.engineMeshDiagnostic?.failure ?? null).catch(() => null);
        await page.screenshot({ path: path.join(config.output, 'engine-debug-failure.png'), fullPage: true }).catch(() => {});
        throw error;
    } finally { await context.close(); }
}

async function gpuCanaryCheck(browser, origin, report, config) {
    const { page, context, events } = await instrumentedPage(browser, origin, report, 'independent-gpu-canary', config);
    try {
        await page.goto(`${origin}/__gpu-canary/`, { waitUntil: 'domcontentloaded' });
        report.gpuCanary = await page.evaluate(initializeGpuCanary,
            { manifestUrl: `${origin}/__shaders/manifest.json`, budgetMs: Math.min(config.timeout, 45000) });
        for (const stage of ['clear', 'triangle', 'cooked-wgsl']) {
            report.gpuCanary = await page.evaluate(stage => window.gpuCanary.runStage(stage), stage);
            if (stage === 'cooked-wgsl') continue;
            const png = await page.locator('canvas').screenshot({ path: path.join(config.output, `gpu-canary-${stage}.png`) });
            const expected = stage === 'clear' ? [51, 77, 102, 255] : [64, 128, 191, 255];
            const pixels = await capturePixels(page, png, [{ name: stage, x: 64, y: 64, expected, tolerance: 3 }]);
            (report.gpuCanaryPixels ??= []).push({ stage, ...pixels });
            for (const sample of pixels.samples)
                for (let channel = 0; channel < 4; channel++)
                    assert(sample.min[channel] >= expected[channel] - sample.tolerance && sample.max[channel] <= expected[channel] + sample.tolerance,
                        `BrowserSmoke.GpuCanaryPixels: ${stage} channel ${channel} expected ${expected[channel]}, got ${sample.min[channel]}..${sample.max[channel]}.`);
        }
        assertNoBrowserErrors(events);
    } catch (error) {
        report.gpuCanary = await page.evaluate(() => window.gpuCanary?.snapshot() ?? null).catch(() => report.gpuCanary);
        await page.screenshot({ path: path.join(config.output, 'gpu-canary-failure.png') }).catch(() => {});
        throw error;
    } finally {
        await page.evaluate(() => window.gpuCanary?.dispose()).catch(() => {});
        await context.close();
    }
}

async function audioStreamCheck(browser, origin, report, config) {
    const { page, context, events } = await instrumentedPage(browser, origin, report, 'engine-audio-stream', config);
    try {
        await page.goto(`${origin}/__audio-probe/`, { waitUntil: 'domcontentloaded' });
        report.audioStream = await page.evaluate(runOfflineAudioProbe,
            { moduleUrl: `${origin}/web-audio-stream.js`, budgetMs: Math.min(config.timeout, 45000) });
        assertNoBrowserErrors(events);
    } finally { await context.close(); }
}

async function enginePageCheck(browser, origin, report, config) {
    const { page, context, events } = await instrumentedPage(browser, origin, report, 'engine-diagnostic', config);
    try {
        await page.goto(`${origin}/engine-diagnostic.html`, { waitUntil: 'domcontentloaded' });
        await page.waitForFunction(() => ['stopped', 'failed'].includes(document.querySelector('#status')?.dataset.state));
        assert(await page.locator('#status').getAttribute('data-state') === 'stopped',
            `BrowserSmoke.EngineExportsFailed: ${await page.locator('#status').textContent()}`);
        if (config.requireWorldPlay) {
            for (let iteration = 0; iteration < 2; iteration++) {
                await page.locator('#manifest-url').fill(`${origin}${config.engineManifest}`);
                await page.locator('#world-form').evaluate(form => form.requestSubmit());
                await page.waitForFunction(() => ['running', 'failed'].includes(document.querySelector('#status')?.dataset.state));
                assert(await page.locator('#status').getAttribute('data-state') === 'running',
                    `BrowserSmoke.WorldPlayFailed: ${await page.locator('#status').textContent()}`);
                await page.waitForFunction(() => window.engineWorldDiagnostic?.statistics().completedFrames >= 12);
                const statistics = await page.evaluate(() => window.engineWorldDiagnostic.statistics());
                assert(statistics.running, 'BrowserSmoke.WorldStoppedBeforeFrames: the engine must complete its own caller-thread frames.');
                (report.worldIterations ??= []).push({ iteration, ...statistics });
                await page.locator('#stop').click();
                await page.waitForFunction(() => ['stopped', 'failed'].includes(document.querySelector('#status')?.dataset.state));
                assert(await page.locator('#status').getAttribute('data-state') === 'stopped', 'BrowserSmoke.WorldTeardownFailed.');
            }
        }
        await page.screenshot({ path: path.join(config.output, 'engine-diagnostic.png'), fullPage: true });
        assertNoBrowserErrors(events);
    } finally { await context.close(); }
}

async function shippingPlayerCheck(browser, origin, report, config) {
    const { page, context, events } = await instrumentedPage(browser, origin, report, 'published-engine-player', config);
    try {
        await page.goto(`${origin}/index.html`, { waitUntil: 'domcontentloaded' });
        assert(await page.locator('#manifest-url').count() === 0 && await page.locator('#world-form').count() === 0,
            'BrowserSmoke.PublishedSetupControls: the shipping player exposes diagnostic manifest controls.');
        await page.waitForFunction(() => ['running', 'failed'].includes(document.querySelector('#status')?.dataset.state));
        assert(await page.locator('#status').getAttribute('data-state') === 'running',
            `BrowserSmoke.PublishedWorldAutostartFailed: ${await page.locator('#status').textContent()}`);
        await page.screenshot({ path: path.join(config.output, 'published-engine-player.png'), fullPage: true });
        assertNoBrowserErrors(events);
    } finally { await context.close(); }
}

async function assetSourceCheck(browser, origin, report, config) {
    const { page, context, events } = await instrumentedPage(browser, origin, report, 'engine-assets', config);
    try {
        await page.goto(`${origin}/engine-diagnostic.html`, { waitUntil: 'domcontentloaded' });
        report.assetSource = await page.evaluate(async manifestUrl => {
            const { engineAssetImports: api } = await import('/engine-assets.js');
            const results = [];
            for (let iteration = 0; iteration < 2; iteration++) {
                const owner = api.create(manifestUrl);
                try {
                    await api.open(owner);
                    const manifest = JSON.parse(api.manifest(owner));
                    const asset = manifest.assets.find(asset => asset.bytes <= 1048576);
                    if (!asset) throw new Error('Asset smoke requires at least one payload no larger than 1 MiB.');
                    const ticket = api.beginRead(owner, asset.path);
                    const length = await api.waitRead(owner, ticket);
                    const bytes = new Uint8Array(length);
                    api.copyRead(owner, ticket, bytes);
                    const digest = [...new Uint8Array(await crypto.subtle.digest('SHA-256', bytes))]
                        .map(value => value.toString(16).padStart(2, '0')).join('');
                    if (digest !== asset.hash || length !== asset.bytes) throw new Error('Asset delivery hash/length mismatch.');
                    api.releaseRead(owner, ticket);
                    let released = false;
                    try { api.copyRead(owner, ticket, bytes); } catch { released = true; }
                    if (!released) throw new Error('Released asset ticket was still readable.');
                    const canceledTicket = api.beginRead(owner, asset.path);
                    const canceledRead = api.waitRead(owner, canceledTicket);
                    api.releaseRead(owner, canceledTicket);
                    let canceled = false;
                    try { await canceledRead; } catch { canceled = true; }
                    if (!canceled) throw new Error('Canceled asset ticket completed successfully.');
                    api.dispose(owner);
                    let disposed = false;
                    try { api.manifest(owner); } catch { disposed = true; }
                    if (!disposed) throw new Error('Disposed asset owner remained accessible.');
                    results.push({ iteration, path: asset.path, bytes: length, digest, released, canceled, disposed });
                } finally { api.dispose(owner); }
            }
            return results;
        }, `${origin}${config.engineManifest}`);
        assertNoBrowserErrors(events);
    } finally { await context.close(); }
}

async function joltCheck(browser, origin, report, config) {
    const { page, context, events } = await instrumentedPage(browser, origin, report, 'jolt-spike', config);
    try {
        await page.addInitScript(() => {
            window.__xreWorkerStarts = [];
            const OriginalWorker = window.Worker;
            window.Worker = new Proxy(OriginalWorker, { construct(target, args) {
                window.__xreWorkerStarts.push(String(args[0]));
                return Reflect.construct(target, args);
            } });
        });
        report.joltIterations = [];
        for (let iteration = 0; iteration < 2; iteration++) {
            const logStart = events.length;
            await page.goto(`${origin}/__jolt/`, { waitUntil: 'domcontentloaded' });
            await page.waitForFunction(() => ['passed', 'failed'].includes(document.querySelector('#status')?.dataset.state));
            const result = await page.evaluate(() => ({ state: document.querySelector('#status').dataset.state,
                detail: document.querySelector('#status').textContent, workers: window.__xreWorkerStarts }));
            report.joltIterations.push({ iteration, ...result });
            assert(result.state === 'passed', `BrowserSmoke.JoltFailed: ${result.detail}`);
            assert(result.workers.length === 0, 'BrowserSmoke.JoltWorkers: the single-threaded spike created workers.');
            assert(events.slice(logStart).some(event => event.text?.includes('teardown complete')),
                'BrowserSmoke.JoltTeardownMissing: native teardown completion was not observed.');
        }
        await page.screenshot({ path: path.join(config.output, 'jolt-spike.png'), fullPage: true });
        assertNoBrowserErrors(events);
    } finally { await context.close(); }
}

async function publishedGameCheck(browser, origin, report, config) {
    const descriptor = JSON.parse(await fs.readFile(path.join(config.gamePublish, 'browser-publish.json'), 'utf8'));
    const manifest = JSON.parse(await fs.readFile(path.join(config.gamePublish, 'content', 'manifest.json'), 'utf8'));
    const parity = config.gameKind === 'rendering-parity';
    const advanced = config.gameKind === 'advanced-rendering-parity';
    const advancedShadow = config.gameKind === 'advanced-shadow-parity';
    const ui = config.gameKind === 'ui-parity';
    const modular = config.gameKind === 'modular-pipeline-parity';
    const meshlet = config.gameKind === 'static-meshlet-parity';
    const worldPath = ui ? '/game/Worlds/BrowserUiParityWorld.asset'
        : advanced || advancedShadow ? '/game/Worlds/AdvancedRenderingParityWorld.asset'
        : modular ? '/game/Worlds/ModularPipelineParityWorld.asset'
        : meshlet ? '/game/Worlds/StaticMeshletParityWorld.asset'
        : parity ? '/game/Worlds/RenderingParityWorld.asset' : '/game/Worlds/RollingBallWorld.asset';
    assert(descriptor.schema === 2 && descriptor.format === 'xrengine-engine-launch' &&
        descriptor.manifest === './content/manifest.json' && manifest.startupWorld === worldPath,
        'BrowserSmoke.GameBundle: expected the Editor-activated canonical game publish.');
    if (meshlet) {
        const baseline = JSON.parse(await fs.readFile(path.join(config.baselinePublish, 'content', 'manifest.json'), 'utf8'));
        assert(baseline.startupWorld === worldPath,
            'BrowserSmoke.StaticMeshletBaseline: CPU and GPU bundles must publish the same saved world.');
        for (const pass of ['select-lod', 'cull-expand', 'finalize-indexed', 'refit-bounds'])
            assert(manifest.pipelineArtifacts?.some(entry => entry.pass === pass && entry.scope === 'meshlets') &&
                baseline.pipelineArtifacts?.some(entry => entry.pass === pass && entry.scope === 'meshlets'),
                `BrowserSmoke.StaticMeshletArtifactMissing: meshlets::${pass}.`);
        await staticMeshletParityGameCheck(browser, origin, report, config, instrumentedPage, assertNoBrowserErrors);
    } else if (ui) {
        for (const [semantic, semanticVersion] of [['UIQuadBatched', 2], ['UIQuadBatchedTexture', 2],
            ['UITextBatchedBitmap', 2], ['UICanvasSurface', 1]])
            assert(manifest.materialVariants?.some(entry => entry.semantic === semantic &&
                entry.semanticVersion === semanticVersion), `BrowserSmoke.UiArtifactMissing: ${semantic} v${semanticVersion}.`);
        assert(manifest.assets?.some(entry => entry.path === '/engine/Fonts/Roboto/Roboto-Regular.cooked.asset'),
            'BrowserSmoke.UiFontMissing: the authored bitmap UI font must be published.');
        await uiParityGameCheck(browser, origin, report, config, instrumentedPage, assertNoBrowserErrors);
    } else if (advancedShadow) {
        await advancedShadowParityGameCheck(browser, origin, report, config, instrumentedPage, assertNoBrowserErrors);
    } else if (advanced) {
        for (const pass of ['visibility-pull', 'depth-pyramid', 'gtao', 'shade-classify', 'shade-native', 'present'])
            assert(manifest.pipelineArtifacts?.some(entry => entry.scope === 'advanced' && entry.pass === pass),
                `BrowserSmoke.AdvancedArtifactMissing: advanced::${pass}.`);
        await advancedRenderingGameCheck(browser, origin, report, config, instrumentedPage, assertNoBrowserErrors);
    } else if (modular) {
        assert(manifest.pipelineArtifacts?.some(entry => entry.scope === 'custom' && entry.pass === 'custom-pass'),
            'BrowserSmoke.ModularArtifactMissing: custom::custom-pass was not published.');
        await modularPipelineGameCheck(browser, origin, report, config, instrumentedPage, assertNoBrowserErrors);
    } else if (parity) {
        assert(manifest.computeArtifacts?.some(entry => entry.kernel === 'packed-skinning') &&
            manifest.materialVariants?.some(entry => entry.semantic === 'StandardLitTexture' &&
                entry.pass === 'opaque-forward' && entry.vertexProfile === 'position-normal-tangent-uv-v1'),
            'BrowserSmoke.RenderingParityArtifacts: mapped normal surfaces and packed deformation artifacts are required.');
        await renderingParityGameCheck(browser, origin, report, config, instrumentedPage, assertNoBrowserErrors);
    } else await rollingBallGameCheck(browser, origin, report, config, instrumentedPage, assertNoBrowserErrors);
}

async function main() {
    const config = readConfig();
    if (config.help) { console.log(help); return; }
    config.uiFrameTracePermit = await claimUiFrameTrace(config);
    config.output = path.resolve(config.output);
    await fs.mkdir(config.output, { recursive: true });
    const report = { schemaVersion: 1, passed: false, startedUtc: new Date().toISOString(),
        node: process.version, platform: process.platform, architecture: process.arch,
        playwright: require('playwright/package.json').version, gpuMode: config.gpuMode,
        gpuDiagnostics: config.gpuDiagnostics, nativeCompileTrace: config.nativeCompileTrace,
        uiFrameTraceRequested: config.uiFrameTrace,
        executable: config.executablePath ? path.basename(config.executablePath) : 'playwright-managed-chromium',
        browserLogs: {}, externalRequests: [], requests: [], checks: [],
        scope: config.gameOnly
            ? `Editor-published ${config.gameKind} in Chromium; not desktop, complete browser or performance acceptance.`
            : 'Renderer diagnostic correctness and selected runtime smoke checks; not full browser, desktop, physical-device or performance acceptance.' };
    const gameCheckName = config.gameKind === 'rollingball'
        ? 'rollingball-editor-published-game' : `${config.gameKind}-editor-published-world`;
    let browser, server, origin;
    const check = async (name, action) => {
        const start = performance.now();
        try { await action(); report.checks.push({ name, status: 'passed', milliseconds: performance.now() - start }); }
        catch (error) {
            report.checks.push({ name, status: 'failed', milliseconds: performance.now() - start, error: String(error), stack: error.stack });
            console.error(`${name}: ${error}`);
        }
    };
    try {
        const hosted = await startServer(config, report.requests);
        server = hosted.server;
        origin = hosted.origin;
        report.launchArguments = browserLaunchOptions(config).args;
        browser = config.uiFrameTrace
            ? await launchUiTraceBrowser(chromium, browserLaunchOptions(config), config.uiFrameTracePermit)
            : await chromium.launch(browserLaunchOptions(config));
        report.browser = browser.version();
        browser.on('disconnected', () => { report.browserDisconnected = {
            time: new Date().toISOString(), closeRequested: report.browserCloseRequested === true }; });
        if (config.gameOnly) {
            await check(gameCheckName,
                () => publishedGameCheck(browser, hosted.origin, report, config));
            if (config.uiFrameTrace) await check('ui-frame-trace-diagnostic', async () => {
                assert(report.uiFrameTrace?.complete === true, 'BrowserSmoke.UiFrameTrace: the bounded trace is incomplete.');
            });
            await check('local-delivery', async () => {
                assert(report.externalRequests.length === 0, 'BrowserSmoke.ExternalRequest: the published application requested resources outside the loopback roots.');
                assert(!report.requests.some(request => request.status >= 400), 'BrowserSmoke.HttpFailure: a served resource request failed; inspect requests in smoke-report.json.');
            });
            report.passed = report.checks.every(result => result.status === 'passed');
            return;
        }
        if (config.gpuDiagnostics) await captureGpuProcessState(browser, report, 'before-engine-depth');
        if (config.engineManifest) await check('engine-depth', () => depthCheck(browser, hosted.origin, report, config));
        else report.checks.push({ name: 'engine-depth', status: 'skipped', reason: '--engine-manifest was not supplied' });
        if (config.engineManifest) await check('engine-texture-sampling-lifetime', () => textureCheck(browser, hosted.origin, report, config));
        else report.checks.push({ name: 'engine-texture-sampling-lifetime', status: 'skipped', reason: '--engine-manifest was not supplied' });
        if (config.engineManifest) {
            await check('engine-lit-hdr-tonemap', () => litCheck(browser, hosted.origin, report, config));
            await check('engine-unlit-materials', () => unlitMaterialsCheck(browser, hosted.origin, report, config,
                instrumentedPage, capturePixels, assertNoBrowserErrors));
            await check('engine-unlit-msaa', () => unlitMsaaCheck(browser, hosted.origin, report, config,
                instrumentedPage, capturePixels, assertNoBrowserErrors));
            await check('engine-unlit-indirect', () => unlitIndirectCheck(browser, hosted.origin, report, config,
                instrumentedPage, capturePixels, assertNoBrowserErrors));
            await check('engine-shared-gtao-bloom', () => effectsCheck(browser, hosted.origin, report, config));
            await check('engine-directional-shadow', () => shadowCheck(browser, hosted.origin, report, config));
            await check('engine-shared-debug-overlay', () => debugOverlayCheck(browser, hosted.origin, report, config));
        } else for (const name of ['engine-lit-hdr-tonemap', 'engine-unlit-materials', 'engine-unlit-msaa', 'engine-unlit-indirect', 'engine-shared-gtao-bloom',
            'engine-directional-shadow', 'engine-shared-debug-overlay'])
            report.checks.push({ name, status: 'skipped', reason: '--engine-manifest was not supplied' });
        if (config.gpuDiagnostics) {
            await captureGpuProcessState(browser, report, 'after-engine-depth');
            await check('independent-gpu-canary-diagnostic-only', () => gpuCanaryCheck(browser, hosted.origin, report, config));
            await captureGpuProcessState(browser, report, 'after-independent-gpu-canary');
        }
        await check(config.requireWorldPlay ? 'engine-world-play-stop' : 'engine-diagnostic-export-boot',
            () => enginePageCheck(browser, hosted.origin, report, config));
        await check('engine-audio-stream-samples', () => audioStreamCheck(browser, hosted.origin, report, config));
        let launchDescriptor;
        try {
            launchDescriptor = JSON.parse(await fs.readFile(path.join(config.browserPublish, 'browser-publish.json'), 'utf8'));
        } catch (error) {
            if (error.code !== 'ENOENT') throw error;
        }
        if (launchDescriptor?.schema === 1 && launchDescriptor.world === null) {
            report.checks.push({ name: 'published-engine-autostart', status: 'skipped',
                reason: 'The bare host contains the frozen reference harness descriptor, with no authored startup world' });
        } else if (launchDescriptor) {
            await check('published-engine-autostart', async () => {
                assert(launchDescriptor.schema === 2 && launchDescriptor.format === 'xrengine-engine-launch' &&
                    launchDescriptor.manifest === './content/manifest.json',
                    'BrowserSmoke.PublishedLaunchDescriptorUnsupported: the supplied publish has no canonical engine launch.');
                await shippingPlayerCheck(browser, hosted.origin, report, config);
            });
        } else report.checks.push({ name: 'published-engine-autostart', status: 'skipped', reason: 'A bare browser runtime publish has no authored launch descriptor' });
        if (config.gamePublish) await check(gameCheckName,
            () => publishedGameCheck(browser, hosted.origin, report, config));
        else report.checks.push({ name: gameCheckName, status: 'skipped',
            reason: '--game-publish was not supplied' });
        if (config.engineManifest) await check('engine-asset-delivery-lifetime', () => assetSourceCheck(browser, hosted.origin, report, config));
        else report.checks.push({ name: 'engine-asset-delivery-lifetime', status: 'skipped', reason: '--engine-manifest was not supplied' });
        if (config.joltSpike) await check('jolt-native-browser-lifetime', () => joltCheck(browser, hosted.origin, report, config));
        else report.checks.push({ name: 'jolt-native-browser-lifetime', status: 'skipped', reason: '--jolt-spike was not supplied' });
        await check('local-delivery', async () => {
            assert(report.externalRequests.length === 0, 'BrowserSmoke.ExternalRequest: the published application requested resources outside the loopback roots.');
            assert(!report.requests.some(request => request.status >= 400), 'BrowserSmoke.HttpFailure: a served resource request failed; inspect requests in smoke-report.json.');
        });
        report.passed = report.checks.every(check => check.status !== 'failed');
    } catch (error) {
        report.error = String(error);
        report.stack = error.stack;
        console.error(error);
    } finally {
        if (browser) {
            if (report.advancedRenderingFailures?.length)
                await captureGpuProcessState(browser, report, 'after-failed-advanced-application');
            report.browserCloseRequested = true;
            await (config.uiFrameTrace ? closeUiTraceBrowser(browser) : browser.close())
                .catch(error => { report.cleanupError = String(error); report.passed = false; });
        }
        if (origin && !report.cleanupError && report.advancedRenderingFailures?.length) {
            try { await runNativeCompileIsolation(chromium, origin, report, config, instrumentedPage); }
            catch (error) {
                if (!report.nativeCompileIsolation?.ownedGpuProfile?.requiresJobTermination) throw error;
            }
        }
        if (report.nativeCompileIsolation?.ownedGpuProfile?.requiresJobTermination) {
            report.passed = false;
            report.ownedProfileForcedTermination = true;
            report.finishedUtc = new Date().toISOString();
            let deadline;
            try {
                server?.closeAllConnections();
                server?.close();
                await Promise.race([
                    fs.writeFile(path.join(config.output, 'smoke-report.json'), JSON.stringify(report, null, 2)),
                    new Promise((_, reject) => {
                        deadline = setTimeout(() => reject(new Error('Owned profile failure report deadline.')), 2000);
                    }),
                ]);
            } catch {
                // An unverified privileged child must not keep the diagnostic alive
                // while reporting or unrelated cleanup waits for more work.
            } finally {
                clearTimeout(deadline);
                process.exit(86);
            }
        }
        if (server) await new Promise(resolve => server.close(resolve));
        report.finishedUtc = new Date().toISOString();
        await fs.writeFile(path.join(config.output, 'smoke-report.json'), JSON.stringify(report, null, 2));
        await fs.writeFile(path.join(config.output, 'browser-console.json'), JSON.stringify(report.browserLogs, null, 2));
        console.log(JSON.stringify({ passed: report.passed, qualification: report.qualification, checks: report.checks,
            error: report.error, report: 'smoke-report.json' }, null, 2));
        if (!report.passed) process.exitCode = 1;
    }
}

await main().catch(error => { console.error(error); process.exitCode = 1; });
