import fs from 'node:fs/promises';
import { constants } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';

// Activation requires a separate review. A local command-line flag is insufficient.
const authorization = Object.freeze({ enabled: false, requestId: '7c47e15a-8186-430c-bc15-56736d2d8a6f',
    activationPath: '.github/diagnostic-activations/ui-frame-trace-20261006.json',
    priorCommit: '1692757ed2bd6354c2d4a27a12ee5bd4cf98026b' });
export function getUiFrameTraceAuthorization() { return authorization; }
const categories = Object.freeze(['gpu.dawn', 'gpu', 'viz', 'cc', 'blink']);
const limits = Object.freeze({ recordingMs: 30000, drainMs: 5000, inputBytes: 16 * 1024 * 1024,
    summaryBytes: 1024 * 1024, records: 4096, bufferKiB: 4096, chunkBytes: 65536 });
const phases = new Set(['B', 'E', 'X', 'b', 'e', 'n', 'i', 'I', 's', 't', 'f', 'S', 'T', 'F', 'C', 'P', 'M', 'R', 'N', 'O', 'D']);
const eventClasses = new Map([
    ['CreatePipelineAsyncEvent::InitializeAsync', 'pipeline-compile'],
    ['CreatePipelineAsyncEvent::InitializeImpl', 'pipeline-compile'],
    ['ShaderModuleVk::GetHandleAndSpirv', 'shader-compile'],
    ['tint::spirv::writer::Generate()', 'shader-compile'],
    ['vkCreateShaderModule', 'shader-compile'],
    ['Queue::Submit', 'gpu-submit'], ['QueueVk::SubmitImpl', 'gpu-submit'],
    ['CommandBufferService::Flush', 'gpu-submit'], ['GpuCommandBufferStub::OnAsyncFlush', 'gpu-submit'],
    ['FireAnimationFrame', 'animation-frame'], ['AnimationFrame::Fire', 'animation-frame'],
    ['PageAnimator::Animate', 'animation-frame'], ['WebViewImpl::BeginMainFrame', 'animation-frame'],
    ['BeginMainThreadFrame', 'compositor-frame'], ['BeginFrame', 'compositor-frame'],
    ['Scheduler::BeginFrame', 'compositor-frame'], ['Display::DrawAndSwap', 'compositor-frame'],
    ['DrawFrame', 'compositor-frame'], ['SubmitCompositorFrame', 'compositor-frame'],
    ['ThreadControllerImpl::RunTask', 'task'], ['ThreadControllerImpl::RunTaskImpl', 'task'], ['RunTask', 'task'],
]);
const permits = new WeakSet();
const launchedPermits = new WeakSet();
const ownedBrowsers = new WeakMap();
let claimAttempted = false;
const fail = code => { throw new Error(`BrowserSmoke.UiFrameTrace: ${code}`); };
const requireTrace = (condition, code) => { if (!condition) fail(code); };

async function readSmall(file, maximum, privateOwner = false) {
    const handle = await fs.open(file, constants.O_RDONLY | constants.O_NONBLOCK | constants.O_NOFOLLOW | constants.O_CLOEXEC);
    try {
        const stat = await handle.stat();
        requireTrace(stat.isFile() && stat.size <= maximum && (!privateOwner ||
            (stat.uid === process.getuid() && !(stat.mode & 0o022))), 'IdentityFileRejected');
        const bytes = Buffer.alloc(maximum + 1);
        const { bytesRead } = await handle.read(bytes, 0, bytes.length, 0);
        requireTrace(bytesRead <= maximum, 'IdentityByteLimit');
        return bytes.subarray(0, bytesRead);
    } finally { await handle.close(); }
}

