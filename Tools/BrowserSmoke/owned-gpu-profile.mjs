import fs from 'node:fs/promises';
import { constants, closeSync, openSync, writeSync } from 'node:fs';
import { spawn, execFile } from 'node:child_process';
import { createHash } from 'node:crypto';
import path from 'node:path';

// This observer is deliberately unavailable to ordinary smoke runs. Its one-shot
// permission is consumed even when setup, ownership validation or recording fails.
const perfPath = '/usr/lib/linux-azure-6.17-tools-6.17.0-1022/perf';
const perfPackage = 'linux-tools-6.17.0-1022-azure';
const perfVersion = '6.17.0-1022.22';
const cleanEnv = { PATH: '/usr/bin:/bin', LC_ALL: 'C', PERF_CONFIG: '/dev/null', DEBUGINFOD_URLS: '' };
const limits = Object.freeze({ raw: 16 * 1024 * 1024, stderr: 64 * 1024, summary: 256 * 1024,
    control: 1024, tids: 64, startupMs: 2000, collectorMs: 10000, analysisMs: 5000 });
const ackFrame = Buffer.from([0x61, 0x63, 0x6b, 0x0a, 0]);
const noFollow = constants.O_NOFOLLOW | constants.O_CLOEXEC;
const requestId = 'owned-native-profile-20261005-7f240de6';
const requestHash = '8ac2d5c4fa0ff42e575e8768758e6c21f5b45f6005d31e65df0acf5babb2311b';
const requestFile = '.github/diagnostic-requests/owned-native-profile-20261005.json';
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const fail = code => { throw Object.assign(new Error(code), { profileCode: code }); };
const requireProfile = (condition, code) => { if (!condition) fail(code); };
const safeReason = error => /^[A-Za-z][A-Za-z0-9]{1,80}$/.test(error?.profileCode ?? '')
    ? error.profileCode : 'Unavailable';
const now = () => performance.now();

async function bounded(promise, milliseconds, code) {
    let timer;
    try { return await Promise.race([promise, new Promise((_, reject) => {
        timer = setTimeout(() => reject(Object.assign(new Error(code), { profileCode: code })), Math.max(1, milliseconds));
    })]); } finally { clearTimeout(timer); }
}

async function readSmall(file, maximum = 64 * 1024) {
    const handle = await fs.open(file, constants.O_RDONLY | constants.O_CLOEXEC);
    try {
        const bytes = Buffer.alloc(maximum + 1);
        const { bytesRead } = await handle.read(bytes, 0, bytes.length, 0);
        requireProfile(bytesRead <= maximum, 'IdentityByteLimit');
        return bytes.subarray(0, bytesRead);
    } finally { await handle.close(); }
}

async function nativeCommand(executable, args, milliseconds = 1000, maximum = 64 * 1024) {
    return new Promise((resolve, reject) => execFile(executable, args,
        { env: cleanEnv, shell: false, timeout: milliseconds, maxBuffer: maximum, killSignal: 'SIGKILL', encoding: 'utf8' },
        (error, stdout) => error ? reject(Object.assign(new Error('NativeCommandUnavailable'),
            { profileCode: 'NativeCommandUnavailable' })) : resolve(stdout)));
}

function parseIdentity(stat, status) {
    const match = stat.match(/^(\d+) \(.+\) ([A-ZI]) (.*)$/s);
    requireProfile(match, 'ProcStatRejected');
    const fields = `${match[2]} ${match[3]}`.trim().split(/\s+/);
    const value = key => status.match(new RegExp(`^${key}:\\s*([^\\n]+)$`, 'm'))?.[1]?.trim();
    const uid = value('Uid')?.split(/\s+/).map(Number);
    const identity = { pid: Number(match[1]), tgid: Number(value('Tgid')), ppid: Number(fields[1]),
        pgrp: Number(fields[2]), start: fields[19], state: fields[0], uid };
    requireProfile(Number(value('Pid')) === identity.pid && Number(value('PPid')) === identity.ppid
        && /^\d+$/.test(identity.start) && uid?.length === 4 && uid.every(Number.isSafeInteger)
        && ['pid', 'tgid', 'ppid', 'pgrp'].every(key => Number.isSafeInteger(identity[key])), 'ProcIdentityRejected');
    return identity;
}

/** Original proc directory handles never resolve a subsequently reused numeric PID. */
class PinnedProcess {
    static async open(file, expectedPid, uid, handles, executable = true) {
        const directory = await fs.open(file, constants.O_RDONLY | constants.O_DIRECTORY | noFollow);
        const pinned = new PinnedProcess(directory, expectedPid, uid, executable);
        handles.push(pinned);
        pinned.original = await pinned.identity();
        requireProfile(pinned.original.pid === expectedPid, 'ProcPidMismatch');
        return pinned;
    }
    constructor(directory, pid, uid, executable) {
        this.directory = directory; this.pid = pid; this.uid = uid; this.checkExecutable = executable;
        this.root = `/proc/self/fd/${directory.fd}`;
    }
    async identity() {
        const [stat, status] = await Promise.all([readSmall(`${this.root}/stat`, 8192), readSmall(`${this.root}/status`, 8192)]);
        const identity = parseIdentity(stat.toString('utf8'), status.toString('utf8'));
        requireProfile(identity.pid === this.pid && (this.uid === null
            ? identity.uid.every(value => value === 0 || value === 1001) && identity.uid[1] === 0
            : identity.uid.every(value => value === this.uid)), 'ProcOwnerMismatch');
        requireProfile(!['Z', 'X', 'x'].includes(identity.state), 'OwnedTaskExited');
        if (this.checkExecutable) {
            const [file, executable] = await Promise.all([fs.readlink(`${this.root}/exe`), fs.stat(`${this.root}/exe`, { bigint: true })]);
            requireProfile(path.isAbsolute(file) && file.length <= 4096 && !file.endsWith(' (deleted)'), 'ExecutableRejected');
            identity.executable = file; identity.device = executable.dev.toString(); identity.inode = executable.ino.toString();
        }
        return identity;
    }
    async revalidate() {
        const current = await this.identity();
        requireProfile(JSON.stringify(current.uid) === JSON.stringify(this.original.uid), 'OriginalIdentityChanged');
        for (const key of ['pid', 'tgid', 'ppid', 'pgrp', 'start', 'executable', 'device', 'inode'])
            requireProfile(current[key] === this.original[key], 'OriginalIdentityChanged');
        return current;
    }
    async exited() {
        try {
            const stat = (await readSmall(`${this.root}/stat`, 8192)).toString('utf8');
            const match = stat.match(/^(\d+) \(.+\) ([A-ZI]) (.*)$/s);
            requireProfile(match && Number(match[1]) === this.pid, 'ExitIdentityUnverified');
            const fields = `${match[2]} ${match[3]}`.trim().split(/\s+/);
            requireProfile(fields[19] === this.original.start, 'ExitIdentityUnverified');
            return ['Z', 'X', 'x'].includes(fields[0]);
        } catch (error) {
            if (['ENOENT', 'ESRCH'].includes(error.code)) return true;
            throw error;
        }
    }
    async close() { await this.directory.close(); }
}

async function getOwnedIds(session) {
    const { processInfo } = await bounded(session.send('SystemInfo.getProcessInfo'), 500, 'OwnedCdpTimeout');
    requireProfile(Array.isArray(processInfo) && processInfo.length <= 1024, 'OwnedCdpRejected');
    const id = type => {
        const found = processInfo.filter(value => value.type.toLowerCase() === type);
        requireProfile(found.length === 1 && Number.isSafeInteger(found[0].id) && found[0].id > 0, 'OwnedCdpAmbiguous');
        return found[0].id;
    };
    return { browser: id('browser'), gpu: id('gpu') };
}

