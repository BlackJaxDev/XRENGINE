import path from 'node:path';
import { canvasGeometry } from './canvas-capture.mjs';

const surface = '#input-surface';
const reference = { width: 1280, height: 720 };
const background = [0.125, 0.25, 0.375];
const backdrop = [0.04, 0.07, 0.12];
const roles = ['none', 'button', 'textbox', 'checkbox'];
const initialControls = [
    ['Actions: 0; Submits: 0; Cancels: 0', 'textbox', [32, 610, 552, 30], true],
    ['Count Action', 'button', [32, 548, 240, 40]],
    ['Clip enabled', 'checkbox', [304, 548, 280, 40]],
    ['Single Line', 'textbox', [32, 484, 552, 40], false],
    ['Multiline', 'textbox', [32, 400, 552, 64], false, true],
    ['Read Only', 'textbox', [32, 348, 552, 36], true],
    ['Rename Target', 'button', [32, 292, 168, 36]],
    ['Hide Target', 'button', [216, 292, 168, 36]],
    ['Deactivate Target', 'button', [400, 292, 184, 36]],
    ['Reparent Target', 'button', [32, 244, 168, 36]],
    ['Remove Target', 'button', [216, 244, 168, 36]],
    ['Restore Target', 'button', [400, 244, 184, 36]],
    ['Clipped Action', 'button', [152, 144, 80, 60]],
    ['Mutation Target', 'button', [328, 168, 248, 48]],
    ['Move Clip', 'button', [32, 72, 128, 36]],
    ['Rotate Clip', 'button', [176, 72, 128, 36]],
    ['Offscreen Action', 'button', [896, 216, 240, 64]],
];

function assert(condition, message) {
    if (!condition) throw new Error(`BrowserSmoke.UiParity: ${message}`);
}
const near = (actual, expected, tolerance = 1) => Math.abs(actual - expected) <= tolerance;
const blend = (color, alpha, destination) => color.map((value, index) => value * alpha + destination[index] * (1 - alpha));
const displayBytes = color => [...color.map(value => Math.round(value * 255)), 255];
// Tonemap.slang: authored exposure=1, Mobius transition=.6, gamma=1.
// UIOutput.slang retains screen display RGB; the world target is linear premultiplied RGBA.
const sceneBytes = color => displayBytes(color.map(value => Math.min(1, value * 1.6 / (value + 0.6))));

function pixelWitnesses(clipping = true) {
    const sample = (name, x, y, color, world = false) => ({ name, x, y,
        expected: world ? sceneBytes(color) : displayBytes(color), world, tolerance: 6 });
    const yellowAlpha = 128 / 255;
    return [
        sample('world-bottom-left-red', 704, 128, [1, 0, 0], true),
        sample('world-bottom-right-green', 1152, 128, [0, 1, 0], true),
        sample('world-top-left-blue', 704, 576, [0, 0, 1], true),
        sample('world-top-right-yellow', 1152, 576, [1, 1, 0], true),
        sample('world-transparent-gap', 720, 416, background, true),
        sample('scene-background', 1248, 416, background, true),
        sample('world-half-red', 816, 416, blend([1, 0, 0], 0.5, background), true),
        sample('world-red-blue-overlap', 912, 416, blend([0, 0, 1], 0.5, blend([1, 0, 0], 0.5, background)), true),
        sample('world-half-blue', 1008, 416, blend([0, 0, 1], 0.5, background), true),
        sample('screen-image-v0-red', 56, 34, [1, 0, 0]),
        sample('screen-image-v0-green', 104, 34, [0, 1, 0]),
        sample('screen-image-v1-blue', 56, 54, [0, 0, 1]),
        sample('screen-image-v1-alpha-yellow', 104, 54, blend([1, 1, 0], yellowAlpha, backdrop)),
        sample('world-image-v0-red', 768, 224, [1, 0, 0], true),
        sample('world-image-v0-green', 832, 224, [0, 1, 0], true),
        sample('world-image-v1-blue', 768, 288, [0, 0, 1], true),
        sample('world-image-v1-alpha-yellow', 832, 288, blend([1, 1, 0], yellowAlpha, background), true),
        sample('screen-backdrop', 600, 420, backdrop),
        sample('screen-clip-parent', 80, 174, [0, 0.25, 0.55]),
        sample('screen-clipped-visible', 192, 174, [0.8, 0.08, 0.08]),
        sample('screen-clipped-outside', 272, 174, clipping ? backdrop : [0.8, 0.08, 0.08]),
    ];
}

