import { promises as fs } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { REQUEST_SHA256, validateActivation, validateInvocation, validateRequest } from './prepare-owned-native-profile.mjs';

const reportDirectory = 'Build/_AgentValidation/00000000-000000-shared/owned-native-shadow-profile-once/report';
const requestPath = '.github/diagnostic-requests/owned-native-shadow-profile-20261007.json';
const reportPath = path.join(reportDirectory, 'smoke-report.json');
const summaryPath = path.join(reportDirectory, 'owned-native-profile-summary.json');
const maxReportBytes = 8 * 1024 * 1024;
const maxSummaryBytes = 128 * 1024;
const hex64 = /^[0-9a-f]{64}$/;
const moduleName = /^[A-Za-z0-9_.+-]{1,128}$/;
const buildId = /^[0-9a-f]{8,128}$/;
const reasonCode = /^[A-Za-z][A-Za-z0-9]{0,80}$/;
const decimal = /^(0|[1-9][0-9]{0,19})$/;
const offset = /^0x[0-9a-f]{1,16}$/;
const symbolName = /^[A-Za-z_$~][A-Za-z0-9_$~:.<>, ()*&+\[\]-]{0,159}$/;

function requireValue(condition, message) { if (!condition) throw new Error(message); }
function object(value, label) {
    requireValue(value !== null && typeof value === 'object' && !Array.isArray(value), `${label} is invalid.`);
    return value;
}
function allowedKeys(value, keys, label) {
    object(value, label);
    requireValue(Object.keys(value).every(key => keys.includes(key)), `${label} has an unknown field.`);
    return value;
}
function exactKeys(value, keys, label) {
    allowedKeys(value, keys, label);
    requireValue(Object.keys(value).length === keys.length, `${label} is incomplete.`);
    return value;
}
function integer(value, max, label) {
    requireValue(Number.isSafeInteger(value) && value >= 0 && value <= max, `${label} is invalid.`);
    return value;
}
function number(value, max, label, nullable = false) {
    if (nullable && value === null) return null;
    requireValue(typeof value === 'number' && Number.isFinite(value) && value >= 0 && value <= max,
        `${label} is invalid.`);
    return value;
}
function choice(value, choices, label) {
    requireValue(choices.includes(value), `${label} is invalid.`);
    return value;
}
function pattern(value, regex, label) {
    requireValue(typeof value === 'string' && regex.test(value), `${label} is invalid.`);
    return value;
}
function list(value, max, label, transform) {
    requireValue(Array.isArray(value) && value.length <= max, `${label} exceeds its limit.`);
    return value.map(transform);
}
function percentage(value, label, nullable = false) { return number(value, 100, label, nullable); }
function module(value) {
    exactKeys(value, ['module', 'buildId'], 'Module');
    return { module: pattern(value.module, moduleName, 'Module name'),
        buildId: pattern(value.buildId, buildId, 'Module build ID') };
}
function moduleCount(value) {
    exactKeys(value, ['module', 'buildId', 'samples', 'percent'], 'Module count');
    return { module: pattern(value.module, moduleName, 'Module name'),
        buildId: pattern(value.buildId, buildId, 'Module build ID'),
        samples: integer(value.samples, 1_000_000, 'Module samples'),
        percent: percentage(value.percent, 'Module percent') };
}
function region(value) {
    exactKeys(value, ['module', 'buildId', 'fileRegionOffset', 'samples', 'percent'], 'Region');
    return { module: pattern(value.module, moduleName, 'Region module'),
        buildId: pattern(value.buildId, buildId, 'Region build ID'),
        fileRegionOffset: pattern(value.fileRegionOffset, offset, 'Region offset'),
        samples: integer(value.samples, 1_000_000, 'Region samples'),
        percent: percentage(value.percent, 'Region percent') };
}
function frame(value) {
    allowedKeys(value, ['module', 'buildId', 'fileRegionOffset'], 'Chain frame');
    if (value.module === 'unknown') {
        exactKeys(value, ['module'], 'Unknown chain frame');
        return { module: 'unknown' };
    }
    exactKeys(value, ['module', 'buildId', 'fileRegionOffset'], 'Chain frame');
    return { module: pattern(value.module, moduleName, 'Chain module'),
        buildId: pattern(value.buildId, buildId, 'Chain build ID'),
        fileRegionOffset: pattern(value.fileRegionOffset, offset, 'Chain offset') };
}
function nativeReport(value) {
    allowedKeys(value, ['status', 'reason', 'symbolHints', 'stderrBytes', 'exitVerified', 'interpretation'],
        'Native symbol report');
    const hints = list(value.symbolHints, 64, 'Symbol hints', hint => {
        exactKeys(hint, ['module', 'symbol', 'samples', 'percent'], 'Symbol hint');
        return { module: pattern(hint.module, moduleName, 'Symbol module'),
            symbol: pattern(hint.symbol, symbolName, 'Exported symbol'),
            samples: integer(hint.samples, 1_000_000, 'Symbol samples'),
            percent: percentage(hint.percent, 'Symbol percent') };
    });
    if (value.reason !== null) pattern(value.reason, reasonCode, 'Native report reason');
    if (value.exitVerified !== undefined) requireValue(typeof value.exitVerified === 'boolean',
        'Native report exit verification is invalid.');
    return { status: choice(value.status, ['completed', 'unavailable'], 'Native symbol status'),
        reason: value.reason ?? null, exitVerified: value.exitVerified ?? null, symbolHints: hints };
}
function collectorSummary(value) {
    const keys = ['manifest', 'samples', 'unknownSamples', 'unknownPercent', 'modules', 'regions',
        'regionBytes', 'chainExamples', 'firstSampleMonotonicNs', 'lastSampleMonotonicNs',
        'observedSampleSpanMs', 'ringLostSamples', 'finalEventLostSamples', 'lossInterpretation',
        'throttled', 'kernelMetadataRecordsDiscarded', 'additionalThreadsObserved',
        'attachedOriginalThreads', 'unavailableModuleCount'];
    exactKeys(value, value.nativeReport === undefined ? keys : [...keys, 'nativeReport'], 'Collector summary');
    requireValue(value.regionBytes === 4096 && typeof value.throttled === 'boolean',
        'Collector summary has invalid sample metadata.');
    const manifest = list(value.manifest, 128, 'Module manifest', module);
    const known = new Set(manifest.map(item => `${item.module}\0${item.buildId}`));
    const modules = list(value.modules, 64, 'Module counts', moduleCount);
    const regions = list(value.regions, 64, 'Region counts', region);
    for (const item of [...modules, ...regions]) requireValue(known.has(`${item.module}\0${item.buildId}`),
        'Collector module identity is absent from its manifest.');
    const chainExamples = list(value.chainExamples, 8, 'Chain examples', chain =>
        list(chain, 32, 'Chain frames', item => {
            const checked = frame(item);
            requireValue(checked.module === 'unknown' || known.has(`${checked.module}\0${checked.buildId}`),
                'Chain module identity is absent from its manifest.');
            return checked;
        }));
    const parsed = {
        manifest,
        samples: integer(value.samples, 1_000_000, 'Sample count'),
        unknownSamples: integer(value.unknownSamples, 1_000_000, 'Unknown sample count'),
        unknownPercent: percentage(value.unknownPercent, 'Unknown percent', true),
        modules, regions, regionBytes: 4096, chainExamples,
        firstSampleMonotonicNs: value.firstSampleMonotonicNs === null ? null
            : pattern(value.firstSampleMonotonicNs, decimal, 'First sample time'),
        lastSampleMonotonicNs: value.lastSampleMonotonicNs === null ? null
            : pattern(value.lastSampleMonotonicNs, decimal, 'Last sample time'),
        observedSampleSpanMs: number(value.observedSampleSpanMs, 10_000, 'Observed sample span', true),
        ringLostSamples: pattern(value.ringLostSamples, decimal, 'Ring loss count'),
        finalEventLostSamples: pattern(value.finalEventLostSamples, decimal, 'Final loss count'),
        throttled: value.throttled,
        kernelMetadataRecordsDiscarded: integer(value.kernelMetadataRecordsDiscarded, 1_000_000, 'Discarded kernel metadata count'),
        additionalThreadsObserved: integer(value.additionalThreadsObserved, 256, 'Additional thread count'),
        attachedOriginalThreads: integer(value.attachedOriginalThreads, 64, 'Original thread count'),
        unavailableModuleCount: integer(value.unavailableModuleCount, 128, 'Unavailable module count'),
    };
    requireValue(parsed.unknownSamples <= parsed.samples, 'Unknown sample count exceeds total.');
    if (value.nativeReport !== undefined) {
        parsed.nativeReport = nativeReport(value.nativeReport);
        for (const hint of parsed.nativeReport.symbolHints) requireValue(
            manifest.some(item => item.module === hint.module), 'Symbol hint module is absent from the manifest.');
    }
    return parsed;
}

