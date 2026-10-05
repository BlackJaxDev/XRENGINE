import { promises as fs } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { validateActivation, validateInvocation, validateRequest } from './prepare-owned-native-profile.mjs';

const reportDirectory = 'Build/_AgentValidation/00000000-000000-shared/owned-native-profile-once/report';
const requestPath = '.github/diagnostic-requests/owned-native-profile-20261005.json';
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
    const checks = list(report.checks, 256, 'Application checks', value => value);
    const app = checks.filter(value => value?.name === 'advanced-rendering-parity-editor-published-world');
    requireValue(app.length === 1, 'Advanced application check is absent or ambiguous.');
    const applicationStatus = choice(app[0].status, ['passed', 'failed', 'skipped'], 'Application outcome');
    const native = object(report.nativeCompileIsolation, 'Native compile');
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
        'stdoutBytes', 'stderrBytes', 'summary', 'requiresJobTermination'], 'Owned profile');
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
            'requestedSampleMs', 'stopAcknowledgedNodeMs', 'stopRequestedNodeMs', 'totalMs'],
        'Profile timing');
    const durations = {};
    for (const field of ['analysisMs', 'collectorLifetimeMs', 'requestedSampleMs', 'totalMs'])
        if (timing[field] !== undefined) durations[field] = number(timing[field], 200_000, `Profile ${field}`, true);
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
        && cleanup.supervisorExitVerified && cleanup.sudoTreeExitVerified,
    'Collector summary lacks verified authorization or exit.');
    const output = {
        schema: 1,
        provenance: { requestId: request.requestId, requestSha256: '8ac2d5c4fa0ff42e575e8768758e6c21f5b45f6005d31e65df0acf5babb2311b',
            workflowRunId: invocation.runId, profilerCodeCommit: invocation.sha,
            sourceBundleCommit: request.sourceCommit, sourceBundleRunId: request.sourceRunId,
            sourceBundleArtifactId: request.sourceArtifactId, sourceArchiveSha256: request.sourceArchiveSha256 },
        application: { status: applicationStatus },
        nativeCompile: { status: nativeStatus, budgetMs: 45000,
            watchdogExpired: native.compileWatchdog?.expired === true, compile: compileOutcome },
        profile: { status: profileStatus, reasons, authorizationConsumed,
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
