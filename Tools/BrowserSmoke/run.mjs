import fs from 'node:fs/promises';
import { createReadStream } from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { pipeline } from 'node:stream/promises';
import { createRequire } from 'node:module';
import { chromium } from 'playwright';
import { readConfig, browserLaunchOptions, depthSamples, help } from './smoke.config.mjs';

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
        { prefix: '/__shaders/', root: await directory(config.shaderArtifacts) },
        ...(config.joltSpike ? [{ prefix: '/__jolt/', root: await directory(config.joltSpike) }] : []),
        { prefix: '/', root: await directory(config.browserPublish) },
    ];
    let authority;
    const server = http.createServer(async (request, response) => {
        let pathname = '<invalid>', status = 500;
        try {
            if (request.headers.host !== authority) { status = 403; throw new Error('Invalid host'); }
            if (!['GET', 'HEAD'].includes(request.method)) { status = 405; throw new Error('Read-only server'); }
            pathname = decodeURIComponent(new URL(request.url, `http://${authority}`).pathname);
            if (pathname === '/favicon.ico') { status = 204; response.writeHead(status); response.end(); return; }
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
        await page.screenshot({ path: path.join(config.output, 'engine-depth-failure.png'), fullPage: true }).catch(() => {});
        throw error;
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

async function main() {
    const config = readConfig();
    if (config.help) { console.log(help); return; }
    config.output = path.resolve(config.output);
    await fs.mkdir(config.output, { recursive: true });
    const report = { schemaVersion: 1, passed: false, startedUtc: new Date().toISOString(),
        node: process.version, platform: process.platform, architecture: process.arch,
        playwright: require('playwright/package.json').version, gpuMode: config.gpuMode,
        executable: config.executablePath ? path.basename(config.executablePath) : 'playwright-managed-chromium',
        browserLogs: {}, externalRequests: [], requests: [], checks: [],
        scope: 'Renderer diagnostic correctness and selected runtime smoke checks; not full browser, desktop, physical-device or performance acceptance.' };
    let browser, server;
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
        report.launchArguments = browserLaunchOptions(config).args;
        browser = await chromium.launch(browserLaunchOptions(config));
        report.browser = browser.version();
        if (config.engineManifest) await check('engine-depth', () => depthCheck(browser, hosted.origin, report, config));
        else report.checks.push({ name: 'engine-depth', status: 'skipped', reason: '--engine-manifest was not supplied' });
        await check(config.requireWorldPlay ? 'engine-world-play-stop' : 'engine-diagnostic-export-boot',
            () => enginePageCheck(browser, hosted.origin, report, config));
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
        if (browser) await browser.close().catch(error => { report.cleanupError = String(error); report.passed = false; });
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