/** Consume one reviewed push authorization before an ordinary browser can be opened. */
export async function claimUiFrameTrace(config) {
    if (!config.uiFrameTrace) return null;
    requireTrace(!claimAttempted, 'AlreadyClaimed');
    claimAttempted = true;
    requireTrace(authorization.enabled && /^[0-9a-f]{40}$/.test(authorization.priorCommit), 'Dormant');
    requireTrace(config.gameOnly === true && config.gameKind === 'ui-parity' && config.gpuMode === 'software'
        && config.timeout === 180000 && !config.nativeCompileTrace && !config.nativeOwnedProfileOnce
        && !config.gpuDiagnostics && !config.headed && !config.executablePath, 'ScopeRejected');
    requireTrace(process.platform === 'linux' && Number.isSafeInteger(process.getuid?.())
        && process.getuid() > 0 && process.getuid() === process.geteuid?.(), 'RunnerIdentityRejected');
    requireTrace(process.env.DEBUG === 'pw:browser' && !process.env.SELENIUM_REMOTE_URL
        && (!process.env.PWDEBUG || process.env.PWDEBUG === '0'), 'BrowserEnvironmentRejected');
    const exact = { GITHUB_ACTIONS: 'true', GITHUB_EVENT_NAME: 'push', GITHUB_RUN_ATTEMPT: '1',
        GITHUB_REPOSITORY: 'BlackJaxDev/XRENGINE', GITHUB_REF: 'refs/heads/codex/webgpu-readiness-audit',
        GITHUB_WORKFLOW: 'Portable browser build and publish', GITHUB_JOB: 'published-game', GAME_KIND: 'ui-parity',
        GITHUB_WORKFLOW_REF: 'BlackJaxDev/XRENGINE/.github/workflows/portable-browser-compile.yml@refs/heads/codex/webgpu-readiness-audit' };
    for (const [key, value] of Object.entries(exact)) requireTrace(process.env[key] === value, 'WorkflowGateRejected');
    const triggerCommit = process.env.GITHUB_SHA, workflowRunId = process.env.GITHUB_RUN_ID;
    requireTrace(/^[0-9a-f]{40}$/.test(triggerCommit ?? '') && triggerCommit !== authorization.priorCommit
        && /^[1-9]\d{0,19}$/.test(workflowRunId ?? '') && process.env.GITHUB_WORKFLOW_SHA === triggerCommit,
    'WorkflowIdentityRejected');
    const root = await fs.realpath(fileURLToPath(new URL('../..', import.meta.url)));
    requireTrace(await fs.realpath(process.env.GITHUB_WORKSPACE ?? '') === root, 'WorkspaceRejected');
    const event = JSON.parse((await readSmall(process.env.GITHUB_EVENT_PATH, 256 * 1024)).toString('utf8'));
    requireTrace(event.forced === false && event.created === false && event.deleted === false
        && event.before === authorization.priorCommit && event.after === triggerCommit && event.ref === exact.GITHUB_REF
        && event.repository?.full_name === exact.GITHUB_REPOSITORY, 'PushRejected');
    const activationFile = process.env.XRE_UI_FRAME_TRACE_ACTIVATION_FILE;
    requireTrace(typeof activationFile === 'string' && path.isAbsolute(activationFile), 'ActivationMissing');
    const temporaryRoot = await fs.realpath(process.env.RUNNER_TEMP ?? '');
    const temporaryStat = await fs.stat(temporaryRoot);
    requireTrace(temporaryStat.isDirectory() && temporaryStat.uid === process.getuid() && !(temporaryStat.mode & 0o022),
        'TemporaryDirectoryRejected');
    const activationPath = await fs.realpath(activationFile);
    requireTrace(activationPath.startsWith(`${temporaryRoot}${path.sep}`), 'ActivationPathRejected');
    const activationCommit = process.env.XRE_UI_FRAME_TRACE_ACTIVATION_COMMIT;
    const activationSha256 = process.env.XRE_UI_FRAME_TRACE_ACTIVATION_SHA256;
    requireTrace(/^[0-9a-f]{40}$/.test(activationCommit ?? '') && /^[0-9a-f]{64}$/.test(activationSha256 ?? ''),
        'ActivationSourceRejected');
    const activationBytes = await readSmall(activationPath, 4096, true);
    requireTrace(createHash('sha256').update(activationBytes).digest('hex') === activationSha256, 'ActivationHashRejected');
    const activation = JSON.parse(activationBytes.toString('utf8'));
    requireTrace(activation.schema === 1 && activation.requestId === authorization.requestId
        && activation.activationPath === authorization.activationPath && activation.priorCommit === authorization.priorCommit
        && activation.triggerCommit === triggerCommit && activation.workflowRunId === workflowRunId
        && activation.runAttempt === 1 && activation.gameKind === 'ui-parity'
        && activation.repository === exact.GITHUB_REPOSITORY && activation.ref === exact.GITHUB_REF
        && activation.workflowPath === '.github/workflows/portable-browser-compile.yml'
        && activation.job === exact.GITHUB_JOB, 'ActivationRejected');
    const permit = Object.freeze({ requestId: authorization.requestId, activationCommit, activationSha256,
        priorCommit: authorization.priorCommit, triggerCommit, workflowRunId, runAttempt: 1 });
    // Leave this small identity-only claim in place after success, failure or interruption.
    const marker = await fs.open(path.join(temporaryRoot, `ui-frame-trace-consumed-${authorization.requestId}-${workflowRunId}`),
        constants.O_WRONLY | constants.O_CREAT | constants.O_EXCL | constants.O_NOFOLLOW | constants.O_CLOEXEC, 0o600);
    try { await marker.writeFile(JSON.stringify(permit)); await marker.sync(); }
    finally { await marker.close(); }
    permits.add(permit);
    return permit;
}

