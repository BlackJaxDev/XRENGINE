import { createBrowserRuntime } from './browser-runtime.js';
import { loadBrowserPublishConfig } from './browser-publish-config.js';

const canvas = document.querySelector('#scene');
const status = document.querySelector('#status');
const restart = document.querySelector('#restart');
const stop = document.querySelector('#stop');
const split = document.querySelector('#split');
const instances = document.querySelector('#instances');
const counters = document.querySelector('#counters');
const capture = document.querySelector('#capture');
const gpuReference = document.querySelector('#gpu-reference');
const gpuReferenceStatus = document.querySelector('#gpu-reference-status');
const submissionStrategy = document.querySelector('#submission-strategy');
const skinningMode = document.querySelector('#skinning-mode');
const applyRendering = document.querySelector('#apply-rendering');
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
const enableAudio = document.querySelector('#enable-audio');
const audioStatus = document.querySelector('#audio-status');
const audioFile = document.querySelector('#audio-file');
const audioLoop = document.querySelector('#audio-loop');
const audioGain = document.querySelector('#audio-gain');
const pauseAudio = document.querySelector('#pause-audio');
const nameForm = document.querySelector('#player-name-form');
const nameInput = document.querySelector('#player-name');
let composingName = false;
let host;
let parked = false;
const pageEvents = new AbortController();
applyRendering.addEventListener('click', async () => {
    if (!host) return;
    try {
        host.stop();
        host.setSubmissionStrategy(submissionStrategy.value);
        host.setSkinningMode(skinningMode.value);
        await host.start();
    } catch (error) { status.textContent = `Rendering options rejected: ${error.message ?? error}`; }
}, { signal: pageEvents.signal });
enableAudio.addEventListener('click', () => {
    void host?.enableAudio().catch(error => { audioStatus.textContent = error.message ?? String(error); });
}, { signal: pageEvents.signal });
audioFile.addEventListener('change', () => {
    const file = audioFile.files?.[0];
    audioFile.value = '';
    if (file && host) void host.playAudioFile(file, audioLoop.checked).catch(error => {
        audioStatus.textContent = error.message ?? String(error);
    });
}, { signal: pageEvents.signal });
audioGain.addEventListener('input', () => host?.audio?.setGain(Number(audioGain.value)), { signal: pageEvents.signal });
pauseAudio.addEventListener('click', () => { void host?.audio?.pause(); }, { signal: pageEvents.signal });
nameInput.addEventListener('compositionstart', () => { composingName = true; }, { signal: pageEvents.signal });
nameInput.addEventListener('compositionend', () => { composingName = false; }, { signal: pageEvents.signal });
nameForm.addEventListener('submit', event => {
    event.preventDefault();
    if (composingName) return;
    try {
        host?.setSceneLabel(nameInput.value);
        status.textContent = `Scene named ${nameInput.value.trim()}.`;
    } catch (error) { status.textContent = error.message ?? String(error); }
}, { signal: pageEvents.signal });
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
gpuReference.addEventListener('click', async () => {
    if (!host || gpuReference.disabled) return;
    const epoch = host.epoch;
    gpuReference.disabled = true;
    gpuReferenceStatus.textContent = 'Running offscreen compute and indirect reference cases…';
    try {
        const result = await host.runGpuReference();
        if (epoch !== host.epoch) return;
        counters.textContent = JSON.stringify(result, null, 2);
        gpuReferenceStatus.textContent = 'Reference cases passed on this session. Full device qualification remains separate.';
    } catch (error) {
        gpuReferenceStatus.textContent = epoch === host.epoch
            ? `GPU reference failed: ${error.message ?? error}` : 'Reference run canceled because the scene stopped or restarted.';
    } finally { gpuReference.disabled = false; }
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
    const published = await loadBrowserPublishConfig(pageEvents.signal);
    const requested = new URLSearchParams(location.search).get('renderer') ?? 'WebGPU';
    if (requested !== 'WebGPU' && requested !== 'Auto')
        throw new Error(`${requested} is not packaged. This application contains WebGPU only.`);
    const createHost = await createBrowserRuntime();
    if (!pageEvents.signal.aborted) {
        host = createHost(canvas, setState, new URLSearchParams(location.search).get('shader') ?? 'browser-unlit');
        host.setSubmissionStrategy(new URLSearchParams(location.search).get('strategy') ?? published.submissionStrategy);
        host.setSkinningMode(new URLSearchParams(location.search).get('skinning') ?? published.skinning);
        submissionStrategy.value = host.submissionStrategy;
        skinningMode.value = host.skinningMode;
        host.setSplitView(split.checked);
        host.setInstanceCount(Number(instances.value));
        host.setCullingEnabled(culling.checked);
        quality.value = published.quality;
        host.setQualityPreset(published.quality);
        host.setUiEnabled(engineUi.checked);
        host.onAudioState = (state, reason) => {
            audioStatus.textContent = state === 'ready' ? 'Sound is ready.' : (reason || 'Choose Enable sound to start audio.');
            enableAudio.textContent = state === 'ready' ? 'Play sample sound' : 'Enable sound';
        };
        host.onContentProgress = progress => {
            const received = (progress.receivedDecodedBytes / (1024 * 1024)).toFixed(1);
            contentProgress.textContent = progress.state === 'complete'
                ? `World loaded: ${progress.consumedAssets} assets, ${received} MiB received.`
                : `Loading world: ${progress.consumedAssets} of ${progress.selectedAssets} assets ready, ${received} MiB received.`;
        };
        const requestedWorld = new URLSearchParams(location.search).get('world') ?? published.world;
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
