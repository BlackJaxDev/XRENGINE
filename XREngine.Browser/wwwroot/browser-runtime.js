import { dotnet } from './_framework/dotnet.js';
import { BrowserCanvasHost } from './browser-canvas-host.js';
import { EngineMeshDiagnosticHost } from './diagnostics/engine-mesh-host.js';
import { engineAssetImports } from './engine-assets.js';
import { installWebGpuImports } from './webgpu-executor-imports.js';

/** One runtime may create several independently owned canvas hosts. */
export async function createBrowserRuntime() {
    const runtime = await dotnet.withDiagnosticTracing(false).create();
    const renderers = new Map();
    runtime.setModuleImports('xrengine.assets', engineAssetImports);
    runtime.setModuleImports('xrengine.audio', {
        updateListener: (id, x, y, z, fx, fy, fz, ux, uy, uz) =>
            renderers.get(id)?.audioService?.setListenerValues(x, y, z, fx, fy, fz, ux, uy, uz)
    });
    installWebGpuImports(runtime, renderers);
    const exports = await runtime.getAssemblyExports('XREngine.Browser');
    if (!exports?.XREngine?.Browser?.BrowserSceneExports ||
        !exports.XREngine.Browser.Diagnostics?.EngineMeshDiagnosticExports)
        throw new Error('BrowserRuntime.ExportsMissing: the browser library exports are not bound.');
    await runtime.runMain(runtime.getConfig().mainAssemblyName, []);
    const createHost = (canvas, onState, shaderName) => new BrowserCanvasHost(
        exports.XREngine.Browser.BrowserSceneExports, renderers, canvas, onState, shaderName);
    createHost.createEngineMeshDiagnostics = (canvas, onState) => new EngineMeshDiagnosticHost(
        exports.XREngine.Browser.Diagnostics.EngineMeshDiagnosticExports, renderers, canvas, onState);
    return createHost;
}
