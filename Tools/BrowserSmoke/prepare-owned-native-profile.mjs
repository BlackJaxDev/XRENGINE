import { createHash } from 'node:crypto';
import { promises as fs } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { inflateRawSync } from 'node:zlib';

// This program only prepares an already published bundle. The browser process never receives GH_TOKEN.
const REQUEST_PATH = '.github/diagnostic-requests/owned-native-shadow-profile-20261007.json';
const ACTIVATION_PATH = '.github/diagnostic-activations/owned-native-shadow-profile-20261007.json';
export const REQUEST_SHA256 = 'e5c0c38fa7c86fe6b2690fa7b7e1612146afd97c3fa8a6ae8c9c68326c3160b3';
const REPOSITORY = 'BlackJaxDev/XRENGINE';
const BRANCH = 'codex/webgpu-readiness-audit';
const WORKFLOW = 'Owned native shadow profile once';
const MAX_API_BYTES = 256 * 1024;
const MAX_ZIP_BYTES = 56_878_765;
const MAX_FILE_BYTES = 128 * 1024 * 1024;
const MAX_EXTRACTED_BYTES = 768 * 1024 * 1024;
const MAX_FILES = 8192;
const GATE_WAIT_MS = 8 * 60 * 1000;
const GATE_POLL_MS = 5000;

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
function requireField(condition, message) { if (!condition) throw new Error(message); }

function exactKeys(value, keys, label) {
    requireField(value && !Array.isArray(value) && typeof value === 'object' &&
        Object.keys(value).sort().join('\0') === [...keys].sort().join('\0'), `${label} has unexpected fields.`);
}

// Comparing compact source text catches duplicate/escaped JSON keys without adding a parser dependency.
function parseFlatJson(bytes, label) {
    const source = bytes.toString('utf8');
    requireField(Buffer.from(source, 'utf8').equals(bytes), `${label} is not UTF-8.`);
    const value = JSON.parse(source);
    let compact = '', quoted = false, escaped = false;
    for (const ch of source) {
        if (!quoted && /\s/.test(ch)) continue;
        compact += ch;
        if (escaped) { escaped = false; continue; }
        if (quoted && ch === '\\') { escaped = true; continue; }
        if (ch === '"') quoted = !quoted;
    }
    requireField(!quoted && compact === JSON.stringify(value), `${label} is ambiguous or noncanonical JSON.`);
    return value;
}

export function validateRequest(bytes) {
    requireField(hash(bytes) === REQUEST_SHA256, 'Diagnostic request bytes changed.');
    const request = parseFlatJson(bytes, 'Diagnostic request');
    exactKeys(request, ['schema', 'requestId', 'repository', 'branch', 'workflow', 'profile',
        'sourceRunId', 'sourceCommit', 'sourceArtifactId', 'sourceArtifactName', 'sourceArchiveSha256',
        'sourceArchiveBytes', 'baselineArtifactId', 'baselineArtifactName', 'baselineArchiveSha256',
        'baselineArchiveBytes', 'selectedPass', 'descriptorSha256', 'wgslSha256', 'wgslBytes',
        'entryPoint', 'minimumPendingMilliseconds', 'targetSamplingMilliseconds',
        'maximumCollectorMilliseconds'], 'Diagnostic request');
    requireField(request.schema === 1 && request.requestId === 'owned-native-shadow-profile-20261007-06b49ec1'
        && request.repository === REPOSITORY && request.branch === BRANCH && request.workflow === WORKFLOW
        && request.profile === 'Native' && request.sourceRunId === 37546153259
        && request.sourceCommit === '37db74ddb1524b31f4536b0d95556919d88fa689'
        && request.sourceArtifactId === 11451741926
        && request.sourceArtifactName === 'windows-editor-advanced-shadow-parity-bundle'
        && request.sourceArchiveSha256 === '238a375e3c29f54ea53a510f29333f41ecde0ab1749deaae777e3813f5ffb69a'
        && request.sourceArchiveBytes === 56_878_530
        && request.baselineArtifactId === 11451337986
        && request.baselineArtifactName === 'windows-editor-advanced-shadow-parity-off-bundle'
        && request.baselineArchiveSha256 === '8871ac4bd9d226960892bc681596476a22757a53b3a0eb458340e802f48ac2a4'
        && request.baselineArchiveBytes === MAX_ZIP_BYTES
        && request.selectedPass === 'shade-native-depth-no-decals'
        && request.descriptorSha256 === 'f81f44c6868773f0936efebaf72f03b146bc6d6c98aacf4e0f462d044eaf8a6c'
        && request.wgslSha256 === '15c26ae39bfb45b1640e9091e381c357fd889429b84888fe61f49f05b66269df'
        && request.wgslBytes === 344_708 && request.entryPoint === 'advancedShadeNative'
        && request.minimumPendingMilliseconds === 20_000
        && request.targetSamplingMilliseconds === 8000 && request.maximumCollectorMilliseconds === 10000,
    'Diagnostic request values changed.');
    return request;
}

