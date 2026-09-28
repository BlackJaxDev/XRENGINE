import { dotnet } from './_framework/dotnet.js';
import { BrowserCanvasHost } from './browser-canvas-host.js';

/** One runtime may create several independently owned canvas hosts. */
async function createBrowserRuntime() {
    const runtime = await dotnet.withDiagnosticTracing(false).create();
    const renderers = new Map();
    const renderer = id => {
        const value = renderers.get(id);
        if (!value) throw new Error('The frame belongs to an inactive canvas session.');
        return value;
    };
    runtime.setModuleImports('xrengine.canvas', {
        beginFrame: (id, generation) => renderer(id).beginFrame(generation),
        draw: (id, view, x, y, width, height,
            m11, m12, m13, m14, m21, m22, m23, m24,
            m31, m32, m33, m34, m41, m42, m43, m44) => renderer(id).draw(view, x, y, width, height,
                m11, m12, m13, m14, m21, m22, m23, m24,
                m31, m32, m33, m34, m41, m42, m43, m44),
        endFrame: id => renderer(id).endFrame(),
        abortFrame: id => renderers.get(id)?.abortFrame()
    });
    const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
    await runtime.runMain(runtime.getConfig().mainAssemblyName, []);
    return (canvas, onState) => new BrowserCanvasHost(exports.XREngine.Browser.BrowserSceneExports, renderers, canvas, onState);
}

const canvas = document.querySelector('#scene');
const status = document.querySelector('#status');
const restart = document.querySelector('#restart');
const stop = document.querySelector('#stop');
const split = document.querySelector('#split');
let host;
let parked = false;
const pageEvents = new AbortController();
const setState = (state, message) => {
    status.dataset.state = state;
    status.textContent = message;
    restart.disabled = !host;
    stop.disabled = !host || state === 'stopped' || state === 'failed';
};
restart.addEventListener('click', () => { void host?.start(); }, { signal: pageEvents.signal });
stop.addEventListener('click', () => host?.stop(), { signal: pageEvents.signal });
split.addEventListener('change', () => host?.setSplitView(split.checked), { signal: pageEvents.signal });
window.addEventListener('pagehide', event => {
    parked = true;
    host?.stop();
    if (!event.persisted) {
        host?.dispose();
        pageEvents.abort();
    }
}, { signal: pageEvents.signal });
window.addEventListener('pageshow', event => {
    if (!event.persisted) return;
    parked = false;
    if (host) void host.start();
}, { signal: pageEvents.signal });

try {
    const requested = new URLSearchParams(location.search).get('renderer') ?? 'WebGPU';
    if (requested !== 'WebGPU' && requested !== 'Auto')
        throw new Error(`${requested} is not packaged. This application contains WebGPU only.`);
    const createHost = await createBrowserRuntime();
    if (!pageEvents.signal.aborted) {
        host = createHost(canvas, setState);
        host.setSplitView(split.checked);
        if (!parked) await host.start();
    }
} catch (error) {
    console.error(error);
    setState('failed', `Startup failed: ${error.message ?? error}`);
}
