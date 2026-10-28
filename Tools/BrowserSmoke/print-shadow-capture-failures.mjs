import fs from 'node:fs/promises';

// Prints the advanced shadow capture failure evidence from smoke-report.json to the job log.
// Readers without artifact access can then see the failing profile, the capture phase timings,
// and the native and GPU snapshots. Large snapshot values are cut to keep the log readable.
const reportPath = process.argv[2];
if (!reportPath) {
    console.error('Usage: node print-shadow-capture-failures.mjs <smoke-report.json>');
    process.exit(2);
}

const limit = 6000;
const bounded = value => {
    if (value === undefined || value === null) return value ?? null;
    const text = JSON.stringify(value);
    return text.length <= limit ? value : `${text.slice(0, limit)}... (${text.length - limit} more characters)`;
};

const report = JSON.parse(await fs.readFile(reportPath, 'utf8'));
const profiles = Object.fromEntries(Object.entries(report.advancedShadowProfiles ?? {}).map(([name, profile]) =>
    [name, { status: profile.status, iterations: profile.iterations?.length ?? 0, failures: profile.failures?.length ?? 0,
        baselineResized: profile.baseline?.resized?.length ?? 0 }]));
const failures = (report.advancedShadowFailures ?? []).map(failure => {
    const capture = failure.capture;
    return {
        profile: failure.profile, state: failure.state, iteration: failure.iteration, error: failure.error,
        capture: capture && {
            name: capture.name, budgetMs: capture.budgetMs, elapsedMs: capture.elapsedMs, attempt: capture.attempt,
            activePhase: capture.activePhase, gate: capture.gate, sessionCleanup: capture.sessionCleanup,
            phases: Object.values(capture.phases ?? {}).filter(phase => phase.status !== 'unrun'),
            steps: capture.steps, omittedSteps: capture.omittedSteps,
            resizeCheckpoints: capture.resizeCheckpoints ?? null,
        },
        nativeCompile: bounded(failure.nativeCompile), submissions: bounded(failure.submissions),
        gpu: bounded(failure.gpu), failureEvidence: bounded(failure.failure),
    };
});
console.log(JSON.stringify({ profiles, failures }, null, 2));