async function pinOwnedTargets(browser, handles) {
    const session = await bounded(browser.newBrowserCDPSession(), 500, 'OwnedCdpTimeout');
    try {
        const ids = await getOwnedIds(session);
        const owner = await PinnedProcess.open(`/proc/${ids.browser}`, ids.browser, 1001, handles);
        const gpu = await PinnedProcess.open(`/proc/${ids.gpu}`, ids.gpu, 1001, handles);
        requireProfile(gpu.original.executable === owner.original.executable
            && gpu.original.inode === owner.original.inode && gpu.original.device === owner.original.device, 'GpuExecutableMismatch');
        const gpuArgs = (await readSmall(`${gpu.root}/cmdline`)).toString('utf8').split('\0');
        const browserArgs = (await readSmall(`${owner.root}/cmdline`)).toString('utf8').split('\0');
        requireProfile(gpuArgs.includes('--type=gpu-process') && !browserArgs.some(value => value.startsWith('--type=')), 'GpuRoleMismatch');
        async function ancestry(start, destination) {
            let child = start;
            for (let depth = 0; depth < 16; depth++) {
                if (child.pid === destination) return;
                requireProfile(child.original.ppid > 0, 'OwnedAncestryRejected');
                const parent = handles.find(value => value.pid === child.original.ppid)
                    ?? await PinnedProcess.open(`/proc/${child.original.ppid}`, child.original.ppid, 1001, handles);
                child = parent;
            }
            fail('OwnedAncestryLimit');
        }
        await ancestry(owner, process.pid);
        await ancestry(gpu, ids.browser);
        const tasks = await fs.readdir(`${gpu.root}/task`);
        requireProfile(tasks.length > 0 && tasks.length <= limits.tids && tasks.every(value => /^[1-9]\d*$/.test(value)), 'GpuThreadLimit');
        const tids = tasks.map(Number).sort((a, b) => a - b);
        requireProfile(tids.includes(ids.gpu) && tids.every(Number.isSafeInteger), 'GpuThreadsRejected');
        for (const tid of tids) {
            const task = await PinnedProcess.open(`${gpu.root}/task/${tid}`, tid, 1001, handles);
            requireProfile(task.original.tgid === ids.gpu && task.original.executable === gpu.original.executable
                && task.original.inode === gpu.original.inode && task.original.device === gpu.original.device, 'GpuThreadGroupMismatch');
        }
        return { session, ids, owner, gpu, tids };
    } catch (error) { await session.detach().catch(() => {}); throw error; }
}

/** Fixed arguments; callers cannot supply events, tools, privileges, or target IDs. */
export function ownedGpuRecordArguments(tids, directory) {
    requireProfile(Array.isArray(tids) && tids.length > 0 && tids.length <= limits.tids
        && tids.every((value, index) => Number.isSafeInteger(value) && value > 0 && (!index || value > tids[index - 1])), 'GpuThreadsRejected');
    requireProfile(path.isAbsolute(directory) && !directory.includes('\0') && directory.length <= 4096, 'TemporaryPathRejected');
    return ['-n', '--', '/usr/bin/timeout', '--signal=INT', '--kill-after=1s', '9s',
        '/usr/bin/env', '-i', 'PATH=/usr/bin:/bin', 'LC_ALL=C', 'PERF_CONFIG=/dev/null', 'DEBUGINFOD_URLS=', perfPath,
        'record', '--event=cpu-clock:u', '--all-user', '--user-callchains', '--freq=49', '--strict-freq', '--call-graph=fp,32',
        `--tid=${tids.join(',')}`, '--no-inherit', '--per-thread', '--mmap-pages=16', '--timestamp', '--clockid=monotonic',
        '--delay=-1', `--control=fifo:${directory}/control,${directory}/ack`, '--synth=mmap', '--no-bpf-event',
        '--no-buildid-cache', '--max-size=8M', '--output=-'];
}

/** Read exactly one NUL-terminated v6.17 ack per command; processing is not ioctl success. */
export class OwnedGpuControl {
    constructor(control, ack) { this.control = control; this.ack = ack; this.pending = false; this.failed = false; }
    async command(command, budgetMs) {
        requireProfile(['ping', 'enable', 'disable', 'stop'].includes(command) && !this.pending && !this.failed, 'ControlStateRejected');
        this.pending = true;
        try {
            const data = Buffer.from(`${command}\n`);
            const { bytesWritten } = await this.control.write(data, 0, data.length, null);
            requireProfile(bytesWritten === data.length, 'ControlWriteIncomplete');
            const deadline = now() + budgetMs, received = Buffer.alloc(limits.control);
            let length = 0;
            while (now() < deadline) {
                try {
                    const { bytesRead } = await this.ack.read(received, length, received.length - length, null);
                    length += bytesRead;
                    requireProfile(length <= ackFrame.length && received.subarray(0, length).equals(ackFrame.subarray(0, length)), 'ControlAckRejected');
                    if (length === ackFrame.length) return;
                } catch (error) { if (!['EAGAIN', 'EWOULDBLOCK'].includes(error.code)) throw error; }
                await pause(Math.min(5, Math.max(1, deadline - now())));
            }
            fail('ControlAckTimeout');
        } catch (error) { this.failed = true; throw error; }
        finally { this.pending = false; }
    }
    async requestStop() {
        // Once framing is uncertain, don't consume another ack as a new command's
        // response. A stop write remains safe; the root timeout owns final cleanup.
        const bytes = Buffer.from('stop\n');
        try { await this.control.write(bytes, 0, bytes.length, null); } catch { /* Root timeout remains active. */ }
    }
}

async function checkInstalledCollector() {
    const executable = await fs.lstat(perfPath);
    requireProfile(executable.isFile() && executable.uid === 0 && !(executable.mode & 0o022)
        && (executable.mode & 0o111) !== 0 && !(executable.mode & 0o6000), 'InstalledPerfRejected');
    const packageText = await nativeCommand('/usr/bin/dpkg-query',
        ['-W', '-f=${binary:Package}\t${Version}\t${db:Status-Status}\n', perfPackage]);
    requireProfile(packageText === `${perfPackage}\t${perfVersion}\tinstalled\n`, 'InstalledPerfVersionChanged');
    const ownerText = await nativeCommand('/usr/bin/dpkg-query', ['-S', perfPath]);
    requireProfile(ownerText.trim().endsWith(`: ${perfPath}`)
        && /^[a-z0-9.+-]+: \/usr\/lib\/linux-azure-6\.17-tools-6\.17\.0-1022\/perf\n?$/.test(ownerText), 'InstalledPerfPackageRejected');
    const actualPackage = ownerText.slice(0, ownerText.indexOf(':'));
    const actualVersion = await nativeCommand('/usr/bin/dpkg-query',
        ['-W', '-f=${Version}\t${db:Status-Status}\n', actualPackage]);
    requireProfile(actualVersion === `${perfVersion}\tinstalled\n`, 'InstalledPerfVersionChanged');
    // No collector is run as a preflight. The disabled recording attempt is the
    // only event/flag/access probe, and failure consumes the authorization.
    return { executable: 'perf', package: actualPackage, version: perfVersion,
        inode: String(executable.ino), byteLength: executable.size };
}

async function createTemporaryDirectory(output) {
    requireProfile(path.isAbsolute(process.env.RUNNER_TEMP ?? ''), 'RunnerTemporaryDirectoryMissing');
    const temporaryRoot = await fs.realpath(process.env.RUNNER_TEMP);
    const outputRoot = await fs.realpath(output);
    requireProfile(temporaryRoot !== outputRoot && !temporaryRoot.startsWith(`${outputRoot}${path.sep}`), 'TemporaryArtifactOverlap');
    const directory = await fs.mkdtemp(path.join(temporaryRoot, 'owned-native-profile-'));
    await fs.chmod(directory, 0o700);
    const identity = await fs.lstat(directory);
    requireProfile(identity.isDirectory() && !identity.isSymbolicLink() && identity.uid === 1001
        && (identity.mode & 0o7777) === 0o700, 'TemporaryDirectoryRejected');
    return { directory, identity };
}

async function openControlFiles(directory) {
    await nativeCommand('/usr/bin/mkfifo', ['--mode=600', '--', `${directory}/control`, `${directory}/ack`]);
    const opened = [];
    try {
        for (const name of ['control', 'ack']) {
            const file = `${directory}/${name}`, before = await fs.lstat(file);
            requireProfile(before.isFIFO() && !before.isSymbolicLink() && before.uid === 1001
                && (before.mode & 0o7777) === 0o600, 'ControlFifoRejected');
            const handle = await fs.open(file, constants.O_RDWR | constants.O_NONBLOCK | noFollow);
            opened.push(handle);
            const after = await handle.stat();
            requireProfile(after.isFIFO() && after.ino === before.ino && after.dev === before.dev
                && after.uid === 1001 && (after.mode & 0o7777) === 0o600, 'ControlFifoChanged');
        }
        return { handles: opened, protocol: new OwnedGpuControl(opened[0], opened[1]) };
    } catch (error) { for (const handle of opened) await handle.close().catch(() => {}); throw error; }
}

async function descendants(rootPid, handles, deadline) {
    const seen = new Map();
    const root = await PinnedProcess.open(`/proc/${rootPid}`, rootPid, null, handles, false);
    seen.set(rootPid, root);
    while (now() < deadline) {
        for (const current of [...seen.values()]) {
            await current.revalidate();
            current.command = (await readSmall(`${current.root}/cmdline`, 8192)).toString('utf8').split('\0').filter(Boolean);
            const text = (await readSmall(`${current.root}/task/${current.pid}/children`, 1024)).toString('utf8').trim();
            requireProfile(!text || /^\d+(?: \d+)*$/.test(text), 'SupervisorChildrenRejected');
            for (const pid of text ? text.split(' ').map(Number) : []) {
                if (seen.has(pid)) continue;
                requireProfile(seen.size < 12, 'SupervisorChildrenLimit');
                const child = await PinnedProcess.open(`/proc/${pid}`, pid, null, handles, false);
                requireProfile(child.original.ppid === current.pid, 'SupervisorAncestryRejected');
                seen.set(pid, child);
            }
        }
        const timeout = [...seen.values()].filter(value => value.command?.[0] === '/usr/bin/timeout');
        const recorder = [...seen.values()].filter(value => value.command?.[0] === perfPath);
        if (timeout.length === 1 && recorder.length === 1) return { root, timeout: timeout[0], recorder: recorder[0], tree: [...seen.values()] };
        requireProfile(timeout.length <= 1 && recorder.length <= 1, 'SupervisorIdentityAmbiguous');
        await pause(5);
    }
    fail('SupervisorIdentityUnavailable');
}

