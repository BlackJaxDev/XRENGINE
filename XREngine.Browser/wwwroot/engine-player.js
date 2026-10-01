import { createEngineRuntime } from './engine-runtime.js';
import { BrowserEngineInput } from './engine-input.js';

const status = document.querySelector('#status');
const retry = document.querySelector('#retry');
const enableAudio = document.querySelector('#enable-audio');
const audioStatus = document.querySelector('#audio-status');
const inputSurface = document.querySelector('#input-surface');
const gamepad = document.querySelector('#gamepad');

let engine;
let input;
let running = false;
let epoch = 0;
let previousFrame;
let frameRequest;

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
        descriptor.manifest !== './content/manifest.json') {
        throw new Error('Published launch descriptor is not a supported engine-world launch');
    }
    return new URL(descriptor.manifest, descriptorUrl).href;
}

async function drainEngine(mine) {
    while (engine && mine === epoch) {
        try {
            await engine.StopAsync();
            return true;
        } catch (error) {
            if (!String(error.message ?? error).includes('jobs remain pending')) throw error;
            await new Promise(resolve => setTimeout(resolve, 100));
        }
    }
    return false;
}

async function stopWorld() {
    const mine = ++epoch;
    running = false;
    input?.reset();
    previousFrame = undefined;
    if (frameRequest !== undefined) cancelAnimationFrame(frameRequest);
    try {
        if (!await drainEngine(mine)) return false;
        if (mine === epoch) report('stopped', 'Engine world stopped');
        return true;
    } catch (error) {
        if (mine === epoch) report('failed', `Engine shutdown failed: ${error.message ?? error}`);
        console.error(error);
        return false;
    }
}

function frame(now) {
    if (!running) return;
    try {
        input?.publish();
        const elapsed = previousFrame === undefined ? 0 : Math.max(0, (now - previousFrame) / 1000);
        previousFrame = now;
        if (!engine.Step(elapsed)) {
            void stopWorld();
            return;
        }
        frameRequest = requestAnimationFrame(frame);
    } catch (error) {
        running = false;
        const failure = `Engine frame failed: ${error.message ?? error}`;
        console.error(error);
        const mine = epoch + 1;
        void stopWorld().then(stopped => { if (stopped && mine === epoch) report('failed', failure); });
    }
}

async function startWorld() {
    const mine = ++epoch;
    retry.hidden = true;
    report('loading', 'Loading the published engine world…');
    try {
        const manifestUrl = await publishedManifestUrl();
        if (mine !== epoch || !await drainEngine(mine)) return;
        const detail = await engine.StartAsync(manifestUrl);
        if (mine !== epoch) return;
        engine.ResetFrameTiming();
        running = true;
        previousFrame = undefined;
        // This build has no production canvas renderer. Never present a blank
        // input surface as evidence that the authored world has rendered.
        report('running', `Engine world ready: ${detail}. Rendering is unavailable in this build.`);
        frameRequest = requestAnimationFrame(frame);
    } catch (error) {
        if (mine !== epoch) return;
        report('failed', `Engine startup failed: ${error.message ?? error}`);
        console.error(error);
    }
}

retry.addEventListener('click', () => {
    if (engine) void startWorld();
    else location.reload();
});
enableAudio.addEventListener('click', () => {
    if (!engine) return;
    const mine = epoch;
    // Invoke resume before awaiting anything so the trusted gesture is retained.
    void engine.UnlockAudioAsync().then(ready => {
        if (mine !== epoch) return;
        audioStatus.textContent = ready ? 'Audio ready' : `Audio ${engine.GetAudioState()}`;
    }, error => {
        if (mine !== epoch) return;
        audioStatus.textContent = `Audio activation failed: ${error.message ?? error}`;
    });
});
document.addEventListener('visibilitychange', () => {
    if (!running) return;
    previousFrame = undefined;
    engine.ResetFrameTiming();
});
window.addEventListener('pagehide', () => { void stopWorld(); });

try {
    engine = await createEngineRuntime();
    input = new BrowserEngineInput(inputSurface, engine, gamepad);
    input.install();
    await startWorld();
} catch (error) {
    report('failed', `Engine runtime failed to load: ${error.message ?? error}`);
    console.error(error);
}