export function validateInvocation(env, event) {
    const sha = env.GITHUB_SHA, runId = env.GITHUB_RUN_ID;
    requireField(env.GITHUB_EVENT_NAME === 'push' && env.GITHUB_REPOSITORY === REPOSITORY
        && env.GITHUB_REF === `refs/heads/${BRANCH}` && env.GITHUB_REF_TYPE === 'branch'
        && env.GITHUB_WORKFLOW === WORKFLOW
        && env.GITHUB_WORKFLOW_REF === `${REPOSITORY}/.github/workflows/browser-native-shadow-profile-once.yml@refs/heads/${BRANCH}`
        && env.GITHUB_RUN_ATTEMPT === '1' && /^[0-9a-f]{40}$/.test(sha ?? '')
        && /^[1-9][0-9]*$/.test(runId ?? ''), 'This is not the admitted first-attempt workflow run.');
    requireField(event?.ref === env.GITHUB_REF && event?.after === sha
        && event?.repository?.full_name === REPOSITORY
        && event?.repository?.fork === false && event?.forced === false
        && event?.deleted === false, 'Push event does not match the admitted repository and commit.');
    requireField(/^[0-9a-f]{40}$/.test(event.before ?? '') && event.before !== '0'.repeat(40)
        && event.before !== sha, 'Push event has no verifiable predecessor.');
    return { sha, runId, before: event.before };
}

export function validateTriggerComparison(comparison, invocation) {
    requireField(comparison?.base_commit?.sha === invocation.before
        && comparison?.merge_base_commit?.sha === invocation.before
        && comparison?.status === 'ahead'
        && Number.isInteger(comparison.total_commits) && comparison.total_commits > 0
        && comparison.total_commits <= 100
        && comparison.ahead_by === comparison.total_commits && comparison.behind_by === 0
        && Array.isArray(comparison.commits) && comparison.commits.length === comparison.total_commits
        && comparison.commits.at(-1)?.sha === invocation.sha
        && Array.isArray(comparison.files) && comparison.files.length < 300
        && comparison.files.some(file => file.filename === REQUEST_PATH
            && ['added', 'modified'].includes(file.status)),
    'The exact push did not change the immutable request.');
}

export function validateActivation(bytes, request, invocation) {
    requireField(bytes.length <= 4096, 'Activation file exceeds its limit.');
    const activation = parseFlatJson(bytes, 'Activation');
    exactKeys(activation, ['schema', 'requestId', 'requestSha256', 'triggerCommit', 'workflowRunId'], 'Activation');
    requireField(activation.schema === 1 && activation.requestId === request.requestId
        && activation.requestSha256 === REQUEST_SHA256 && activation.triggerCommit === invocation.sha
        && String(activation.workflowRunId) === invocation.runId
        && (typeof activation.workflowRunId === 'number' && Number.isSafeInteger(activation.workflowRunId)
            || typeof activation.workflowRunId === 'string' && /^[1-9][0-9]*$/.test(activation.workflowRunId)),
    'Activation does not match this exact admitted run.');
    return activation;
}

function allowedRedirect(url) {
    const host = url.hostname.toLowerCase();
    return url.protocol === 'https:' && !url.username && !url.password && !url.hash &&
        (host === 'api.github.com' || host === 'objects.githubusercontent.com'
            || host === 'release-assets.githubusercontent.com'
            || host.endsWith('.blob.core.windows.net') || /^github-production-[a-z0-9-]+\.s3(?:\.[a-z0-9-]+)?\.amazonaws\.com$/.test(host));
}