async function verifySupervisorTree(tree, args) {
    const expectedTimeout = args.slice(2), expectedPerf = args.slice(args.indexOf(perfPath));
    requireProfile(JSON.stringify(tree.timeout.command) === JSON.stringify(expectedTimeout)
        && JSON.stringify(tree.recorder.command) === JSON.stringify(expectedPerf)
        && tree.recorder.original.ppid === tree.timeout.pid
        && tree.timeout.original.pgrp === tree.timeout.pid && tree.recorder.original.pgrp === tree.timeout.pid
        && tree.timeout.original.uid.every(value => value === 0)
        && tree.recorder.original.uid.every(value => value === 0), 'SupervisorIdentityRejected');
    const expectedSudo = JSON.stringify(['/usr/bin/sudo', ...args]);
    const wrappers = tree.tree.filter(value => value !== tree.timeout && value !== tree.recorder);
    requireProfile(wrappers.length >= 1 && wrappers.length <= 2 && wrappers.includes(tree.root)
        && wrappers.every(value => JSON.stringify(value.command) === expectedSudo), 'UnexpectedPrivilegedDescendant');
    let parent = tree.timeout.original.ppid;
    const visited = new Set();
    for (let index = 0; index < wrappers.length; index++) {
        const wrapper = wrappers.find(value => value.pid === parent);
        requireProfile(wrapper && !visited.has(wrapper), 'SupervisorAncestryRejected');
        visited.add(wrapper); parent = wrapper.original.ppid;
    }
    requireProfile(visited.size === wrappers.length && parent === process.pid, 'SupervisorAncestryRejected');
    for (const current of tree.tree) {
        await current.revalidate();
        const argsNow = (await readSmall(`${current.root}/cmdline`, 8192)).toString('utf8').split('\0').filter(Boolean);
        requireProfile(JSON.stringify(argsNow) === JSON.stringify(current.command), 'SupervisorIdentityChanged');
    }
}

async function readElfManifest(file, expectedInode = null) {
    const handle = await fs.open(file, constants.O_RDONLY | constants.O_CLOEXEC);
    try {
        const stat = await handle.stat({ bigint: true });
        requireProfile(stat.isFile() && (!expectedInode || stat.ino.toString() === expectedInode), 'ModuleIdentityChanged');
        const read = async (position, count) => {
            requireProfile(Number.isSafeInteger(position) && position >= 0 && count <= 65536, 'ElfLayoutRejected');
            const bytes = Buffer.alloc(count), { bytesRead } = await handle.read(bytes, 0, count, position);
            requireProfile(bytesRead === count, 'ElfTruncated'); return bytes;
        };
        const header = await read(0, 64);
        requireProfile(header.subarray(0, 6).equals(Buffer.from([0x7f, 0x45, 0x4c, 0x46, 2, 1])), 'ElfFormatRejected');
        const count = header.readUInt16LE(56), size = header.readUInt16LE(54), offset = Number(header.readBigUInt64LE(32));
        requireProfile(count > 0 && count <= 256 && size === 56, 'ElfLayoutRejected');
        const programs = await read(offset, size * count), ids = new Set();
        for (let index = 0; index < count; index++) {
            const start = index * size;
            if (programs.readUInt32LE(start) !== 4) continue;
            const position = Number(programs.readBigUInt64LE(start + 8));
            const length = Number(programs.readBigUInt64LE(start + 32));
            requireProfile(length <= 65536, 'ElfNoteLimit');
            const notes = await read(position, length);
            let cursor = 0;
            while (cursor < notes.length) {
                requireProfile(cursor + 12 <= notes.length, 'ElfNoteRejected');
                const names = notes.readUInt32LE(cursor), description = notes.readUInt32LE(cursor + 4), type = notes.readUInt32LE(cursor + 8);
                const nameStart = cursor + 12, valueStart = nameStart + ((names + 3) & ~3);
                cursor = valueStart + ((description + 3) & ~3);
                requireProfile(cursor <= notes.length, 'ElfNoteRejected');
                if (type === 3 && names === 4 && notes.subarray(nameStart, nameStart + 4).equals(Buffer.from('GNU\0'))) {
                    requireProfile(description >= 8 && description <= 64, 'ElfBuildIdRejected');
                    ids.add(notes.subarray(valueStart, valueStart + description).toString('hex'));
                }
            }
        }
        requireProfile(ids.size === 1, 'ElfBuildIdUnavailable');
        const basename = path.basename(file);
        requireProfile(/^[A-Za-z0-9_.+-]{1,160}$/.test(basename), 'ModuleBasenameRejected');
        return { module: basename, buildId: [...ids][0], inode: stat.ino.toString(), device: stat.dev.toString() };
    } finally { await handle.close(); }
}

async function consumeAuthorization(config) {
    requireProfile(process.platform === 'linux' && process.getuid?.() === 1001 && process.geteuid?.() === 1001, 'RunnerIdentityRejected');
    requireProfile(config.nativeOwnedProfileOnce === true && config.gameOnly === true
        && config.gameKind === 'advanced-rendering-parity' && config.gpuMode === 'software', 'ProfileScopeRejected');
    const exact = { GITHUB_EVENT_NAME: 'push', GITHUB_RUN_ATTEMPT: '1', GITHUB_REPOSITORY: 'BlackJaxDev/XRENGINE',
        GITHUB_REF: 'refs/heads/codex/webgpu-readiness-audit', GITHUB_WORKFLOW: 'Owned native GPU profile once',
        GITHUB_WORKFLOW_REF: 'BlackJaxDev/XRENGINE/.github/workflows/browser-native-profile-once.yml@refs/heads/codex/webgpu-readiness-audit' };
    for (const [key, value] of Object.entries(exact)) requireProfile(process.env[key] === value, 'WorkflowGateRejected');
    requireProfile(/^[1-9]\d{0,19}$/.test(process.env.GITHUB_RUN_ID ?? '')
        && /^[0-9a-f]{40}$/.test(process.env.GITHUB_SHA ?? ''), 'WorkflowIdentityRejected');
    const request = await readSmall(path.resolve(requestFile), 4096);
    requireProfile(createHash('sha256').update(request).digest('hex') === requestHash, 'RequestIdentityRejected');
    const expectedRequest = JSON.parse(request.toString('utf8'));
    requireProfile(expectedRequest.requestId === requestId, 'RequestIdentityRejected');
    const temporaryRoot = await fs.realpath(process.env.RUNNER_TEMP ?? '');
    const activationFile = process.env.XRE_OWNED_PROFILE_ACTIVATION_FILE;
    requireProfile(typeof activationFile === 'string' && path.isAbsolute(activationFile), 'ActivationMissing');
    const activationPath = await fs.realpath(activationFile);
    requireProfile(activationPath.startsWith(`${temporaryRoot}${path.sep}`), 'ActivationPathRejected');
    const stat = await fs.lstat(activationFile);
    requireProfile(stat.isFile() && !stat.isSymbolicLink() && stat.uid === 1001
        && !(stat.mode & 0o022) && stat.size <= 4096, 'ActivationFileRejected');
    const activation = JSON.parse((await readSmall(activationFile, 4096)).toString('utf8'));
    requireProfile(activation.schema === 1 && activation.requestId === requestId && activation.requestSha256 === requestHash
        && activation.triggerCommit === process.env.GITHUB_SHA && String(activation.workflowRunId) === process.env.GITHUB_RUN_ID,
    'ActivationIdentityRejected');
    const marker = path.join(temporaryRoot, `owned-native-profile-consumed-${process.env.GITHUB_RUN_ID}`);
    const handle = await fs.open(marker, constants.O_WRONLY | constants.O_CREAT | constants.O_EXCL | noFollow, 0o600);
    try {
        await handle.writeFile(JSON.stringify({ requestId, runId: process.env.GITHUB_RUN_ID, commit: process.env.GITHUB_SHA,
            consumedUtc: new Date().toISOString() }));
        await handle.sync();
    } finally { await handle.close(); }
    // Deliberately never delete the marker, including on setup or ownership failure.
    return { requestId, runId: process.env.GITHUB_RUN_ID, profilerCodeCommit: process.env.GITHUB_SHA,
        sourceBundleCommit: expectedRequest.sourceCommit, sourceBundleRunId: String(expectedRequest.sourceRunId),
        sourceBundleArtifactId: String(expectedRequest.sourceArtifactId), sourceArchiveSha256: expectedRequest.sourceArchiveSha256,
        runAttempt: 1, consumed: true };
}