/** Keep closed event classes and scalar timing data. Never copy event names or arguments. */
export function summarizeUiFrameTrace(bytes) {
    requireTrace(Buffer.isBuffer(bytes) && bytes.length <= limits.inputBytes, 'InputLimit');
    const parsed = JSON.parse(bytes.toString('utf8'));
    const events = Array.isArray(parsed) ? parsed : parsed?.traceEvents;
    requireTrace(Array.isArray(events), 'TraceShape');
    const summary = emptySummary();
    addEvents(summary, events);
    return summary;
}

function emptySummary() {
    return { eventCount: 0, recordCount: 0, omittedRecords: 0, invalidEvents: 0,
        categoryCounts: {}, classCounts: {}, phaseCounts: {}, records: [] };
}

function addEvents(summary, events) {
    summary.eventCount += events.length;
    for (const event of events) {
        if (!event || typeof event !== 'object' || Array.isArray(event)) { summary.invalidEvents++; continue; }
        const eventCategories = typeof event.cat === 'string' && event.cat.length <= 256 ? event.cat.split(',') : [];
        const category = categories.find(value => eventCategories.includes(value)) ?? 'other';
        const eventClass = eventClasses.get(event.name) ?? 'other';
        const phase = phases.has(event.ph) ? event.ph : 'other';
        summary.categoryCounts[category] = (summary.categoryCounts[category] ?? 0) + 1;
        summary.classCounts[eventClass] = (summary.classCounts[eventClass] ?? 0) + 1;
        summary.phaseCounts[phase] = (summary.phaseCounts[phase] ?? 0) + 1;
        if (summary.records.length === limits.records) { summary.omittedRecords++; continue; }
        const record = { eventClass, category, phase };
        for (const field of ['pid', 'tid'])
            if (Number.isSafeInteger(event[field]) && event[field] >= 0) record[field] = event[field];
        for (const field of ['ts', 'dur'])
            if (Number.isFinite(event[field]) && event[field] >= 0) record[field] = event[field];
        summary.records.push(record);
    }
    summary.recordCount = summary.records.length;
}

/** Count an upper bound without making a second raw JSON string. */
function traceInputBytes(value, maximum) {
    let remaining = maximum;
    const charge = bytes => { remaining -= bytes; if (remaining < 0) throw new Error('InputLimit'); };
    function visit(item, depth) {
        if (depth > 64) throw new Error('Batch');
        if (item === null) { charge(4); return; }
        if (typeof item === 'string') { charge(2 + item.length * 6); return; }
        if (typeof item === 'number') { charge(32); return; }
        if (typeof item === 'boolean') { charge(5); return; }
        if (typeof item !== 'object') throw new Error('Batch');
        charge(2);
        if (Array.isArray(item)) {
            if (item.length > remaining) throw new Error('InputLimit');
            for (const entry of item) { charge(1); visit(entry, depth + 1); }
        } else {
            for (const key in item) {
                if (!Object.hasOwn(item, key)) continue;
                charge(4 + key.length * 6);
                visit(item[key], depth + 1);
            }
        }
    }
    charge(1024);
    visit(value, 0);
    return maximum - remaining;
}

async function bounded(action, milliseconds) {
    if (milliseconds <= 0) throw new Error('Budget');
    let timer;
    try {
        return await Promise.race([Promise.resolve().then(action), new Promise((_, reject) => {
            timer = setTimeout(() => reject(new Error('Budget')), milliseconds);
        })]);
    } finally { clearTimeout(timer); }
}

