import { dotnet } from './_framework/dotnet.js';

const status = document.querySelector('#status');
const restart = document.querySelector('#restart');
let scene;
let frameId = 0;
let running = false;
let previousTime;
let accumulator = 0;
let steps = 0;
const fixedStep = 1000 / 60;

function cancelFrame() {
    cancelAnimationFrame(frameId);
    frameId = 0;
    previousTime = undefined;
    accumulator = 0;
}

function fail(error) {
    running = false;
    cancelFrame();
    try { scene?.Shutdown(); } catch (cleanupError) { console.error(cleanupError); }
    status.dataset.state = 'failed';
    status.textContent = `Scene boot failed: ${error.message ?? error}`;
    console.error(error);
    restart.disabled = !scene;
}

function frame(time) {
    frameId = 0;
    if (!running || document.hidden) return;
    try {
        if (previousTime !== undefined)
            accumulator += Math.min(Math.max(time - previousTime, 0), fixedStep * 4);
        previousTime = time;
        let catchUp = 0;
        while (accumulator >= fixedStep && catchUp < 4 && steps < 120) {
            steps = scene.Step();
            accumulator -= fixedStep;
            catchUp++;
        }
        if (steps === 120) {
            running = false;
            status.textContent = scene.Complete();
            status.dataset.state = 'passed';
            restart.disabled = false;
            return;
        }
        frameId = requestAnimationFrame(frame);
    } catch (error) { fail(error); }
}

function start() {
    try {
        cancelFrame();
        scene.Start();
        steps = 0;
        running = true;
        status.dataset.state = 'running';
        status.textContent = 'Advancing the engine scene for 120 fixed updates…';
        restart.disabled = true;
        if (!document.hidden) frameId = requestAnimationFrame(frame);
    } catch (error) { fail(error); }
}

restart.addEventListener('click', start);
document.addEventListener('visibilitychange', () => {
    cancelFrame();
    if (running && !document.hidden) frameId = requestAnimationFrame(frame);
});
window.addEventListener('pagehide', () => {
    running = false;
    cancelFrame();
    scene?.Shutdown();
});
window.addEventListener('pageshow', event => {
    if (event.persisted && scene) start();
});

try {
    const runtime = await dotnet.withDiagnosticTracing(false).create();
    const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
    scene = exports.XREngine.Browser.SceneBoot;
    await runtime.runMain(runtime.getConfig().mainAssemblyName, []);
    start();
} catch (error) { fail(error); }
