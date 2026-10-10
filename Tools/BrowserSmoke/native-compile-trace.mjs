import fs from 'node:fs/promises';
import path from 'node:path';
import { createHash, randomUUID } from 'node:crypto';

const categories = Object.freeze(['gpu.dawn']);
const knownCategories = Object.freeze([...categories, 'gpu.dawn.validation', 'gpu.dawn.recording', 'gpu.dawn.gpu_work']);
const maxBytes = 16 * 1024 * 1024;
const maxEvents = 100000;
const maxStageEvents = 256;
const processCpuQueryBudgetMs = 500;
const processCpuDeadlineLeadMs = 1000;
const stages = new Set(['CreatePipelineAsyncEvent::InitializeAsync', 'CreatePipelineAsyncEvent::InitializeImpl',
    'ShaderModuleVk::GetHandleAndSpirv', 'tint::spirv::writer::Generate()', 'vkCreateShaderModule']);

async function bounded(action, budgetMs) {
    if (budgetMs <= 0) throw new Error('TraceBudget');
    let timer;
    try {
        return await Promise.race([Promise.resolve().then(action), new Promise((_, reject) => {
            timer = setTimeout(() => reject(new Error('TraceBudget')), budgetMs);
        })]);
    } finally { clearTimeout(timer); }
}

/** Exported event shapes only. No inferred native durations, CPU usage or driver attribution. */
export function summarizeNativeCompileTrace(bytes, markers = []) {
    const parsed = JSON.parse(bytes.toString('utf8'));
    const events = Array.isArray(parsed) ? parsed : parsed.traceEvents;
    if (!Array.isArray(events)) throw new Error('TraceShape');
    const summary = { eventCount: events.length, inspectedEvents: Math.min(events.length, maxEvents),
        eventLimitReached: events.length > maxEvents, nativeEvents: 0, knownStageEvents: 0,
        stageEventsOmitted: 0, phaseCounts: {}, stages: [], clockSyncMarkers: [],
        interpretation: 'Raw exported event shapes. Begin/end events are not paired here. Trace may include device cleanup. Clock markers have request/ack bounds and are not GPU barriers; without matching markers, Node/page boundaries cannot be mapped to trace timestamps. Missing events do not prove a native stage was skipped. No event brackets vkCreateComputePipelines.' };
    const knownMarkers = new Map(markers.map(marker => [marker.syncId, marker]));
    for (let index = 0; index < summary.inspectedEvents; index++) {
        const event = events[index];
        const marker = knownMarkers.get(event?.args?.sync_id);
        if (marker && Number.isFinite(event.ts) && summary.clockSyncMarkers.length < 8)
            summary.clockSyncMarkers.push({ name: marker.name, syncId: marker.syncId, ts: event.ts });
        if (!event || typeof event.cat !== 'string' || !event.cat.split(',').some(value => categories.includes(value))) continue;
        summary.nativeEvents++;
        if (!stages.has(event.name)) continue;
        summary.knownStageEvents++;
        const phase = ['B', 'E', 'X', 'b', 'e', 'n', 'i', 'I'].includes(event.ph) ? event.ph : 'other';
        summary.phaseCounts[phase] = (summary.phaseCounts[phase] ?? 0) + 1;
        if (summary.stages.length >= maxStageEvents) { summary.stageEventsOmitted++; continue; }
        const entry = { name: event.name, phase };
        for (const field of ['pid', 'tid', 'ts', 'dur'])
            if (Number.isFinite(event[field])) entry[field] = event[field];
        summary.stages.push(entry);
    }
    return summary;
}

