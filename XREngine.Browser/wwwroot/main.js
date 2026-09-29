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
    runtime.setModuleImports('xrengine.webgpu', {
        getCapabilities: id => JSON.stringify(renderer(id).getCapabilities()),
        configurePipeline: (id, json) => renderer(id).focusedPipeline.configure(json),
        configurePipelineMaterial: (id, material, json) => renderer(id).focusedPipeline.configureMaterial(material, json),
        submitPipelinePacket: (id, bytes) => renderer(id).focusedPipeline.submit(bytes),
        createBuffer: (id, size, usage, label) => renderer(id).resources.createBuffer(size, usage, label),
        writeBuffer: (id, handle, offset, bytes) => renderer(id).resources.writeBuffer(handle, offset, bytes),
        copyBuffer: (id, source, sourceOffset, destination, destinationOffset, size) =>
            renderer(id).resources.copyBuffer(source, sourceOffset, destination, destinationOffset, size),
        createTextureResource: (id, width, height, mips, samples, format, usage, label) =>
            renderer(id).resources.createTexture(width, height, mips, samples, format, usage, label),
        uploadTextureMip: (id, handle, mip, x, y, width, height, bytes) =>
            renderer(id).resources.uploadTextureMip(handle, mip, x, y, width, height, bytes),
        createTextureView: (id, texture, baseMip, mipCount, aspect, label) =>
            renderer(id).resources.createTextureView(texture, baseMip, mipCount, aspect, label),
        createSampler: (id, addressU, addressV, minFilter, magFilter, mipmapFilter, label) =>
            renderer(id).resources.createSampler(addressU, addressV, minFilter, magFilter, mipmapFilter, label),
        createMesh: (id, vertices, indices) => renderer(id).createMesh(vertices, indices),
        createTexture: (id, width, height, bytes) => renderer(id).createTexture(width, height, bytes),
        createCookedTexture: (id, description, bytes) => renderer(id).createCookedTexture(description, bytes),
        createMaterial: (id, texture, r, g, b, a) => renderer(id).createMaterial(texture, r, g, b, a),
        destroyResource: (id, handle) => renderer(id).destroyResource(handle),
        submitPacket: (id, packet) => renderer(id).submitPacket(packet),
        submitUploads: (id, commands, payload) => renderer(id).submitUploads(commands, payload),
        copyTexture: (id, source, destination, sourceX, sourceY, destinationX, destinationY, width, height) =>
            renderer(id).copyTexture(source, destination, sourceX, sourceY, destinationX, destinationY, width, height),
        beginBufferReadback: (id, handle, offset, size) => renderer(id).readback.beginBuffer(handle, offset, size),
        beginTextureReadback: (id, handle, mip, x, y, width, height) => renderer(id).readback.beginTexture(handle, mip, x, y, width, height),
        beginCompletion: id => renderer(id).readback.beginCompletion(),
        waitReadback: (id, ticket) => renderer(id).readback.wait(ticket),
        copyReadback: (id, ticket, destination) => renderer(id).readback.copy(ticket, destination),
        releaseReadback: (id, ticket) => renderers.get(id)?.readback.release(ticket),
        createShaderModule: (id, source, label) => renderer(id).commands.createShaderModule(source, label),
        createBindingLayout: (id, json) => renderer(id).commands.createBindingLayout(json),
        createBindingGroup: (id, json) => renderer(id).commands.createBindingGroup(json),
        createRenderPipeline: (id, json) => renderer(id).commands.createRenderPipeline(json),
        createComputePipeline: (id, json) => renderer(id).commands.createComputePipeline(json),
        prepareCommands: (id, json) => renderer(id).commands.prepareCommands(json),
        submitPreparedCommands: (id, handle) => renderer(id).commands.submitPreparedCommands(handle),
        disposeRenderer: id => renderers.get(id)?.dispose()
    });
    const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
    await runtime.runMain(runtime.getConfig().mainAssemblyName, []);
    return (canvas, onState, shaderName) => new BrowserCanvasHost(
        exports.XREngine.Browser.BrowserSceneExports, renderers, canvas, onState, shaderName);
}