const sampleMask = 0x10227n, trackingMask = 0x10207n;
const userContext = 0xfffffffffffffe00n;
const maxUserAddress = 0x00007fffffffffffn;

function checkAttribute(attr) {
    requireProfile(attr.length === 136 && attr.readUInt32LE(0) === 1 && attr.readUInt32LE(4) === 136, 'EventAttributeRejected');
    const tracking = attr.readBigUInt64LE(8) === 9n;
    requireProfile(tracking || attr.readBigUInt64LE(8) === 0n, 'EventConfigRejected');
    requireProfile(attr.readBigUInt64LE(16) === 49n && attr.readBigUInt64LE(24) === (tracking ? trackingMask : sampleMask)
        && attr.readBigUInt64LE(32) === 0x14n
        && attr.readBigUInt64LE(40) === (tracking ? 0x423943761n : 0x2240461n)
        && attr.readInt32LE(92) === 1 && attr.readUInt16LE(108) === (tracking ? 0 : 32), 'EventCollectionRejected');
    // Only the exact reviewed fields above may be nonzero. In particular this
    // rejects inherited events, registers, raw stacks, memory, branch and AUX data.
    const rest = Buffer.from(attr);
    for (const [start, end] of [[0, 48], [92, 96], [108, 110]]) rest.fill(0, start, end);
    requireProfile(rest.every(value => value === 0), 'EventExtensionRejected');
    return { tracking, sampleType: tracking ? trackingMask : sampleMask };
}

function terminated(bytes, start, end, maximum = 4096) {
    requireProfile(start < end && end <= bytes.length && end - start <= maximum, 'RecordStringRejected');
    const stop = bytes.indexOf(0, start);
    requireProfile(stop >= start && stop < end && bytes.subarray(stop, end).every(value => value === 0), 'RecordStringRejected');
    return bytes.subarray(start, stop).toString('utf8');
}

