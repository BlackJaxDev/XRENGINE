import { execFile } from 'node:child_process';
import { promises as fs } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { performance } from 'node:perf_hooks';

// Inspect prerequisites only. This program never starts a collector or attaches
// to a process, and cannot consume or reactivate a profiling authorization.
const perfPath = '/usr/lib/linux-azure-6.17-tools-6.17.0-1022/perf';
const perfPackage = 'linux-tools-6.17.0-1022-azure';
const perfVersion = '6.17.0-1022.22';
const budgetMs = 5000;
const environment = { PATH: '/usr/bin:/bin', LC_ALL: 'C' };

async function command(label, executable, args, expected) {
    const started = performance.now();
    const result = await new Promise(resolve => execFile(executable, args,
        { env: environment, shell: false, timeout: budgetMs, maxBuffer: 8192,
            killSignal: 'SIGKILL', encoding: 'utf8' },
        (error, stdout) => resolve({ error, stdout })));
    const durationMs = performance.now() - started;
    const outcome = result.error
        ? result.error.code === 'ERR_CHILD_PROCESS_STDIO_MAXBUFFER' ? 'output-limit'
            : result.error.killed ? 'timed-out' : 'failed'
        : 'completed';
    return {
        report: { label, budgetMs, durationMs, outcome,
            exceededOriginalOneSecondBudget: durationMs > 1000,
            exitCode: Number.isInteger(result.error?.code) ? result.error.code : result.error ? null : 0,
            expectedOutputMatched: !result.error && expected(result.stdout) },
        stdout: result.error ? null : result.stdout,
    };
}

async function main() {
    if (process.platform !== 'linux' || process.geteuid?.() === 0)
        throw new Error('The prerequisite inspection requires an unprivileged Linux process.');
    if (process.argv.length !== 3)
        throw new Error('Specify one JSON report path.');

    const report = { schema: 1, collectorExecuted: false, privilegedCommands: false,
        originalCommandBudgetMs: 1000, inspectionCommandBudgetMs: budgetMs,
        installedFile: {}, commands: [], temporaryFilesDeleted: false };
    try {
        const stat = await fs.lstat(perfPath);
        report.installedFile = { present: true, regularFile: stat.isFile(),
            rootOwned: stat.uid === 0, executable: (stat.mode & 0o111) !== 0,
            unprivilegedWritable: (stat.mode & 0o022) !== 0,
            specialPermissionBits: (stat.mode & 0o6000) !== 0 };
    } catch (error) {
        report.installedFile = { present: false, reason: error.code === 'ENOENT' ? 'missing' : 'unreadable' };
    }

    const metadata = await command('expected-package-metadata', '/usr/bin/dpkg-query',
        ['-W', '-f=${binary:Package}\t${Version}\t${db:Status-Status}\n', perfPackage],
        text => text === `${perfPackage}\t${perfVersion}\tinstalled\n`);
    report.commands.push(metadata.report);
    const ownership = await command('installed-file-package-owner', '/usr/bin/dpkg-query', ['-S', perfPath],
        text => /^[a-z0-9.+-]+: \/usr\/lib\/linux-azure-6\.17-tools-6\.17\.0-1022\/perf\n?$/.test(text));
    report.commands.push(ownership.report);
    if (ownership.report.expectedOutputMatched) {
        const owner = ownership.stdout.slice(0, ownership.stdout.indexOf(':'));
        const version = await command('owning-package-version', '/usr/bin/dpkg-query',
            ['-W', '-f=${Version}\t${db:Status-Status}\n', owner],
            text => text === `${perfVersion}\tinstalled\n`);
        report.commands.push(version.report);
    }

    const temporary = await fs.mkdtemp(path.join(process.env.RUNNER_TEMP || os.tmpdir(), 'xr-diagnostic-prerequisites-'));
    try {
        const fifo = await command('owned-fifo-creation', '/usr/bin/mkfifo',
            ['--mode=600', '--', path.join(temporary, 'control'), path.join(temporary, 'ack')],
            text => text.length === 0);
        report.commands.push(fifo.report);
        if (fifo.report.outcome === 'completed') {
            const entries = await Promise.all(['control', 'ack'].map(name => fs.lstat(path.join(temporary, name))));
            fifo.report.expectedOutputMatched = entries.every(stat => stat.isFIFO()
                && stat.uid === process.geteuid() && (stat.mode & 0o7777) === 0o600);
        }
    } finally {
        await fs.rm(temporary, { recursive: true, force: false });
        try { await fs.lstat(temporary); }
        catch (error) { if (error.code === 'ENOENT') report.temporaryFilesDeleted = true; else throw error; }
        if (!report.temporaryFilesDeleted) throw new Error('Prerequisite temporary cleanup was not verified.');
    }

    const destination = path.resolve(process.argv[2]);
    await fs.mkdir(path.dirname(destination), { recursive: true });
    await fs.writeFile(destination, `${JSON.stringify(report, null, 2)}\n`);
    console.log('Recorded unprivileged prerequisite outcomes; no collector was executed.');
}

await main();