/** Capture existing read-only exports without exposing a managed input/action entry point. */
async function installObservation(page) {
    await page.evaluate(async () => {
        const { BrowserEngineInput } = await import('./engine-input.js');
        const { EngineCanvasHost } = await import('./engine-canvas-host.js');
        const prototype = BrowserEngineInput.prototype;
        const original = prototype.syncTextFocus;
        const originalStart = EngineCanvasHost.prototype.start;
        const controller = new AbortController();
        const trace = [];
        let omitted = 0;
        const observation = { installed: true, inputObserved: false, failure: null };
        globalThis.uiParityObservation = observation;
        const lifecycle = [], states = [], failures = [];
        let omittedEvidence = 0;
        const canvas = document.querySelector('#input-surface');
        const nodeIds = new WeakMap();
        let nextNodeId = 0;
        const nodeId = element => {
            if (!element) return null;
            if (!nodeIds.has(element)) nodeIds.set(element, ++nextNodeId);
            return nodeIds.get(element);
        };
        const rect = element => {
            const value = element.getBoundingClientRect();
            return { x: value.x, y: value.y, width: value.width, height: value.height };
        };
        const recordLifecycle = type => {
            if (lifecycle.length === 32) { omittedEvidence++; return; }
            lifecycle.push({ time: Date.now(), type, hidden: document.hidden, focused: document.hasFocus(),
                attached: canvas.isConnected, bounds: rect(canvas), width: canvas.width, height: canvas.height });
        };
        document.addEventListener('xrengine-canvas-failed', event => {
            if (failures.length === 4) { omittedEvidence++; return; }
            failures.push({ time: Date.now(), detail: event.detail });
            // Deliver terminal evidence independently of a later page evaluation.
            void globalThis.uiParityFailureObserved?.(event.detail).catch(() => {});
        }, { capture: true, signal: controller.signal });
        for (const type of ['visibilitychange', 'freeze', 'resume'])
            document.addEventListener(type, () => recordLifecycle(type), { signal: controller.signal });
        for (const type of ['pagehide', 'pageshow', 'resize'])
            window.addEventListener(type, () => recordLifecycle(type), { signal: controller.signal });
        const sizeObserver = new ResizeObserver(() => recordLifecycle('canvas-resize'));
        sizeObserver.observe(canvas);
        const status = document.querySelector('#status');
        const recordState = () => {
            const state = status.dataset.state, message = status.textContent.slice(0, 4096);
            if (states.at(-1)?.state === state && states.at(-1)?.message === message) return;
            if (states.length === 32) { omittedEvidence++; return; }
            states.push({ time: Date.now(), state, message });
        };
        const statusObserver = new MutationObserver(recordState);
        statusObserver.observe(status, { attributes: true, attributeFilter: ['data-state'], childList: true });
        recordState();
        globalThis.uiParityEvidence = () => ({ observation, lifecycle, states, failures, omittedEvidence });
        const dom = element => {
            if (!element) return null;
            const native = element.querySelector('input,textarea') ?? element;
            return { nodeId: nodeId(element), nativeNodeId: nodeId(native), tag: native.tagName,
                name: native.getAttribute('aria-label'),
                role: native.getAttribute('role') ?? (native.matches('input,textarea') ? 'textbox' : native.tagName === 'BUTTON' ? 'button' : null),
                readOnly: native.matches('input,textarea') ? native.readOnly : native.getAttribute('aria-readonly'),
                multiline: native.matches('input,textarea') ? native.tagName === 'TEXTAREA' : native.getAttribute('aria-multiline'),
                checked: native.getAttribute('aria-checked'), hidden: native.getAttribute('aria-hidden'),
                tabIndex: native.tabIndex, focused: native === document.activeElement,
                value: native.matches('input,textarea') ? native.value : null,
                selectionStart: native.selectionStart ?? null, selectionEnd: native.selectionEnd ?? null,
                pointerEvents: getComputedStyle(native).pointerEvents, bounds: rect(native) };
        };
        for (const type of ['pointerdown', 'pointerup', 'click', 'keydown', 'keyup', 'input', 'compositionstart', 'compositionend']) {
            document.addEventListener(type, event => {
                if (trace.length >= 512) { omitted++; return; }
                trace.push({ type, trusted: event.isTrusted, target: event.target.id || event.target.getAttribute?.('aria-label') || event.target.tagName,
                    key: event.key ?? null, pointerType: event.pointerType ?? null, composing: event.isComposing ?? null,
                    x: event.clientX ?? null, y: event.clientY ?? null });
            }, { capture: true, signal: controller.signal });
        }
        function restore() {
            if (prototype.syncTextFocus === wrapper) prototype.syncTextFocus = original;
            if (EngineCanvasHost.prototype.start === observeStart) EngineCanvasHost.prototype.start = originalStart;
        }
        function observeStart(...args) {
            EngineCanvasHost.prototype.start = originalStart;
            const host = this;
            globalThis.uiParityRendering = () => {
                let managed;
                try { managed = host.engine.GetCanvasRenderingStatus(); }
                catch (error) { managed = String(error); }
                const renderer = host.renderer, scopes = renderer?.commands?.engineFrame?.scopes;
                const receipts = [];
                for (let index = 0; index < Math.min(scopes?.receipts?.length ?? 0, 64); index++) {
                    const receipt = scopes.receipts[index];
                    if (!receipt.active) continue;
                    receipts.push({ sequence: receipt.context.sequence, owner: receipt.context.owner,
                        generation: receipt.context.generation, remaining: receipt.remaining,
                        closed: receipt.closed, submitted: receipt.submitted, scopeCount: receipt.scopeCount,
                        deviceMatches: receipt.device === renderer.device,
                        ownerMatches: receipt.context.owner === renderer._owner,
                        generationMatches: receipt.context.generation === renderer._generation });
                }
                return { time: Date.now(), frameTimestamp: host.previousFrame, now: performance.now(),
                    managed, hidden: document.hidden, focused: document.hasFocus(),
                    attached: host.canvas.isConnected, canvas: rect(host.canvas),
                    bitmapWidth: host.canvas.width, bitmapHeight: host.canvas.height,
                    drawable: host.drawable, presented: host.presented, surfaceGeneration: host.surfaceGeneration,
                    request: host.request, session: host.session, epoch: host.epoch,
                    controllerAborted: host.controller?.signal.aborted ?? null,
                    frozen: Boolean(host.frozen), pageHidden: Boolean(host.pageHidden),
                    validatingRecoveryFrame: Boolean(host.validatingRecoveryFrame),
                    rendererReady: host.rendererReady, recovering: host.recovering, failed: host.failed,
                    firstFrameSeconds: host.firstFrameSeconds, admissionWaitSeconds: host.admissionWaitSeconds,
                    deferredFrameSeconds: host.deferredFrameSeconds,
                    receipts: scopes ? { capacity: scopes.receipts.length, disposed: scopes.disposed,
                        completedSequence: scopes.completedSequence, trackCompletion: scopes.trackCompletion,
                        active: receipts } : null,
                    renderer: host.renderer?.getStatistics(), failure: host.renderer?.getFailureDiagnostics() };
            };
            return originalStart.apply(this, args);
        }
        function wrapper(...args) {
            try { return original.apply(this, args); }
            finally {
                // Observation must preserve the player's result, including a thrown error.
                try {
                    restore();
                    const input = this, engine = this.engine, canvas = this.canvas;
                    observation.inputObserved = true;
                    globalThis.uiParityRead = (includeRendering = false) => ({
                        observation, rendering: includeRendering ? globalThis.uiParityRendering?.() : undefined,
                        evidence: includeRendering ? globalThis.uiParityEvidence() : undefined,
                        revision: input.controlRevision,
                        status: document.querySelector('#status')?.textContent,
                        state: document.querySelector('#status')?.dataset.state,
                        canvas: { ...rect(canvas), bitmapWidth: canvas.width, bitmapHeight: canvas.height, devicePixelRatio },
                        controls: Array.from({ length: engine.GetAccessibleControlCount() }, (_, index) => ({
                            generation: engine.GetAccessibleControlGeneration(index), version: engine.GetAccessibleControlVersion(index),
                            role: engine.GetAccessibleControlRole(index), name: engine.GetAccessibleControlName(index),
                            readOnly: engine.GetAccessibleControlReadOnly(index), multiline: engine.GetAccessibleControlMultiline(index),
                            checked: engine.GetAccessibleControlChecked(index), focused: engine.GetAccessibleControlFocused(index),
                            x: engine.GetAccessibleControlX(index), y: engine.GetAccessibleControlY(index),
                            width: engine.GetAccessibleControlWidth(index), height: engine.GetAccessibleControlHeight(index),
                        })),
                        proxies: [...(input.controlRoot?.children ?? [])].map(dom),
                        text: { generation: input.textGeneration, value: engine.GetTextInputValue(),
                            version: engine.GetTextInputContentVersion(), label: engine.GetTextInputLabel(),
                            cursor: engine.GetTextInputCursor(), readOnly: engine.GetTextInputReadOnly(),
                            singleLine: engine.GetTextInputSingleLine(), composing: input.textComposing, pending: input.textCommitPending },
                        active: dom(document.activeElement), events: trace.splice(0), omittedEvents: omitted,
                    });
                } catch (error) { observation.failure = String(error); }
            }
        }
        prototype.syncTextFocus = wrapper;
        EngineCanvasHost.prototype.start = observeStart;
        globalThis.uiParityCleanup = () => {
            sizeObserver.disconnect(); statusObserver.disconnect();
            restore(); controller.abort(); delete globalThis.uiParityRead; delete globalThis.uiParityRendering;
            delete globalThis.uiParityObservation; delete globalThis.uiParityEvidence; delete globalThis.uiParityCleanup;
        };
    });
}