async function requestApi(token, uri, { binary = false, maxBytes = MAX_API_BYTES } = {}) {
    let url = new URL(uri);
    for (let redirects = 0; redirects <= 3; redirects++) {
        requireField(allowedRedirect(url), 'Artifact endpoint left the approved hosts.');
        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), binary ? 120000 : 20000);
        try {
            const response = await fetch(url, { redirect: 'manual', signal: controller.signal,
                headers: redirects === 0 && url.hostname === 'api.github.com' ? {
                    Authorization: `Bearer ${token}`, Accept: 'application/vnd.github+json',
                    'X-GitHub-Api-Version': '2022-11-28', 'User-Agent': 'xrengine-owned-native-profile-once',
                } : { 'User-Agent': 'xrengine-owned-native-profile-once' } });
            if ([301, 302, 303, 307, 308].includes(response.status)) {
                const location = response.headers.get('location');
                requireField(location && redirects < 3, 'Artifact redirect is missing or excessive.');
                await response.body?.cancel();
                url = new URL(location, url);
                continue;
            }
            if (response.status === 404 && !binary) { await response.body?.cancel(); return null; }
            requireField(response.ok, 'GitHub did not return the requested diagnostic resource.');
            const declared = Number(response.headers.get('content-length'));
            requireField(!Number.isFinite(declared) || declared <= maxBytes, 'Diagnostic resource exceeds its limit.');
            const chunks = [];
            let length = 0;
            for await (const chunk of response.body) {
                length += chunk.length;
                requireField(length <= maxBytes, 'Diagnostic resource exceeds its limit.');
                chunks.push(chunk);
            }
            return Buffer.concat(chunks, length);
        } finally {
            clearTimeout(timer);
        }
    }
    throw new Error('Artifact redirect was excessive.');
}

async function apiJson(token, endpoint, maxBytes = MAX_API_BYTES) {
    const bytes = await requestApi(token, `https://api.github.com/repos/${REPOSITORY}/${endpoint}`,
        { maxBytes });
    return bytes === null ? null : JSON.parse(bytes.toString('utf8'));
}

async function pollActivation(token, request, invocation) {
    const deadline = Date.now() + GATE_WAIT_MS;
    const endpoint = `contents/${ACTIVATION_PATH}?ref=${encodeURIComponent(BRANCH)}`;
    while (Date.now() < deadline) {
        const file = await apiJson(token, endpoint);
        if (file !== null) {
            requireField(file.type === 'file' && file.path === ACTIVATION_PATH
                && file.encoding === 'base64' && typeof file.content === 'string'
                && file.size <= 4096, 'Activation is not a bounded same-branch file.');
            const bytes = Buffer.from(file.content.replace(/\s/g, ''), 'base64');
            const blobId = createHash('sha1').update(`blob ${bytes.length}\0`).update(bytes).digest('hex');
            requireField(bytes.length === file.size && blobId === file.sha,
                'Activation bytes are incomplete.');
            return { activation: validateActivation(bytes, request, invocation), bytes };
        }
        await sleep(Math.min(GATE_POLL_MS, Math.max(0, deadline - Date.now())));
    }
    throw new Error('No exact activation appeared before the gate deadline.');
}

function zipPath(name) {
    requireField(typeof name === 'string' && name.length > 0 && name.length <= 512
        && !name.startsWith('/') && !name.includes('\\') && !name.includes('\0')
        && !name.includes(':')
        && name.split('/').every(segment => segment !== '' && segment !== '.' && segment !== '..'),
    'ZIP contains an unsafe path.');
    return name;
}

function crc32(bytes) {
    let crc = -1;
    for (const byte of bytes) {
        crc ^= byte;
        for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0);
    }
    return (crc ^ -1) >>> 0;
}

