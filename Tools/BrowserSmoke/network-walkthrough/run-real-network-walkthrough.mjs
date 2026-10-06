// The PowerShell wrapper owns the one-run approval/certificate lifecycle.
// This file deliberately keeps API bearers and delivered handoffs in memory.
import fs from 'node:fs/promises';
import { writeFileSync } from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { randomBytes, randomUUID } from 'node:crypto';
import { createRequire } from 'node:module';
import { parseArgs } from 'node:util';
import { pathToFileURL } from 'node:url';

const { values } = parseArgs({ options: {
    repo: { type: 'string' }, run: { type: 'string' }, site: { type: 'string' },
    manifest: { type: 'string' }, server: { type: 'string' }, service: { type: 'string' },
    thumbprint: { type: 'string' },
    'preflight-only': { type: 'boolean', default: false },
} });
const preflightOnly = values['preflight-only'];
for (const name of ['repo', 'run', 'site', 'manifest', ...(preflightOnly ? [] : ['server', 'service', 'thumbprint'])])
    if (!values[name]) throw new Error(`Missing public parameter ${name}`);
const { chromium } = createRequire(path.join(values.repo, 'Tools', 'BrowserSmoke', 'package.json'))('playwright');
const { browserLaunchOptions } = await import(pathToFileURL(path.join(values.repo, 'Tools', 'BrowserSmoke', 'smoke.config.mjs')));
const site = await fs.realpath(values.site);
const manifest = JSON.parse(await fs.readFile(values.manifest, 'utf8'));
const catalog = JSON.parse(await fs.readFile(path.join(site, 'content', 'manifest.json'), 'utf8'));
if (manifest.worldEntryPoint !== 'World.asset' || manifest.metadata?.browserStartupWorld !== '/game/World.asset'
    || catalog.worldPackage !== 'world-package.json' || catalog.startupWorld !== '/game/World.asset'
    || typeof manifest.manifestHash !== 'string' || typeof manifest.asset?.contentHash !== 'string'
    || manifest.asset.contentHash.toLowerCase() !== manifest.manifestHash.toLowerCase()
    || !manifest.packageId || !manifest.buildVersion) throw new Error('Published shared package is incomplete');

// The PowerShell supervisor independently kills this exact contained job at eight
// minutes. This timer and bounded evaluations also handle hung Playwright callbacks.
const liveDeadline = Date.now() + (preflightOnly ? 150000 : 420000);
let cleanupDeadline = null;
const hardExit = setTimeout(() => process.exit(124), preflightOnly ? 175000 : 475000);
class WalkthroughFailure extends Error { }
const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
const remaining = () => Math.max(1, (cleanupDeadline ?? liveDeadline) - Date.now());
async function bounded(label, work, limit = 10000) {
    let timeout;
    try {
        return await Promise.race([Promise.resolve().then(work), new Promise((_, reject) => {
            timeout = setTimeout(() => reject(new WalkthroughFailure(label + 'Timeout')), Math.min(limit, remaining()));
        })]);
    } finally { clearTimeout(timeout); }
}
async function gpuSandboxProof(browser) {
    let session, finished = false;
    const detach = attached => bounded('BrowserProofDetach', () => attached.detach(), 750).catch(() => null);
    try {
        return await bounded('BrowserProof', async () => {
            const attached = await browser.newBrowserCDPSession();
            if (finished) { await detach(attached); return null; }
            session = attached;
            const systemInfo = await session.send('SystemInfo.getInfo');
            const sandboxed = systemInfo?.gpu?.auxAttributes?.sandboxed;
            if (typeof sandboxed !== 'boolean')
                throw new WalkthroughFailure('BrowserProofUnavailable');
            return { gpuProcessSandboxed: sandboxed };
        }, 5000);
    } catch (error) {
        if (error instanceof WalkthroughFailure) throw error;
        throw new WalkthroughFailure('BrowserProofUnavailable');
    } finally {
        finished = true;
        if (session) await detach(session);
    }
}
// Do not inherit runner credentials, tracing, crash-dump switches, NODE_OPTIONS,
// Playwright DEBUG settings, proxy credentials, or legacy XRE_* overrides.
const allowedEnvironment = new Set(['SYSTEMROOT','WINDIR','PATH','PATHEXT','TEMP','TMP',
    'USERPROFILE','LOCALAPPDATA','APPDATA','PROGRAMDATA','PROGRAMFILES','PROGRAMFILES(X86)',
    'COMMONPROGRAMFILES','COMMONPROGRAMFILES(X86)','COMSPEC','DOTNET_ROOT','DOTNET_ROOT_X64']);