/** Opt-in tracing of one owned browser. All failures remain diagnostic and source-free in the report. */
export async function startNativeCompileTrace(browser, output, result) {
    const data = result.nativeTrace = { enabled: true, status: 'starting', startedUtc: new Date().toISOString(),
        requestedCategories: [...categories], availableCategories: [], maxBytes, maxEvents, maxStageEvents,
        streamFormat: 'json', traceBufferSizeInKb: 4096, argumentFilterRequested: true,
        drainBudgetMs: 5000, complete: false, dataLossOccurred: null, eof: false, bytes: 0,
        startAcknowledgedBeforeReplay: false,
        identity: { browser: result.browser, module: result.recipe.module, recipeSha256: result.recipeSha256,
            backend: result.backendComparison?.isolated ?? null }, markers: [], errors: [], cleanup: {},
        processCpu: { status: 'not-started', queryBudgetMs: processCpuQueryBudgetMs,
            preDeadlineLeadMs: processCpuDeadlineLeadMs, snapshots: [],
            interpretation: 'Cumulative CPU seconds across every GPU-process thread. The delta cannot identify worker CPU, a compiler stage, or the cause of low activity. Query request/response times bound each read. Node cleanup overlap is recorded; page disposal can precede Node cleanup, so page cleanup overlap cannot be excluded.' },
        lifecycle: 'The page keeps its existing device disposal. Tracing is drained after page cleanup and before context/browser closure; no stop-before-device-destroy guarantee.' };
    let session, requested = false, finalizing, resolveComplete, completionRecord, drainFinished = false;
    const completed = new Promise(resolve => { resolveComplete = resolve; });
    const chunks = [];
    const streamClosures = new Map();
    const markerTasks = [];
    const processCpuTasks = [];
    let processCpuTimer;
    let processCpuStopped = false;
    const failure = (stage, error) => {
        if (data.errors.length < 12) data.errors.push({ stage,
            name: /^[A-Za-z][A-Za-z0-9]{0,63}$/.test(error?.name ?? '') ? error.name : 'Error',
            reason: ['TraceBudget', 'TraceStream', 'TraceFormat', 'TraceCompression', 'TraceChunk',
                'TraceChunkLimit', 'TraceByteLimit', 'TraceEmptyChunk', 'TraceShape', 'TraceCategories'].includes(error?.message)
                ? error.message : 'ProtocolOrIoError' });
    };
    const controller = {
        mark(name) {
            if (name === 'compile-deadline' || name === 'node-cleanup-started') {
                if (name === 'node-cleanup-started') data.processCpu.nodeCleanupStartedAtNodeMonotonicMs = performance.now();
                stopProcessCpuObservation(name);
            }
            if (data.markers.length >= 8) return;
            const marker = { name, utc: new Date().toISOString(), nodeMonotonicMs: performance.now(),
                syncId: `native-compile-${name}-${randomUUID()}` };
            data.markers.push(marker);
            if (session && requested && !drainFinished && data.status === 'recording') {
                // These commands never block the compile callback or its watchdog.
                markerTasks.push(bounded(() => session.send('Tracing.recordClockSyncMarker', { syncId: marker.syncId }), 500)
                    .then(() => { marker.acknowledgedNodeMonotonicMs = performance.now(); }, error => {
                        marker.failed = true;
                        failure('clock-marker', error);
                    }));
            }
        },
        startProcessCpuObservation(compileBudgetMs, compileStartedAtNodeMonotonicMs = performance.now()) {
            const observation = data.processCpu;
            if (processCpuStopped || observation.status !== 'not-started') return;
            if (!Number.isFinite(compileBudgetMs) || !Number.isFinite(compileStartedAtNodeMonotonicMs)
                || compileBudgetMs <= processCpuDeadlineLeadMs + processCpuQueryBudgetMs) {
                observation.status = 'unavailable';
                observation.reason = 'InvalidCompileBudget';
                return;
            }
            observation.status = 'pending';
            observation.startedAtNodeMonotonicMs = compileStartedAtNodeMonotonicMs;
            observation.compileBudgetMs = compileBudgetMs;
            observation.scheduledLateReadAfterMs = compileBudgetMs - processCpuDeadlineLeadMs;
            observation.snapshots = [{ name: 'compile-start', status: 'scheduled' }, { name: 'before-deadline', status: 'scheduled' }];
            // Neither read is awaited by the compile callback or watchdog.
            processCpuTasks.push(readProcessCpu(observation.snapshots[0]));
            processCpuTimer = setTimeout(() => {
                processCpuTimer = undefined;
                const late = observation.snapshots[1];
                if (performance.now() >= observation.startedAtNodeMonotonicMs + compileBudgetMs - processCpuQueryBudgetMs) {
                    late.status = 'unavailable';
                    late.reason = 'PreDeadlineWindowMissed';
                    return;
                }
                processCpuTasks.push(readProcessCpu(late));
            }, Math.max(0, observation.startedAtNodeMonotonicMs + observation.scheduledLateReadAfterMs - performance.now()));
        },
        finish() { return finalizing ??= finish(); },
        async browserClosed(closed) {
            data.cleanup.ownerBrowserClosed = closed;
            if (session && !data.cleanup.sessionDetached) {
                if (closed) data.cleanup.sessionReleasedByBrowserClose = true;
                else await detach();
            }
            session?.off('Tracing.tracingComplete', onComplete);
            await Promise.all(streamClosures.values());
        },
    };
    function stopProcessCpuObservation(reason) {
        processCpuStopped = true;
        clearTimeout(processCpuTimer);
        if (data.processCpu.status === 'not-started') { data.processCpu.status = 'cancelled'; data.processCpu.reason = reason; }
        const late = data.processCpu.snapshots[1];
        if (late?.status === 'scheduled') { late.status = 'cancelled'; late.reason = reason; }
    }
    async function readProcessCpu(snapshot) {
        snapshot.status = 'pending';
        snapshot.requestedUtc = new Date().toISOString();
        snapshot.requestedNodeMonotonicMs = performance.now();
        try {
            if (!session) throw new Error('ProcessCpuSessionUnavailable');
            const value = await bounded(() => session.send('SystemInfo.getProcessInfo'), processCpuQueryBudgetMs);
            if (!Array.isArray(value?.processInfo) || value.processInfo.length > 1024) throw new Error('ProcessCpuShape');
            const gpu = value.processInfo.filter(process => process?.type === 'GPU');
            if (gpu.length !== 1) throw new Error(gpu.length ? 'ProcessCpuAmbiguousGpu' : 'ProcessCpuMissingGpu');
            if (!Number.isSafeInteger(gpu[0].id) || gpu[0].id <= 0 || !Number.isFinite(gpu[0].cpuTime) || gpu[0].cpuTime < 0)
                throw new Error('ProcessCpuShape');
            snapshot.pid = gpu[0].id;
            snapshot.cpuSeconds = gpu[0].cpuTime;
            snapshot.status = 'available';
        } catch (error) {
            snapshot.status = 'unavailable';
            snapshot.reason = ['TraceBudget', 'ProcessCpuSessionUnavailable', 'ProcessCpuShape',
                'ProcessCpuAmbiguousGpu', 'ProcessCpuMissingGpu'].includes(error?.message) ? error.message : 'ProtocolOrIoError';
        } finally {
            snapshot.completedUtc = new Date().toISOString();
            snapshot.completedNodeMonotonicMs = performance.now();
            snapshot.elapsedMs = snapshot.completedNodeMonotonicMs - snapshot.requestedNodeMonotonicMs;
        }
    }
    function summarizeProcessCpu() {
        const observation = data.processCpu;
        for (const snapshot of observation.snapshots) {
            if (!Number.isFinite(snapshot.requestedNodeMonotonicMs)) continue;
            snapshot.overlappedNodeCleanup = Number.isFinite(observation.nodeCleanupStartedAtNodeMonotonicMs)
                && snapshot.completedNodeMonotonicMs >= observation.nodeCleanupStartedAtNodeMonotonicMs;
            snapshot.completedAfterScheduledDeadline = snapshot.completedNodeMonotonicMs
                >= observation.startedAtNodeMonotonicMs + observation.compileBudgetMs;
        }
        if (observation.status !== 'pending') return;
        const [first, last] = observation.snapshots;
        if (first.status !== 'available' || last.status !== 'available') { observation.status = 'unavailable'; return; }
        if (first.pid !== last.pid) { observation.status = 'pid-replaced'; return; }
        if (last.cpuSeconds < first.cpuSeconds) { observation.status = 'counter-decreased'; return; }
        observation.status = 'available';
        observation.delta = { pid: first.pid, cpuSeconds: last.cpuSeconds - first.cpuSeconds,
            minimumIntervalSeconds: Math.max(0, last.requestedNodeMonotonicMs - first.completedNodeMonotonicMs) / 1000,
            maximumIntervalSeconds: (last.completedNodeMonotonicMs - first.requestedNodeMonotonicMs) / 1000 };
    }
    async function closeStream(handle, late = false) {
        if (streamClosures.has(handle)) return streamClosures.get(handle);
        const closing = (async () => {
            if (late) data.cleanup.lateStreamReceived = true;
            try { await bounded(() => session.send('IO.close', { handle }), 500); data.cleanup.streamClosed = true; }
            catch (error) { failure('close-stream', error); data.cleanup.streamClosed = false; }
        })();
        streamClosures.set(handle, closing);
        return closing;
    }
    function onComplete(value) {
        completionRecord = value;
        data.flushCompletedUtc = new Date().toISOString();
        data.dataLossOccurred = typeof value.dataLossOccurred === 'boolean' ? value.dataLossOccurred : null;
        resolveComplete(value);
        if (drainFinished && typeof value.stream === 'string' && value.stream)
            void closeStream(value.stream, true);
    }
    async function detach() {
        try { await bounded(() => session.detach(), 500); data.cleanup.sessionDetached = true; }
        catch (error) { failure('detach', error); data.cleanup.sessionDetached = false; }
    }
    async function finish() {
        let stream;
        stopProcessCpuObservation('trace-finish');
        data.outcome = { status: result.status, compileWatchdog: result.compileWatchdog,
            compile: result.replay?.compile ?? null };
        data.identity.cookedArtifact = result.replay?.cookedArtifact ?? null;
        data.pageCleanup = { timeOriginMs: result.replay?.timeOriginMs ?? null, ...result.replay?.cleanup };
        const deadline = performance.now() + data.drainBudgetMs;
        let stage = 'stop';
        const remaining = action => bounded(action, Math.max(0, deadline - performance.now()));
        try {
            await remaining(() => Promise.all(processCpuTasks));
            if (!session || !requested) return;
            controller.mark('trace-stop-requested');
            await remaining(() => Promise.all(markerTasks));
            await remaining(() => session.send('Tracing.end'));
            stage = 'flush';
            const completion = await remaining(() => completed);
            stream = completion.stream;
            if (typeof stream !== 'string' || !stream) throw new Error('TraceStream');
            if (completion.traceFormat && completion.traceFormat !== 'json') throw new Error('TraceFormat');
            if (completion.streamCompression && completion.streamCompression !== 'none') throw new Error('TraceCompression');
            stage = 'read';
            while (!data.eof) {
                const value = await remaining(() => session.send('IO.read', { handle: stream, size: 65536 }));
                if (typeof value.data !== 'string' || typeof value.eof !== 'boolean') throw new Error('TraceChunk');
                if (value.data.length > 262144) throw new Error('TraceChunkLimit');
                const bytes = Buffer.from(value.data, value.base64Encoded ? 'base64' : 'utf8');
                const available = maxBytes - data.bytes;
                if (bytes.length > available) {
                    if (available) chunks.push(bytes.subarray(0, available));
                    data.bytes = maxBytes;
                    data.byteLimitReached = true;
                    throw new Error('TraceByteLimit');
                }
                chunks.push(bytes);
                data.bytes += bytes.length;
                data.eof = value.eof;
                if (!bytes.length && !data.eof) throw new Error('TraceEmptyChunk');
            }
            data.transportComplete = data.eof && data.dataLossOccurred === false;
            data.complete = data.transportComplete && data.startAcknowledgedBeforeReplay;
            data.status = data.complete ? 'captured' : 'incomplete';
        } catch (error) {
            failure(stage, error);
            data.status = 'incomplete';
        } finally {
            summarizeProcessCpu();
            drainFinished = true;
            const receivedStream = stream ?? completionRecord?.stream;
            if (typeof receivedStream === 'string' && receivedStream) await closeStream(receivedStream, !stream);
            if (session) {
                // A timed-out stop may deliver its IO handle later. Retain the
                // listener until the owned browser closes so that handle is closed.
                if (requested && !completionRecord) data.cleanup.sessionRetainedUntilBrowserClose = true;
                else {
                    await detach();
                    session.off('Tracing.tracingComplete', onComplete);
                }
            }
            if (chunks.length) {
                const bytes = Buffer.concat(chunks, data.bytes);
                data.sha256 = createHash('sha256').update(bytes).digest('hex');
                const artifact = `native-compile-trace-${randomUUID()}.${data.eof ? 'json' : 'partial.json'}`;
                try { await fs.writeFile(path.join(output, artifact), bytes); data.artifact = artifact; }
                catch (error) { failure('write-artifact', error); data.complete = false; data.status = 'incomplete'; }
                if (data.eof) {
                    try {
                        data.summary = summarizeNativeCompileTrace(bytes, data.markers);
                        data.nativeStagesObserved = data.summary.knownStageEvents > 0;
                        const observed = new Set(data.summary.clockSyncMarkers.map(marker => marker.syncId));
                        data.clockCorrelationAvailable = data.markers.length > 0 && data.markers.every(marker => observed.has(marker.syncId));
                    }
                    catch (error) {
                        failure('summarize', error);
                        data.summaryUnavailable = true;
                        data.complete = false;
                        data.status = 'incomplete';
                    }
                }
            }
            data.finishedUtc = new Date().toISOString();
        }
    }
    let stage = 'attach';
    try {
        let accepting = true;
        const attachment = browser.newBrowserCDPSession();
        void attachment.then(value => {
            if (!accepting && !session) void bounded(() => value.detach(), 500).catch(() => {});
        }, () => {});
        try { session = await bounded(() => attachment, 3000); }
        finally { accepting = false; }
        session.on('Tracing.tracingComplete', onComplete);
        stage = 'metadata';
        const [version, supported] = await bounded(() => Promise.all([
            session.send('Browser.getVersion'), session.send('Tracing.getCategories'),
        ]), 3000);
        data.browserVersion = { protocolVersion: version.protocolVersion, product: version.product,
            revision: version.revision, jsVersion: version.jsVersion };
        if (!Array.isArray(supported.categories)) throw new Error('TraceCategories');
        data.availableCategories = supported.categories.filter(value => knownCategories.includes(value));
        data.missingCategories = categories.filter(value => !data.availableCategories.includes(value));
        stage = 'start';
        requested = true;
        await bounded(() => session.send('Tracing.start', { transferMode: 'ReturnAsStream', streamFormat: 'json',
            streamCompression: 'none', tracingBackend: 'chrome', traceConfig: { recordMode: 'recordUntilFull',
                traceBufferSizeInKb: data.traceBufferSizeInKb, enableSampling: false, enableSystrace: false,
                enableArgumentFilter: true, includedCategories: [...categories], excludedCategories: ['*'] } }), 3000);
        data.startAcknowledgedBeforeReplay = true;
        data.startAcknowledgedUtc = new Date().toISOString();
        data.status = 'recording';
        controller.mark('trace-started');
    } catch (error) { failure(stage, error); data.status = 'unavailable'; }
    return controller;
}
