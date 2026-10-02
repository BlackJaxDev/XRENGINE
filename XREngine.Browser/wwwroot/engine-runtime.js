import { dotnet } from './_framework/dotnet.js';
import { engineAssetImports } from './engine-assets.js';
import { engineAudioImports } from './engine-audio.js';
import { installWebGpuImports } from './webgpu-executor-imports.js';
import { installEngineNetworkLifecycle } from './engine-network.js';

/** Boots the shared Engine and RuntimeWorld exports without constructing the reference scene. */
export async function createEngineRuntime(renderers = new Map()) {
    const runtime = await dotnet.withDiagnosticTracing(false).create();
    runtime.setModuleImports('xrengine.assets', engineAssetImports);
    runtime.setModuleImports('xrengine.engineAudio', engineAudioImports);
    installWebGpuImports(runtime, renderers);
    const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
    await runtime.runMain(runtime.getConfig().mainAssemblyName, []);
    const engine = exports.XREngine.Browser.BrowserEngineExports;
    installEngineNetworkLifecycle(engine);
    return engine;
}