/** Retain the exact child process for bounded, unprivileged trace cleanup. */
export async function launchUiTraceBrowser(chromium, options, permit) {
    requireTrace(permits.has(permit) && !launchedPermits.has(permit), 'LaunchPermitRejected');
    launchedPermits.add(permit);
    const server = await chromium.launchServer({ ...options, host: '127.0.0.1', port: 0 });
    const child = server.process();
    let exited = child.exitCode !== null || child.signalCode !== null, resolveExit;
    const exit = new Promise(resolve => { resolveExit = resolve; });
    const onExit = () => { exited = true; resolveExit(); };
    if (exited) resolveExit();
    else child.once('exit', onExit);
    const owner = { permit, exit,
        async kill(deadline) {
            // The supported API kills only this server's process group. Exit is
            // checked separately because temporary-directory cleanup can take longer.
            if (!exited) void server.kill().catch(() => {});
            await bounded(() => exit, deadline - performance.now());
            requireTrace(exited, 'BrowserExitUnverified');
            child.off('exit', onExit);
        },
        async close(deadline) {
            if (!exited) {
                void server.close().catch(() => {});
                try { await bounded(() => exit, Math.min(1000, deadline - performance.now())); }
                catch { await owner.kill(deadline); }
            }
            child.off('exit', onExit);
        },
    };
    try {
        const endpoint = server.wsEndpoint(), parsed = new URL(endpoint);
        requireTrace(parsed.protocol === 'ws:' && parsed.hostname === '127.0.0.1'
            && !parsed.username && !parsed.password && parsed.pathname.length >= 24, 'BrowserEndpointRejected');
        const browser = await chromium.connect(endpoint, { timeout: 3000 });
        ownedBrowsers.set(browser, owner);
        return browser;
    } catch {
        await owner.kill(performance.now() + limits.drainMs);
        fail('BrowserConnectionUnavailable');
    }
}

export async function closeUiTraceBrowser(browser) {
    const owner = ownedBrowsers.get(browser);
    requireTrace(owner, 'BrowserOwnerMissing');
    await owner.close(performance.now() + limits.drainMs);
    ownedBrowsers.delete(browser);
}