const childEnvironment = Object.fromEntries(Object.entries(process.env)
    .filter(([key]) => allowedEnvironment.has(key.toUpperCase())));
function requireCondition(condition, code) { if (!condition) throw new WalkthroughFailure(code); }
// Classify the retained cold failure inside the page. Only fixed labels cross
// the Playwright boundary; exception text, stacks, paths, and URLs stay private.
function classifyColdBrowserFailure() {
    const message = globalThis.__networkFixtureHost?.failure?.message;
    const result = { present: typeof message === 'string', startupStage: 'other',
        exceptionKind: 'other', engineCondition: 'other' };
    if (typeof message !== 'string') return result;
    const stages = new Set(['admit startup', 'open asset catalog', 'install published type metadata',
        'install runtime asset services', 'initialize game registrations', 'load shader catalog',
        'preload default UI font', 'load startup world', 'bind authored UI fonts',
        'load startup settings', 'hydrate essential roots', 'configure game bootstrap',
        'start engine world']);
    const startup = /^(?:System\.InvalidOperationException: )?BrowserEngine\.StartupFailed \[([^\]\r\n]{1,64})\]: ([A-Za-z.]{1,64})(?::|$)/.exec(message);
    if (startup) {
        if (stages.has(startup[1])) result.startupStage = startup[1];
        const kinds = new Map([
            ['System.InvalidOperationException', 'InvalidOperation'],
            ['System.IO.InvalidDataException', 'InvalidData'],
            ['System.InvalidDataException', 'InvalidData'],
            ['System.NotSupportedException', 'NotSupported'],
            ['System.OperationCanceledException', 'OperationCanceled'],
            ['System.IO.FileNotFoundException', 'FileNotFound'],
            ['System.AggregateException', 'Aggregate'],
        ]);
        result.exceptionKind = kinds.get(startup[2]) ?? 'other';
    }
    const conditions = [
        ['PublishedMetadataMissing', 'PublishedMetadata.Missing:'],
        ['BrowserJoltMissing', 'Browser Jolt physics is not installed.'],
        ['WorldPackageInvalid', 'AssetSource.WorldPackageInvalid:'],
        ['StartupWorldMissing', 'AssetSource.StartupWorldMissing:'],
        ['CanvasSessionUnavailable', 'WebGPU.EngineCanvas.SessionUnavailable:'],
        ['DeviceAcquisitionTimeout', 'WebGPU device acquisition exceeded 20000 ms.'],
        ['WebGpuUnavailable', 'WebGPU requires a secure context and navigator.gpu.'],
        ['AdapterUnavailable', 'No WebGPU adapter is available.'],
        ['CanvasContextUnavailable', 'WebGPU canvas context is unavailable.'],
        ['CanvasFormatUnsupported', 'WebGPU.EngineCanvas.FormatUnsupported:'],
    ];
    for (const [kind, marker] of conditions)
        if (message.includes(marker)) { result.engineCondition = kind; break; }
    return result;
}
async function until(label, milliseconds, sample, good) {
    const expires = Math.min(Date.now() + milliseconds, cleanupDeadline ?? liveDeadline);
    while (Date.now() < expires) {
        const result = await bounded(label, sample, Math.max(1, expires - Date.now()));
        if (good(result)) return result;
        await delay(250);
    }
    throw new WalkthroughFailure(`${label}Timeout`);
}
const mime = { '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript',
    '.json': 'application/json', '.wasm': 'application/wasm', '.css': 'text/css',
    '.wgsl': 'text/plain', '.bin': 'application/octet-stream', '.png': 'image/png' };