const canvas = document.querySelector('#scene');
const status = document.querySelector('#status');
const restart = document.querySelector('#restart');
const stop = document.querySelector('#stop');
const split = document.querySelector('#split');
const instances = document.querySelector('#instances');
const counters = document.querySelector('#counters');
const capture = document.querySelector('#capture');
const importScene = document.querySelector('#import-scene');
const demo = document.querySelector('#demo');
const culling = document.querySelector('#culling');
const recolor = document.querySelector('#recolor');
const streamTexture = document.querySelector('#stream-texture');
const quality = document.querySelector('#quality');
const engineUi = document.querySelector('#engine-ui');
const worldForm = document.querySelector('#world-form');
const worldUrl = document.querySelector('#world-url');
const contentProgress = document.querySelector('#content-progress');
let host;
let parked = false;
const pageEvents = new AbortController();
const setState = (state, message) => {
    split.disabled = instances.disabled = Boolean(host?.snapshotJson || host?.contentUrl);
    streamTexture.disabled = Boolean(host?.snapshotJson || host?.contentUrl) || state !== 'running';
    status.dataset.state = state;
    status.textContent = message;
    restart.disabled = !host;
    stop.disabled = !host || state === 'stopped' || state === 'failed';
    if (state === 'stopped') contentProgress.textContent = '';
};
worldForm.addEventListener('submit', event => {
    event.preventDefault();
    if (host) void host.loadCookedWorld(worldUrl.value).catch(error => {
        status.textContent = `World loading failed: ${error.message ?? error}`;
    });
}, { signal: pageEvents.signal });
restart.addEventListener('click', () => { void host?.start(); }, { signal: pageEvents.signal });
stop.addEventListener('click', () => host?.stop(), { signal: pageEvents.signal });
split.addEventListener('change', () => host?.setSplitView(split.checked), { signal: pageEvents.signal });
instances.addEventListener('change', () => host?.setInstanceCount(Number(instances.value)), { signal: pageEvents.signal });
culling.addEventListener('change', () => host?.setCullingEnabled(culling.checked), { signal: pageEvents.signal });
quality.addEventListener('change', () => {
    try { host?.setQualityPreset(quality.value); }
    catch (error) { console.error(error); status.textContent = `Quality change rejected: ${error.message ?? error}`; }
}, { signal: pageEvents.signal });
engineUi.addEventListener('change', () => host?.setUiEnabled(engineUi.checked), { signal: pageEvents.signal });
recolor.addEventListener('click', () => {
    try { host?.recolorFirstMesh(); }
    catch (error) { console.error(error); status.textContent = `Material update rejected: ${error.message ?? error}`; }
}, { signal: pageEvents.signal });
capture.addEventListener('click', () => {
    const snapshot = host?.getStatistics();
    counters.textContent = snapshot ? JSON.stringify(snapshot, null, 2) : 'No active renderer.';
}, { signal: pageEvents.signal });
streamTexture.addEventListener('click', () => {
    try { host?.streamDemoTexture(); }
    catch (error) { console.error(error); status.textContent = `Texture upload rejected: ${error.message ?? error}`; }
}, { signal: pageEvents.signal });
importScene.addEventListener('change', async () => {
    const file = importScene.files?.[0];
    importScene.value = '';
    if (!file || !host) return;
    try { await host.loadSnapshot(file); }
    catch (error) { console.error(error); status.textContent = `Scene import rejected: ${error.message ?? error}`; }
}, { signal: pageEvents.signal });
demo.addEventListener('click', () => {
    if (!host) return;
    host.snapshotJson = null;
    host.contentUrl = null;
    void host.start();
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
        host = createHost(canvas, setState, new URLSearchParams(location.search).get('shader') ?? 'browser-unlit');
        host.setSplitView(split.checked);
        host.setInstanceCount(Number(instances.value));
        host.setCullingEnabled(culling.checked);
        host.setQualityPreset(quality.value);
        host.setUiEnabled(engineUi.checked);
        host.onContentProgress = progress => {
            const received = (progress.receivedDecodedBytes / (1024 * 1024)).toFixed(1);
            contentProgress.textContent = progress.state === 'complete'
                ? `World loaded: ${progress.consumedAssets} assets, ${received} MiB received.`
                : `Loading world: ${progress.consumedAssets} of ${progress.selectedAssets} assets ready, ${received} MiB received.`;
        };
        const requestedWorld = new URLSearchParams(location.search).get('world');
        if (requestedWorld) {
            worldUrl.value = requestedWorld;
            host.contentUrl = requestedWorld;
        }
        if (!parked) await host.start();
    }
} catch (error) {
    console.error(error);
    setState('failed', `Startup failed: ${error.message ?? error}`);
}
