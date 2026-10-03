import { createBrowserRuntime } from './browser-runtime.js';
import { loadBrowserPublishConfig } from './browser-publish-config.js';

const canvas = document.querySelector('#scene');
const shell = document.querySelector('#shell');
const status = document.querySelector('#status');
const progress = document.querySelector('#content-progress');
const audioStatus = document.querySelector('#audio-status');
const enableAudio = document.querySelector('#enable-audio');
const restart = document.querySelector('#restart');
const pageEvents = new AbortController();
let host;
let parked = false;

function setState(state, message) {
    shell.dataset.state = state;
    status.dataset.state = state;
    status.textContent = message;
    restart.hidden = state !== 'failed' && state !== 'stopped';
    if (state === 'stopped') progress.textContent = '';
    shell.hidden = state === 'running' && enableAudio.hidden && audioStatus.hidden;
}

enableAudio.addEventListener('click', () => {
    // Resume within the trusted gesture; the world chooses when to play sound.
    void host?.enableAudio().catch(error => setState('failed', `Audio activation failed: ${error.message ?? error}`));
}, { signal: pageEvents.signal });
restart.addEventListener('click', () => {
    if (host) void host.start();
    else location.reload();
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
    if (!published.world)
        throw new Error('Browser launch descriptor requires a cooked startup world.');
    const createHost = await createBrowserRuntime();
    if (!pageEvents.signal.aborted) {
        host = createHost(canvas, setState);
        host.setSubmissionStrategy(published.submissionStrategy);
        host.setSkinningMode(published.skinning);
        host.setQualityPreset(published.quality);
        // The reference scene's fixture overlay is only part of the developer harness.
        host.setUiEnabled(false);
        host.contentUrl = published.world;
        host.onAudioState = (state, reason) => {
            enableAudio.hidden = state === 'ready';
            audioStatus.hidden = state !== 'denied';
            audioStatus.textContent = state === 'denied' ? `Audio unavailable: ${reason}` : '';
            shell.hidden = shell.dataset.state === 'running' && enableAudio.hidden && audioStatus.hidden;
        };
        host.onContentProgress = current => {
            const received = (current.receivedDecodedBytes / (1024 * 1024)).toFixed(1);
            progress.textContent = current.state === 'complete'
                ? `World loaded: ${current.consumedAssets} assets, ${received} MiB received.`
                : `Loading world: ${current.consumedAssets} of ${current.selectedAssets} assets ready, ${received} MiB received.`;
        };
        if (!parked) await host.start();
    }
} catch (error) {
    console.error(error);
    setState('failed', `Startup failed: ${error.message ?? error}`);
}
