import fs from 'node:fs/promises';
import { constants } from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import { getUiFrameTraceAuthorization } from './ui-frame-trace.mjs';

const git = promisify(execFile);
const repository = 'BlackJaxDev/XRENGINE';
const ref = 'refs/heads/codex/webgpu-readiness-audit';
const workflowPath = '.github/workflows/portable-browser-compile.yml';
const workflowRef = `${repository}/${workflowPath}@${ref}`;
const job = 'published-game';
const gameKind = 'ui-parity';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const sha = value => typeof value === 'string' && /^[0-9a-f]{40}$/.test(value);
const runId = value => typeof value === 'string' && /^[1-9][0-9]{0,19}$/.test(value);
const assert = (condition, code) => { if (!condition) throw new Error(code); };
const equalKeys = (value, names) => value !== null && typeof value === 'object' && !Array.isArray(value)
    && Object.keys(value).sort().join('\0') === [...names].sort().join('\0');

async function readRegular(file, maximum, privateOwner = false) {
    assert(typeof file === 'string' && path.isAbsolute(file), 'LocalPathRejected');
    const handle = await fs.open(file, constants.O_RDONLY | constants.O_NOFOLLOW | constants.O_NONBLOCK | constants.O_CLOEXEC);
    try {
        const stat = await handle.stat();
        assert(stat.isFile() && stat.size <= maximum && (!privateOwner ||
            (stat.uid === process.getuid() && !(stat.mode & 0o022))), 'LocalFileRejected');
        const bytes = Buffer.alloc(maximum + 1);
        const { bytesRead } = await handle.read(bytes, 0, bytes.length, 0);
        assert(bytesRead === stat.size && bytesRead <= maximum, 'LocalFileSizeChanged');
        return bytes.subarray(0, bytesRead);
    } finally { await handle.close(); }
}

async function ownedDirectory(file) {
    assert(typeof file === 'string' && path.isAbsolute(file) && !/[\r\n\0]/.test(file), 'TemporaryPathRejected');
    const absolute = path.resolve(file);
    assert(absolute === file, 'TemporaryPathRejected');
    let part = path.parse(absolute).root;
    for (const name of absolute.slice(part.length).split(path.sep).filter(Boolean)) {
        part = path.join(part, name);
        const stat = await fs.lstat(part);
        assert(stat.isDirectory() && !stat.isSymbolicLink() && !(stat.mode & 0o002), 'TemporaryDirectoryRejected');
    }
    const stat = await fs.lstat(absolute);
    assert(stat.uid === process.getuid() && !(stat.mode & 0o022), 'TemporaryDirectoryRejected');
    return absolute;
}

async function fileCommandPath(file, temporaryRoot) {
    assert(typeof file === 'string' && path.isAbsolute(file) && !/[\r\n\0]/.test(file)
        && file.startsWith(`${temporaryRoot}${path.sep}`),
        'FileCommandPathRejected');
    const parent = await ownedDirectory(path.dirname(file));
    assert(parent.startsWith(`${temporaryRoot}${path.sep}`), 'FileCommandPathRejected');
    const handle = await fs.open(file, constants.O_RDONLY | constants.O_NOFOLLOW | constants.O_NONBLOCK | constants.O_CLOEXEC);
    try {
        const stat = await handle.stat();
        assert(stat.isFile() && stat.uid === process.getuid() && !(stat.mode & 0o022), 'FileCommandRejected');
    } finally { await handle.close(); }
}

async function fileCommand(file, value, temporaryRoot) {
    await fileCommandPath(file, temporaryRoot);
    const handle = await fs.open(file, constants.O_WRONLY | constants.O_APPEND | constants.O_NOFOLLOW | constants.O_NONBLOCK | constants.O_CLOEXEC);
    try {
        const stat = await handle.stat();
        assert(stat.isFile() && stat.uid === process.getuid() && !(stat.mode & 0o022), 'FileCommandRejected');
        await handle.writeFile(value);
    } finally { await handle.close(); }
}