/** Bounded pipe-format decoder. Raw addresses and paths never enter its summary. */
export class OwnedGpuPerfDecoder {
    constructor({ pid, tids, write = () => {} }) {
        requireProfile(Number.isSafeInteger(pid) && Array.isArray(tids) && tids.includes(pid) && tids.length <= 64, 'DecoderTargetsRejected');
        this.pid = pid; this.tids = new Set(tids); this.write = write;
        this.pending = Buffer.alloc(0); this.header = false; this.bytes = 0;
        this.attributes = []; this.ids = new Map(); this.maps = []; this.samples = [];
        this.idTids = new Map();
        this.recordCounts = {}; this.finishedInit = false; this.enabled = false;
        this.ksymbolRecordsDiscarded = 0; this.ringLost = 0n; this.finalEventLost = new Map();
        this.throttled = false; this.additionalThreads = new Set();
    }
    push(chunk) {
        this.bytes += chunk.length;
        requireProfile(this.bytes <= limits.raw, 'RawByteLimit');
        // A record's u16 length bounds carry-over. Never concatenate the full capture.
        requireProfile(this.pending.length <= 65535, 'DecoderCarryLimit');
        const bytes = this.pending.length ? Buffer.concat([this.pending, chunk]) : chunk;
        let offset = 0;
        if (!this.header) {
            if (bytes.length < 16) { this.pending = Buffer.from(bytes); return; }
            requireProfile(bytes.subarray(0, 8).equals(Buffer.from('PERFILE2')) && bytes.readBigUInt64LE(8) === 16n, 'PerfPipeHeaderRejected');
            this.write(bytes.subarray(0, 16)); this.header = true; offset = 16;
        }
        while (offset + 8 <= bytes.length) {
            const size = bytes.readUInt16LE(offset + 6);
            requireProfile(size >= 8, 'RecordLengthRejected');
            if (offset + size > bytes.length) break;
            const record = bytes.subarray(offset, offset + size);
            if (this.record(record)) this.write(record);
            offset += size;
        }
        this.pending = Buffer.from(bytes.subarray(offset));
        requireProfile(this.pending.length <= 65535, 'DecoderCarryLimit');
    }
    ready() {
        requireProfile(this.header && this.finishedInit && this.idIndexSeen && this.threadMapSeen && this.featuresComplete && this.attributes.length === 2
            && this.attributes.filter(value => value.tracking).length === 1
            && this.attributes.filter(value => !value.tracking).length === 1, 'DisabledAttributesIncomplete');
        for (const attribute of this.attributes)
            requireProfile(attribute.ids.length === this.tids.size, 'EventTargetCountMismatch');
    }
    finish() { this.ready(); requireProfile(this.pending.length === 0, 'TruncatedRecord'); }
    owner(pid, tid, original = true) {
        requireProfile(pid === this.pid && Number.isSafeInteger(tid) && tid > 0
            && (!original || this.tids.has(tid)), 'RecordOwnershipRejected');
    }
    trailer(record, minimum, synthetic = false, lost = false) {
        requireProfile(record.length >= minimum + 24, 'RecordTrailerTruncated');
        const offset = record.length - 24, pid = record.readUInt32LE(offset), tid = record.readUInt32LE(offset + 4);
        const time = record.readBigUInt64LE(offset + 8), id = record.readBigUInt64LE(offset + 16);
        if (!this.finishedInit && synthetic && pid === 0 && tid === 0 && time === 0n && id === 0n) return offset;
        if (lost && pid === 0 && tid === 0 && time === 0n && this.ids.has(id.toString())) return offset;
        this.owner(pid, tid);
        requireProfile(this.ids.has(id.toString()) && this.idTids.get(id.toString()) === tid, 'RecordEventIdRejected');
        return offset;
    }
    record(record) {
        const type = record.readUInt32LE(0), misc = record.readUInt16LE(4);
        this.recordCounts[type] = (this.recordCounts[type] ?? 0) + 1;
        if (type === 64) {
            requireProfile(misc === 0 && !this.finishedInit && this.attributes.length < 2 && record.length >= 152
                && (record.length - 144) % 8 === 0, 'HeaderAttributeRejected');
            const attribute = checkAttribute(record.subarray(8, 144)), ids = [];
            for (let offset = 144; offset < record.length; offset += 8) {
                const id = record.readBigUInt64LE(offset).toString();
                requireProfile(id !== '0' && !this.ids.has(id) && ids.length < limits.tids, 'EventIdsRejected');
                ids.push(id); this.ids.set(id, attribute);
            }
            attribute.ids = ids; attribute.bytes = Buffer.from(record.subarray(8, 144)); this.attributes.push(attribute); return true;
        }
        if (type === 82 || type === 68) {
            requireProfile(misc === 0 && record.length === 8, 'BoundaryRecordRejected');
            if (type === 82) { requireProfile(!this.finishedInit, 'RepeatedInitialization'); this.finishedInit = true; this.ready(); }
            return true;
        }
        if (type === 9) {
            requireProfile(this.enabled && this.finishedInit && misc === 2 && record.length >= 56, 'SampleContextRejected');
            const attribute = this.ids.get(record.readBigUInt64LE(8).toString());
            requireProfile(attribute && !attribute.tracking, 'SampleEventRejected');
            const ip = record.readBigUInt64LE(16), pid = record.readUInt32LE(24), tid = record.readUInt32LE(28);
            this.owner(pid, tid);
            requireProfile(this.idTids.get(record.readBigUInt64LE(8).toString()) === tid, 'SampleIdThreadMismatch');
            const time = record.readBigUInt64LE(32), period = record.readBigUInt64LE(40), count = record.readBigUInt64LE(48);
            requireProfile(ip <= maxUserAddress && count <= 33n && period > 0n
                && record.length === 56 + Number(count) * 8, 'SampleLayoutRejected');
            const chain = []; let contexts = 0;
            for (let offset = 56; offset < record.length; offset += 8) {
                const address = record.readBigUInt64LE(offset);
                if (address === userContext) { contexts++; requireProfile(contexts === 1 && offset === 56, 'CallchainContextRejected'); }
                else { requireProfile(address <= maxUserAddress && chain.length < 32, 'CallchainAddressRejected'); chain.push(address); }
            }
            requireProfile(this.samples.length < 65536, 'SampleCountLimit');
            this.samples.push({ ip, tid, time, period, chain }); return true;
        }
        if (type === 17) {
            // Stock perf enables this metadata despite user-only samples. Validate
            // framing only, then discard without decoding its address/name fields.
            requireProfile(misc === 0 && record.length >= 56 && record.length <= 560, 'KernelMetadataLayoutRejected');
            const end = this.trailer(record, 25);
            requireProfile((end - 24) % 8 === 0 && [1, 2].includes(record.readUInt16LE(20))
                && record.readUInt16LE(22) <= 1, 'KernelMetadataLayoutRejected');
            const terminator = record.indexOf(0, 24);
            requireProfile(terminator >= 24 && terminator < end && record.subarray(terminator, end).every(value => value === 0), 'KernelMetadataLayoutRejected');
            this.ksymbolRecordsDiscarded++; return false;
        }
        if (type === 3) {
            requireProfile((misc & ~0x2000) === 0, 'CommContextRejected');
            const end = this.trailer(record, 17, true);
            this.owner(record.readUInt32LE(8), record.readUInt32LE(12));
            terminated(record, 16, end, 32); return true;
        }
        if (type === 10) {
            requireProfile((misc & ~0x4000) === 2 && this.maps.length < 4096, 'MappingContextRejected');
            const end = this.trailer(record, 73, true);
            this.owner(record.readUInt32LE(8), record.readUInt32LE(12));
            const start = record.readBigUInt64LE(16), length = record.readBigUInt64LE(24), offset = record.readBigUInt64LE(32);
            const prot = record.readUInt32LE(64), flags = record.readUInt32LE(68);
            const filename = terminated(record, 72, end);
            const vsyscall = filename === '[vsyscall]' && start === 0xffffffffff600000n && length === 4096n && offset === 0n;
            requireProfile((vsyscall || start <= maxUserAddress && length > 0n && start + length <= maxUserAddress + 1n)
                && (prot & 4) !== 0 && !(prot & ~7) && (flags & ~0x7fffffff) === 0, 'MappingLayoutRejected');
            let buildId = null, inode = null;
            if (misc & 0x4000) {
                const size = record[40];
                requireProfile(size >= 1 && size <= 20 && record[41] === 0 && record.readUInt16LE(42) === 0
                    && record.subarray(44 + size, 64).every(value => value === 0), 'MappingBuildIdRejected');
                buildId = record.subarray(44, 44 + size).toString('hex');
            } else inode = record.readBigUInt64LE(48).toString();
            requireProfile(filename.length > 0 && (path.isAbsolute(filename) || /^\[[A-Za-z0-9_:.-]+\]$/.test(filename)
                || filename === '//anon'), 'MappingFilenameRejected');
            if (vsyscall) return false;
            this.maps.push({ start, end: start + length, offset, filename, buildId, inode,
                sampleIndex: this.samples.length, executable: true }); return true;
        }
        if (type === 4 || type === 7) {
            requireProfile(misc === 0 && record.length === 56, 'TaskRecordRejected');
            this.trailer(record, 32);
            const pid = record.readUInt32LE(8), ppid = record.readUInt32LE(12), tid = record.readUInt32LE(16), ptid = record.readUInt32LE(20);
            this.owner(pid, tid, type === 4);
            if (type === 7) this.owner(ppid, ptid);
            else requireProfile(ppid > 0 && ptid > 0, 'TaskParentRejected');
            if (!this.tids.has(tid)) { requireProfile(this.additionalThreads.size < 256, 'AdditionalThreadLimit'); this.additionalThreads.add(tid); }
            return false;
        }
        if (type === 2 || type === 13) {
            requireProfile(misc === 0 && record.length === (type === 2 ? 48 : 40), 'LostRecordRejected');
            this.trailer(record, type === 2 ? 24 : 16, false, type === 13);
            if (type === 2) requireProfile(this.ids.has(record.readBigUInt64LE(8).toString()), 'LostEventRejected');
            if (type === 2) this.ringLost += record.readBigUInt64LE(16);
            else {
                const id = record.readBigUInt64LE(record.length - 8).toString();
                requireProfile(!this.finalEventLost.has(id), 'RepeatedFinalLoss');
                this.finalEventLost.set(id, record.readBigUInt64LE(8));
            }
            return true;
        }
        if (type === 5 || type === 6) {
            requireProfile(misc === 0 && record.length === 56, 'ThrottleRecordRejected');
            this.trailer(record, 32);
            requireProfile(this.ids.has(record.readBigUInt64LE(16).toString())
                && this.ids.has(record.readBigUInt64LE(24).toString()), 'ThrottleEventRejected');
            this.throttled = true; return true;
        }
        return this.metadata(record, type, misc);
    }
    metadata(record, type, misc) {
        requireProfile(misc === 0 && !this.finishedInit, 'UnexpectedMetadata');
        // Each recognized metadata structure is checked below before being
        // discarded. It is never passed to reporting or included in artifacts.
        if (type === 74) {
            requireProfile(record.length === 16 && record.readUInt16LE(8) === 0 && record.readUInt16LE(10) === 1
                && record.readUInt16LE(12) === 0xffff && record.readUInt16LE(14) === 0, 'CpuMapRejected');
            return false;
        }
        if (type === 73) {
            requireProfile(!this.threadMapSeen && record.length >= 16 && record.readBigUInt64LE(8) === BigInt(this.tids.size)
                && record.length === 16 + this.tids.size * 24, 'ThreadMapRejected');
            const found = new Set();
            for (let offset = 16; offset < record.length; offset += 24) {
                const tid = Number(record.readBigUInt64LE(offset));
                this.owner(this.pid, tid); requireProfile(!found.has(tid), 'ThreadMapRejected'); found.add(tid);
                terminated(record, offset + 8, offset + 24, 16);
            }
            this.threadMapSeen = true; return false;
        }
        if (type === 69) {
            requireProfile(!this.idIndexSeen && record.length >= 16 && record.readBigUInt64LE(8) === BigInt(this.ids.size)
                && record.length === 16 + this.ids.size * 32 && this.ids.size === this.tids.size * 2, 'IdIndexRejected');
            const found = new Set(), pairings = new Set();
            for (let offset = 16; offset < record.length; offset += 32) {
                const id = record.readBigUInt64LE(offset).toString(), index = record.readBigUInt64LE(offset + 8);
                const cpu = record.readBigUInt64LE(offset + 16), tid = Number(record.readBigUInt64LE(offset + 24));
                requireProfile(this.ids.has(id) && !found.has(id) && cpu === 0xffffffffffffffffn
                    && index < BigInt(this.tids.size * 2), 'IdIndexRejected');
                this.owner(this.pid, tid); found.add(id);
                this.idTids.set(id, tid);
                const pairing = `${this.ids.get(id).tracking}:${tid}`;
                requireProfile(!pairings.has(pairing), 'IdThreadPairingRejected'); pairings.add(pairing);
            }
            this.idIndexSeen = true; return false;
        }
        if (type === 79) {
            requireProfile(record.length === 56 && record.readBigUInt64LE(8) <= 63n
                && record.readBigUInt64LE(16) > 0n && record[48] === 1 && record[49] <= 1
                && record.subarray(50).every(value => value === 0), 'TimeConversionRejected');
            return false;
        }
        if (type === 78) {
            requireProfile(record.length >= 40 && record.length % 8 === 0, 'EventUpdateRejected');
            const kind = record.readBigUInt64LE(8), attribute = this.ids.get(record.readBigUInt64LE(16).toString());
            requireProfile(attribute && attribute.ids[0] === record.readBigUInt64LE(16).toString(), 'EventUpdateIdRejected');
            if (kind === 0n || kind === 1n) requireProfile(!attribute.tracking, 'TrackingEventUpdateRejected');
            if (kind === 0n) requireProfile(record.length === 48 && terminated(record, 24, record.length, 24) === 'msec', 'EventUnitRejected');
            else if (kind === 1n) requireProfile(record.length === 48 && record.readDoubleLE(24) === 0.000001
                && record.subarray(32).every(value => value === 0), 'EventScaleRejected');
            else if (kind === 2n) requireProfile(record.length === (attribute.tracking ? 48 : 56)
                && terminated(record, 24, record.length, 32) === (attribute.tracking ? 'dummy:u' : 'cpu-clock:u'), 'EventNameRejected');
            else fail('EventUpdateTypeRejected');
            return false;
        }
        if (type === 80) { this.feature(record); return this.featuresComplete === true; }
        fail('UnexpectedRecordType');
    }
    feature(record) {
        requireProfile(record.length >= 16, 'FeatureTruncated');
        const feature = Number(record.readBigUInt64LE(8));
        requireProfile(Number.isSafeInteger(feature) && feature > (this.lastFeature ?? 0), 'FeatureOrderRejected');
        this.lastFeature = feature;
        let cursor = 16;
        const take = bytes => {
            requireProfile(Number.isSafeInteger(bytes) && bytes >= 0 && cursor + bytes <= record.length, 'FeatureTruncated');
            const start = cursor; cursor += bytes; return start;
        };
        const u32 = () => record.readUInt32LE(take(4));
        const u64 = () => record.readBigUInt64LE(take(8));
        const count = value => { requireProfile(value <= BigInt(record.length), 'FeatureCountRejected'); return Number(value); };
        const string = () => {
            const length = u32(); requireProfile(length > 0 && length % 64 === 0, 'FeatureStringRejected');
            const start = take(length); return terminated(record, start, start + length, 65535);
        };
        const strings = number => { requireProfile(number <= record.length / 4, 'FeatureCountRejected'); for (let i = 0; i < number; i++) string(); };
        switch (feature) {
            case 3: case 4: case 5: case 6: case 8: case 9: string(); break;
            case 7: {
                this.availableCpus = u32(); const online = u32();
                requireProfile(this.availableCpus > 0 && this.availableCpus <= 8192 && online <= this.availableCpus, 'FeatureCpuCountRejected'); break;
            }
            case 10: case 23: u64(); break;
            case 11: strings(u32()); break;
            case 12: {
                requireProfile(u32() === 2 && u32() === 136 && this.attributes.length === 2, 'FeatureEventDescriptionRejected');
                const found = new Set();
                for (let i = 0; i < 2; i++) {
                    const start = take(136), attr = record.subarray(start, start + 136);
                    const match = this.attributes.find(value => value.bytes.equals(attr));
                    requireProfile(match && !found.has(match), 'FeatureEventDescriptionRejected'); found.add(match);
                    const total = u32(); requireProfile(total === match.ids.length, 'FeatureEventDescriptionRejected');
                    requireProfile(string() === (match.tracking ? 'dummy:u' : 'cpu-clock:u'), 'FeatureEventNameRejected');
                    const ids = new Set(); for (let id = 0; id < total; id++) ids.add(u64().toString());
                    requireProfile(ids.size === total && match.ids.every(id => ids.has(id)), 'FeatureEventIdsRejected');
                }
                break;
            }
            case 13: {
                requireProfile(this.availableCpus, 'FeatureTopologyOrderRejected');
                strings(u32()); strings(u32()); take(this.availableCpus * 8);
                if (cursor < record.length) { strings(u32()); take(this.availableCpus * 4); }
                break;
            }
            case 14: {
                const total = count(BigInt(u32()));
                for (let i = 0; i < total; i++) { u32(); u64(); u64(); string(); } break;
            }
            case 16: {
                const total = count(BigInt(u32())); for (let i = 0; i < total; i++) { u32(); string(); } break;
            }
            case 17: requireProfile(u32() === 0, 'FeatureGroupsRejected'); break;
            case 21: u64(); u64(); break;
            case 22: {
                requireProfile(u64() === 1n, 'FeatureMemoryVersionRejected'); u64(); const total = count(u64());
                for (let i = 0; i < total; i++) {
                    u64(); const bits = u64(); requireProfile(bits === u64(), 'FeatureBitmapRejected');
                    take(count((bits + 63n) / 64n) * 8);
                }
                break;
            }
            case 28: {
                const total = count(BigInt(u32())); for (let i = 0; i < total; i++) { string(); string(); } break;
            }
            case 29: requireProfile(u32() === 1 && u32() === 1, 'FeatureClockRejected'); u64(); u64(); break;
            case 31: {
                const total = count(BigInt(u32()));
                for (let i = 0; i < total; i++) { const caps = count(BigInt(u32())); for (let c = 0; c < caps; c++) { string(); string(); } string(); }
                break;
            }
            case 32: this.featuresComplete = true; break;
            default: fail('UnexpectedFeatureType');
        }
        requireProfile(cursor === record.length, 'FeatureLengthRejected');
    }
}

