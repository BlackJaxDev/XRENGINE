import fs from 'node:fs/promises';
import { constants } from 'node:fs';
import { execFile } from 'node:child_process';
import os from 'node:os';
import path from 'node:path';

const budgetMs = 8000;
const commandBudgetMs = 2000;
const maxBytes = 64 * 1024;
const versions = [14, 15, 16, 17, 18, 19, 20, 21];
const fields = ['Pid', 'PPid', 'TracerPid', 'Uid', 'Gid', 'CapInh', 'CapPrm', 'CapEff',
    'CapBnd', 'CapAmb', 'NoNewPrivs', 'Seccomp', 'Seccomp_filters'];

function reason(error) {
    return ['ENOENT', 'EACCES', 'EPERM', 'EIO', 'ENOTDIR', 'Budget', 'ByteLimit', 'NotRegularFile']
        .includes(error?.code) ? error.code : 'Unavailable';
}

/** Section metadata only: never request or retain a symbol table's entries. */
export function summarizeNativeProfileElf(output) {
    const sectionNames = [...output.matchAll(/^\s*\[\s*\d+\]\s+(\.[\w.-]+)\s+/gm)].map(match => match[1]);
    const names = new Set(sectionNames);
    return {
        buildIds: [...new Set([...output.matchAll(/Build ID:\s*([a-fA-F0-9]{8,128})\b/g)]
            .map(match => match[1].toLowerCase()))].slice(0, 4),
        sectionCount: sectionNames.length,
        symbolSections: ['.symtab', '.dynsym'].filter(name => names.has(name)),
        debugSections: [...names].filter(name => /^\.(?:z?debug_|gnu_debug(?:link|altlink|data)|gdb_index)/.test(name)).slice(0, 32),
        unwindSections: ['.eh_frame', '.eh_frame_hdr', '.ARM.exidx'].filter(name => names.has(name)),
    };
}

