import fs from 'node:fs/promises';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const repository = 'BlackJaxDev/XRENGINE';
const branch = 'codex/webgpu-readiness-audit';
const workflow = '.github/workflows/browser-network-walkthrough-once.yml';
const requestPath = '.github/diagnostic-requests/network-walkthrough-20261005.json';
const activationPath = '.github/diagnostic-activations/network-walkthrough-20261005.json';
const requestId = '1b6a802b-34b4-4e71-9f85-a968334df1be';
const jobKey = 'network-walkthrough';
const files = [workflow, ...['Invoke-NetworkWalkthrough.ps1', 'run-real-network-walkthrough.mjs',
    'admit-network-walkthrough.mjs', 'Test-NetworkWalkthroughStatic.ps1', 'producer.json', 'README.md']
    .map(name => `Tools/BrowserSmoke/network-walkthrough/${name}`)].sort();
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
class AdmissionFailure extends Error { }
const requireValue = (condition, code) => { if (!condition) throw new AdmissionFailure(code); };
const sha = value => typeof value === 'string' && /^[0-9a-f]{40}$/.test(value);
const digest = value => typeof value === 'string' && /^[0-9a-f]{64}$/.test(value);
const id = value => typeof value === 'string' && /^[1-9][0-9]{0,17}$/.test(value);
function keys(value, names, label) {
    requireValue(value && !Array.isArray(value) && typeof value === 'object'
        && Object.keys(value).sort().join('\0') === [...names].sort().join('\0'), label);
}
function canonicalJson(bytes, label) {
    requireValue(bytes.length <= 65536, `${label}Size`);
    const text = bytes.toString('utf8');
    requireValue(Buffer.from(text, 'utf8').equals(bytes), `${label}Encoding`);
    const value = JSON.parse(text);
    let compact = '', quoted = false, escaped = false;
    for (const ch of text) {
        if (!quoted && /\s/.test(ch)) continue;
        compact += ch;
        if (escaped) { escaped = false; continue; }
        if (quoted && ch === '\\') { escaped = true; continue; }
        if (ch === '"') quoted = !quoted;
    }
    requireValue(!quoted && compact === JSON.stringify(value), `${label}AmbiguousJson`);
    return value;
}
function validity(created, expires, maximumHours, label) {
    const start = Date.parse(created), end = Date.parse(expires), now = Date.now();
    requireValue(Number.isFinite(start) && Number.isFinite(end) && start <= now + 60000
        && end > now && end > start && end - start <= maximumHours * 3600000, `${label}Expired`);
}
async function api(endpoint, optional = false) {
    const token = process.env.GH_TOKEN;
    requireValue(token, 'ReadOnlyGitHubTokenMissing');
    const response = await fetch(`https://api.github.com/repos/${repository}/${endpoint}`, {
        redirect: 'error', signal: AbortSignal.timeout(20000), headers: {
            authorization: `Bearer ${token}`, accept: 'application/vnd.github+json',
            'X-GitHub-Api-Version': '2022-11-28', 'User-Agent': 'xrengine-network-walkthrough',
        },
    });
    if (optional && response.status === 404) { await response.body?.cancel(); return null; }
    requireValue(response.ok, 'GitHubReadFailed');
    const chunks = []; let size = 0;
    for await (const chunk of response.body) {
        size += chunk.length;
        requireValue(size <= 1024 * 1024, 'GitHubResponseTooLarge');
        chunks.push(chunk);
    }
    return JSON.parse(Buffer.concat(chunks, size).toString('utf8'));
}
async function content(relative, commit, optional = false) {
    requireValue(sha(commit), 'ImmutableCommitMissing');
    const file = await api(`contents/${relative}?ref=${commit}`, optional);
    if (file === null) return null;
    requireValue(file.type === 'file' && file.path === relative && file.encoding === 'base64'
        && file.size > 0 && file.size <= 65536, 'RepositoryFileShape');
    const bytes = Buffer.from(file.content.replace(/\s/g, ''), 'base64');
    requireValue(bytes.length === file.size && createHash('sha1').update(`blob ${bytes.length}\0`).update(bytes).digest('hex') === file.sha,
        'RepositoryFileIdentity');
    return bytes;
}
async function invocation() {
    const env = process.env;
    requireValue(env.GITHUB_REPOSITORY === repository && env.GITHUB_EVENT_NAME === 'push'
        && env.GITHUB_REF === `refs/heads/${branch}` && env.GITHUB_RUN_ATTEMPT === '1'
        && env.GITHUB_JOB === jobKey && env.GITHUB_WORKFLOW_REF === `${repository}/${workflow}@refs/heads/${branch}`
        && sha(env.GITHUB_SHA) && env.GITHUB_WORKFLOW_SHA === env.GITHUB_SHA && id(env.GITHUB_RUN_ID), 'InvocationNotFirstApprovedBranchRun');
    const event = JSON.parse(await fs.readFile(env.GITHUB_EVENT_PATH, 'utf8'));
    requireValue(event.repository?.full_name === repository && event.repository?.fork === false
        && event.ref === env.GITHUB_REF && event.after === env.GITHUB_SHA && sha(event.before)
        && event.before !== '0'.repeat(40) && event.before !== event.after
        && event.forced === false && event.deleted === false, 'PushEventMismatch');
    const jobs = await api(`actions/runs/${env.GITHUB_RUN_ID}/attempts/1/jobs?per_page=100`);
    requireValue(jobs.total_count === 1 && jobs.jobs?.length === 1, 'OneNonMatrixJobRequired');
    const job = jobs.jobs[0];
    requireValue(String(job.run_id) === env.GITHUB_RUN_ID && job.run_attempt === 1 && job.status === 'in_progress'
        && job.runner_name === env.RUNNER_NAME && id(String(job.id)), 'LiveJobMismatch');
    return { triggerCommit: env.GITHUB_SHA, before: event.before, runId: env.GITHUB_RUN_ID,
        runAttempt: 1, jobId: String(job.id), runnerName: env.RUNNER_NAME,
        runnerMachineName: env.COMPUTERNAME, jobStartedUtc: job.started_at, workflowCommit: env.GITHUB_WORKFLOW_SHA };
}
async function inspect(staticResultPath, allowStaticOnly) {
    const current = await invocation();
    const comparison = await api(`compare/${current.before}...${current.triggerCommit}`);
    requireValue(comparison.base_commit?.sha === current.before && comparison.merge_base_commit?.sha === current.before
        && comparison.status === 'ahead' && comparison.total_commits > 0 && comparison.total_commits <= 100
        && comparison.commits?.length === comparison.total_commits && comparison.commits.at(-1)?.sha === current.triggerCommit
        && Array.isArray(comparison.files) && comparison.files.length < 300, 'PushComparisonUnverifiable');
    const changedRequest = comparison.files.find(file => file.filename === requestPath);
    if (allowStaticOnly && (!changedRequest || changedRequest.status === 'removed'))
        return { candidate: false, staticOnly: true };
    requireValue(comparison.total_commits === 1 && comparison.files.length === 1 && changedRequest?.status === 'added', 'DedicatedNewRequestPushRequired');
    const bytes = await content(requestPath, current.triggerCommit);
    const request = canonicalJson(bytes, 'Request');
    keys(request, ['schema', 'requestId', 'repository', 'branch', 'workflowPath', 'helperCommit',
        'sourceCommit', 'artifactSha256', 'createdUtc', 'expiresUtc', 'files', 'preflightSeconds', 'liveSeconds', 'cleanupSeconds', 'certificateMinutes'], 'RequestKeys');
    const producer = JSON.parse(await fs.readFile(path.join(root, 'Tools/BrowserSmoke/network-walkthrough/producer.json'), 'utf8'));
    requireValue(request.schema === 1 && request.requestId === requestId && request.repository === repository
        && request.branch === branch && request.workflowPath === workflow && sha(request.helperCommit)
        && request.sourceCommit === producer.sourceCommit && request.artifactSha256 === producer.artifactSha256
        && request.preflightSeconds === 180 && request.liveSeconds === 480 && request.cleanupSeconds === 60
        && request.certificateMinutes === 30, 'RequestValues');
    validity(request.createdUtc, request.expiresUtc, 24, 'Request');
    keys(request.files, files, 'RequestFileSet');
    for (const relative of files) {
        requireValue(digest(request.files[relative]), 'RequestFileDigest');
        const local = await fs.readFile(path.join(root, relative));
        requireValue(hash(local) === request.files[relative], 'LocalApprovedFileChanged');
        requireValue(hash(await content(relative, request.helperCommit)) === request.files[relative], 'ApprovedHelperCommitMismatch');
    }
    const ancestry = await api(`compare/${request.helperCommit}...${current.before}`);
    requireValue(['identical', 'ahead'].includes(ancestry.status) && ancestry.behind_by === 0
        && ancestry.merge_base_commit?.sha === request.helperCommit, 'HelperCommitNotAncestor');
    const staticBytes = await fs.readFile(staticResultPath);
    const staticResult = JSON.parse(staticBytes);
    requireValue(staticResult.result === 'passed' && staticResult.powershellParsed === true
        && staticResult.embeddedPowerShellParsed === true && staticResult.ownedJobCompiled === true
        && staticResult.certificateMutationReached === false, 'WindowsStaticPrerequisiteFailed');
    for (const relative of files.filter(name => name !== 'Tools/BrowserSmoke/network-walkthrough/README.md'))
        requireValue(staticResult.files?.[relative] === request.files[relative], 'StaticCheckedBytesChanged');
    return { candidate: true, schema: 1, repository, requestPath, requestId, requestSha256: hash(bytes), request,
        ...current, staticResultPath: path.relative(root, staticResultPath).replaceAll('\\', '/'),
        staticResultSha256: hash(staticBytes), runRootRelative: `Build/_AgentValidation/20261005-170000-network-walkthrough-${current.runId}` };
}
async function verifyAdmission(file) {
    const saved = canonicalJson(await fs.readFile(file), 'Admission');
    const current = await inspect(path.resolve(root, saved.staticResultPath), false);
    requireValue(JSON.stringify(saved) === JSON.stringify(current), 'PreparedAdmissionChanged');
    return current;
}
async function verifyPrepared(file, admitted) {
    const bytes = await fs.readFile(file);
    const state = JSON.parse(bytes);
    requireValue(state.schema === 1 && state.result === 'prepared' && state.requestSha256 === admitted.requestSha256
        && state.triggerCommit === admitted.triggerCommit && state.runId === admitted.runId && state.runAttempt === 1
        && state.jobId === admitted.jobId && state.staticResultSha256 === admitted.staticResultSha256
        && state.sourceCommit === admitted.request.sourceCommit && state.artifactSha256 === admitted.request.artifactSha256
        && state.certificateMutationReached === false && state.preflightPassed === true && state.allOwnedProcessesExited === true,
    'PreparedStateNotQualified');
    return { state, sha256: hash(bytes) };
}
async function latestActivation() {
    const reference = await api(`git/ref/heads/${branch}`);
    requireValue(sha(reference.object?.sha), 'ActivationCommitMissing');
    const bytes = await content(activationPath, reference.object.sha, true);
    return bytes === null ? null : { bytes, commit: reference.object.sha };
}
async function validateActivation(found, admitted, prepared) {
    const activation = canonicalJson(found.bytes, 'Activation');
    keys(activation, ['schema', 'requestId', 'requestSha256', 'helperCommit', 'triggerCommit', 'workflowSha256',
        'runId', 'runAttempt', 'jobId', 'runnerName', 'preparedStateSha256', 'staticResultSha256',
        'prerequisiteArtifactId', 'prerequisiteArtifactDigest', 'approvedAtUtc', 'expiresUtc',
        'certificateMinutes', 'liveSeconds', 'cleanupSeconds'], 'ActivationKeys');
    requireValue(activation.schema === 1 && activation.requestId === requestId
        && activation.requestSha256 === admitted.requestSha256 && activation.helperCommit === admitted.request.helperCommit
        && activation.triggerCommit === admitted.triggerCommit && activation.workflowSha256 === admitted.request.files[workflow]
        && activation.runId === admitted.runId && activation.runAttempt === 1 && activation.jobId === admitted.jobId
        && activation.runnerName === admitted.runnerName && activation.preparedStateSha256 === prepared.sha256
        && activation.staticResultSha256 === admitted.staticResultSha256 && id(activation.prerequisiteArtifactId)
        && /^sha256:[0-9a-f]{64}$/.test(activation.prerequisiteArtifactDigest)
        && activation.certificateMinutes === 30 && activation.liveSeconds === 480 && activation.cleanupSeconds === 60,
    'ActivationDoesNotAuthorizeThisPreparedJob');
    validity(activation.approvedAtUtc, activation.expiresUtc, 1, 'Activation');
    requireValue(Date.parse(activation.expiresUtc) - Date.now() >= 9 * 60000, 'ActivationWindowTooShort');
    requireValue(Date.parse(activation.expiresUtc) <= Date.parse(admitted.request.expiresUtc), 'ActivationExceedsRequestWindow');
    const artifact = await api(`actions/artifacts/${activation.prerequisiteArtifactId}`);
    requireValue(String(artifact.workflow_run?.id) === admitted.runId && artifact.workflow_run?.head_sha === admitted.triggerCommit
        && artifact.name === `network-walkthrough-prerequisites-${admitted.runId}-1` && artifact.expired === false
        && artifact.digest === activation.prerequisiteArtifactDigest, 'PrerequisiteEvidenceProvenance');
    requireValue(String(process.env.XRE_NETWORK_PREREQUISITE_ARTIFACT_ID) === activation.prerequisiteArtifactId,
        'ThisJobDidNotUploadPrerequisiteArtifact');
    requireValue(Number.isFinite(Date.parse(admitted.jobStartedUtc))
        && Date.now() < Date.parse(admitted.jobStartedUtc) + 64 * 60000, 'JobLacksFullLiveCleanupWindow');
    return { schema: 1, activationPath, activationCommit: found.commit, activationSha256: hash(found.bytes), activation };
}
const { positionals, values } = parseArgs({ allowPositionals: true, options: {
    output: { type: 'string' }, admission: { type: 'string' }, state: { type: 'string' },
    activation: { type: 'string' }, 'static-result': { type: 'string' },
} });
try {
    const [mode, ...extra] = positionals;
    requireValue(extra.length === 0, 'UnexpectedArguments');
    let result;
    if (mode === 'inspect') result = await inspect(path.resolve(values['static-result']), true);
    else if (mode === 'verify-request') result = await verifyAdmission(values.admission);
    else if (mode === 'wait-activation' || mode === 'verify-activation') {
        const admitted = await verifyAdmission(values.admission);
        const prepared = await verifyPrepared(values.state, admitted);
        if (mode === 'wait-activation') {
            const deadline = Math.min(Date.now() + 10 * 60000, Date.parse(admitted.request.expiresUtc));
            while (Date.now() < deadline) {
                const found = await latestActivation();
                if (found) { result = await validateActivation(found, admitted, prepared); break; }
                await new Promise(resolve => setTimeout(resolve, Math.min(10000, Math.max(1, deadline - Date.now()))));
            }
            requireValue(result, 'NoActivationBeforeDeadline');
        } else {
            const saved = canonicalJson(await fs.readFile(values.activation), 'ActivationEnvelope');
            const immutable = await content(activationPath, saved.activationCommit);
            requireValue(hash(immutable) === saved.activationSha256, 'ImmutableActivationChanged');
            const latest = await latestActivation();
            requireValue(latest && hash(latest.bytes) === saved.activationSha256, 'ActivationRevokedOrChanged');
            result = await validateActivation({ bytes: immutable, commit: saved.activationCommit }, admitted, prepared);
            requireValue(JSON.stringify(result) === JSON.stringify(saved), 'ActivationEnvelopeChanged');
        }
    } else throw new AdmissionFailure('UnknownMode');
    if (values.output) await fs.writeFile(values.output, JSON.stringify(result, null, 2), { flag: 'wx' });
    if (mode === 'inspect' && process.env.GITHUB_OUTPUT)
        await fs.appendFile(process.env.GITHUB_OUTPUT, `candidate=${result.candidate ? 'true' : 'false'}\n`);
    process.stdout.write(JSON.stringify({ result: 'passed', mode, candidate: result.candidate ?? true }) + '\n');
} catch (error) {
    process.stderr.write((error instanceof AdmissionFailure ? error.message : 'NetworkWalkthroughAdmissionFailed') + '\n');
    process.exitCode = 1;
}