export async function extractVerifiedZip(archive, destination) {
    requireField(archive.length <= MAX_ZIP_BYTES, 'Artifact archive exceeds its limit.');
    const eocdMin = Math.max(0, archive.length - 65557);
    let eocd = -1;
    for (let at = archive.length - 22; at >= eocdMin; at--)
        if (archive.readUInt32LE(at) === 0x06054b50 && at + 22 + archive.readUInt16LE(at + 20) === archive.length) { eocd = at; break; }
    requireField(eocd >= 0, 'Artifact is not a bounded ZIP archive.');
    const count = archive.readUInt16LE(eocd + 10), centralSize = archive.readUInt32LE(eocd + 12),
        centralOffset = archive.readUInt32LE(eocd + 16);
    requireField(archive.readUInt16LE(eocd + 4) === 0 && archive.readUInt16LE(eocd + 6) === 0
        && count === archive.readUInt16LE(eocd + 8) && count > 0 && count <= MAX_FILES
        && centralSize <= 8 * 1024 * 1024 && centralOffset + centralSize <= eocd,
    'Artifact ZIP directory is unsupported.');
    const files = [];
    let offset = centralOffset, total = 0;
    const seen = new Set();
    for (let i = 0; i < count; i++) {
        requireField(offset + 46 <= centralOffset + centralSize && archive.readUInt32LE(offset) === 0x02014b50,
            'Artifact ZIP directory is corrupt.');
        const madeBy = archive.readUInt8(offset + 5), flags = archive.readUInt16LE(offset + 8),
            method = archive.readUInt16LE(offset + 10), crc = archive.readUInt32LE(offset + 16),
            compressed = archive.readUInt32LE(offset + 20), expanded = archive.readUInt32LE(offset + 24),
            nameLength = archive.readUInt16LE(offset + 28), extraLength = archive.readUInt16LE(offset + 30),
            commentLength = archive.readUInt16LE(offset + 32), attrs = archive.readUInt32LE(offset + 38),
            local = archive.readUInt32LE(offset + 42);
        const end = offset + 46 + nameLength + extraLength + commentLength;
        requireField(end <= centralOffset + centralSize && compressed <= MAX_ZIP_BYTES
            && expanded <= MAX_FILE_BYTES && (flags & ~0x0808) === 0
            && (method === 0 || method === 8) && local < centralOffset,
        'Artifact ZIP entry is unsupported.');
        const rawName = archive.subarray(offset + 46, offset + 46 + nameLength).toString('utf8');
        const directory = rawName.endsWith('/');
        const name = zipPath(directory ? rawName.slice(0, -1) : rawName);
        requireField(!seen.has(name), 'Artifact ZIP has a duplicate path.');
        seen.add(name);
        const fileType = (attrs >>> 16) & 0o170000;
        requireField(directory
            ? (fileType === 0o040000 || fileType === 0) && compressed === 0 && expanded === 0 && crc === 0
            : (fileType === 0o100000 || madeBy !== 3 && fileType === 0),
            'Artifact ZIP contains a nonregular entry.');
        requireField(offset + 46 + nameLength + extraLength + commentLength === end
            && local + 30 <= centralOffset && archive.readUInt32LE(local) === 0x04034b50,
        'Artifact ZIP local entry is corrupt.');
        const localFlags = archive.readUInt16LE(local + 6), localMethod = archive.readUInt16LE(local + 8),
            localNameLength = archive.readUInt16LE(local + 26), localExtraLength = archive.readUInt16LE(local + 28),
            content = local + 30 + localNameLength + localExtraLength;
        requireField(localFlags === flags && localMethod === method && content + compressed <= centralOffset
            && archive.subarray(local + 30, local + 30 + localNameLength).equals(Buffer.from(rawName, 'utf8')),
        'Artifact ZIP local entry disagrees with its directory.');
        const encoded = archive.subarray(content, content + compressed);
        const bytes = method === 0 ? encoded : inflateRawSync(encoded, { maxOutputLength: MAX_FILE_BYTES });
        requireField(bytes.length === expanded && crc32(bytes) === crc, 'Artifact ZIP payload failed verification.');
        total += bytes.length;
        requireField(total <= MAX_EXTRACTED_BYTES, 'Artifact expansion exceeds its limit.');
        if (!directory) files.push({ name, bytes });
        offset = end;
    }
    requireField(offset === centralOffset + centralSize, 'Artifact ZIP directory has trailing entries.');
    for (const { name, bytes } of files) {
        const target = path.join(destination, ...name.split('/'));
        await fs.mkdir(path.dirname(target), { recursive: true, mode: 0o700 });
        await fs.writeFile(target, bytes, { flag: 'wx', mode: 0o600 });
    }
}

function artifactPath(url) {
    requireField(typeof url === 'string' && /^payload\/[0-9a-f]{64}\.bin$/.test(url),
        'Published shader payload path is invalid.');
    return path.join('content', url);
}