/** Read-only inventory before the existing compile watchdog starts; never establishes permission to attach. */
export async function captureNativeProfileCapabilities(processes) {
    const started = performance.now(), deadline = started + budgetMs;
    const result = { status: 'collecting', platform: process.platform, startedUtc: new Date().toISOString(),
        budgetMs, commandBudgetMs, maxCommandStreamBytes: maxBytes, maxFileBytes: maxBytes,
        maxPathDirectories: 32, maxToolPaths: 8, maxPackageRecords: 96,
        tools: Object.fromEntries(['perf', 'lldb', 'gdb', 'readelf', 'symbolizer', 'addr2line']
            .map(name => [name, { status: 'unavailable', reason: 'NotCollected', paths: [] }])),
        packages: { status: 'unavailable', reason: 'NotCollected' },
        controls: Object.fromEntries(['perfEventParanoid', 'ptraceScope']
            .map(name => [name, { status: 'unavailable', reason: 'NotCollected' }])), processes: [], binaries: [],
        interpretation: 'Executable/package presence and read-only kernel policy values do not establish usable profiling, debugger attachment permission, symbol resolution or a compiler bottleneck. No profiler, debugger or symbolizer is started; no attachment, sampling, installation, network lookup or security change is attempted. SwiftShader candidates are adjacent installed files, not proof of loaded mappings.' };
    if (process.platform !== 'linux') {
        result.status = 'unavailable'; result.reason = 'LinuxInventoryOnly';
        return result;
    }
    let stopped = false, timer;
    const children = new Set();
    const check = () => { if (stopped || performance.now() >= deadline) throw Object.assign(new Error(), { code: 'Budget' }); };
    async function read(file) {
        check();
        const handle = await fs.open(file, 'r');
        try {
            check();
            if (!(await handle.stat()).isFile()) throw Object.assign(new Error(), { code: 'NotRegularFile' });
            const bytes = Buffer.alloc(maxBytes + 1);
            const { bytesRead } = await handle.read(bytes, 0, bytes.length, 0);
            if (bytesRead > maxBytes) throw Object.assign(new Error(), { code: 'ByteLimit' });
            check();
            return bytes.subarray(0, bytesRead).toString('utf8');
        } finally { await handle.close(); }
    }
    async function executable(file) {
        check();
        try {
            const stat = await fs.stat(file);
            if (!stat.isFile()) return null;
            await fs.access(file, constants.X_OK);
            const resolvedPath = await fs.realpath(file);
            check();
            return { path: file, resolvedPath, mode: (stat.mode & 0o7777).toString(8) };
        } catch (error) { if (reason(error) === 'Budget') throw error; return null; }
    }
    async function command(file, args) {
        check();
        return new Promise(resolve => {
            const child = execFile(file, args, { encoding: 'utf8', timeout: Math.max(1, Math.min(commandBudgetMs,
                Math.floor(deadline - performance.now()))), maxBuffer: maxBytes, killSignal: 'SIGKILL',
                windowsHide: true, env: { ...process.env, LC_ALL: 'C', DEBUGINFOD_URLS: '' } }, (error, stdout) => {
                children.delete(child);
                resolve({ status: error ? 'unavailable' : 'available',
                    reason: error ? error.killed ? 'CommandTimeout' : error.code === 'ERR_CHILD_PROCESS_STDIO_MAXBUFFER'
                        ? 'CommandByteLimit' : Number.isInteger(error.code) ? `Exit${error.code}` : reason(error) : null,
                    output: stdout ?? '' });
            });
            children.add(child);
        });
    }
    async function toolsInventory() {
        const inherited = (process.env.PATH ?? '').split(path.delimiter)
            .filter(value => path.isAbsolute(value) && value.length <= 4096);
        const allDirectories = [...new Set(['/usr/bin', '/bin', ...versions.map(value => `/usr/lib/llvm-${value}/bin`),
            `/usr/lib/linux-tools/${os.release()}`, `/usr/lib/linux-tools-${os.release()}`, ...inherited])];
        const directories = allDirectories.slice(0, result.maxPathDirectories);
        result.toolSearch = { directories, truncated: allDirectories.length > directories.length,
            scope: 'Bounded PATH plus standard LLVM/kernel tool directories; no recursive search or tool execution during discovery. Only system readelf and dpkg-query are used for subsequent static metadata.' };
        const families = { perf: ['perf'], lldb: ['lldb', ...versions.map(value => `lldb-${value}`)],
            gdb: ['gdb'], readelf: ['readelf', 'llvm-readelf', ...versions.map(value => `llvm-readelf-${value}`)],
            symbolizer: ['llvm-symbolizer', ...versions.map(value => `llvm-symbolizer-${value}`)],
            addr2line: ['addr2line', 'eu-addr2line', 'llvm-addr2line'] };
        for (const [family, names] of Object.entries(families)) {
            const found = result.tools[family] = { status: 'unavailable', reason: 'NoExecutableFoundInBoundedSearch', paths: [] };
            for (const name of names) for (const directory of directories) {
                if (found.paths.length >= result.maxToolPaths) { found.truncated = true; break; }
                const entry = await executable(path.join(directory, name));
                if (entry && !found.paths.some(value => value.resolvedPath === entry.resolvedPath)) found.paths.push(entry);
            }
            if (found.paths.length) { found.status = 'executable-found'; found.reason = null; }
        }
    }
    async function packageInventory() {
        // Use the installed native package reader, never a shell or debugger wrapper.
        if (!await executable('/usr/bin/dpkg-query')) {
            result.packages = { status: 'unavailable', reason: 'DpkgQueryNotFound' }; return;
        }
        const value = await command('/usr/bin/dpkg-query', ['-W', '-f=${binary:Package}\t${Version}\t${db:Status-Status}\n',
            'lldb*', 'gdb', 'binutils*', 'llvm*', 'linux-tools*', 'elfutils']);
        const rows = value.output.split('\n').filter(line => /^[a-z0-9][a-z0-9+.:~-]*\t[^\t\r\n]{1,160}\tinstalled$/.test(line));
        result.packages = { status: value.status, reason: value.reason, query: 'Installed dpkg metadata only',
            truncated: rows.length > result.maxPackageRecords, records: rows.slice(0, result.maxPackageRecords)
                .map(line => { const [name, version] = line.split('\t'); return { name, version }; }) };
        // A missing requested glob can return exit 1 while other installed records remain valid.
        if (value.reason === 'Exit1' && rows.length) result.packages.status = 'partial';
    }
    async function controlsInventory() {
        for (const [name, file] of [['perfEventParanoid', '/proc/sys/kernel/perf_event_paranoid'],
            ['ptraceScope', '/proc/sys/kernel/yama/ptrace_scope']]) {
            try {
                const value = (await read(file)).trim();
                result.controls[name] = /^-?\d{1,12}$/.test(value) ? { status: 'available', value }
                    : { status: 'unavailable', reason: 'UnexpectedValue' };
            } catch (error) { result.controls[name] = { status: 'unavailable', reason: reason(error) }; }
        }
    }
    async function processInventory() {
        const owned = [{ role: 'runner', pid: process.pid }];
        for (const role of ['browser', 'GPU']) {
            const matches = Array.isArray(processes) && processes.length <= 1024
                ? processes.filter(value => typeof value?.type === 'string' && value.type.toLowerCase() === role.toLowerCase()) : [];
            if (matches.length !== 1 || !Number.isSafeInteger(matches[0]?.id) || matches[0].id <= 0) {
                result.processes.push({ role, status: 'unavailable', reason: 'OwnedProcessIdUnavailableOrAmbiguous' });
            } else owned.push({ role, pid: matches[0].id });
        }
        for (const owner of owned) {
            const entry = { ...owner, status: 'unavailable', fields: {} };
            result.processes.push(entry);
            try {
                const text = await read(`/proc/${owner.pid}/status`);
                for (const field of fields) {
                    const value = text.match(new RegExp(`^${field}:\\s*([^\\r\\n]+)$`, 'm'))?.[1]?.trim();
                    if (value && /^[\da-fA-F\t ]{1,128}$/.test(value)) entry.fields[field] = value;
                }
                if (Number(entry.fields.Pid) !== owner.pid) throw Object.assign(new Error(), { code: 'Unavailable' });
                entry.status = 'available';
                entry.missingFields = fields.filter(field => !Object.hasOwn(entry.fields, field));
                if (owner.role !== 'runner') {
                    check();
                    entry.executable = await fs.readlink(`/proc/${owner.pid}/exe`);
                    if (!path.isAbsolute(entry.executable) || entry.executable.length > 4096) delete entry.executable;
                }
            } catch (error) { entry.status = 'unavailable'; entry.reason = reason(error); }
        }
    }
    async function binaryInventory() {
        const chrome = result.processes.find(value => value.role === 'browser' && value.status === 'available')?.executable
            ?? result.processes.find(value => value.role === 'GPU' && value.status === 'available')?.executable;
        if (!chrome) {
            result.binaries.push({ role: 'chrome', status: 'unavailable', reason: 'OwnedExecutableUnavailable' },
                { role: 'swiftshader', status: 'unavailable', reason: 'OwnedExecutableUnavailable' }); return;
        }
        const readelf = await executable('/usr/bin/readelf');
        const candidates = [{ role: 'chrome', path: chrome }];
        const adjacent = [path.join(path.dirname(chrome), 'libvk_swiftshader.so'),
            path.join(path.dirname(chrome), 'swiftshader', 'libvk_swiftshader.so')];
        for (const file of adjacent) candidates.push({ role: 'swiftshader', path: file });
        for (const candidate of candidates) {
            const entry = { ...candidate, status: 'unavailable' };
            result.binaries.push(entry);
            try {
                check();
                const stat = await fs.stat(candidate.path);
                if (!stat.isFile()) throw Object.assign(new Error(), { code: 'NotRegularFile' });
                entry.byteLength = stat.size;
                if (!readelf) { entry.reason = 'SystemReadelfNotFound'; continue; }
                const value = await command(readelf.path, ['--wide', '--section-headers', '--notes', '--', candidate.path]);
                entry.status = value.status; entry.reason = value.reason;
                if (value.status === 'available') entry.elf = summarizeNativeProfileElf(value.output);
            } catch (error) { entry.reason = reason(error); }
        }
    }
    try {
        await Promise.race([(async () => {
            await controlsInventory();
            await processInventory();
            await toolsInventory();
            await packageInventory();
            await binaryInventory();
            result.status = 'collected';
        })(), new Promise((_, reject) => { timer = setTimeout(() => {
            stopped = true;
            for (const child of children) child.kill('SIGKILL');
            reject(Object.assign(new Error(), { code: 'Budget' }));
        }, budgetMs); })]);
    } catch (error) { result.status = 'incomplete'; result.reason = reason(error); }
    finally { stopped = true; clearTimeout(timer); }
    result.elapsedMs = performance.now() - started;
    // Late read completions cannot mutate the report or start a subprocess after the deadline.
    return structuredClone(result);
}
