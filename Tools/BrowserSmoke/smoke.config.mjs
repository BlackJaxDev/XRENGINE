import { parseArgs } from 'node:util';

export function readConfig(argv = process.argv.slice(2), env = process.env) {
    const { values } = parseArgs({ args: argv, strict: true, allowPositionals: false, options: {
        'browser-publish': { type: 'string' },
        'game-publish': { type: 'string' },
        'baseline-publish': { type: 'string' },
        'game-kind': { type: 'string', default: 'rollingball' },
        'game-only': { type: 'boolean', default: false },
        'shader-artifacts': { type: 'string' },
        'jolt-spike': { type: 'string' },
        'engine-manifest': { type: 'string' },
        'require-world-play': { type: 'boolean', default: false },
        output: { type: 'string' },
        'gpu-mode': { type: 'string', default: 'native' },
        'gpu-diagnostics': { type: 'boolean', default: false },
        'native-compile-trace': { type: 'boolean', default: false },
        'ui-frame-trace': { type: 'boolean', default: false },
        'native-owned-profile-once': { type: 'boolean', default: false },
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
    if (values['game-kind'] === 'static-meshlet-parity' && !values['baseline-publish'])
        throw new Error('BrowserSmoke.Config: static-meshlet-parity requires --baseline-publish.');
    if (values['baseline-publish'] && values['game-kind'] !== 'static-meshlet-parity')
        throw new Error('BrowserSmoke.Config: --baseline-publish is only for static-meshlet-parity.');
    if (!['native', 'software'].includes(values['gpu-mode']))
        throw new Error('BrowserSmoke.Config: --gpu-mode must be native or software.');
    if (!['rollingball', 'rendering-parity', 'advanced-rendering-parity', 'ui-parity', 'modular-pipeline-parity', 'static-meshlet-parity'].includes(values['game-kind']))
        throw new Error('BrowserSmoke.Config: --game-kind must be rollingball, rendering-parity, advanced-rendering-parity, ui-parity, modular-pipeline-parity or static-meshlet-parity.');
    if (values['native-compile-trace'] && (!gameOnly || values['game-kind'] !== 'advanced-rendering-parity'))
        throw new Error('BrowserSmoke.Config: --native-compile-trace requires the Advanced game-only qualification.');
    if (values['ui-frame-trace'] && (!gameOnly || values['game-kind'] !== 'ui-parity' || values['gpu-mode'] !== 'software'))
        throw new Error('BrowserSmoke.Config: --ui-frame-trace requires the activated UI software game-only qualification.');
    if (values['native-owned-profile-once'] && (!gameOnly ||
        values['game-kind'] !== 'advanced-rendering-parity' || values['gpu-mode'] !== 'software' ||
        !env.XRE_OWNED_PROFILE_ACTIVATION_FILE))
        throw new Error('BrowserSmoke.Config: the one-shot profile requires the activated Advanced software game-only diagnostic.');
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
        browserPublish: values['browser-publish'], gamePublish: values['game-publish'],
        baselinePublish: values['baseline-publish'], gameOnly, gameKind: values['game-kind'],
        shaderArtifacts: values['shader-artifacts'],
        joltSpike: values['jolt-spike'], output: values.output, engineManifest,
        requireWorldPlay: values['require-world-play'], gpuMode: values['gpu-mode'],
        gpuDiagnostics: values['gpu-diagnostics'],
        nativeCompileTrace: values['native-compile-trace'],
        uiFrameTrace: values['ui-frame-trace'],
        nativeOwnedProfileOnce: values['native-owned-profile-once'],
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
  [--baseline-publish <same-fixture-cpu-published-game-root>]
  [--shader-artifacts <schema3-engine-shader-artifacts-directory>]
  --output <evidence-directory>
  [--game-only] [--game-kind rollingball|rendering-parity|advanced-rendering-parity|ui-parity|modular-pipeline-parity|static-meshlet-parity]
  [--jolt-spike <published-spike-wwwroot>]
  [--engine-manifest /relative/engine-assets/manifest.json]
  [--require-world-play] [--gpu-mode native|software] [--gpu-diagnostics] [--headed]
  [--native-compile-trace] [--native-owned-profile-once] [--ui-frame-trace] [--timeout-ms 180000]

XRE_BROWSER_EXECUTABLE may select an already-installed Chromium executable.
Otherwise use Playwright's managed Chromium (install with playwright install chromium).
Only the supplied filesystem roots are served, on 127.0.0.1 at an ephemeral port.
--game-only requires --game-publish and runs just the selected Editor-published game check.
--native-compile-trace records bounded native Dawn events only for Advanced's isolated native control arm.
--native-owned-profile-once remains disabled unless the dedicated job supplies an exact-run activation.
--ui-frame-trace remains disabled unless the UI job supplies the reviewed one-run activation.
Software mode is labeled API/shader correctness evidence, never hardware acceptance.
`;