async function summarizeRegions(decoder, executableManifest, deadline) {
    const modules = new Map(), unavailableModules = new Set();
    const paths = [...new Set(decoder.maps.map(value => value.filename).filter(file => path.isAbsolute(file)
        && !file.startsWith('//') && !file.endsWith(' (deleted)')))];
    requireProfile(paths.length <= 128, 'ModuleManifestLimit');
    for (const file of paths) {
        requireProfile(now() < deadline, 'AnalysisTimeout');
        const maps = decoder.maps.filter(value => value.filename === file);
        try {
            const manifest = await readElfManifest(file, maps.find(value => value.inode && value.inode !== '0')?.inode);
            requireProfile(maps.every(value => !value.buildId || value.buildId === manifest.buildId), 'ModuleBuildIdMismatch');
            modules.set(file, manifest);
        } catch { unavailableModules.add(file); }
    }
    const describe = ip => {
        const candidates = decoder.maps.filter(value => ip >= value.start && ip < value.end);
        if (!candidates.length) return null;
        const map = candidates[0];
        // Ring streams are not globally time-ordered. An address range which ever
        // changed identity is unknown rather than guessed from arrival order.
        if (candidates.some(value => value.filename !== map.filename || value.start !== map.start
            || value.offset !== map.offset || value.buildId !== map.buildId)) return null;
        const manifest = modules.get(map.filename);
        if (!manifest) return null;
        return { module: manifest.module, buildId: manifest.buildId,
            fileRegionOffset: `0x${((map.offset + ip - map.start) & ~4095n).toString(16)}` };
    };
    const counts = new Map(), moduleCounts = new Map(), examples = [], seenChains = new Set();
    let unknown = 0;
    for (const sample of decoder.samples) {
        const region = describe(sample.ip);
        if (!region) unknown++;
        else {
            const key = `${region.module}:${region.buildId}:${region.fileRegionOffset}`;
            const entry = counts.get(key) ?? { ...region, samples: 0 }; entry.samples++; counts.set(key, entry);
            const moduleKey = `${region.module}:${region.buildId}`;
            const module = moduleCounts.get(moduleKey) ?? { module: region.module, buildId: region.buildId, samples: 0 };
            module.samples++; moduleCounts.set(moduleKey, module);
        }
        if (examples.length < 8) {
            const chain = sample.chain.slice(0, 32).map(ip => describe(ip) ?? { module: 'unknown' });
            const key = JSON.stringify(chain);
            if (!seenChains.has(key)) { seenChains.add(key); examples.push(chain); }
        }
    }
    const total = decoder.samples.length;
    const sorted = values => [...values].sort((a, b) => b.samples - a.samples).slice(0, 64)
        .map(value => ({ ...value, percent: total ? Number((100 * value.samples / total).toFixed(3)) : 0 }));
    const times = decoder.samples.map(value => value.time);
    const first = times.reduce((a, b) => a === null || b < a ? b : a, null), last = times.reduce((a, b) => a === null || b > a ? b : a, null);
    return { summary: { manifest: [executableManifest, ...modules.values()].filter((value, index, all) =>
            all.findIndex(other => other.module === value.module && other.buildId === value.buildId) === index)
            .map(({ module, buildId }) => ({ module, buildId })),
        samples: total, unknownSamples: unknown, unknownPercent: total ? Number((100 * unknown / total).toFixed(3)) : null,
        modules: sorted(moduleCounts.values()), regions: sorted(counts.values()), regionBytes: 4096, chainExamples: examples,
        firstSampleMonotonicNs: first?.toString() ?? null, lastSampleMonotonicNs: last?.toString() ?? null,
        observedSampleSpanMs: first === null ? null : Number(last - first) / 1e6,
        ringLostSamples: decoder.ringLost.toString(), finalEventLostSamples: [...decoder.finalEventLost.values()].reduce((a, b) => a + b, 0n).toString(),
        lossInterpretation: 'Ring loss and final event loss can overlap; they are not added together.',
        throttled: decoder.throttled, kernelMetadataRecordsDiscarded: decoder.ksymbolRecordsDiscarded,
        additionalThreadsObserved: decoder.additionalThreads.size, attachedOriginalThreads: decoder.tids.size,
        unavailableModuleCount: unavailableModules.size }, modules: new Set([...modules.values()].map(value => value.module)) };
}