const pageServer = http.createServer(async (request, response) => {
    try {
        if (request.headers.host !== `127.0.0.1:${pageServer.address().port}` || !['GET', 'HEAD'].includes(request.method)) {
            response.writeHead(403); response.end(); return;
        }
        const url = new URL(request.url, `http://${request.headers.host}`);
        const segments = decodeURIComponent(url.pathname).split('/').filter(Boolean);
        if (segments.some(segment => segment === '..' || segment.startsWith('.') || segment.includes('\\') || segment.includes('\0'))) {
            response.writeHead(403); response.end(); return;
        }
        const candidate = path.join(site, ...(segments.length ? segments : ['index.html']));
        const actual = await fs.realpath(candidate);
        if (!actual.startsWith(site + path.sep)) { response.writeHead(403); response.end(); return; }
        const bytes = await fs.readFile(actual);
        response.writeHead(200, { 'content-type': mime[path.extname(actual).toLowerCase()] ?? 'application/octet-stream',
            'cache-control': 'no-store', 'x-content-type-options': 'nosniff',
            'content-security-policy': "connect-src 'self' blob: data:" + (preflightOnly ? '' : ' wss://localhost:15200/realtime') });
        response.end(request.method === 'HEAD' ? undefined : bytes);
    } catch { response.writeHead(404); response.end(); }
});
await new Promise((resolve, reject) => { pageServer.once('error', reject); pageServer.listen(0, '127.0.0.1', resolve); });
const origin = `http://127.0.0.1:${pageServer.address().port}`;
const adminToken = preflightOnly ? null : randomBytes(32).toString('hex');
const playerToken = preflightOnly ? null : randomBytes(32).toString('hex');
const api = 'http://127.0.0.1:5088';
const gatewayUrl = 'wss://localhost:15200/realtime';
const serviceConfig = path.join(values.run, 'service-config.json');
if (!preflightOnly) await fs.writeFile(serviceConfig, JSON.stringify({
    listenUrl: api, hostId: 'kinematic-single-run', serverExecutable: values.server,
    workingRoot: path.join(values.run, 'private-workers'), preserveWorkersOnAgentCrash: false,
    bindAddress: '127.0.0.1', advertisedHost: 'localhost', firstUdpPort: 15200, lastUdpPort: 15200,
    maxInstances: 1, maxPlayerSlots: 2, workerCpuPercent: 50,
    users: [
        { userId: 'fixture-admin', tokenEnvironmentVariable: 'XRE_LOCAL_ADMIN_TOKEN', isAdministrator: true },
        { userId: 'fixture-player', tokenEnvironmentVariable: 'XRE_LOCAL_PLAYER_TOKEN', isAdministrator: false },
    ],
    packages: { [manifest.packageId]: values.manifest },
    realtimeTls: { listenAddress: '127.0.0.1', certificateThumbprint: values.thumbprint,
        useMachineCertificateStore: false, useWebSocket: true, allowedWebSocketOrigins: [origin],
        maximumConnections: 2, maximumConnectionsPerAddress: 2 },
}));

let service, browser, context, page, instanceId;
const reservations = [];
const result = { result: 'incomplete', packageId: manifest.packageId, buildVersion: manifest.buildVersion,
    pageOrigin: origin, gpuMode: 'software', chromiumSandboxRequested: true,
    webGpuAdapterRequested: 'swiftshader', mode: preflightOnly ? 'preflight-only' : 'real-network',
    certificateMutationReached: !preflightOnly, stage: 'Starting', checks: {}, worker: {} };
function writeResult() { writeFileSync(path.join(values.run, preflightOnly ? 'preflight-result.public.json' : 'nonsecret-result.json'), JSON.stringify(result, null, 2)); }
function stage(name) { result.stage = name; writeResult(); }
const workerFailureCodes = new Set(['OwnershipUnverified', 'ConfigurationFailed', 'LaunchCancelled', 'LaunchFailed',
    'WorkerContractRejected', 'StartupTimeout', 'WorkerLeaseExpired', 'ShutdownTimeout', 'encrypted_ingress_lost', 'management_poll_failed']);
