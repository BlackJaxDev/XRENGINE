import { createEngineRuntime } from './engine-runtime.js';
import { BrowserEngineInput } from './engine-input.js';

const status = document.querySelector('#status');
const form = document.querySelector('#world-form');
const url = document.querySelector('#manifest-url');
const stop = document.querySelector('#stop');
const enableAudio = document.querySelector('#enable-audio');
const audioStatus = document.querySelector('#audio-status');
const inputSurface = document.querySelector('#input-surface');
const gamepad = document.querySelector('#gamepad');
const requested = new URLSearchParams(location.search).get('manifest');
if (requested) url.value = requested;

let engine;
let input;
let running = false;
let epoch = 0;
let previousFrame;
let frameRequest;
let completedFrames = 0;
window.engineWorldDiagnostic = Object.freeze({
    statistics: () => ({ running, completedFrames }),
});

function report(state, message) {
    status.dataset.state = state;
    status.textContent = message;
    stop.disabled = state !== 'running';
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
        completedFrames++;
        frameRequest = requestAnimationFrame(frame);
    } catch (error) {
        running = false;
        const failure = `Engine frame failed: ${error.message ?? error}`;
        console.error(error);
        const mine = epoch + 1;
        void stopWorld().then(stopped => { if (stopped && mine === epoch) report('failed', failure); });
    }
}

async function stopWorld() {
    const mine = ++epoch;
    running = false;
    input?.reset();
    previousFrame = undefined;
    if (frameRequest !== undefined) cancelAnimationFrame(frameRequest);
    report('stopping', 'Stopping the engine world…');
    while (engine && mine === epoch) {
        try {
            await engine.StopAsync();
            break;
        } catch (error) {
            if (!String(error.message ?? error).includes('jobs remain pending')) {
                if (mine === epoch) report('failed', `Engine shutdown failed: ${error.message ?? error}`);
                return false;
            }
            await new Promise(resolve => setTimeout(resolve, 100));
        }
    }
    if (mine === epoch) report('stopped', 'Engine world stopped.');
    return true;
}

form.addEventListener('submit', async event => {
    event.preventDefault();
    const mine = ++epoch;
    if (!engine) return;
    report('loading', 'Fetching and constructing the engine world…');
    try {
        await engine.StopAsync();
        const detail = await engine.StartAsync(url.value);
        if (mine !== epoch) return;
        engine.ResetFrameTiming();
        running = true;
        completedFrames = 0;
        previousFrame = undefined;
        report('running', `Engine world ready: ${detail}. Rendering is not yet wired to this page.`);
        frameRequest = requestAnimationFrame(frame);
    } catch (error) {
        if (mine !== epoch) return;
        report('failed', `Engine startup failed: ${error.message ?? error}`);
        console.error(error);
    }
});
stop.addEventListener('click', () => { void stopWorld(); });
enableAudio.addEventListener('click', () => {
    if (!engine) return;
    const mine = epoch;
    // Invoke resume before awaiting anything so the trusted gesture is retained.
    void engine.UnlockAudioAsync().then(ready => {
        if (mine !== epoch) return;
        audioStatus.textContent = ready ? 'Audio ready.' : `Audio ${engine.GetAudioState()}.`;
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
    report('stopped', 'Ready to load an engine asset manifest.');
    if (requested) form.requestSubmit();
} catch (error) {
    report('failed', `Engine runtime failed to load: ${error.message ?? error}`);
    console.error(error);
}