async function api(url, deadline, optional = false) {
    assert(Date.now() < deadline, 'SetupDeadline');
    assert(url.origin === 'https://api.github.com' && url.pathname.startsWith(`/repos/${repository}/`), 'ApiTargetRejected');
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), Math.min(5000, Math.max(1, deadline - Date.now())));
    try {
        const headers = { accept: 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28',
            'User-Agent': 'xrengine-ui-frame-trace' };
        if (process.env.GH_TOKEN) headers.authorization = `Bearer ${process.env.GH_TOKEN}`;
        const response = await fetch(url, { redirect: 'error', signal: controller.signal, headers });
        assert(response.url === url.href, 'ApiRedirectRejected');
        if (optional && response.status === 404) { await response.body?.cancel(); return null; }
        assert(response.ok && response.body, 'ApiReadFailed');
        const length = Number(response.headers.get('content-length'));
        assert(!Number.isFinite(length) || length <= 16 * 1024, 'ApiResponseTooLarge');
        const reader = response.body.getReader();
        const chunks = []; let count = 0;
        try {
            while (true) {
                const { done, value } = await reader.read();
                if (done) break;
                count += value.byteLength;
                assert(count <= 16 * 1024, 'ApiResponseTooLarge');
                chunks.push(value);
            }
        } finally { reader.releaseLock(); }
        return JSON.parse(Buffer.concat(chunks, count).toString('utf8'));
    } finally { controller.abort(); clearTimeout(timer); }
}

function canonicalActivation(bytes) {
    assert(bytes.length > 0 && bytes.length <= 4096, 'ActivationSizeRejected');
    const value = bytes.toString('utf8');
    assert(Buffer.from(value, 'utf8').equals(bytes), 'ActivationUtf8Rejected');
    const record = JSON.parse(value);
    assert(value === JSON.stringify(record) || value === `${JSON.stringify(record)}\n`, 'ActivationCanonicalJsonRejected');
    const names = ['schema', 'requestId', 'activationPath', 'priorCommit', 'triggerCommit', 'workflowRunId',
        'runAttempt', 'gameKind', 'repository', 'ref', 'workflowPath', 'job'];
    assert(equalKeys(record, names), 'ActivationKeysRejected');
    return record;
}