function recordWorker(status) {
    // Keep only bounded protocol enums and numeric counters, never launch config,
    // roster identities, handoff payloads, arbitrary failure messages, or logs.
    if (['Starting', 'Ready', 'Draining', 'Stopping', 'Stopped', 'Failed'].includes(status?.observedState))
        result.worker.observedState = status.observedState;
    result.worker.exitObserved = status?.exitObserved === true;
    if (status?.failureCode) result.worker.failureCode = workerFailureCodes.has(status.failureCode) ? status.failureCode : 'OtherWorkerFailure';
    for (const key of ['simulatedInputCount', 'authoritativeTransformBytes', 'synchronizedPlayers', 'encryptedConnections'])
        if (Number.isSafeInteger(status?.metrics?.[key]) && status.metrics[key] >= 0) result.worker[key] = status.metrics[key];
    if (typeof status?.metrics?.encryptedIngressListening === 'boolean') result.worker.encryptedIngressListening = status.metrics.encryptedIngressListening;
    return status;
}
async function apiRequest(method, route, token, body, optional = false) {
    const response = await fetch(api + route, { method, signal: AbortSignal.timeout(10000),
        headers: { authorization: `Bearer ${token}`, ...(body === undefined ? {} : { 'content-type': 'application/json' }) },
        body: body === undefined ? undefined : JSON.stringify(body) });
    if (optional && response.status === 409) return null;
    requireCondition(response.ok, `ServiceHttp${response.status}`);
    return response.status === 204 ? null : bounded('ServiceBody', () => response.json());
}
const workerStatus = async () => recordWorker(await apiRequest('GET', `/v1/instances/${instanceId}/status`, adminToken));
function statusString() { return bounded('BrowserStatus', () => page.evaluate(() => globalThis.__networkFixtureHost.engine.GetNetworkSimulationStatus()), 3000); }
function parseReady(value) {
    const match = /^network=ready; player=(\d+); clientAcknowledged=(\d+); pose=\(([^,]+),([^,]+),([^,]+)\)$/.exec(value);
    if (!match) return null;
    const pose = match.slice(3).map(Number);
    return pose.every(Number.isFinite) ? { player: Number(match[1]), ack: Number(match[2]), pose } : null;
}
async function readyPose() {
    return until('BrowserReadyPose', 30000, async () => parseReady(await statusString()), value => value !== null);
}
async function reserveAndJoin(clientId) {
    const reservation = await apiRequest('POST', `/v1/instances/${instanceId}/reservations`, playerToken,
        { operationId: randomUUID(), clientId, buildVersion: manifest.buildVersion });
    requireCondition(reservation?.reservationId, 'MissingReservationId');
    reservations.push(reservation.reservationId);
    const route = `/v1/instances/${instanceId}/reservations/${reservation.reservationId}`;
    const launch = await until('HandoffDelivery', 30000,
        () => apiRequest('GET', `${route}/handoff`, playerToken, undefined, true), value => value?.handoff);
    requireCondition(launch.handoff.reservationId === reservation.reservationId, 'WrongHandoffReservation');
    requireCondition(launch.handoff.endpoint?.host === 'localhost' && launch.handoff.endpoint?.port === 15200
        && launch.handoff.endpoint?.transport === 'WebSocket', 'UnexpectedGatewayEndpoint');
    // Only the protocol handoff enters the page. No launch paths, bearer, or credential object.
    await bounded('ConnectHandoff', () => page.evaluate(handoff => globalThis.__networkFixtureHost.engine.ConnectWebSocketAsync(JSON.stringify(handoff), false),
        launch.handoff), 30000);
    const ready = await readyPose();
    const worker = await until('ServerSynchronized', 30000, workerStatus,
        status => status?.metrics?.synchronizedPlayers === 1 &&
            status.roster?.some(entry => entry.reservationId === reservation.reservationId && entry.state === 'Synchronized'));
    requireCondition(worker.metrics.encryptedConnections === 1, 'NoRealEncryptedConnection');
    return { reservationId: reservation.reservationId, ready, worker };
}