export async function validatePublishedBundle(root, request, state) {
    requireField(state === 'on' || state === 'off', 'Shadow bundle role is invalid.');
    const readSmall = async relative => {
        const bytes = await fs.readFile(path.join(root, relative));
        requireField(bytes.length <= 1024 * 1024, 'Published metadata exceeds its limit.');
        return bytes;
    };
    const descriptor = JSON.parse((await readSmall('browser-publish.json')).toString('utf8'));
    const manifest = JSON.parse((await readSmall(path.join('content', 'manifest.json'))).toString('utf8'));
    requireField(descriptor.schema === 2 && descriptor.format === 'xrengine-engine-launch'
        && descriptor.manifest === './content/manifest.json' && manifest.schema === 1
        && manifest.format === 'xrengine-assets'
        && manifest.startupWorld === '/game/Worlds/AdvancedRenderingParityWorld.asset',
    'Published Advanced bundle identity is invalid.');
    const assets = new Map((manifest.assets ?? []).map(asset => [asset.path, asset]));
    const shaders = new Map((manifest.shaderArtifacts ?? []).map(shader => [shader.identity, shader]));
    requireField(assets.size === manifest.assets?.length && shaders.size === manifest.shaderArtifacts?.length,
        'Published bundle catalogs contain duplicates.');
    const advancedPasses = ['visibility-pull', 'depth-pyramid', 'gtao', 'shade-classify', 'shade-native',
        'present', request.selectedPass];
    const advancedBindings = new Map();
    for (const pass of advancedPasses) {
        const matches = (manifest.pipelineArtifacts ?? []).filter(value => value.scope === 'advanced' && value.pass === pass);
        requireField(matches.length === 1 && shaders.has(matches[0].descriptorIdentity),
            'Published Advanced shader binding is missing or ambiguous.');
        advancedBindings.set(pass, matches[0].descriptorIdentity);
    }
    const descriptors = new Map();
    for (const shader of shaders.values()) {
        requireField(/^[0-9a-f]{64}$/.test(shader.identity ?? ''), 'Published shader identity is invalid.');
        for (const assetPath of [shader.descriptor, shader.source]) {
            const asset = assets.get(assetPath);
            requireField(asset && Number.isSafeInteger(asset.bytes) && asset.bytes > 0
                && /^[0-9a-f]{64}$/.test(asset.hash ?? ''), 'Published shader asset is invalid.');
            const bytes = await fs.readFile(path.join(root, artifactPath(asset.url)));
            requireField(bytes.length === asset.bytes && hash(bytes) === asset.hash,
                'Published shader payload hash differs from its manifest.');
            if (assetPath === shader.descriptor) {
                requireField(hash(bytes) === shader.identity, 'Published shader descriptor identity differs.');
                const metadata = JSON.parse(bytes.toString('utf8'));
                const source = assets.get(shader.source);
                requireField(metadata.source?.sha256 === source.hash && metadata.source?.byteLength === source.bytes,
                    'Published shader descriptor/source provenance differs.');
                descriptors.set(shader.identity, metadata);
            }
        }
    }
    for (const [pass, identity] of advancedBindings) {
        const metadata = descriptors.get(identity);
        requireField(metadata?.schemaVersion === 3 && metadata?.pass === pass
            && metadata?.target === 'WebGPUWgsl', 'Published Advanced descriptor does not match its binding.');
    }
    const selectedIdentity = advancedBindings.get(request.selectedPass);
    const selected = descriptors.get(selectedIdentity);
    requireField(selectedIdentity === request.descriptorSha256
        && selected.name === `engine-advanced-${request.selectedPass}`
        && selected.source.sha256 === request.wgslSha256
        && selected.source.byteLength === request.wgslBytes
        && selected.entryPoints?.compute === request.entryPoint
        && Object.keys(selected.entryPoints).length === 1,
    'Published shadow compiler input differs from the immutable request.');
    return { shaderCount: shaders.size };
}