async function main() {
    const authorization = getUiFrameTraceAuthorization();
    if (!authorization.enabled) return;
    const deadline = Date.now() + 20000;
    assert(sha(authorization.priorCommit) && authorization.activationPath ===
        '.github/diagnostic-activations/ui-frame-trace-loaded-20261006.json' &&
        authorization.requestId === '3e875a33-496f-4054-a609-af4970a51fe1', 'SourcePolicyRejected');
    const env = process.env;
    assert(process.platform === 'linux' && Number.isSafeInteger(process.getuid?.())
        && process.getuid() > 0 && process.getuid() === process.geteuid?.(), 'RunnerIdentityRejected');
    const exact = { GITHUB_ACTIONS: 'true', GITHUB_EVENT_NAME: 'push', GITHUB_RUN_ATTEMPT: '1',
        GITHUB_REPOSITORY: repository, GITHUB_REF: ref, GITHUB_WORKFLOW: 'Portable browser build and publish',
        GITHUB_WORKFLOW_REF: workflowRef, GITHUB_JOB: job, GAME_KIND: gameKind };
    for (const [key, value] of Object.entries(exact)) assert(env[key] === value, 'WorkflowGateRejected');
    assert(sha(env.GITHUB_SHA) && env.GITHUB_SHA !== authorization.priorCommit
        && env.GITHUB_WORKFLOW_SHA === env.GITHUB_SHA && runId(env.GITHUB_RUN_ID), 'WorkflowIdentityRejected');
    assert(await fs.realpath(env.GITHUB_WORKSPACE ?? '') === await fs.realpath(root), 'WorkspaceRejected');
    const temporaryRoot = await ownedDirectory(env.RUNNER_TEMP);
    assert(typeof env.GITHUB_EVENT_PATH === 'string' && env.GITHUB_EVENT_PATH.startsWith(`${temporaryRoot}${path.sep}`),
        'EventPathRejected');
    await ownedDirectory(path.dirname(env.GITHUB_EVENT_PATH));
    const event = JSON.parse((await readRegular(env.GITHUB_EVENT_PATH, 256 * 1024, true)).toString('utf8'));
    assert(event.ref === ref && event.after === env.GITHUB_SHA && event.repository?.full_name === repository
        && event.repository?.fork === false && sha(event.before), 'PushEventRejected');
    if (event.before !== authorization.priorCommit) return;
    assert(event.forced === false && event.created === false && event.deleted === false
        && Array.isArray(event.commits) && event.commits.length === 1 && event.commits[0]?.id === event.after
        && event.head_commit?.id === event.after, 'OneCommitPushRequired');
    const { stdout: head } = await git('git', ['rev-parse', '--verify', 'HEAD'],
        { cwd: root, timeout: 2000, maxBuffer: 128, env: { PATH: env.PATH } });
    assert(head.trim() === env.GITHUB_SHA, 'CheckoutMismatch');
    assert(typeof env.GITHUB_ENV === 'string' && typeof env.GITHUB_OUTPUT === 'string', 'FileCommandsMissing');
    await fileCommandPath(env.GITHUB_ENV, temporaryRoot);
    await fileCommandPath(env.GITHUB_OUTPUT, temporaryRoot);
    assert(Date.now() < deadline, 'SetupDeadline');
    const branchUrl = new URL(`https://api.github.com/repos/${repository}/git/ref/heads/codex/webgpu-readiness-audit`);
    const branch = await api(branchUrl, deadline);
    assert(branch.ref === ref && branch.object?.type === 'commit' && sha(branch.object.sha), 'ActivationRefRejected');
    const activationCommit = branch.object.sha;
    const contentsUrl = new URL(`https://api.github.com/repos/${repository}/contents/${authorization.activationPath}`);
    contentsUrl.searchParams.set('ref', activationCommit);
    const file = await api(contentsUrl, deadline, true);
    assert(file !== null, 'ActivationMissing');
    assert(file.type === 'file' && file.path === authorization.activationPath && file.name === path.basename(authorization.activationPath)
        && file.encoding === 'base64' && Number.isSafeInteger(file.size) && file.size > 0 && file.size <= 4096
        && sha(file.sha) && typeof file.content === 'string', 'ActivationFileRejected');
    const base64 = file.content.replace(/\n/g, '');
    assert(/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(base64),
        'ActivationBase64Rejected');
    const bytes = Buffer.from(base64, 'base64');
    assert(bytes.length === file.size && bytes.toString('base64') === base64
        && createHash('sha1').update(`blob ${bytes.length}\0`).update(bytes).digest('hex') === file.sha,
    'ActivationBlobRejected');
    const record = canonicalActivation(bytes);
    assert(record.schema === 1 && record.requestId === authorization.requestId
        && record.activationPath === authorization.activationPath && record.priorCommit === authorization.priorCommit
        && record.triggerCommit === env.GITHUB_SHA && record.workflowRunId === env.GITHUB_RUN_ID
        && record.runAttempt === 1 && record.gameKind === gameKind && record.repository === repository
        && record.ref === ref && record.workflowPath === workflowPath && record.job === job,
    'ActivationIdentityRejected');
    assert(Date.now() < deadline, 'SetupDeadline');
    const directory = await fs.mkdtemp(path.join(temporaryRoot, 'ui-frame-trace-'));
    await fs.chmod(directory, 0o700);
    const activationFile = path.join(directory, 'activation.json');
    const handle = await fs.open(activationFile,
        constants.O_WRONLY | constants.O_CREAT | constants.O_EXCL | constants.O_NOFOLLOW | constants.O_CLOEXEC, 0o600);
    try { await handle.writeFile(bytes); await handle.sync(); } finally { await handle.close(); }
    const digest = createHash('sha256').update(bytes).digest('hex');
    await fileCommand(env.GITHUB_ENV,
        `XRE_UI_FRAME_TRACE_ACTIVATION_FILE=${activationFile}\nXRE_UI_FRAME_TRACE_ACTIVATION_COMMIT=${activationCommit}\nXRE_UI_FRAME_TRACE_ACTIVATION_SHA256=${digest}\n`, temporaryRoot);
    await fileCommand(env.GITHUB_OUTPUT, 'armed=true\n', temporaryRoot);
    process.stdout.write('UI frame trace activation armed\n');
}

try { await main(); }
catch { process.stderr.write('UI frame trace preparation failed\n'); process.exitCode = 1; }
