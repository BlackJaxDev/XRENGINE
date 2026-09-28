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
        createMesh: (id, vertices, indices) => renderer(id).createMesh(vertices, indices),
        createTexture: (id, width, height, bytes) => renderer(id).createTexture(width, height, bytes),
        createMaterial: (id, texture, r, g, b, a) => renderer(id).createMaterial(texture, r, g, b, a),
        destroyResource: (id, handle) => renderer(id).destroyResource(handle),
        submitPacket: (id, packet) => renderer(id).submitPacket(packet)
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
const instances = document.querySelector('#instances');
const counters = document.querySelector('#counters');
const capture = document.querySelector('#capture');
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
instances.addEventListener('change', () => host?.setInstanceCount(Number(instances.value)), { signal: pageEvents.signal });
capture.addEventListener('click', () => {
    const snapshot = host?.getStatistics();
    counters.textContent = snapshot ? JSON.stringify(snapshot, null, 2) : 'No active renderer.';
}, { signal: pageEvents.signal });
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
        host.setInstanceCount(Number(instances.value));
        if (!parked) await host.start();
    }
} catch (error) {
    console.error(error);
    setState('failed', `Startup failed: ${error.message ?? error}`);
}