async function read(page) {
    return page.evaluate(() => typeof globalThis.uiParityRead === 'function' ? globalThis.uiParityRead(true) : {
        observationUnavailable: true, observation: globalThis.uiParityObservation,
        evidence: globalThis.uiParityEvidence?.(),
        rendering: globalThis.uiParityRendering?.(), state: document.querySelector('#status')?.dataset.state,
        status: document.querySelector('#status')?.textContent, controls: [], proxies: [],
    });
}

async function boundedObservation(operation, milliseconds) {
    let timer;
    try {
        return await Promise.race([operation, new Promise((_, reject) => {
            timer = setTimeout(() => reject(new Error(`UI observation exceeded ${milliseconds} ms.`)), milliseconds);
        })]);
    } finally { clearTimeout(timer); }
}
function control(state, name) { return state.controls.find(item => item.name === name); }
function counts(state) {
    const match = state.controls.find(item => /^Actions: \d+; Submits: \d+; Cancels: \d+$/.test(item.name))?.name.match(/\d+/g);
    return match?.map(Number);
}
function controlPixels(state, name) {
    const value = control(state, name);
    assert(value, `Missing control ${name}.`);
    const { bitmapWidth: width, bitmapHeight: height } = state.canvas;
    return [value.x * width, (1 - value.y - value.height) * height, value.width * width, value.height * height];
}
function assertBounds(state, name, expected) {
    const actual = controlPixels(state, name);
    assert(actual.every((value, index) => near(value, expected[index])), `${name} bounds ${JSON.stringify(actual)} != ${JSON.stringify(expected)}.`);
}
function assertProxyBounds(state) {
    assert(state.proxies.length === state.controls.length, 'Native proxy count differs from engine traversal.');
    state.controls.forEach((value, index) => {
        const proxy = state.proxies[index];
        const expected = { x: state.canvas.x + value.x * state.canvas.width,
            y: state.canvas.y + value.y * state.canvas.height,
            width: value.width * state.canvas.width, height: value.height * state.canvas.height };
        assert(proxy.name === value.name, `Native proxy order/name differs at ${index}: ${proxy.name} != ${value.name}.`);
        assert(Object.keys(expected).every(key => near(proxy.bounds[key], expected[key])),
            `Native ${value.name} CSS bounds ${JSON.stringify(proxy.bounds)} != current engine projection ${JSON.stringify(expected)}.`);
    });
}
function assertInitial(state) {
    assert(state.canvas.bitmapWidth === reference.width && state.canvas.bitmapHeight === reference.height,
        `Reference backing size must be 1280x720: ${JSON.stringify(state.canvas)}.`);
    assert(state.controls.length === 17 && state.proxies.length === 17, 'Expected exactly 17 engine controls and native proxies.');
    assertProxyBounds(state);
    initialControls.forEach(([name, role, bounds, readOnly = false, multiline = false], index) => {
        const actual = state.controls[index], proxy = state.proxies[index];
        assert(actual.name === name && roles[actual.role] === role, `Initial engine traversal mismatch at ${index}: ${JSON.stringify(actual)}.`);
        assert(proxy.name === name && proxy.role === role && proxy.tabIndex === 0 && proxy.hidden !== 'true',
            `Initial native semantics mismatch at ${index}: ${JSON.stringify(proxy)}.`);
        assert(actual.readOnly === readOnly && actual.multiline === multiline && actual.checked === (role === 'checkbox' ? 1 : 0),
            `Initial state mismatch for ${name}.`);
        if (role === 'textbox') assert(String(proxy.readOnly) === String(readOnly) && String(proxy.multiline) === String(multiline), `Native text state mismatch for ${name}.`);
        if (role === 'checkbox') assert(proxy.checked === 'true', 'Native checkbox must initially be checked.');
        assert(proxy.pointerEvents === 'none', 'Pointer witnesses require the real canvas route beneath native proxies.');
        assertBounds(state, name, bounds);
    });
}