async function nativeReport(rawFile, allowedModules, budgetMs) {
    if (budgetMs < 300) return { status: 'unavailable', reason: 'NativeReportBudgetUnavailable', symbolHints: [] };
    const args = ['-i', 'PATH=/usr/bin:/bin', 'LC_ALL=C', 'PERF_CONFIG=/dev/null', 'DEBUGINFOD_URLS=', perfPath,
        '--no-pager', 'report', '--stdio', '--input=-', '--sort=dso,symbol', '--show-nr-samples', '--no-children',
        '--call-graph=none', '--percent-limit=0', '--ignore-vmlinux'];
    const input = await fs.open(rawFile, constants.O_RDONLY | noFollow);
    let timer, finalTimer;
    try {
        return await new Promise(resolve => {
            const child = spawn('/usr/bin/env', args, { shell: false, env: cleanEnv, stdio: ['pipe', 'pipe', 'pipe'] });
            const output = []; let outputBytes = 0, errorBytes = 0, failed = null, settled = false;
            const stop = reason => { failed ??= reason; child.kill('SIGKILL'); };
            timer = setTimeout(() => stop('NativeReportTimeout'), budgetMs - 250);
            const stream = input.createReadStream({ autoClose: false });
            finalTimer = setTimeout(() => {
                settled = true; stream.destroy(); child.stdout.destroy(); child.stderr.destroy();
                resolve({ status: 'unavailable', reason: 'NativeReportExitUnverified', symbolHints: [], exitVerified: false });
            }, budgetMs);
            stream.on('error', () => stop('NativeReportInputFailed'));
            child.stdin.on('error', () => { failed ??= 'NativeReportInputFailed'; stream.destroy(); });
            stream.pipe(child.stdin);
            child.stdout.on('data', bytes => {
                outputBytes += bytes.length;
                if (outputBytes > limits.summary) stop('NativeReportByteLimit'); else output.push(bytes);
            });
            child.stderr.on('data', bytes => {
                errorBytes += bytes.length;
                if (errorBytes > limits.stderr) stop('NativeReportErrorByteLimit');
                // Drain without storing symbol text or file paths from stderr.
            });
            child.on('error', () => { failed ??= 'NativeReportUnavailable'; });
            child.on('close', code => {
                clearTimeout(timer); clearTimeout(finalTimer); stream.destroy();
                if (settled) return;
                settled = true;
                const hints = [];
                if (!failed && code === 0) for (const line of Buffer.concat(output, outputBytes).toString('utf8').split('\n')) {
                    const match = line.match(/^\s*(\d+(?:\.\d+)?)%\s+(\d+)\s+(\S+)\s+\[\.\]\s+(.+?)\s*$/);
                    if (!match || !allowedModules.has(match[3])) continue;
                    const symbol = match[4];
                    if (!/^[A-Za-z_$~][A-Za-z0-9_$~:.<>, ()*&+\[\]-]{0,159}$/.test(symbol)) continue;
                    if (hints.length < 64) hints.push({ module: match[3], symbol, samples: Number(match[2]), percent: Number(match[1]) });
                }
                resolve({ status: failed || code !== 0 ? 'unavailable' : 'completed',
                    reason: failed ?? (code === 0 ? null : 'NativeReportFailed'), symbolHints: hints, stderrBytes: errorBytes, exitVerified: true,
                    interpretation: 'Exported or nearest symbols are hints only, not attribution to private functions or source lines.' });
            });
        });
    } finally { clearTimeout(timer); clearTimeout(finalTimer); await input.close(); }
}

