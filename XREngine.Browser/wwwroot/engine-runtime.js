import { dotnet } from './_framework/dotnet.js';
import { engineAssetImports } from './engine-assets.js';
import { engineAudioImports } from './engine-audio.js';

/** Boots the shared Engine and RuntimeWorld exports without constructing the reference scene. */
export async function createEngineRuntime() {
    const runtime = await dotnet.withDiagnosticTracing(false).create();
    runtime.setModuleImports('xrengine.assets', engineAssetImports);
    runtime.setModuleImports('xrengine.engineAudio', engineAudioImports);
    const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
    await runtime.runMain(runtime.getConfig().mainAssemblyName, []);
    return exports.XREngine.Browser.BrowserEngineExports;
}