async function inspectPixels(page, image, state, witnesses) {
    return page.evaluate(async ({ encoded, geometry, witnesses }) => {
        const bytes = Uint8Array.from(atob(encoded), value => value.charCodeAt(0));
        const bitmap = await createImageBitmap(new Blob([bytes], { type: 'image/png' }));
        // Decode the captured PNG only. This surface never replaces or paints the engine canvas.
        const decoded = new OffscreenCanvas(bitmap.width, bitmap.height);
        const context = decoded.getContext('2d', { willReadFrequently: true });
        context.drawImage(bitmap, 0, 0); bitmap.close();
        const pixels = context.getImageData(0, 0, decoded.width, decoded.height).data;
        const point = (x, y, world) => [
            x * decoded.width / (world ? 1280 : geometry.bitmapWidth),
            decoded.height - y * decoded.height / (world ? 720 : geometry.bitmapHeight),
        ];
        const samples = witnesses.map(witness => {
            const [px, py] = point(witness.x, witness.y, witness.world);
            const min = [255, 255, 255, 255], max = [0, 0, 0, 0], sum = [0, 0, 0, 0];
            for (let y = Math.round(py) - 2; y <= Math.round(py) + 2; y++) {
                for (let x = Math.round(px) - 2; x <= Math.round(px) + 2; x++) {
                    for (let channel = 0; channel < 4; channel++) {
                        const value = pixels[(y * decoded.width + x) * 4 + channel];
                        min[channel] = Math.min(min[channel], value); max[channel] = Math.max(max[channel], value); sum[channel] += value;
                    }
                }
            }
            return { ...witness, capturedX: px, capturedY: py, min, max, average: sum.map(value => value / 25) };
        });
        const glyphs = [
            { name: 'screen-heading', x: 32, y: 652, width: 300, height: 40, world: false },
            { name: 'offscreen-heading', x: 752, y: 548, width: 352, height: 40, world: true },
        ].map(region => {
            const [left, bottom] = point(region.x, region.y, region.world);
            const [right, top] = point(region.x + region.width, region.y + region.height, region.world);
            let ink = 0, columns = 0, runs = 0, prior = false, total = 0;
            for (let x = Math.ceil(left); x < Math.floor(right); x++) {
                let occupied = false;
                for (let y = Math.ceil(top); y < Math.floor(bottom); y++) {
                    const offset = (y * decoded.width + x) * 4;
                    const white = pixels[offset] > 220 && pixels[offset + 1] > 220 && pixels[offset + 2] > 220;
                    total++; if (white) { ink++; occupied = true; }
                }
                if (occupied) { columns++; if (!prior) runs++; }
                prior = occupied;
            }
            return { ...region, ink, total, columns, runs };
        });
        return { width: decoded.width, height: decoded.height, samples, glyphs };
    }, { encoded: image.toString('base64'), geometry: state.canvas, witnesses });
}
function assertPixels(pixels) {
    for (const sample of pixels.samples) {
        assert(sample.average.every((value, index) => near(value, sample.expected[index], sample.tolerance)),
            `Pixel ${sample.name}: ${JSON.stringify(sample.average)} != ${JSON.stringify(sample.expected)} (tolerance ${sample.tolerance}).`);
        assert(sample.max.every((value, index) => value - sample.min[index] <= 8), `Pixel ${sample.name} is not a uniform interior patch.`);
    }
    for (const glyph of pixels.glyphs)
        assert(glyph.ink >= 70 && glyph.ink < glyph.total * 0.45 && glyph.columns >= 30 && glyph.runs >= 4,
            `Cooked bitmap glyph ink missing or solid rectangle in ${glyph.name}: ${JSON.stringify(glyph)}.`);
}

