import { createEngineRuntime } from './engine-runtime.js';
import { BrowserEngineInput } from './engine-input.js';
import { EngineCanvasHost } from './engine-canvas-host.js';

const status = document.querySelector('#status');
const retry = document.querySelector('#retry');
const enableAudio = document.querySelector('#enable-audio');
const audioStatus = document.querySelector('#audio-status');
const canvas = document.querySelector('#input-surface');
const gamepad = document.querySelector('#gamepad');

let engine;
let host;
let epoch = 0;
let pageHidden = false;
function report(state, message) {
    status.dataset.state = state;
    status.textContent = message;
    retry.hidden = state !== 'failed';
}

async function publishedManifestUrl() {
    const descriptorUrl = new URL('./browser-publish.json', import.meta.url);
    const response = await fetch(descriptorUrl, {
        mode: 'same-origin', credentials: 'same-origin', redirect: 'error', cache: 'no-store'
    });
    if (!response.ok) throw new Error(`Published launch descriptor: HTTP ${response.status}`);
    const descriptor = await response.json();
    if (descriptor?.schema !== 2 || descriptor.format !== 'xrengine-engine-launch' ||
        descriptor.manifest !== './content/manifest.json' ||
        (descriptor.quality != null && !['low', 'balanced', 'high'].includes(descriptor.quality)))
        throw new Error('Published launch descriptor is not a supported engine-world launch');
    return { manifest: new URL(descriptor.manifest, descriptorUrl).href, quality: descriptor.quality ?? '' };
}

async function start() {
    const mine = ++epoch;
    try {
        const launch = await publishedManifestUrl();
        if (mine !== epoch) return;
        host.setPageHidden(pageHidden);
        await host.start(launch.manifest, launch.quality);
    } catch (error) {
        if (mine !== epoch) return;
        report('failed', `Engine startup failed: ${error.message ?? error}`);
        console.error(error);
    }
}

retry.addEventListener('click', () => {
    if (host) void start();
    else location.reload();
});
enableAudio.addEventListener('click', () => {
    if (!engine) return;
    const mine = epoch;
    void engine.UnlockAudioAsync().then(ready => {
        if (mine === epoch) audioStatus.textContent = ready ? 'Audio ready' : `Audio ${engine.GetAudioState()}`;
    }, error => {
        if (mine === epoch) audioStatus.textContent = `Audio activation failed: ${error.message ?? error}`;
    });
});
window.addEventListener('pagehide', event => {
    pageHidden = true;
    host?.setPageHidden(true);
    // A cached document keeps the authored world and resumes its frame clock
    // on pageshow. A discarded document releases its engine ownership.
    if (event.persisted) return;
    ++epoch;
    if (host) void host.stop().catch(error => console.error('Engine page teardown failed:', error));
});
window.addEventListener('pageshow', event => {
    if (!event.persisted) return;
    pageHidden = false;
    host?.setPageHidden(false);
});

try {
    const renderers = new Map();
    engine = await createEngineRuntime(renderers);
    const input = new BrowserEngineInput(canvas, engine, gamepad);
    input.install();
    host = new EngineCanvasHost(engine, renderers, canvas, input, report);
    await start();
} catch (error) {
    report('failed', `Engine runtime failed to load: ${error.message ?? error}`);
    console.error(error);
}