/** Diagnostic only. No flag, application arm, or Uber arm can invoke this observer. */
export function createOwnedGpuProfile({ browser, page, config, result }) {
    if (config.nativeOwnedProfileOnce !== true) return null;
    const diagnostic = result.ownedGpuProfile = {
        scope: 'One owned Native-isolation CPU observation; original application verdict and 45000 ms compile budget remain authoritative.',
        status: 'scheduled', limits, event: 'cpu-clock:u', frequencyHz: 49, callchainAddresses: 32,
        targetSamplingMs: 8000, maximumCollectorMs: 10000, requestedBoundaryMeaning: 'FIFO acknowledgements confirm processing, not ioctl success.',
        interpretation: 'Timings include profiler overhead. Stripped binaries and frame-pointer chains cannot establish private functions, source lines, compiler passes, or the separate application stall.',
        cleanup: { rawDeleted: false, recorderExitVerified: false, supervisorExitVerified: false, sudoTreeExitVerified: false },
        timing: {}, reasons: [],
    };
    let scheduled, running, started = false, cancelled = false;
    let resolveFatal;
    const fatal = new Promise(resolve => { resolveFatal = resolve; });
    const terminateJob = () => { diagnostic.requiresJobTermination = true; resolveFatal(); };
    const note = code => { if (!diagnostic.reasons.includes(code) && diagnostic.reasons.length < 16) diagnostic.reasons.push(code); };
    const check = () => requireProfile(!cancelled, 'ProfileCancelled');
    const compilePending = async () => {
        check();
        requireProfile(!result.compileWatchdog?.expired && !result.replay, 'CompileNotPending');
        requireProfile(browser.isConnected() && !page.isClosed(), 'OwnedBrowserDisconnected');
        const pending = await bounded(page.evaluate(() => {
            const snapshot = globalThis.nativeCompileIsolation?.snapshot();
            return snapshot?.compile?.status === 'pending' && !snapshot.deviceLoss && !snapshot.explicitDestroyRequested;
        }),
            500, 'CompileStateUnavailable');
        requireProfile(pending === true, 'CompileNotPending'); check();
    };
    async function run() {
        let temporary, fifo, rawFd = null, owned, decoder, tree, collector, collectorClosed, closeResult;
        let stdoutBytes = 0, stderrBytes = 0, stopRequested = false, parserFailed = false, stopped = false;
        let candidate = null, manifest = null, collectorTimer, shutdownPromise;
        const pinned = [], privilegedPinned = [];
        const overallStarted = now();
        const stop = reason => { note(reason); stopRequested = true; };
        const shutdown = () => shutdownPromise ??= (async () => {
            stopped = true;
            if (!collector) return;
            diagnostic.timing.disableRequestedNodeMs = now();
            if (fifo && !fifo.protocol.pending && !fifo.protocol.failed) {
                try { await fifo.protocol.command('disable', 250); diagnostic.timing.disableAcknowledgedNodeMs = now(); }
                catch (error) { note(safeReason(error)); }
            }
            if (fifo) {
                diagnostic.timing.stopRequestedNodeMs = now();
                if (!fifo.protocol.pending && !fifo.protocol.failed) {
                    try { await fifo.protocol.command('stop', 250); diagnostic.timing.stopAcknowledgedNodeMs = now(); }
                    catch (error) { note(safeReason(error)); await fifo.protocol.requestStop(); }
                } else await fifo.protocol.requestStop();
            }
            // Never signal the sudo wrapper or stop its root supervisor. The
            // already-running timeout owns INT at 9s and group KILL at 10s.
            const remaining = Math.max(1, diagnostic.timing.collectorSpawnNodeMs + limits.collectorMs + 750 - now());
            try { closeResult = await bounded(collectorClosed, remaining, 'CollectorExitUnverified'); }
            catch (error) { note(safeReason(error)); collector.stdout.destroy(); collector.stderr.destroy(); }
            diagnostic.timing.collectorExitNodeMs = closeResult?.at ?? null;
            if (closeResult && (closeResult.code !== 0 || closeResult.signal)) note('CollectorIncompleteExit');
            if (tree) {
                try {
                    diagnostic.cleanup.recorderExitVerified = await tree.recorder.exited();
                    diagnostic.cleanup.supervisorExitVerified = await tree.timeout.exited();
                    diagnostic.cleanup.sudoTreeExitVerified = (await Promise.all(tree.tree.map(value => value.exited()))).every(Boolean);
                } catch { note('PrivilegedExitUnverified'); }
                if (!diagnostic.cleanup.recorderExitVerified || !diagnostic.cleanup.supervisorExitVerified
                    || !diagnostic.cleanup.sudoTreeExitVerified) note('PrivilegedExitUnverified');
            } else note('PrivilegedIdentityUnverified');
            if (!diagnostic.cleanup.recorderExitVerified || !diagnostic.cleanup.supervisorExitVerified
                || !diagnostic.cleanup.sudoTreeExitVerified) terminateJob();
            if (closeResult && closeResult.at - diagnostic.timing.collectorSpawnNodeMs > limits.collectorMs) note('CollectorDurationExceeded');
        })();
        try {
            diagnostic.authorization = await consumeAuthorization(config);
            diagnostic.status = 'preparing'; check(); await compilePending();
            // Setup is finite and complete before privileged launch. No command
            // except the one fixed recorder invocation will use sudo.
            const setupDeadline = now() + 5000;
            diagnostic.collector = await checkInstalledCollector(); check();
            owned = await pinOwnedTargets(browser, pinned); check();
            manifest = await readElfManifest(owned.owner.original.executable, owned.owner.original.inode);
            requireProfile(now() < setupDeadline, 'ProfileSetupTimeout');
            temporary = await createTemporaryDirectory(config.output); check();
            fifo = await openControlFiles(temporary.directory); check();
            rawFd = openSync(`${temporary.directory}/user-profile.pipe`, constants.O_WRONLY | constants.O_CREAT | constants.O_EXCL | noFollow, 0o600);
            decoder = new OwnedGpuPerfDecoder({ pid: owned.ids.gpu, tids: owned.tids, write: bytes => {
                let offset = 0;
                while (offset < bytes.length) offset += writeSync(rawFd, bytes, offset, bytes.length - offset);
            } });
            diagnostic.target = { browserPid: owned.ids.browser, gpuPid: owned.ids.gpu, threadIds: owned.tids,
                executable: manifest.module, executableBuildId: manifest.buildId, originalDescriptors: pinned.length,
                source: 'Fresh owned-browser CDP; pinned original proc directories, UID/executable/starttime/ancestry/thread-group checks.' };
            await compilePending();
            for (const process of pinned) await process.revalidate();
            const installed = await fs.lstat(perfPath);
            requireProfile(installed.isFile() && installed.uid === 0 && !(installed.mode & 0o022)
                && String(installed.ino) === diagnostic.collector.inode && installed.size === diagnostic.collector.byteLength, 'InstalledPerfChanged');
            requireProfile(now() < setupDeadline, 'ProfileSetupTimeout'); check();
            const args = ownedGpuRecordArguments(owned.tids, temporary.directory);
            diagnostic.timing.collectorSpawnNodeMs = now();
            collector = spawn('/usr/bin/sudo', args, { shell: false, env: cleanEnv, stdio: ['ignore', 'pipe', 'pipe'] });
            collectorClosed = new Promise(resolve => {
                collector.on('error', () => stop('CollectorSpawnFailed'));
                collector.on('close', (code, signal) => resolve({ code, signal, at: now() }));
            });
            collector.stdout.on('data', bytes => {
                stdoutBytes += bytes.length;
                if (parserFailed) return;
                try { decoder.push(bytes); }
                catch (error) { parserFailed = true; stop(safeReason(error)); collector.stdout.destroy(); }
            });
            collector.stdout.on('error', () => stop('CollectorPipeFailed'));
            collector.stderr.on('data', bytes => {
                stderrBytes += bytes.length;
                if (stderrBytes > limits.stderr) stop('CollectorErrorByteLimit');
                // Never keep stderr; it may contain addresses, paths or source text.
            });
            collector.stderr.on('error', () => stop('CollectorErrorPipeFailed'));
            collectorTimer = setTimeout(() => { stopRequested = true; void shutdown(); }, 8500);
            const startupDeadline = diagnostic.timing.collectorSpawnNodeMs + limits.startupMs;
            diagnostic.status = 'attached-disabled';
            await fifo.protocol.command('ping', Math.max(1, startupDeadline - now()));
            diagnostic.timing.pingAcknowledgedNodeMs = now();
            check(); requireProfile(!stopRequested, 'CollectorInitializationFailed');
            // stdout and the control FIFO are separate pipes. Drain already-written
            // headers to FINISHED_INIT within the same two-second startup bound.
            while (!decoder.finishedInit && now() < startupDeadline && !stopRequested) await pause(5);
            decoder.ready();
            tree = await descendants(collector.pid, privilegedPinned, startupDeadline);
            await verifySupervisorTree(tree, args);
            diagnostic.privilegedProcesses = [tree.timeout, tree.recorder].map((value, index) => ({
                role: index ? 'recorder' : 'supervisor', pid: value.pid, starttime: value.original.start,
                uid: value.original.uid, commandVerified: true, processGroup: value.original.pgrp }));
            // The entire original task set is checked after perf opened disabled
            // events. A missing original task rejects the attach before enabling.
            for (const process of pinned) await process.revalidate();
            const currentIds = await getOwnedIds(owned.session);
            requireProfile(currentIds.browser === owned.ids.browser && currentIds.gpu === owned.ids.gpu, 'OwnedCdpIdentityChanged');
            await compilePending();
            requireProfile(now() < startupDeadline && !stopRequested, 'CollectorStartupTimeout'); check();
            diagnostic.target.postAttachOriginalTasksVerified = true;
            diagnostic.timing.enableRequestedNodeMs = now();
            decoder.enabled = true;
            await fifo.protocol.command('enable', Math.min(250, Math.max(1, startupDeadline - now())));
            diagnostic.timing.enableAcknowledgedNodeMs = now();
            diagnostic.status = 'sampling';
            const sampleDeadline = Math.min(diagnostic.timing.enableRequestedNodeMs + 8000,
                diagnostic.timing.collectorSpawnNodeMs + 8500);
            while (!cancelled && !stopRequested && now() < sampleDeadline) {
                await pause(Math.min(100, Math.max(1, sampleDeadline - now())));
                if (cancelled || stopRequested || stopped) break;
                await owned.owner.revalidate(); await owned.gpu.revalidate();
                const currentTasks = await fs.readdir(`${owned.gpu.root}/task`);
                requireProfile(currentTasks.length <= 256 && currentTasks.every(value => /^[1-9]\d*$/.test(value)), 'GpuThreadObservationRejected');
                for (const value of currentTasks.map(Number)) if (!decoder.tids.has(value)) decoder.additionalThreads.add(value);
                await compilePending();
            }
            if (cancelled) note('CompileSettledOrCancelled');
        } catch (error) { note(safeReason(error)); }
        finally {
            await shutdown(); clearTimeout(collectorTimer);
            if (rawFd !== null) { closeSync(rawFd); rawFd = null; }
            diagnostic.stdoutBytes = stdoutBytes; diagnostic.stderrBytes = stderrBytes;
            const analysisStarted = now(), analysisDeadline = analysisStarted + limits.analysisMs;
            try {
                if (decoder && !parserFailed && closeResult && diagnostic.cleanup.recorderExitVerified
                    && diagnostic.cleanup.supervisorExitVerified && diagnostic.cleanup.sudoTreeExitVerified
                    && diagnostic.target?.postAttachOriginalTasksVerified) {
                    decoder.finish();
                    const regions = await bounded(summarizeRegions(decoder, manifest, analysisDeadline),
                        Math.max(1, analysisDeadline - now()), 'AnalysisTimeout');
                    candidate = regions.summary;
                    if (candidate.samples > 0 && now() < analysisDeadline) {
                        candidate.nativeReport = await nativeReport(`${temporary.directory}/user-profile.pipe`, regions.modules,
                            Math.max(1, analysisDeadline - now()));
                        if (candidate.nativeReport.exitVerified === false) {
                            terminateJob(); note('NativeReportExitUnverified');
                        }
                    }
                    if (candidate.ringLostSamples !== '0' || candidate.finalEventLostSamples !== '0' || candidate.throttled) note('IncompleteSampleCoverage');
                    if (candidate.samples === 0) note('NoSamples');
                }
            } catch (error) { candidate = null; note(safeReason(error)); }
            diagnostic.timing.analysisMs = now() - analysisStarted;
            if (fifo) for (const handle of fifo.handles) await handle.close().catch(() => note('FifoCloseFailed'));
            if (owned) await bounded(owned.session.detach(), 250, 'CdpDetachTimeout').catch(() => note('CdpDetachUnavailable'));
            for (const process of [...privilegedPinned, ...pinned]) await process.close().catch(() => note('ProcDescriptorCloseFailed'));
            if (temporary) {
                try {
                    const current = await fs.lstat(temporary.directory);
                    requireProfile(current.isDirectory() && !current.isSymbolicLink() && current.uid === 1001
                        && current.ino === temporary.identity.ino && current.dev === temporary.identity.dev, 'TemporaryDirectoryChanged');
                    await fs.rm(temporary.directory, { recursive: true, force: false });
                    try { await fs.lstat(temporary.directory); fail('TemporaryDeletionUnverified'); }
                    catch (error) { if (error.code !== 'ENOENT') throw error; }
                    diagnostic.cleanup.rawDeleted = true;
                } catch (error) { note(safeReason(error)); }
            } else diagnostic.cleanup.rawDeleted = true;
            diagnostic.timing.cleanupFinishedNodeMs = now();
            diagnostic.timing.totalMs = now() - overallStarted;
            diagnostic.timing.collectorLifetimeMs = closeResult ? closeResult.at - diagnostic.timing.collectorSpawnNodeMs : null;
            diagnostic.timing.requestedSampleMs = diagnostic.timing.enableRequestedNodeMs
                ? diagnostic.timing.disableRequestedNodeMs - diagnostic.timing.enableRequestedNodeMs : null;
            // Summary admission is last. No raw file, maps, command lines, kernel
            // metadata or original addresses can leave this observer.
            if (candidate && diagnostic.cleanup.rawDeleted && diagnostic.cleanup.recorderExitVerified
                && diagnostic.cleanup.supervisorExitVerified && diagnostic.cleanup.sudoTreeExitVerified) {
                diagnostic.summary = candidate;
                diagnostic.status = diagnostic.reasons.length ? 'incomplete' : 'completed';
            } else diagnostic.status = 'unavailable';
            if (Buffer.byteLength(JSON.stringify(diagnostic)) > limits.summary) {
                delete diagnostic.summary; diagnostic.status = 'unavailable'; note('SummaryByteLimit');
            }
            if (!diagnostic.cleanup.rawDeleted) terminateJob();
            if (collector && (!diagnostic.cleanup.recorderExitVerified || !diagnostic.cleanup.supervisorExitVerified
                || !diagnostic.cleanup.sudoTreeExitVerified))
                terminateJob();
        }
    }
    return {
        fatal,
        begin() {
            if (started) return;
            started = true;
            scheduled = setTimeout(() => {
                running = run().catch(() => {
                    diagnostic.status = 'unavailable'; note('ObserverCleanupFailed'); terminateJob();
                });
            }, 1000);
        },
        async finish() {
            cancelled = true; clearTimeout(scheduled);
            if (running) await Promise.race([running, fatal.then(() => bounded(running, 1000, 'ProfileCleanupTimeout')
                .catch(() => note('ProfileCleanupTimeout')))]);
            else { diagnostic.status = 'skipped'; diagnostic.cleanup.rawDeleted = true; note('CompileSettledBeforeCapture'); }
        },
    };
}