async function frames(page, count = 3) {
    await page.evaluate(count => new Promise((resolve, reject) => {
        let request;
        const timer = setTimeout(() => {
            cancelAnimationFrame(request);
            reject(new Error('BrowserSmoke.UiParity: browser animation frames did not advance.'));
        }, 5000);
        const next = () => {
            if (--count <= 0) { clearTimeout(timer); resolve(); }
            else request = requestAnimationFrame(next);
        };
        request = requestAnimationFrame(next);
    }), count);
}
async function waitRunning(page) {
    await page.waitForFunction(() => ['running', 'failed'].includes(document.querySelector('#status')?.dataset.state));
}

/** Qualifies a normally cooked BrowserUiParity project through the shipping Editor player. */
export async function uiParityGameCheck(browser, origin, report, config, instrumentedPage, assertNoBrowserErrors) {
    report.uiParityIterations = [];
    report.uiParityQualification = {
        instrumentation: 'Read-only input-sync and host-start observers installed before the unchanged player entry script and restored on first invocation or cleanup. Existing getters, host/receipt fields, renderer statistics and native DOM only; no managed action/input export is invoked.',
        input: 'Browser-delivered mouse/keyboard and Chromium touch protocol events target the actual canvas or native keyboard proxy.',
        composition: 'Synthetic DOM composition routing only; physical OS IME acceptance remains pending.',
        lifecycle: 'Two independent fresh browser contexts. Resource retirement and physical desktop/device captures require separate live evidence.',
        unexercisedActions: ['Move Clip', 'Rotate Clip'],
    };
    for (let iteration = 0; iteration < 2; iteration++) {
        const { page, context, events } = await instrumentedPage(browser, origin, report, `ui-parity-${iteration}`, config);
        const result = { iteration, status: 'running', steps: [], counts: [0, 0, 0] };
        report.uiParityIterations.push(result);
        let failed = false, touch;
        const checkpoint = async (name, witnesses = null) => {
            const record = { name, capture: `ui-parity-${iteration}-${name}.png` };
            result.steps.push(record);
            // Publish partial evidence before any capture/expectation can throw.
            // Require the existing frame-clock witness before Playwright's stable-element
            // wait, which itself cannot finish while browser animation frames are stalled.
            await frames(page, 2);
            record.geometry = await canvasGeometry(page, surface);
            // scrollIntoView may move the CSS rectangle; let the normal input sync reposition proxies.
            await frames(page, 2);
            record.state = await read(page);
            const image = await page.locator(surface).screenshot({ path: path.join(config.output, record.capture) });
            if (witnesses) record.pixels = await inspectPixels(page, image, record.state, witnesses);
            return record;
        };
        const settled = async (name, increment = 0, submit = 0, cancel = 0, witnesses = null) => {
            result.counts = result.counts.map((value, index) => value + [increment, submit, cancel][index]);
            if (increment) await page.waitForFunction(expected => {
                const status = [...document.querySelectorAll('[aria-label]')].find(element => /^Actions: \d+; Submits: \d+; Cancels: \d+$/.test(element.getAttribute('aria-label')));
                return Number(status?.getAttribute('aria-label').match(/\d+/)?.[0]) >= expected;
            }, result.counts[0], { timeout: Math.min(config.timeout, 10000) });
            await frames(page, 4);
            await page.waitForTimeout(150);
            const record = await checkpoint(name, witnesses);
            assert(record.state.state === 'running', `${name}: ${record.state.status}`);
            assertProxyBounds(record.state);
            assert(JSON.stringify(counts(record.state)) === JSON.stringify(result.counts), `${name}: callback counts ${JSON.stringify(counts(record.state))} != ${JSON.stringify(result.counts)}.`);
            if (witnesses) assertPixels(record.pixels);
            return record.state;
        };
        const pointClick = async (x, y) => {
            const geometry = await canvasGeometry(page, surface);
            const point = { x: geometry.x + x / geometry.bitmapWidth * geometry.width,
                y: geometry.y + (1 - y / geometry.bitmapHeight) * geometry.height };
            result.steps.push({ name: 'mouse-delivery', geometry, point });
            const target = await page.evaluate(point => document.elementFromPoint(point.x, point.y)?.id, point);
            assert(target === 'input-surface', `Mouse point is covered by ${target ?? 'another element'}.`);
            await page.mouse.move(point.x, point.y);
            await page.mouse.down(); await frames(page, 3); await page.mouse.up();
        };
        const clickControl = async name => {
            // Re-query engine-projected bounds for every pointer, including after resize/reparent.
            const state = await read(page), [x, y, width, height] = controlPixels(state, name);
            await pointClick(x + width / 2, y + height / 2);
        };
        const act = async (name, step = name.toLowerCase().replaceAll(' ', '-')) => {
            const before = await read(page);
            await clickControl(name);
            const after = await settled(step, 1);
            assert(after.revision > before.revision, `${step}: semantic revision did not advance.`);
            assert(after.events.some(event => event.type === 'pointerdown' && event.trusted && event.pointerType === 'mouse' && event.target === 'input-surface'),
                `${step}: no trusted mouse press reached the engine canvas.`);
            return after;
        };
        const focusText = async (name, expected) => {
            await clickControl(name);
            await page.waitForFunction(name => document.activeElement?.matches('input,textarea') && document.activeElement.getAttribute('aria-label') === name, name);
            const state = await settled(`focus-${name.toLowerCase().replaceAll(' ', '-')}`);
            assert(state.text.label === name && state.text.value === expected && state.active.value === expected, `${name}: initial native/engine text differs.`);
            return state;
        };
        try {
            await page.setViewportSize({ width: 1376, height: 1080 });
            await page.exposeFunction('uiParityFailureObserved', detail => {
                if ((result.canvasFailures ??= []).length < 4)
                    result.canvasFailures.push({ time: Date.now(), detail });
            });
            // Hold the unchanged entry script until its exact imported prototypes are
            // observed. DOMContentLoaded and a running status can both be too late.
            await page.route(`${origin}/__game/engine-player.js`, async route => {
                try {
                    await boundedObservation((async () => {
                        await page.locator(surface).waitFor({ state: 'attached', timeout: 5000 });
                        await installObservation(page);
                    })(), 5000);
                    result.observationInstalled = true;
                    await route.continue();
                } catch (error) {
                    result.observationInstallError = String(error);
                    await route.abort('failed');
                }
            }, { times: 1 });
            await page.goto(`${origin}/__game/index.html`, { waitUntil: 'domcontentloaded' });
            assert(result.observationInstalled && !result.observationInstallError,
                `Observer installation before the player entry failed: ${result.observationInstallError ?? 'entry script was not observed'}.`);
            // Only layout size is changed; the normal host owns backing allocation and resize delivery.
            await page.locator(surface).evaluate(canvas => { canvas.style.width = '1280px'; canvas.style.height = '720px'; });
            await waitRunning(page);
            result.startup = await page.locator('#status').evaluate(element => ({ state: element.dataset.state, detail: element.textContent }));
            if (result.startup.state !== 'running') {
                result.startup.capture = `ui-parity-${iteration}-startup-failure.png`;
                try {
                    await page.screenshot({ path: path.join(config.output, result.startup.capture), fullPage: true, timeout: 5000 });
                } catch (error) { result.startup.captureError = String(error); }
            }
            assert(result.startup.state === 'running', `Authored player startup: ${result.startup.detail}`);
            result.startup.observation = await boundedObservation(read(page), 5000);
            assert(!result.startup.observation.observationUnavailable,
                `The running player did not reach the installed input observer: ${JSON.stringify(result.startup.observation.observation)}.`);
            await page.waitForFunction(() => globalThis.uiParityRead().controls.length >= 17);
            // The shared toggle publishes its bound property on its normal late tick.
            await page.waitForFunction(() => globalThis.uiParityRead().controls
                .find(control => control.name === 'Clip enabled')?.checked === 1 &&
                document.querySelector('[role="checkbox"][aria-label="Clip enabled"]')?.getAttribute('aria-checked') === 'true');
            const initial = await checkpoint('initial', pixelWitnesses());
            assert(initial.state.state === 'running' && /Browser\s*UI\s*Parity/i.test(initial.state.status), `Authored player startup: ${initial.state.status}`);
            assert(await page.locator('#manifest-url, #world-form').count() === 0, 'The diagnostic shell cannot qualify a shipping player.');
            assertInitial(initial.state); assertPixels(initial.pixels);

            // Native Tab traversal must transfer into the real readonly text service, then the button.
            await page.locator(surface).focus(); await page.keyboard.press('Tab');
            await page.waitForFunction(() => document.activeElement?.matches('input[readonly]'));
            const tabStatus = await settled('tab-status');
            assert(tabStatus.active.name === initialControls[0][0] && tabStatus.active.value === initialControls[0][0], 'Tab did not reach the readonly status text.');
            await page.keyboard.press('Tab');
            const tabButton = await settled('tab-count-button');
            assert(tabButton.active.name === 'Count Action', 'Tab order did not advance to Count Action.');
            for (const [key, eventKey] of [['Enter', 'Enter'], ['Space', ' ']]) {
                await page.keyboard.press(key);
                const activated = await settled(`keyboard-${key.toLowerCase()}`, 1);
                assert(activated.events.some(event => event.type === 'keydown' && event.key === eventKey && event.trusted && event.target === 'Count Action'),
                    `Native ${key} did not reach the Count Action keyboard proxy.`);
            }
            await act('Count Action', 'mouse-screen');
            await act('Offscreen Action', 'mouse-offscreen');

            if (iteration === 0) {
                touch = await context.newCDPSession(page);
                await touch.send('Emulation.setTouchEmulationEnabled', { enabled: true, maxTouchPoints: 1 });
                const geometry = await canvasGeometry(page, surface);
                const current = await read(page), [x, y, width, height] = controlPixels(current, 'Count Action');
                const point = { x: geometry.x + (x + width / 2) / geometry.bitmapWidth * geometry.width,
                    y: geometry.y + (1 - (y + height / 2) / geometry.bitmapHeight) * geometry.height };
                result.steps.push({ name: 'touch-delivery', geometry, point });
                assert(await page.evaluate(point => document.elementFromPoint(point.x, point.y)?.id, point) === 'input-surface', 'Touch must hit the canvas.');
                await touch.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ ...point, id: 0 }] });
                await frames(page, 3);
                await touch.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
                const touched = await settled('touch-screen', 1);
                assert(touched.events.some(event => event.type === 'pointerdown' && event.pointerType === 'touch' && event.trusted && event.target === 'input-surface'), 'No trusted canvas touch was observed.');
                await touch.send('Emulation.setTouchEmulationEnabled', { enabled: false });

                await act('Clipped Action', 'clip-visible-hit');
                await pointClick(272, 174); await settled('clip-outside-rejected');
                await clickControl('Clip enabled');
                await page.getByRole('checkbox', { name: 'Clip enabled', exact: true }).waitFor();
                await page.waitForFunction(() => document.querySelector('[role="checkbox"][aria-label="Clip enabled"]')?.getAttribute('aria-checked') === 'false');
                await settled('clip-disabled', 1, 0, 0, pixelWitnesses(false));
                await pointClick(272, 174); await settled('clip-revealed-hit', 1);
                await clickControl('Clip enabled');
                await page.waitForFunction(() => document.querySelector('[role="checkbox"][aria-label="Clip enabled"]')?.getAttribute('aria-checked') === 'true');
                await settled('clip-restored', 1, 0, 0, pixelWitnesses());

                await focusText('Single Line', 'single seed');
                await page.keyboard.press('ControlOrMeta+A'); await page.keyboard.insertText('alpha beta');
                await page.keyboard.press('Home');
                await page.keyboard.down('Shift');
                for (let index = 0; index < 5; index++) await page.keyboard.press('ArrowRight');
                await page.keyboard.up('Shift');
                const selected = await checkpoint('single-line-selection');
                assert(selected.state.active.selectionStart === 0 && selected.state.active.selectionEnd === 5, 'Native partial text selection was not preserved.');
                await page.keyboard.insertText('gamma');
                const edited = await settled('single-line-edit');
                assert(edited.text.value === 'gamma beta' && edited.active.value === 'gamma beta', 'Selection replacement did not reach shared text.');
                await page.keyboard.press('Enter'); await settled('single-line-submit', 1, 1);
                await page.keyboard.press('Escape'); await settled('single-line-cancel', 1, 0, 1);

                await focusText('Multiline', 'first line\nsecond line');
                await page.keyboard.press('ControlOrMeta+A'); await page.keyboard.insertText('edited first');
                await page.keyboard.press('Enter'); await page.keyboard.insertText('edited second');
                const multiline = await settled('multiline-edit');
                assert(multiline.text.value === 'edited first\nedited second' && !multiline.text.singleLine, 'Native multiline edit/newline did not persist.');
                await page.locator('textarea[aria-label="Multiline"]').evaluate(element => {
                    element.dispatchEvent(new CompositionEvent('compositionstart', { bubbles: true, data: '' }));
                    element.setRangeText(' é', element.value.length, element.value.length, 'end');
                    element.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertCompositionText', data: ' é', isComposing: true }));
                });
                const composing = await checkpoint('synthetic-composition-in-progress');
                assert(composing.state.text.composing && composing.state.text.value === multiline.text.value && composing.state.active.value.endsWith(' é'), 'Synthetic composition committed before compositionend.');
                await page.locator('textarea[aria-label="Multiline"]').evaluate(element => {
                    element.dispatchEvent(new CompositionEvent('compositionend', { bubbles: true, data: ' é' }));
                    element.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: ' é' }));
                });
                const composed = await settled('synthetic-composition-committed');
                assert(composed.text.value === `${multiline.text.value} é` && composed.text.version === multiline.text.version + 1, 'Synthetic composition was lost or committed more than once.');
                await focusText('Read Only', 'readonly seed');
                await page.keyboard.press('ControlOrMeta+A'); await page.keyboard.insertText('rejected'); await page.keyboard.press('Backspace');
                const readonly = await settled('readonly-rejected');
                assert(readonly.text.readOnly && readonly.text.value === 'readonly seed' && readonly.active.value === 'readonly seed', 'Readonly control accepted an edit.');
                await focusText('Multiline', `${multiline.text.value} é`);
                await focusText('Single Line', 'gamma beta');

                const target = await page.getByRole('button', { name: 'Mutation Target', exact: true }).elementHandle();
                const beforeRename = await read(page), generation = control(beforeRename, 'Mutation Target').generation;
                const renamed = await act('Rename Target');
                assert(control(renamed, 'Renamed Target')?.generation === generation && !control(renamed, 'Mutation Target'), 'Rename replaced the target or retained its old name.');
                const hidden = await act('Hide Target');
                assert(hidden.controls.length === 16 && !control(hidden, 'Renamed Target'), 'Hidden target remains accessible.');
                const hiddenProxy = { name: 'hidden-proxy-retirement', connected: await target.evaluate(element => element.isConnected) };
                result.steps.push(hiddenProxy);
                assert(!hiddenProxy.connected, 'Hidden proxy was not detached.');
                // Deliberately synthetic stale-node delivery checks listener retirement, not physical input.
                await target.evaluate(element => element.click()); await settled('hidden-stale-proxy-rejected');
                const shown = await act('Hide Target', 'target-shown');
                assert(control(shown, 'Renamed Target')?.generation !== generation, 'Showing the target reused its hidden proxy generation.');
                const reparented = await act('Reparent Target');
                assert(control(reparented, 'Renamed Target')?.generation === control(shown, 'Renamed Target')?.generation,
                    'Reparenting replaced the existing target generation.');
                assertBounds(reparented, 'Renamed Target', [328, 80, 248, 48]);
                assert(reparented.controls[14].name === 'Rotate Clip' && reparented.controls[15].name === 'Renamed Target', 'Reparent did not update native traversal order.');
                assert(reparented.proxies[15].name === 'Renamed Target', 'Native traversal retained the old parent order.');
                await pointClick(452, 192); await settled('reparent-old-location-rejected');
                await act('Renamed Target', 'reparent-new-location-hit');
                const inactive = await act('Deactivate Target');
                assert(inactive.controls.length === 16 && !control(inactive, 'Renamed Target'), 'Inactive target remains accessible.');
                await act('Restore Target', 'restore-inactive-target');
                const oldProxy = await page.getByRole('button', { name: 'Mutation Target', exact: true }).elementHandle();
                const oldState = await read(page), oldGeneration = control(oldState, 'Mutation Target').generation;
                const removed = await act('Remove Target');
                assert(removed.controls.length === 16 && !control(removed, 'Mutation Target'), 'Removed target remains accessible.');
                const removedProxy = { name: 'removed-proxy-retirement', connected: await oldProxy.evaluate(element => element.isConnected) };
                result.steps.push(removedProxy);
                assert(!removedProxy.connected, 'Removed native proxy remains attached.');
                await oldProxy.evaluate(element => element.click());
                await pointClick(452, 192); await settled('removed-target-rejected');
                const restored = await act('Restore Target');
                assert(restored.controls.length === 17 && control(restored, 'Mutation Target')?.generation !== oldGeneration, 'Restore reused a retired generation.');
                assertBounds(restored, 'Mutation Target', [328, 168, 248, 48]);
                await oldProxy.evaluate(element => element.click()); await settled('restored-stale-proxy-rejected');
                await act('Mutation Target', 'restored-target-hit');
                await target.dispose(); await oldProxy.dispose();
            }

            const beforeResize = await read(page);
            await page.setViewportSize({ width: 1504, height: 1160 });
            await page.locator(surface).evaluate(canvas => { canvas.style.width = '1408px'; canvas.style.height = '792px'; });
            await page.waitForFunction(() => {
                const canvas = document.querySelector('#input-surface');
                return canvas.width === 1408 && canvas.height === 792 && document.querySelector('#status')?.dataset.state === 'running';
            });
            const resized = await settled('resized', 0, 0, 0, pixelWitnesses());
            assert(resized.revision > beforeResize.revision && resized.controls.length === 17, 'Resize did not refresh control projection.');
            assertBounds(resized, 'Count Action', [32, 548, 240, 40]);
            assertBounds(resized, 'Offscreen Action', [985.6, 237.6, 264, 70.4]);
            await act('Count Action', 'resized-screen-hit');
            await act('Offscreen Action', 'resized-offscreen-hit');
            assertNoBrowserErrors(events);
            result.status = 'passed';
        } catch (error) {
            failed = true; result.status = 'failed'; result.error = String(error);
            // Failure evidence must not wait for scrolling, stable element bounds,
            // or another animation frame from an already-stalled renderer.
            const failure = { name: 'failure', capture: `ui-parity-${iteration}-failure-page.png` };
            result.steps.push(failure);
            await Promise.all([
                boundedObservation(read(page), 5000).then(state => { failure.state = state; },
                    observationError => { failure.observationError = String(observationError); }),
                page.screenshot({ path: path.join(config.output, failure.capture), fullPage: true, timeout: 5000 })
                    .catch(captureError => { result.failureCaptureError = String(captureError); }),
            ]);
            throw error;
        } finally {
            // Secondary cleanup errors must never hide the original assertion or player failure.
            const cleanupErrors = [];
            try { await boundedObservation(page.evaluate(() => globalThis.uiParityCleanup?.()), 5000); }
            catch (error) { cleanupErrors.push(String(error)); }
            if (touch) try { await touch.detach(); } catch (error) { cleanupErrors.push(String(error)); }
            try { await context.close(); } catch (error) { cleanupErrors.push(String(error)); }
            if (cleanupErrors.length) {
                result.cleanupErrors = cleanupErrors;
                if (!failed) {
                    result.status = 'failed';
                    throw new Error(`BrowserSmoke.UiParityCleanup: ${cleanupErrors.join('; ')}`);
                }
            }
            if (!failed) assertNoBrowserErrors(events);
        }
    }
}