export function sanitizeReport(report, request, invocation) {
    object(report, 'Smoke report');
    requireValue(report.schemaVersion === 1 && report.gpuMode === 'software', 'Smoke report scope is invalid.');
    requireValue(report.nativeOwnedShadowProfileOnce === true && report.nativeCompileTrace === false &&
        report.gpuDiagnostics === false && report.uiFrameTraceRequested === false &&
        report.nativeCompileIsolationComparison === undefined,
    'Smoke report is not the isolated shadow diagnostic.');
    const checks = list(report.checks, 256, 'Application checks', value => value);
    const app = checks.filter(value => value?.name === 'advanced-shadow-parity-editor-published-world');
    requireValue(app.length === 1, 'Shadow application check is absent or ambiguous.');
    const applicationStatus = choice(app[0].status, ['passed', 'failed', 'skipped'], 'Application outcome');
    requireValue(applicationStatus === 'failed', 'The selected shadow application did not fail.');
    const selectedFailures = list(report.advancedShadowFailures, 16, 'Shadow failures', value => value)
        .filter(value => value?.profile === 'small' && value?.state === 'on' && value?.iteration === 0);
    requireValue(selectedFailures.length === 1, 'The selected shadow failure is absent or ambiguous.');
    const selectedCapture = selectedFailures[0].nativeCompile;
    const selectedRecords = selectedCapture?.records?.filter(value => value.status === 'pending') ?? [];
    requireValue(Array.isArray(selectedCapture?.captureErrors) && selectedCapture.captureErrors.length === 0 &&
        selectedRecords.length === 1 && selectedRecords[0].recipeStatus === 'ready' &&
        selectedRecords[0].pass === request.selectedPass &&
        selectedRecords[0].recipe?.pipeline?.label === `engine-advanced-${request.selectedPass}` &&
        selectedRecords[0].recipe?.compute?.entryPoint === request.entryPoint &&
        selectedRecords[0].recipe?.module?.sha256 === request.wgslSha256 &&
        selectedRecords[0].recipe?.module?.byteLength === request.wgslBytes,
    'The selected shadow recipe differs from the request.');
    const native = object(report.nativeCompileIsolation, 'Native compile');
    requireValue(native.applicationBrowserClosed === true && native.cleanup?.browserClosed === true &&
        native.catalogKey === `advanced::${request.selectedPass}` &&
        native.shadowTarget?.profile === 'small' && native.shadowTarget?.state === 'on' &&
        native.shadowTarget?.iteration === 0 && native.shadowTarget?.pass === request.selectedPass &&
        native.shadowTarget?.descriptorIdentity === request.descriptorSha256 &&
        native.shadowTarget?.wgslSha256 === request.wgslSha256 &&
        native.shadowTarget?.wgslBytes === request.wgslBytes &&
        native.shadowTarget?.entryPoint === request.entryPoint &&
        native.verifiedCookedArtifact?.descriptorIdentity === request.descriptorSha256 &&
        native.verifiedCookedArtifact?.sha256 === request.wgslSha256 &&
        native.verifiedCookedArtifact?.byteLength === request.wgslBytes &&
        native.verifiedCookedArtifact?.entryPoint === request.entryPoint &&
        native.backendComparison?.status === 'matched',
    'Native replay identity, backend, or browser lifetime differs.');
    requireValue(native.compileBudgetMs === 45000, 'Native compile budget changed.');
    const nativeStatus = choice(native.status,
        ['skipped', 'compiled', 'compile-timeout', 'gpu-error', 'failed', 'compile-watchdog-timeout', 'diagnostic-failed'],
        'Native compile outcome');
    requireValue(nativeStatus !== 'compile-watchdog-timeout' || native.compileWatchdog?.expired === true,
        'Native compile watchdog outcome is inconsistent.');
    const compile = native.replay?.compile;
    const compileOutcome = compile === undefined ? null : {
        status: choice(compile.status, ['pending', 'fulfilled', 'rejected', 'timed-out'], 'Compile call outcome'),
        elapsedMs: number(compile.elapsedMs, 200_000, 'Compile elapsed time', true),
        startedAtMs: number(compile.startedAtMs, 200_000, 'Compile start time', true),
        callReturnedAtMs: number(compile.callReturnedAtMs, 200_000, 'Compile call return time', true),
    };
    const profile = native.ownedGpuProfile;
    requireValue(profile !== undefined, 'Owned profile result is absent.');
    allowedKeys(profile, ['scope', 'status', 'limits', 'event', 'frequencyHz', 'callchainAddresses',
        'targetSamplingMs', 'maximumCollectorMs', 'requestedBoundaryMeaning', 'interpretation',
        'cleanup', 'timing', 'reasons', 'authorization', 'collector', 'target', 'privilegedProcesses',
        'stdoutBytes', 'stderrBytes', 'summary', 'requiresJobTermination', 'pendingGate',
        'prerequisiteCommands'], 'Owned profile');
    const cleanup = exactKeys(profile.cleanup,
        ['rawDeleted', 'recorderExitVerified', 'supervisorExitVerified', 'sudoTreeExitVerified'], 'Collector cleanup');
    requireValue(Object.values(cleanup).every(value => typeof value === 'boolean'), 'Collector cleanup is invalid.');
    requireValue(profile.requiresJobTermination !== true && cleanup.rawDeleted === true,
        'Collector cleanup requires job termination or raw data remains.');
    const profileStatus = choice(profile.status, ['completed', 'incomplete', 'unavailable', 'skipped'], 'Profile outcome');
    requireValue(['completed', 'incomplete'].includes(profileStatus) === (profile.summary !== undefined),
        'Profile status and summary disagree.');
    const reasons = list(profile.reasons, 16, 'Profile reason codes', value => pattern(value, reasonCode, 'Profile reason'));
    const timing = allowedKeys(profile.timing,
        ['analysisMs', 'cleanupFinishedNodeMs', 'collectorExitNodeMs', 'collectorLifetimeMs',
            'collectorSpawnNodeMs', 'disableAcknowledgedNodeMs', 'disableRequestedNodeMs',
            'enableAcknowledgedNodeMs', 'enableRequestedNodeMs', 'pingAcknowledgedNodeMs',
            'requestedSampleMs', 'stopAcknowledgedNodeMs', 'stopRequestedNodeMs', 'totalMs',
            'authorizationClaimedNodeMs', 'setupMs', 'beforeSpawnPendingMs', 'beforeSpawnDeadlineRemainingMs'],
        'Profile timing');
    const durations = {};
    for (const field of ['analysisMs', 'collectorLifetimeMs', 'requestedSampleMs', 'totalMs',
        'setupMs', 'beforeSpawnPendingMs', 'beforeSpawnDeadlineRemainingMs'])
        if (timing[field] !== undefined) durations[field] = number(timing[field], 200_000, `Profile ${field}`, true);
    const pendingGate = profile.pendingGate === undefined ? null : (() => {
        exactKeys(profile.pendingGate, ['minimumPendingMs', 'startedAtPageMs', 'observedAtPageMs',
            'pendingMs', 'observedAtNodeMs'], 'Pending gate');
        requireValue(profile.pendingGate.minimumPendingMs === request.minimumPendingMilliseconds,
            'Pending gate threshold differs.');
        const started = number(profile.pendingGate.startedAtPageMs, 1_000_000_000, 'Page compile start');
        const observed = number(profile.pendingGate.observedAtPageMs, 1_000_000_000, 'Page observation');
        const elapsed = number(profile.pendingGate.pendingMs, 45_000, 'Page pending time');
        number(profile.pendingGate.observedAtNodeMs, 1_000_000_000, 'Node observation');
        requireValue(observed >= started && Math.abs(observed - started - elapsed) < 5 &&
            elapsed >= request.minimumPendingMilliseconds,
        'Pending gate elapsed time is inconsistent.');
        return { observedPendingMs: elapsed };
    })();
    const labels = ['expected-package-metadata', 'installed-file-package-owner',
        'owning-package-version', 'owned-fifo-creation'];
    const prerequisites = profile.prerequisiteCommands === undefined ? [] :
        list(profile.prerequisiteCommands, 4, 'Prerequisite commands', (value, index) => {
            exactKeys(value, ['label', 'budgetMs', 'elapsedMs', 'outcome', 'exitCode',
                'expectedOutputMatched'], 'Prerequisite command');
            requireValue(value.label === labels[index], 'Prerequisite command order differs.');
            number(value.budgetMs, 1000, 'Prerequisite command budget');
            const elapsedMs = number(value.elapsedMs, 5000, 'Prerequisite command elapsed time');
            const outcome = choice(value.outcome, ['completed', 'timed-out', 'output-limit', 'failed'],
                'Prerequisite command outcome');
            if (value.exitCode !== null) integer(value.exitCode, 255, 'Prerequisite command exit');
            requireValue(typeof value.expectedOutputMatched === 'boolean',
                'Prerequisite command expected-output match is invalid.');
            return { label: value.label, elapsedMs, outcome, exitCode: value.exitCode,
                expectedOutputMatched: value.expectedOutputMatched };
        });
    let authorizationConsumed = false;
    if (profile.authorization !== undefined) {
        const auth = exactKeys(profile.authorization, ['requestId', 'runId', 'profilerCodeCommit',
            'sourceBundleCommit', 'sourceBundleRunId', 'sourceBundleArtifactId', 'sourceArchiveSha256',
            'runAttempt', 'consumed'], 'Profile authorization');
        requireValue(auth.requestId === request.requestId && auth.runId === invocation.runId
            && auth.profilerCodeCommit === invocation.sha && auth.sourceBundleCommit === request.sourceCommit
            && auth.sourceBundleRunId === String(request.sourceRunId)
            && auth.sourceBundleArtifactId === String(request.sourceArtifactId)
            && auth.sourceArchiveSha256 === request.sourceArchiveSha256 && auth.runAttempt === 1
            && auth.consumed === true, 'Profile authorization provenance differs.');
        authorizationConsumed = true;
    }
    if (profile.summary !== undefined) requireValue(authorizationConsumed && cleanup.recorderExitVerified
        && cleanup.supervisorExitVerified && cleanup.sudoTreeExitVerified && pendingGate !== null &&
        prerequisites.length === labels.length && prerequisites.every(value =>
            value.outcome === 'completed' && value.exitCode === 0 && value.expectedOutputMatched),
    'Collector summary lacks verified authorization or exit.');
    const output = {
        schema: 1,
        provenance: { requestId: request.requestId, requestSha256: REQUEST_SHA256,
            workflowRunId: invocation.runId, profilerCodeCommit: invocation.sha,
            sourceBundleCommit: request.sourceCommit, sourceBundleRunId: request.sourceRunId,
            sourceBundleArtifactId: request.sourceArtifactId, sourceArchiveSha256: request.sourceArchiveSha256 },
        application: { status: applicationStatus },
        nativeCompile: { status: nativeStatus, budgetMs: 45000,
            watchdogExpired: native.compileWatchdog?.expired === true, compile: compileOutcome },
        profile: { status: profileStatus, reasons, authorizationConsumed, pendingGate, prerequisites,
            cleanup: { rawDeleted: cleanup.rawDeleted, recorderExitVerified: cleanup.recorderExitVerified,
                supervisorExitVerified: cleanup.supervisorExitVerified, sudoTreeExitVerified: cleanup.sudoTreeExitVerified },
            timing: durations },
    };
    if (profile.summary !== undefined) output.profile.summary = collectorSummary(profile.summary);
    requireValue(Buffer.byteLength(`${JSON.stringify(output)}\n`) <= maxSummaryBytes,
        'Sanitized summary exceeds its limit.');
    return output;
}

async function main() {
    const env = process.env;
    const event = JSON.parse(await fs.readFile(env.GITHUB_EVENT_PATH, 'utf8'));
    const invocation = validateInvocation(env, event);
    const request = validateRequest(await fs.readFile(requestPath));
    const activation = await fs.readFile(env.XRE_OWNED_PROFILE_ACTIVATION_FILE);
    validateActivation(activation, request, invocation);
    const stat = await fs.stat(reportPath);
    requireValue(stat.isFile() && stat.size <= maxReportBytes, 'Smoke report is absent or oversized.');
    const bytes = await fs.readFile(reportPath);
    requireValue(bytes.length <= maxReportBytes, 'Smoke report exceeds its limit.');
    const sanitized = sanitizeReport(JSON.parse(bytes.toString('utf8')), request, invocation);
    await fs.writeFile(summaryPath, `${JSON.stringify(sanitized)}\n`, { flag: 'wx', mode: 0o600 });
    await fs.appendFile(env.GITHUB_OUTPUT, 'upload_allowed=true\n');
    console.log('Owned native profile summary passed strict artifact admission.');
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    main().catch(() => { console.error('Owned native profile summary was withheld.'); process.exitCode = 1; });
}