async function main() {
    const env = process.env;
    requireField(env.GH_TOKEN && env.GITHUB_OUTPUT && env.RUNNER_TEMP && env.GITHUB_EVENT_PATH,
        'Preparation is missing its ephemeral CI context.');
    const eventBytes = await fs.readFile(env.GITHUB_EVENT_PATH);
    requireField(eventBytes.length <= 1024 * 1024, 'Push event exceeds its limit.');
    const invocation = validateInvocation(env, JSON.parse(eventBytes.toString('utf8')));
    const request = validateRequest(await fs.readFile(REQUEST_PATH));
    requireField(invocation.sha !== request.sourceCommit,
        'Profiler code and source bundle must have distinct commit identities.');
    const comparison = await apiJson(env.GH_TOKEN,
        `compare/${invocation.before}...${invocation.sha}?per_page=100`, 1024 * 1024);
    validateTriggerComparison(comparison, invocation);
    const runnerTemp = await fs.realpath(env.RUNNER_TEMP);
    const privateRoot = await fs.mkdtemp(path.join(runnerTemp, 'owned-native-profile-'));
    await fs.chmod(privateRoot, 0o700);
    let ready = false;
    try {
        const { bytes: activation } = await pollActivation(env.GH_TOKEN, request, invocation);
        const activationFile = path.join(privateRoot, 'activation.json');
        await fs.writeFile(activationFile, activation, { flag: 'wx', mode: 0o600 });
        const sourceRun = await apiJson(env.GH_TOKEN, `actions/runs/${request.sourceRunId}`);
        requireField(sourceRun?.id === request.sourceRunId && sourceRun?.repository?.full_name === REPOSITORY
            && sourceRun?.head_repository?.full_name === REPOSITORY
            && sourceRun?.repository?.id === 652511392 && sourceRun?.head_repository?.id === 652511392
            && sourceRun?.head_branch === BRANCH
            && sourceRun?.head_sha === request.sourceCommit && sourceRun?.status === 'completed'
            && sourceRun?.event === 'push' && sourceRun?.run_attempt === 1
            && sourceRun?.path === '.github/workflows/portable-browser-compile.yml',
        'Source run provenance differs.');
        const sources = [
            { state: 'on', id: request.sourceArtifactId, name: request.sourceArtifactName,
                sha256: request.sourceArchiveSha256, bytes: request.sourceArchiveBytes },
            { state: 'off', id: request.baselineArtifactId, name: request.baselineArtifactName,
                sha256: request.baselineArchiveSha256, bytes: request.baselineArchiveBytes },
        ];
        let shaderCount = 0;
        for (const source of sources) {
            const artifact = await apiJson(env.GH_TOKEN, `actions/artifacts/${source.id}`);
            requireField(artifact?.id === source.id && artifact?.name === source.name
                && artifact?.workflow_run?.id === request.sourceRunId
                && artifact?.workflow_run?.repository_id === 652511392
                && artifact?.workflow_run?.head_repository_id === 652511392
                && artifact?.workflow_run?.head_branch === BRANCH
                && artifact?.workflow_run?.head_sha === request.sourceCommit && artifact?.expired === false
                && artifact?.size_in_bytes === source.bytes && artifact?.digest === `sha256:${source.sha256}`,
            'Source artifact provenance, size, or service digest differs.');
            const archive = await requestApi(env.GH_TOKEN,
                `https://api.github.com/repos/${REPOSITORY}/actions/artifacts/${source.id}/zip`,
                { binary: true, maxBytes: source.bytes });
            requireField(archive?.length === source.bytes && hash(archive) === source.sha256,
                'Source ZIP SHA-256 differs from the immutable request.');
            source.published = path.join(privateRoot, `published-${source.state}`);
            await fs.mkdir(source.published, { mode: 0o700 });
            await extractVerifiedZip(archive, source.published);
            const bundle = await validatePublishedBundle(source.published, request, source.state);
            shaderCount += bundle.shaderCount;
        }
        await fs.appendFile(env.GITHUB_OUTPUT,
            `game_publish=${sources[0].published}\nbaseline_publish=${sources[1].published}\nactivation_file=${activationFile}\nprivate_root=${privateRoot}\n`,
            { encoding: 'utf8' });
        ready = true;
        console.log(`Verified one admitted activation, two exact shadow bundles, and ${shaderCount} shader descriptors.`);
    } finally {
        if (!ready) await fs.rm(privateRoot, { recursive: true, force: true });
    }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    main().catch(() => { console.error('Owned native profile preparation failed closed.'); process.exitCode = 1; });
}
