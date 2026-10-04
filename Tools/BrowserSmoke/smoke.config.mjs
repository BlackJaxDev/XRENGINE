import { parseArgs } from 'node:util';

export function readConfig(argv = process.argv.slice(2), env = process.env) {
    const { values } = parseArgs({ args: argv, strict: true, allowPositionals: false, options: {
        'browser-publish': { type: 'string' },
        'game-publish': { type: 'string' },
        'game-kind': { type: 'string', default: 'rollingball' },
        'game-only': { type: 'boolean', default: false },
        'shader-artifacts': { type: 'string' },
        'jolt-spike': { type: 'string' },
        'engine-manifest': { type: 'string' },
        'require-world-play': { type: 'boolean', default: false },
        output: { type: 'string' },
        'gpu-mode': { type: 'string', default: 'native' },
        'gpu-diagnostics': { type: 'boolean', default: false },
        'timeout-ms': { type: 'string', default: '180000' },
        headed: { type: 'boolean', default: false },
        help: { type: 'boolean', default: false },
    } });
    if (values.help) return { help: true };
    const gameOnly = values['game-only'];
    for (const required of gameOnly ? ['game-publish', 'output'] : ['browser-publish', 'shader-artifacts', 'output'])
        if (!values[required]) throw new Error(`BrowserSmoke.Config: --${required} is required.`);
    if (gameOnly && (values['engine-manifest'] || values['jolt-spike'] || values['require-world-play'] || values['gpu-diagnostics']))
        throw new Error('BrowserSmoke.Config: game-only mode does not run engine diagnostics.');
    if (!['native', 'software'].includes(values['gpu-mode']))
        throw new Error('BrowserSmoke.Config: --gpu-mode must be native or software.');
    if (!['rollingball', 'rendering-parity', 'advanced-rendering-parity', 'ui-parity', 'modular-pipeline-parity'].includes(values['game-kind']))
        throw new Error('BrowserSmoke.Config: --game-kind must be rollingball, rendering-parity, advanced-rendering-parity, ui-parity or modular-pipeline-parity.');
    const timeout = Number(values['timeout-ms']);
    if (!Number.isInteger(timeout) || timeout < 1000 || timeout > 300000)
        throw new Error('BrowserSmoke.Config: timeout must be between 1000 and 300000 milliseconds.');
    if (values['require-world-play'] && !values['engine-manifest'])
        throw new Error('BrowserSmoke.Config: --require-world-play requires --engine-manifest.');
    const engineManifest = values['engine-manifest'];
    if (engineManifest && (!engineManifest.startsWith('/') || engineManifest.startsWith('//') ||
        engineManifest.includes('\\') || engineManifest.includes('..') || engineManifest.includes('?') || engineManifest.includes('#')))
        throw new Error('BrowserSmoke.Config: --engine-manifest must be a root-relative path inside browser publish output.');
    return {
        browserPublish: values['browser-publish'], gamePublish: values['game-publish'], gameOnly, gameKind: values['game-kind'],
        shaderArtifacts: values['shader-artifacts'],
        joltSpike: values['jolt-spike'], output: values.output, engineManifest,
        requireWorldPlay: values['require-world-play'], gpuMode: values['gpu-mode'],
        gpuDiagnostics: values['gpu-diagnostics'],
        timeout, headed: values.headed, executablePath: env.XRE_BROWSER_EXECUTABLE || undefined,
    };
}

export function browserLaunchOptions(config) {
    const args = [];
    // Software execution is an explicit qualification mode, never a silent fallback.
    // These developer switches are used only with the harness's trusted loopback roots.
    if (config.gpuMode === 'software') args.push(
        '--enable-unsafe-webgpu', '--use-angle=swiftshader', '--use-vulkan=swiftshader', '--enable-features=Vulkan');
    if (config.gpuDiagnostics) args.push('--enable-logging=stderr', '--vmodule=gpu*=1,webgpu*=1,dawn*=1');
    return {
        headless: !config.headed,
        ...(config.executablePath ? { executablePath: config.executablePath } : { channel: 'chromium' }),
        args,
    };
}

export const depthSamples = Object.freeze([
    { name: 'near-left-and-occlusion', x: 141, y: 280, expected: [64, 0, 191, 255], tolerance: 8 },
    { name: 'far-right-transform', x: 371, y: 280, expected: [191, 0, 64, 255], tolerance: 8 },
    { name: 'near-lower-left-orientation', x: 100, y: 330, expected: [64, 0, 191, 255], tolerance: 8 },
    { name: 'above-left-sloping-edge', x: 100, y: 180, expected: [13, 13, 13, 255], tolerance: 5 },
    { name: 'background-upper', x: 256, y: 40, expected: [13, 13, 13, 255], tolerance: 5 },
    { name: 'background-corner', x: 20, y: 20, expected: [13, 13, 13, 255], tolerance: 5 },
]);

export const help = `Usage: node Tools/BrowserSmoke/run.mjs
  [--browser-publish <published-wwwroot>]
  [--game-publish <editor-published-game-root>]
  [--shader-artifacts <schema3-engine-shader-artifacts-directory>]
  --output <evidence-directory>
  [--game-only] [--game-kind rollingball|rendering-parity|advanced-rendering-parity|ui-parity|modular-pipeline-parity]
  [--jolt-spike <published-spike-wwwroot>]
  [--engine-manifest /relative/engine-assets/manifest.json]
  [--require-world-play] [--gpu-mode native|software] [--gpu-diagnostics] [--headed]
  [--timeout-ms 180000]

XRE_BROWSER_EXECUTABLE may select an already-installed Chromium executable.
Otherwise use Playwright's managed Chromium (install with playwright install chromium).
Only the supplied filesystem roots are served, on 127.0.0.1 at an ephemeral port.
--game-only requires --game-publish and runs just the selected Editor-published game check.
Software mode is labeled API/shader correctness evidence, never hardware acceptance.
`;