let exitCode = 1;
try {
    if (!preflightOnly) {
        stage('ServiceStart');
        service = spawn(values.service, ['--config', serviceConfig], { cwd: values.repo, windowsHide: true,
            env: { ...childEnvironment, XRE_LOCAL_ADMIN_TOKEN: adminToken, XRE_LOCAL_PLAYER_TOKEN: playerToken },
            stdio: 'ignore' });
        let serviceSpawnError = null;
        service.once('error', error => { serviceSpawnError = error; });
        await until('ServiceReady', 15000, async () => {
            if (serviceSpawnError || service.exitCode !== null || service.signalCode !== null) throw new WalkthroughFailure('ServiceExited');
            try { return (await fetch(`${api}/health`, { signal: AbortSignal.timeout(1000) })).ok; }
            catch { return false; }
        }, Boolean);
        stage('CreateInstance');
        const created = await apiRequest('POST', '/v1/instances', playerToken, {
            operationId: randomUUID(), packageId: manifest.packageId, displayName: 'Network kinematic validation',
            maxPlayers: 2, isPublic: false });
        instanceId = created.instanceId;
        requireCondition(instanceId, 'MissingInstanceId');
        stage('WorkerReady');
        await until('RealWorkerReady', 100000, workerStatus,
            status => status?.observedState === 'Ready' && status?.metrics?.encryptedIngressListening === true && status.processId > 0);
    }

    stage('BrowserLaunch');
    const launchOptions = browserLaunchOptions({ gpuMode: 'software', headed: false, gpuDiagnostics: false });
    browser = await bounded('BrowserLaunch', () => chromium.launch({
        ...launchOptions,
        args: [...launchOptions.args, '--use-webgpu-adapter=swiftshader'],
        chromiumSandbox: true,
        timeout: 30000, env: childEnvironment }), 35000);
    result.browserVersion = browser.version();
    context = await bounded('BrowserContext', () => browser.newContext({ viewport: { width: 1280, height: 900 },
        serviceWorkers: 'block' })); // normal TLS validation; no persistent context/tracing
    context.setDefaultTimeout(10000);
    context.setDefaultNavigationTimeout(60000);
    await context.route('**/*', route => {
        const url = new URL(route.request().url());
        if (url.origin === origin || ['blob:', 'data:'].includes(url.protocol)
            || !preflightOnly && (url.href === gatewayUrl || url.href === gatewayUrl.replace('wss:', 'https:'))) return route.continue();
        return route.abort('blockedbyclient');
    });
    page = await context.newPage();
    let unexpectedSocket = false;
    let observedGateway = false;
    // Observation only: no WebSocket proxy or custom handshake substitutes for
    // Chromium's normal trusted WSS connection.
    page.on('websocket', socket => {
        if (socket.url() === gatewayUrl) observedGateway = true;
        else unexpectedSocket = true;
    });
    let observedPlayer = false;
    await page.route(`${origin}/engine-player.js`, async route => {
        await page.locator('#input-surface').waitFor({ state: 'attached', timeout: 5000 });
        await bounded('InstallHostObserver', () => page.evaluate(async () => {
            const { EngineCanvasHost } = await import('./engine-canvas-host.js');
            const original = EngineCanvasHost.prototype.start;
            EngineCanvasHost.prototype.start = function (...args) {
                globalThis.__networkFixtureHost = this;
                return original.apply(this, args);
            };
        }), 5000);
        observedPlayer = true;
        await route.continue();
    }, { times: 1 });
    stage('PublishedPageLoad');
    await page.goto(`${origin}/index.html`, { waitUntil: 'domcontentloaded' });
    requireCondition(observedPlayer, 'PreEntryObserverMissed');
    stage('PublishedFirstFrame');
    await page.waitForFunction(() => {
        const host = globalThis.__networkFixtureHost;
        if (document.querySelector('#status')?.dataset.state === 'failed') throw new Error('PublishedWorldFailed');
        return host?.session > 0 && host.rendererReady && !host.failed && host.presented === true
            && host.engine.HasPresentedCanvasFrame() === true && host.engine.GetCanvasPreparationState() === 1
            && document.querySelector('#status')?.dataset.state === 'running';
    }, null, { timeout: 60000 });
    result.checks.publishedFirstFrame = true;
    stage('BrowserEnvironmentProof');
    const adapter = await bounded('BrowserAdapterProof', () => page.evaluate(() => {
        const info = globalThis.__networkFixtureHost?.renderer?.device?.adapterInfo;
        if (!info) return { adapter: 'unavailable' };
        const fields = [info.description, info.device, info.vendor, info.architecture];
        const adapter = fields.some(field => typeof field === 'string' && /swiftshader/i.test(field))
            ? 'swiftshader' : 'other';
        return { adapter, ...(typeof info.isFallbackAdapter === 'boolean'
            ? { fallback: info.isFallbackAdapter } : {}) };
    }), 3000);
    result.checks.browserEnvironment = { ...adapter };
    Object.assign(result.checks.browserEnvironment, await gpuSandboxProof(browser));
    writeResult();
    requireCondition(adapter.adapter === 'swiftshader', 'WebGpuAdapterNotSwiftShader');
    requireCondition(result.checks.browserEnvironment.gpuProcessSandboxed === true, 'BrowserGpuSandboxNotProven');
    if (preflightOnly) requireCondition(!observedGateway && !unexpectedSocket, 'UnexpectedPreflightSocket');
    if (!preflightOnly) {
        stage('FirstJoin');
        const first = await reserveAndJoin(`browser-${randomUUID()}`);
        requireCondition(observedGateway && !unexpectedSocket, 'UnexpectedBrowserSocket');
        result.checks.firstReady = true;
        const before = first.ready;
        const beforeServer = first.worker.metrics;
        stage('KeyboardMovement');
        await page.locator('#input-surface').focus();
        await page.keyboard.down('w');
        let moving;
        try {
            moving = await until('KeyboardMovement', 5000, readyPose,
                sample => sample.ack > before.ack && sample.pose[2] < before.pose[2] - 0.25);
        } finally { await page.keyboard.up('w'); }
        const afterServer = await until('AuthoritativeSimulation', 10000, workerStatus,
            sample => sample?.metrics?.simulatedInputCount > beforeServer.simulatedInputCount &&
                sample.metrics.authoritativeTransformBytes > beforeServer.authoritativeTransformBytes);
        await delay(700);
        const released = await readyPose();
        requireCondition(Math.abs(released.pose[2] - moving.pose[2]) < 1.5, 'MovementDidNotSettle');
        result.checks.movement = { zBefore: before.pose[2], zDuring: moving.pose[2], zAfterRelease: released.pose[2],
            acknowledgedBefore: before.ack, acknowledgedAfter: released.ack,
            simulatedInputsBefore: beforeServer.simulatedInputCount,
            simulatedInputsAfter: afterServer.metrics.simulatedInputCount,
            transformBytesBefore: beforeServer.authoritativeTransformBytes,
            transformBytesAfter: afterServer.metrics.authoritativeTransformBytes };

        stage('SuspendAndFreshJoin');
        await bounded('SuspendNetwork', () => page.evaluate(() => globalThis.__networkFixtureHost.engine.SuspendNetwork()));
        requireCondition((await statusString()) === 'network=inactive', 'RetiredManagerStillVisible');
        await apiRequest('DELETE', `/v1/instances/${instanceId}/reservations/${first.reservationId}`, playerToken);
        await until('OldReservationGone', 15000, workerStatus,
            sample => !sample?.roster?.some(entry => entry.reservationId === first.reservationId));
        const second = await reserveAndJoin(`browser-${randomUUID()}`);
        requireCondition(second.reservationId !== first.reservationId, 'OldHandoffReused');
        result.checks.freshJoin = { newReservation: true, secondReady: true,
            secondPlayer: second.ready.player, oldManagerInactive: true };
    }
    result.result = 'checks-passed-awaiting-cleanup';
    stage('ChecksCompleted');
    exitCode = 0;
} catch (error) {
    result.result = 'failed';
    result.failedStage = result.stage;
    // Only local assertion labels are written. Never serialize arbitrary exception data.
    result.failure = error instanceof WalkthroughFailure ? error.message : 'WalkthroughFailed';
    if (Number.isInteger(service?.exitCode)) result.serviceExitCode = service.exitCode;
    if (page) {
        result.browser = await bounded('FailureReadiness', () => page.evaluate(() => {
            const host = globalThis.__networkFixtureHost;
            const pageState = document.querySelector('#status')?.dataset.state;
            const networkState = host?.engine?.GetNetworkState();
            return { pageState: ['running', 'loading', 'failed', 'suspended', 'recovering'].includes(pageState) ? pageState : 'other',
                networkState: ['local', 'connecting', 'authenticating', 'synchronizing', 'ready', 'suspended', 'failed'].includes(networkState) ? networkState : 'other',
                hostPresent: !!host, rendererReady: host?.rendererReady === true, hostFailed: host?.failed === true,
                sessionActive: host?.session > 0 };
        }), 1000).catch(() => ({ observationUnavailable: true }));
        result.browser.coldFailure = await bounded('FailureClass', () => page.evaluate(classifyColdBrowserFailure), 1000)
            .catch(() => ({ observationUnavailable: true }));
    }
    writeResult();
} finally {
    cleanupDeadline = Date.now() + 45000;
    const cleanupStep = (label, work, limit = 3000) => bounded(label, work, limit).catch(() => null);
    if (page) await cleanupStep('ReleaseKeyboard', () => page.keyboard.up('w'));
    if (page) await cleanupStep('SuspendNetwork', () => page.evaluate(() => globalThis.__networkFixtureHost?.engine.SuspendNetwork()));
    if (instanceId) {
        for (const id of reservations)
            await cleanupStep('RevokeReservation', () => apiRequest('DELETE', '/v1/instances/' + instanceId + '/reservations/' + id, playerToken));
        await cleanupStep('StopWorker', () => apiRequest('POST', '/v1/instances/' + instanceId + '/stop', adminToken));
        const stopped = await cleanupStep('ObserveWorker', () => until('OwnedWorkerExit', 15000, workerStatus,
            status => status?.exitObserved === true), 16000);
        result.cleanup = { workerExitObserved: stopped?.exitObserved === true };
        if (!stopped) { result.result = 'failed'; result.failure = 'OwnedWorkerExitUnconfirmed'; exitCode = 1; }
    }
    const contextClosed = !context || await cleanupStep('CloseContext', async () => { await context.close(); return true; }) === true;
    const browserClosed = !browser || await cleanupStep('CloseBrowser', async () => { await browser.close(); return true; }) === true;
    result.cleanup = { ...result.cleanup, contextClosed, browserClosed };
    if (!contextClosed || !browserClosed) { result.result = 'failed'; result.failure = 'BrowserExitUnconfirmed'; exitCode = 1; }
    pageServer.closeAllConnections();
    await cleanupStep('ClosePageServer', () => new Promise(resolve => pageServer.close(resolve)));
    // The parent job owns every service/worker/browser descendant and checks zero
    // members. A ChildProcess also retains this exact service child relationship.
    if (service && service.exitCode === null && service.signalCode === null) {
        service.kill();
        await cleanupStep('ServiceExit', () => until('ServiceExit', 5000,
            () => service.exitCode !== null || service.signalCode !== null, Boolean), 5500);
    }
    result.cleanup = { ...result.cleanup, serviceExitObserved: !service || service.exitCode !== null || service.signalCode !== null };
    if (!result.cleanup.serviceExitObserved) { result.result = 'failed'; result.failure = 'ServiceExitUnconfirmed'; exitCode = 1; }
    // ONLY the parent removes private files, after its job has zero active members.
    result.result = exitCode === 0 ? 'passed' : 'failed';
    writeResult();
}
clearTimeout(hardExit);
process.exit(exitCode);