/** ReportEvents avoids Chromium's filesystem-backed ReturnAsStream transport. */
export async function startUiFrameTrace(browser, report, permit) {
    requireTrace(permit && permits.delete(permit), 'PermitRejected');
    const owner = ownedBrowsers.get(browser);
    requireTrace(owner?.permit === permit, 'BrowserOwnerMissing');
    const data = report.uiFrameTrace = { status: 'starting', recordingLimitMs: limits.recordingMs,
        drainLimitMs: limits.drainMs, inputLimitBytes: limits.inputBytes, summaryLimitBytes: limits.summaryBytes,
        recordLimit: limits.records, bufferKiB: limits.bufferKiB, categories: [...categories],
        argumentFilter: true, sampling: false, systrace: false, startAcknowledged: false,
        stopRequested: false, recordingCapReached: false, complete: false, dataLossOccurred: null,
        bytes: 0, batches: 0, inputAccounting: 'conservative-json-upper-bound',
        inputLimitReached: false, aborted: false, errors: [], summary: emptySummary(),
        cleanup: { rawDataRetained: false, sessionDetached: false, browserClosed: false } };
    let session, requested = false, finishing, recordingTimer, hardTimer, acceptData = true, resolveComplete;
    let startUncertain = false;
    const hardDeadline = performance.now() + limits.recordingMs;
    void owner.exit.then(() => { clearTimeout(recordingTimer); clearTimeout(hardTimer); });
    const completed = new Promise(resolve => { resolveComplete = resolve; });
    const errors = new Set(['Budget', 'Batch', 'InputLimit']);
    const failure = (stage, error) => {
        if (data.errors.length < 12) data.errors.push({ stage, reason: errors.has(error?.message) ? error.message : 'Unavailable' });
    };
    const onComplete = value => {
        data.dataLossOccurred = typeof value?.dataLossOccurred === 'boolean' ? value.dataLossOccurred : null;
        data.completionReceived = true;
        resolveComplete();
    };
    const onData = value => {
        if (!acceptData) return;
        try {
            if (!Array.isArray(value?.value)) throw new Error('Batch');
            // Only this callback holds the raw event objects.
            const bytes = traceInputBytes(value, limits.inputBytes - data.bytes);
            data.bytes += bytes;
            data.batches++;
            addEvents(data.summary, value.value);
        } catch (error) {
            acceptData = false;
            data.inputLimitReached ||= error?.message === 'InputLimit';
            data.aborted = true;
            failure('collect', error);
            void controller.finish('input-limit');
        }
    };
    const controller = {
        get aborted() { return data.aborted; },
        finish(reason = 'iteration-exit') {
            return finishing ??= finish(['initial-checkpoint', 'iteration-exit', 'recording-cap', 'start-failed', 'input-limit'].includes(reason)
                ? reason : 'iteration-exit');
        },
    };
    async function finish(reason) {
        data.stopReason = reason;
        data.recordingCapReached = reason === 'recording-cap';
        data.aborted ||= ['recording-cap', 'start-failed', 'input-limit'].includes(reason);
        const started = performance.now(), deadline = Math.min(started + limits.drainMs, hardDeadline);
        // Leave time for a detached session, or a verified owned-browser shutdown.
        const drain = action => bounded(action, deadline - performance.now() - 2000);
        let stage = 'stop';
        try {
            if (!session || !requested) { data.status = 'unavailable'; return; }
            data.stopRequested = true;
            await drain(() => session.send('Tracing.end'));
            data.stopAcknowledged = true;
            stage = 'complete';
            await drain(() => completed);
            clearTimeout(recordingTimer);
            clearTimeout(hardTimer);
            data.complete = data.startAcknowledged && data.completionReceived && data.dataLossOccurred === false
                && !data.recordingCapReached && !data.inputLimitReached && data.errors.length === 0;
            data.status = data.complete ? 'captured' : 'incomplete';
        } catch (error) { failure(stage, error); data.status = 'incomplete'; }
        finally {
            acceptData = false;
            if (session) {
                try {
                    await bounded(() => session.detach(), Math.min(500, deadline - performance.now()));
                    data.cleanup.sessionDetached = true;
                } catch (error) { failure('detach', error); data.complete = false; }
                session.off('Tracing.dataCollected', onData);
                session.off('Tracing.tracingComplete', onComplete);
            }
            if (data.aborted || startUncertain || (requested && (!data.stopAcknowledged || !data.completionReceived || !data.cleanup.sessionDetached))) {
                data.aborted = true;
                data.complete = false;
                try {
                    await owner.kill(deadline);
                    data.cleanup.browserClosed = true;
                } catch (error) { failure('browser-close', error); data.cleanup.shutdownUnverified = true; }
            }
            if ((data.stopAcknowledged && data.completionReceived) || data.cleanup.browserClosed) {
                clearTimeout(recordingTimer);
                clearTimeout(hardTimer);
            }
            data.drainElapsedMs = performance.now() - started;
            if (data.aborted) data.status = 'aborted';
            else if (data.status === 'captured' && !data.complete) data.status = 'incomplete';
            // Include pretty-print expansion used by smoke-report.json in the bound.
            // Leave space for a late shutdown result if process exit is still pending.
            while (data.summary.records.length) {
                const bytes = Buffer.byteLength(JSON.stringify({ uiFrameTrace: data }, null, 2));
                if (bytes <= limits.summaryBytes - 4096) break;
                const keep = Math.max(0, Math.floor(data.summary.records.length * (limits.summaryBytes - 8192) / bytes));
                data.summary.omittedRecords += data.summary.records.length - keep;
                data.summary.records.length = keep;
                data.summary.recordCount = keep;
            }
        }
    }
    let stage = 'attach';
    recordingTimer = setTimeout(() => { void controller.finish('recording-cap'); }, limits.recordingMs - limits.drainMs);
    hardTimer = setTimeout(() => {
        data.aborted = true;
        data.recordingCapReached = true;
        void owner.kill(hardDeadline).then(() => { data.cleanup.browserClosed = true; }, error => {
            failure('hard-stop', error); data.cleanup.shutdownUnverified = true;
        });
    }, limits.recordingMs - 1000);
    try {
        let accepting = true;
        const attachment = browser.newBrowserCDPSession();
        void attachment.then(value => {
            if (!accepting && !session) void bounded(() => value.detach(), 500)
                .then(() => { data.cleanup.lateSessionDetached = true; }, error => { failure('late-detach', error); });
        }, () => {});
        try { session = await bounded(() => attachment, 3000); }
        finally { accepting = false; }
        session.on('Tracing.dataCollected', onData);
        session.on('Tracing.tracingComplete', onComplete);
        stage = 'start';
        requested = true;
        startUncertain = true;
        await bounded(() => session.send('Tracing.start', { transferMode: 'ReportEvents', tracingBackend: 'chrome',
            traceConfig: { recordMode: 'recordUntilFull', traceBufferSizeInKb: limits.bufferKiB,
                enableSampling: false, enableSystrace: false, enableArgumentFilter: true,
                includedCategories: [...categories], excludedCategories: ['*'] } }), 3000);
        startUncertain = false;
        data.startAcknowledged = true;
        if (!finishing) data.status = 'recording';
    } catch (error) { failure(stage, error); await controller.finish('start-failed'); }
    return controller;
}
